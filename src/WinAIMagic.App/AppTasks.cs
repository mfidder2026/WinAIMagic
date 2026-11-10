using System.Runtime.InteropServices;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>Runs the scheduled tasks and the heartbeat: on time, with the model started when needed and stopped again
/// afterwards, each run a conversation of its own, and a notification when it is done or needs the user.</summary>
public sealed partial class AppState
{
    public TaskStore Tasks { get; } = new();
    /// <summary>The connectors (Instellingen → Connectors).</summary>
    public McpManager Connectors { get; } = McpManager.Shared ??= new McpManager();

    /// <summary>The conversations of task runs, with their task (Heartbeat.TaskId for a heartbeat check).</summary>
    public Dictionary<Guid, Guid> TaskRuns { get; } = [];
    /// <summary>Tasks whose model is loading for them (Nu uitvoeren shows "Model laden…").</summary>
    public HashSet<Guid> TasksStarting { get; } = [];
    /// <summary>What the last heartbeat found, for Taken and the tray.</summary>
    public string? HeartbeatStatus { get; private set; }
    /// <summary>A conversation a click on a notification asks to open.</summary>
    public event Action<Guid>? OpenRequested;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? taskTimer;
    private bool modelStartedByTasks, keptAwake;

    /// <summary>Tasks or a heartbeat that can go on with the window closed.</summary>
    public bool HasBackgroundWork => Settings.Heartbeat || Tasks.Tasks.Any(t => t.Enabled)
        || (Settings.FeatureTelegram && Telegram.Token != null);

    /// <summary>Called once the window is there: the timer and the chat client's signals.</summary>
    public void StartTasks()
    {
        if (taskTimer != null) return;
        Chat.Tasks = Tasks;
        Notifier.SetUp(this);
        Chat.Finished += id => Ui.TryEnqueue(() =>
        {
            RunFinished(id);
            SpeakIfWanted(id);
        });
        Chat.Sent += id =>
        {
            // the user used the model: it is theirs now, a task must not stop it
            lock (TaskRuns) if (!TaskRuns.ContainsKey(id)) modelStartedByTasks = false;
        };
        Chat.ApprovalAsked += a => Ui.TryEnqueue(() =>
        {
            ApprovalNeeded(a);
            bool isTask;
            lock (TaskRuns) isTask = TaskRuns.ContainsKey(a.Conversation);
            TelegramBridge.Approval(a, isTask);
        });
        Tasks.Changed += Notify;
        Connectors.Changed += Notify;
        taskTimer = Ui.CreateTimer();
        taskTimer.Interval = TimeSpan.FromSeconds(30);
        taskTimer.Tick += (_, _) => TickTasks();
        // after sleep the timer's next tick comes within 30 s, and a missed run is made up for then
        taskTimer.Start();
        TickTasks();
        StartTelegram();
    }

    public void TickTasks()
    {
        var now = DateTimeOffset.Now;
        foreach (var task in Tasks.TakeDue(now)) _ = RunTask(task);
        if (Heartbeat.IsDue(now)) _ = RunHeartbeat();
    }

    /// <summary>One heartbeat: the model goes through the checklist and only speaks up when something needs the user. A check with
    /// nothing to report leaves no conversation behind.</summary>
    public async Task RunHeartbeat(bool forced = false)
    {
        var now = DateTimeOffset.Now;
        Heartbeat.LastRun = now;
        var time = now.ToString("t", AppLanguages.Current.Culture());
        void Say(string text) { HeartbeatStatus = text; Notify(); }
        if (Heartbeat.Checklist() is not { } checklist) { Say(L($"{time}: overgeslagen, de checklist is leeg.")); return; }
        // the user and the tasks go first
        bool others;
        lock (TaskRuns) others = TaskRuns.Count > 0;
        if (!forced && (Chat.Busy || others)) { Say(L($"{time}: overgeslagen, het model was bezig.")); return; }
        if (!EngineState.IsRunning && !forced && !Settings.HeartbeatMayLoad) { Say(L($"{time}: overgeslagen, het model was niet geladen.")); return; }
        if (!await EnsureModel()) { Say(L($"{time}: het model kon niet starten.")); return; }
        var stamp = now.ToString("g", AppLanguages.Current.Culture());
        var c = new Conversation { Title = "♥ " + L("Controle") + " · " + stamp, Task = Heartbeat.TaskId, Hidden = true };
        Store.Insert(c);
        lock (TaskRuns) TaskRuns[c.Id] = Heartbeat.TaskId;
        Say(L($"{time}: bezig met controleren…"));
        Send(Heartbeat.Prompt(checklist), [], c.Id);
    }

    /// <summary>One run of a task: a new conversation with its prompt. Null when the model could not start.</summary>
    public async Task<Guid?> RunTask(ScheduledTask task)
    {
        TasksStarting.Add(task.Id);
        Notify();
        bool ready = await EnsureModel();
        TasksStarting.Remove(task.Id);
        Notify();
        if (!ready)
        {
            Notifier.Post(L($"Taak niet uitgevoerd: {task.Title}"), L("Het model kon niet starten. Kijk in WinAIMagic bij Modellen of Engine."), null);
            return null;
        }
        var stamp = DateTimeOffset.Now.ToString("g", AppLanguages.Current.Culture());
        var c = new Conversation { Title = "⏱ " + task.Title + " · " + stamp, Workspace = task.Workspace, Task = task.Id };
        Store.Insert(c);
        lock (TaskRuns) TaskRuns[c.Id] = task.Id;
        Tasks.Ran(task.Id, c.Id);
        KeepAwake(true);
        Send(task.Prompt, [], c.Id);
        return c.Id;
    }

    /// <summary>Nu uitvoeren: the task runs now, besides its schedule.</summary>
    public void RunTaskNow(Guid id)
    {
        if (Tasks.Task(id) is not { } t) return;
        Tasks.MarkRun(id);
        _ = RunTask(t);
    }

    /// <summary>The model is loaded, or loads now; false when it cannot (not downloaded, an image being made, start failed).</summary>
    public async Task<bool> EnsureModel()
    {
        if (EngineState.IsRunning) return true;
        if (EngineState.Status != EngineStatus.Starting)
        {
            if (!Host.CanStart) return false;
            modelStartedByTasks = true;
            // the start may first wait for the second engine to make room or for the cluster check: until it has begun,
            // the engine's state is the old one (the Mac's startBegun)
            await StartEngineTask();
        }
        var start = DateTime.UtcNow;
        // a "failed" or "stopped" from before this start is old news: only after the engine was starting (or after a grace)
        // does it mean this start failed. Before, a task gave up at once and the engine loaded anyway.
        bool sawStarting = false;
        while ((DateTime.UtcNow - start).TotalSeconds < 1800)
        {
            double waited = (DateTime.UtcNow - start).TotalSeconds;
            switch (EngineState.Status)
            {
                case EngineStatus.Running: return true;
                case EngineStatus.Starting: sawStarting = true; break;
                case EngineStatus.Failed when sawStarting || waited > 15: return false;
                case EngineStatus.Stopped when sawStarting || waited > 15: return false;
            }
            await Task.Delay(1000);
        }
        return false;
    }

    private void RunFinished(Guid id)
    {
        Guid taskId;
        bool last;
        lock (TaskRuns)
        {
            if (!TaskRuns.Remove(id, out taskId)) return;
            last = TaskRuns.Count == 0;
        }
        if (taskId == Heartbeat.TaskId) HeartbeatFinished(id);
        else TaskNotification(id, taskId);
        if (!last) return;
        KeepAwake(false);
        // the model a task loaded goes again, unless the user used it meanwhile or something else runs
        if (modelStartedByTasks && Settings.TasksStopModel && !Chat.Busy) StopEngine();
        modelStartedByTasks = false;
        Notify();
    }

    /// <summary>Instellingen → Agent → "Lees antwoorden vanzelf voor": the answer of a conversation on screen, not of a task or
    /// Telegram.</summary>
    private void SpeakIfWanted(Guid id)
    {
        var s = Settings;
        bool isTask;
        lock (TaskRuns) isTask = TaskRuns.ContainsKey(id);
        if (!s.FeatureVoice || !s.SpeakAnswers || isTask || !WindowActive) return;
        if (Store.Find(id) is not { Telegram: null, Task: null } c) return;
        if (c.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Length > 0 && m.Error == null) is { } last) Speech.Shared.Speak(last.Text, last.Id);
    }

    private void HeartbeatFinished(Guid id)
    {
        var time = DateTimeOffset.Now.ToString("t", AppLanguages.Current.Culture());
        var last = Store.Find(id)?.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (last?.Error == null && Heartbeat.IsNothing(last?.Text ?? ""))
        {
            Store.Delete(id);
            HeartbeatStatus = L($"Laatste controle {time}: niets te melden.");
            return;
        }
        // something to say: the conversation shows, under Taken and in the sidebar
        Store.Update(id, c => c.Hidden = null);
        Store.Save();
        var answer = last?.Error ?? (last?.Text ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        HeartbeatStatus = L($"Laatste controle {time}: er is iets te melden.");
        Notifier.Post(L("WinAIMagic heeft iets voor je"), answer, id);
        TelegramBridge.Notify(L("WinAIMagic heeft iets voor je"), last?.Error ?? last?.Text ?? "");
    }

    private void TaskNotification(Guid id, Guid taskId)
    {
        var title = Tasks.Task(taskId)?.Title ?? L("Taak");
        var last = Store.Find(id)?.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (last?.Error is { } error)
        {
            Notifier.Post(L($"Taak gestopt: {title}"), error, id);
            TelegramBridge.Notify(L($"Taak gestopt: {title}"), error);
        }
        else
        {
            var answer = (last?.Text ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
            Notifier.Post(L($"Taak klaar: {title}"), answer ?? L("Open het gesprek voor de uitkomst."), id);
            TelegramBridge.Notify(L($"Taak klaar: {title}"), ChatClient.WithoutDoneWord(last?.Text ?? ""));
        }
    }

    /// <summary>A run waits for the user: say so when nobody is looking (a task, or the window in the background). A task's
    /// approval that nobody gives within a day counts as no.</summary>
    private void ApprovalNeeded(Approval a)
    {
        bool isTask;
        lock (TaskRuns) isTask = TaskRuns.ContainsKey(a.Conversation);
        if (!isTask && WindowActive) return;
        var title = Store.Find(a.Conversation)?.DisplayTitle ?? "";
        Notifier.Post(L($"Goedkeuring nodig: {title}"), a.Title, a.Conversation);
        if (!isTask) return;
        _ = Task.Delay(TimeSpan.FromHours(24)).ContinueWith(_ => Ui.TryEnqueue(() =>
        {
            if (Chat.ApprovalFor(a.Conversation) is { } still && still.Id == a.Id) Chat.Answer(still, false);
        }), TaskScheduler.Default);
    }

    /// <summary>The window is on screen and in front.</summary>
    public bool WindowActive { get; set; } = true;

    /// <summary>Opens a conversation (a click on a notification, the tray).</summary>
    public void Open(Guid conversation)
    {
        if (Store.Find(conversation) == null) return;
        Store.Update(conversation, c => c.Hidden = null);
        Pane = new Pane(PaneKind.Chat, conversation);
        OpenRequested?.Invoke(conversation);
    }

    /// <summary>A task in progress keeps the PC awake (not the screen). Called on the UI thread, which lives as long as the app.</summary>
    private void KeepAwake(bool on)
    {
        if (on == keptAwake) return;
        keptAwake = on;
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);
    }

    private void StopTasks()
    {
        taskTimer?.Stop();
        Telegram.Stop();
        StopAgent();
        Speech.Shared.StopSpeaking();
        KeepAwake(false);
        Connectors.StopAll();
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
