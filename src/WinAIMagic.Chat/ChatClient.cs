using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>A change the model wants to make (write a file, run a command), waiting for the user's yes or no.</summary>
public sealed record Approval(Guid Conversation, ToolCall Call, string Title, string Detail)
{
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>What the card warns about (a sensitive setting, a plan that follows reading the web).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>A system plan: its steps, with the current value where the app could read it first.</summary>
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
    /// <summary>Needs administrator rights: Windows asks (UAC) after this yes, and "always" is not offered.</summary>
    public bool Admin { get; init; }
    /// <summary>Asks every time, also after "always" (sending a mail, a click that sends something).</summary>
    public bool EveryTime { get; init; }
}

/// <summary>One step of a system plan on the approval card.</summary>
public sealed record PlanStep(string Title, string Command, string? Current, bool Admin);

/// <summary>Allow, and whether that goes for the rest of this chat in this app session.</summary>
public readonly record struct ApprovalAnswer(bool Allow, bool Always = false);

/// <summary>What the engine answered with instead of a stream.</summary>
public sealed class ChatEngineException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Talks to the local engine's OpenAI-compatible endpoint: streams answers and thinking, and runs the tool loop
/// (web search, page reading, work in a folder) until the model answers without calling a tool. Started on the UI thread,
/// the loop and every store change stay there, as on the Mac's main actor; network and tools run elsewhere.</summary>
public sealed class ChatClient(HttpClient? http = null)
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http = http ?? SharedHttp;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3600);
    /// <summary>At this share of the context the conversation is summarized before the next request.</summary>
    public const double CompactAt = 0.8;
    /// <summary>How many times in a row the model is told to go on without it doing anything in between.</summary>
    public const int MaxNudges = 3;
    /// <summary>One answer may take at most this share of the context: a model that plans aloud for 50,000 tokens fills the whole
    /// context, gets summarized, and starts the same plan again (GLM on the Mac). A big file still fits (16K tokens at 64K).</summary>
    public const double AnswerShare = 0.25;
    public int MaxAnswerTokens => Math.Max(4096, (int)(ContextLimit * AnswerShare));
    /// <summary>The engine runs with vision now: the model can look at an image file (view_image). Set by the app.</summary>
    public static Func<bool> SeesImages { get; set; } = () => false;
    /// <summary>Pictures the model looked at stay in its requests, the last few of them: each costs hundreds of tokens.</summary>
    public const int ImagesKept = 3;

    /// <summary>Any conversation at work.</summary>
    public bool Busy { get { lock (sync) return runs.Count > 0; } }
    /// <summary>This conversation has a run going: each works on its own, and they take turns for the model (Gate).</summary>
    public bool IsBusy(Guid conversation) { lock (sync) return runs.ContainsKey(conversation); }
    /// <summary>The conversations with a run going.</summary>
    public IReadOnlyList<Guid> Running { get { lock (sync) return [.. runs.Keys]; } }
    /// <summary>The conversation a run works for, carried through every await of that run.</summary>
    public static Guid? RunConversation => runConversation.Value;
    private static readonly AsyncLocal<Guid?> runConversation = new();
    /// <summary>A conversation got a question (the user's, or a task's).</summary>
    public event Action<Guid>? Sent;
    /// <summary>A conversation's run ended (the scheduler hears of its tasks this way).</summary>
    public event Action<Guid>? Finished;
    /// <summary>A run waits for the user's approval (a task in the background gives a notification).</summary>
    public event Action<Approval>? ApprovalAsked;
    /// <summary>Summarizing the start of a conversation to free context.</summary>
    public bool Compacting { get; private set; }
    /// <summary>Writing down what this task taught the model, for WINAIMAGIC.md.</summary>
    public bool Learning { get; private set; }
    /// <summary>The engine's context in tokens (set when it starts).</summary>
    public int ContextLimit { get; set; } = 65_536;
    /// <summary>The model the engine runs, for the system prompt.</summary>
    public string ModelName { get; set; } = "DeepSeek-V4.1-Flash";
    /// <summary>A question waiting for an answer, of any conversation.</summary>
    public Approval? PendingApproval { get { lock (sync) return approvals.Values.FirstOrDefault(); } }
    /// <summary>The approval this conversation waits for.</summary>
    public Approval? ApprovalFor(Guid conversation) { lock (sync) return approvals.GetValueOrDefault(conversation); }
    /// <summary>The card's "make a restore point first" (admin plans); on again for every new admin card.</summary>
    public bool Snapshot { get; set; } = true;

    /// <summary>The loop that Send started last; done when that conversation's run ends.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;
    /// <summary>This conversation's run, or a finished task.</summary>
    public Task CompletionOf(Guid conversation) { lock (sync) return runs.GetValueOrDefault(conversation)?.Task ?? Task.CompletedTask; }

    /// <summary>The engine's queue for one base address (the main and the second engine each have their own).</summary>
    public ModelGate Gate(Uri baseUrl)
    {
        lock (sync)
        {
            var key = baseUrl.GetLeftPart(UriPartial.Authority);
            if (!gates.TryGetValue(key, out var gate)) gates[key] = gate = new ModelGate();
            return gate;
        }
    }

    /// <summary>The conversation the model is writing for while this one waits its turn; null when it does not wait.</summary>
    public Guid? WaitingBehind(Guid conversation)
    {
        Uri? url;
        lock (sync) url = runs.GetValueOrDefault(conversation)?.BaseUrl;
        if (url == null) return null;
        var gate = Gate(url);
        return gate.Waiting.Contains(conversation) && gate.Holder is { } other && other != conversation ? other : null;
    }

    /// <summary>Busy, Compacting, Learning, an approval or a queue changed.</summary>
    public event Action? StateChanged;
    /// <summary>New text in a message (conversation, message, content, reasoning): at most every 0.1 s, never synchronous
    /// with the stream, on the thread Send was called on. The store has the text already.</summary>
    public event Action<Guid, Guid, string, string>? Delta;
    /// <summary>A token arrived, for the live speed chart. On a network thread: do not touch the UI from it.</summary>
    public event Action<int>? TokensStreamed;
    /// <summary>Something the user should read: a search engine failed and another took over.</summary>
    public event Action<string>? Notice;
    /// <summary>Asks the user. Without it the question waits in PendingApproval until Answer. Stop cancels the token and
    /// counts as no.</summary>
    public Func<Approval, CancellationToken, Task<ApprovalAnswer>>? AskUser { get; set; }

    private sealed class RunState(CancellationTokenSource cts, Uri baseUrl)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public Uri BaseUrl { get; } = baseUrl;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly object sync = new();
    private readonly Dictionary<Guid, RunState> runs = [];
    private readonly Dictionary<Guid, Approval> approvals = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> answers = [];
    private readonly Dictionary<string, ModelGate> gates = [];
    /// <summary>The connectors each running conversation loaded.</summary>
    private readonly Dictionary<Guid, List<string>> loaded = [];
    /// <summary>The connectors of the conversation that runs, for its requests.</summary>
    private List<string> LoadedConnectors
    {
        get { lock (sync) return RunConversation is { } id && loaded.TryGetValue(id, out var l) ? [.. l] : []; }
        set { lock (sync) if (RunConversation is { } id) loaded[id] = [.. value]; }
    }
    private static McpManager? Connectors => McpManager.Shared;
    /// <summary>Where schedule_task puts what the model plans (the app's tasks); null: no such tool.</summary>
    public TaskStore? Tasks { get; set; }
    /// <summary>Conversations where the user chose "always allow", for this app session.</summary>
    private readonly HashSet<Guid> trusted = [];
    /// <summary>The store of the conversations that run, for their notes as they were when they began (FrozenNotes).</summary>
    private ConversationStore? runStore;

    /// <summary>Web questions need a few rounds; work in a folder (read, write, run, read the log, fix) needs many more.</summary>
    private int MaxToolRounds => WorkspaceActive || Settings.Shared.SystemManagement || LoadedConnectors.Count > 0 || AgentOn ? 200 : 20;
    private static bool AgentOn => AgentTools.All.Exists(s => s.IsOn);
    private static bool WorkspaceActive => Settings.Shared.WorkspaceTools && WorkspaceTools.Workspace != null;

    public void Answer(Approval approval, bool allow, bool always = false)
    {
        TaskCompletionSource<bool>? waiting;
        lock (sync)
        {
            if (always) trusted.Add(approval.Conversation);
            answers.Remove(approval.Id, out waiting);
            if (approvals.TryGetValue(approval.Conversation, out var a) && a.Id == approval.Id) approvals.Remove(approval.Conversation);
        }
        waiting?.TrySetResult(allow);
        StateChanged?.Invoke();
    }

    private bool IsPending(Approval approval) { lock (sync) return answers.ContainsKey(approval.Id); }

    private Task<bool> AskApproval(ToolCall call, Guid conversation, CancellationToken ct)
    {
        var (title, detail) = WorkspaceTools.ApprovalText(call);
        return Ask(new Approval(conversation, call, title, detail), ct);
    }

    /// <summary>"Always in this conversation" covers everything but admin rights and what asks every time.</summary>
    private async Task<bool> Ask(Approval approval, CancellationToken ct)
    {
        lock (sync) if (trusted.Contains(approval.Conversation) && !approval.Admin && !approval.EveryTime) return true;
        if (approval.Admin) Snapshot = true;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            answers[approval.Id] = answer;
            approvals[approval.Conversation] = approval;
        }
        StateChanged?.Invoke();
        ApprovalAsked?.Invoke(approval);
        // Stop is a no, also when it comes in just before the question stands
        using var stop = ct.Register(() => { if (IsPending(approval)) Answer(approval, false); });
        if (ct.IsCancellationRequested) answer.TrySetResult(false);
        if (AskUser is { } ask) _ = Bridge(ask, approval, ct);
        return await answer.Task;
    }

    private async Task Bridge(Func<Approval, CancellationToken, Task<ApprovalAnswer>> ask, Approval approval, CancellationToken ct)
    {
        ApprovalAnswer given;
        try { given = await ask(approval, ct); }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException) { given = new ApprovalAnswer(false); }
        if (IsPending(approval)) Answer(approval, given.Allow, given.Always);
    }

    /// <summary>Stops this conversation's run; the others go on.</summary>
    public void Stop(Guid conversation)
    {
        RunState? run;
        Approval? waiting;
        lock (sync)
        {
            run = runs.GetValueOrDefault(conversation);
            waiting = approvals.GetValueOrDefault(conversation);
        }
        run?.Cts.Cancel();
        if (waiting != null) Answer(waiting, false);
    }

    /// <summary>Stops every run.</summary>
    public void Stop()
    {
        foreach (var id in Running) Stop(id);
    }

    public void Send(string text, IReadOnlyList<Attachment> attachments, Guid conversation, ConversationStore store, Uri baseUrl)
    {
        if (IsBusy(conversation)) return;
        FillStopped(conversation, store);   // a conversation that ended in a crash: its question must be answered first
        store.Update(conversation, c =>
        {
            c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text, Attachments = [.. attachments] });
            // a finished list goes with the next question
            if (c.Steps is { } steps && steps.TrueForAll(s => s.IsDone)) c.Steps = null;
            if (c.Title == Conversation.DefaultTitle)
            {
                var t = text.Length == 0 ? (attachments.FirstOrDefault()?.Filename ?? "Afbeelding") : text;
                c.Title = TextLimit.Prefix(t, 48).Replace("\n", " ");
            }
        });
        runStore = store;
        var cts = new CancellationTokenSource();
        var run = new RunState(cts, baseUrl);
        lock (sync) runs[conversation] = run;
        StateChanged?.Invoke();
        Sent?.Invoke(conversation);
        // this conversation's folder for the tools and the system prompt, for the whole run
        var folder = store.Find(conversation)?.Workspace ?? "";
        var task = RunAndLearn(conversation, folder, run, store, baseUrl, SynchronizationContext.Current, cts.Token);
        run.Task = task;
        Completion = task;
    }

    private async Task RunAndLearn(Guid id, string folder, RunState run, ConversationStore store, Uri baseUrl, SynchronizationContext? owner, CancellationToken ct)
    {
        // both flow through every await of this run and no further: two runs never see each other's folder
        runConversation.Value = id;
        WorkspaceTools.Current = folder;
        try
        {
            await Run(id, store, baseUrl, owner, ct);
            await Learn(id, store, baseUrl, ct);
            // the answer is done: its place in the engine goes to the SSD now, not only when another conversation takes the
            // model or the app quits (a quit or a crash that comes first would lose it)
            if (Gate(baseUrl).Holder == null && SlotKeeper.At(baseUrl) is { } keeper && keeper.Current == id) await keeper.SaveCurrentAsync();
        }
        // a stop can also surface as another error of the cancelled request (an IOException from the stream): never a red line
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            store.Update(id, c => { if (c.Messages.Count > 0) c.Messages[^1].Error = e.Message; });
        }
        finally
        {
            lock (sync)
            {
                if (runs.GetValueOrDefault(id) == run) runs.Remove(id);
                loaded.Remove(id);
            }
            StateChanged?.Invoke();
            store.Save();
            Finished?.Invoke(id);
        }
    }

    private async Task Run(Guid id, ConversationStore store, Uri baseUrl, SynchronizationContext? owner, CancellationToken ct)
    {
        LoadedConnectors = store.Find(id)?.Connectors ?? [];
        // connectors loaded before (or always loaded) run again, so their tools are in the request
        if (Connectors is { } connectors) await connectors.EnsureRunningAsync(LoadedConnectors, ct);
        if (KeepsNotes && WorkspaceTools.Workspace is { } folder) WorkspaceNotes.CreateIfNeeded(folder);
        LoadMatchingSkills(id, store);
        var activities = new List<ToolActivity>();
        var sources = new List<Source>();
        int nudges = 0;
        int rounds = MaxToolRounds;
        // only work in a folder needs the model's own KLAAR to end: an answer after reading the system or the web is finished
        // when it does not announce a next step
        bool changedFiles = false;
        // rounds of tools since the model last updated its step list: a model forgets to tick steps off
        int roundsSinceSteps = 0;
        // a step was taken in an earlier round of this answer
        bool tookSteps = false;
        // "ga door" on work that is under way: the plan was made, so even the first round only takes the next step
        bool goingOn = store.Find(id) is { } started && IsGoOn(started);
        try
        {
            for (int round = 0; round < rounds; round++)
            {
                if (store.Find(id)?.ContextTokens is { } used && used > ContextLimit * CompactAt)
                    await CompactAsync(id, store, baseUrl, null, ct);
                if (ct.IsCancellationRequested) return;
                // each round keeps its own steps, so the chat shows thinking, text and steps in the order they came
                if (activities.Count > 0) tookSteps = true;
                activities = [];
                var reply = new ChatMessage { Role = ChatRole.Assistant, Sources = [.. sources] };
                int budget = ThinkingBudget(Settings.Shared.ThinkingBudgetNow, goingOn ? round + 1 : round);
                store.Update(id, c => c.Messages.Add(reply));

                var throttle = new StreamThrottle((content, reasoning) =>
                {
                    store.Update(id, _ =>
                    {
                        reply.Text += content;
                        reply.Reasoning += reasoning;
                    });
                    Delta?.Invoke(id, reply.Id, content, reasoning);
                }, context: owner)
                {
                    Prepared = p => store.Update(id, _ => reply.Preparing = p),
                };

                StreamResult result;
                bool retried = false;
                while (true)
                {
                    var c = store.Find(id);
                    if (c == null) return;
                    var history = c.Unsummarized;
                    history.RemoveAt(history.Count - 1);
                    try
                    {
                        result = await Task.Run(() => StreamAsync(history, c.Summary, baseUrl, throttle, budget, ct), CancellationToken.None);
                        await throttle.DrainAsync();
                        break;
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        await throttle.DrainAsync();
                        store.Update(id, _ => reply.Preparing = null);
                        return;
                    }
                    catch (ChatEngineException e) when (!retried && e.Status == 400 && e.Message.Contains("context", StringComparison.OrdinalIgnoreCase))
                    {
                        // the conversation no longer fits: summarize its start and try again once
                        retried = true;
                        await CompactAsync(id, store, baseUrl, reply.Id, ct);
                    }
                    catch (Exception e)
                    {
                        await throttle.DrainAsync();
                        store.Update(id, _ => { reply.Error = e.Message; reply.Preparing = null; });
                        return;
                    }
                }
                if (result.ContextTokens is { } tokens) store.Update(id, c => c.ContextTokens = tokens);

                store.Update(id, _ =>
                {
                    reply.Preparing = null;
                    reply.ToolCalls = result.ToolCalls;
                    reply.TokensPerSecond = result.TokensPerSecond;
                    reply.PromptTokensPerSecond = result.PromptTokensPerSecond;
                });
                if (ct.IsCancellationRequested) return;
                if (result.ToolCalls.Count == 0)
                {
                    bool cutOff = result.FinishReason == "length";
                    if (!LooksUnfinished(reply.Text, reply.Reasoning, cutOff, WorkspaceActive || tookSteps || activities.Count > 0, changedFiles))
                    {
                        // KLAAR is a word for the app, not for the user
                        store.Update(id, _ => reply.Text = WithoutDoneWord(reply.Text));
                        return;
                    }
                    if (nudges >= MaxNudges)
                    {
                        // it keeps stopping: say so, instead of just falling silent
                        store.Update(id, _ => reply.Error = L($"Het model stopte {MaxNudges} keer zonder verder te werken of te zeggen dat de taak af is. Zeg \"ga door\", of geef het een aanwijzing."));
                        return;
                    }
                    nudges++;
                    if (cutOff) store.Update(id, _ => reply.CutOff = true);
                    // "go on where you were" made a model that plans aloud write the same plan again, without end
                    var nudge = cutOff ? L("Je antwoord werd te lang en is afgekapt; je krijgt alleen het begin en het eind terug. Denk of plan niet opnieuw in tekst: doe nu meteen de volgende stap met gereedschap. Schrijf grote code in delen: één bestand of één deel per write_file.")
                        : L("Je stopte zonder gereedschap te gebruiken. Is de taak nog niet af, roep dan nu meteen het volgende gereedschap aan, zonder het eerst aan te kondigen; werkt iets niet, probeer dan een andere aanpak. Is de taak wel af en gecontroleerd, begin je bericht dan met het woord KLAAR en zeg kort wat het resultaat is.");
                    store.Update(id, c => c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = nudge, Automatic = true }));
                    continue;
                }
                nudges = 0;

                // a picture cannot go in a tool result: it follows them, as a message of the app with the image
                var looked = new List<Attachment>();
                if (result.ToolCalls.Exists(t => t.Name == WorkSteps.ToolName)) roundsSinceSteps = 0; else roundsSinceSteps++;
                var stepsNudge = WorkSteps.Reminder(store.Find(id)?.Steps, roundsSinceSteps);
                if (stepsNudge != null) roundsSinceSteps = 0;

                // run the tools; their steps stay with this round, the sources go on to the next
                for (int place = 0; place < result.ToolCalls.Count; place++)
                {
                    var call = result.ToolCalls[place];
                    bool isWorkspace = WorkspaceTools.Names.Contains(call.Name);
                    bool isSystem = SystemTools.Names.Contains(call.Name);
                    bool isSkill = call.Name == Skills.ToolName;
                    bool isApp = AppTools.Names.Contains(call.Name);
                    bool isSteps = call.Name == WorkSteps.ToolName;
                    bool isSchedule = call.Name == TaskTool.ToolName;
                    bool isLoadConnector = call.Name == McpManager.LoadTool;
                    var connectorTool = Connectors?.Route(call.Name);
                    var agent = AgentTools.For(call.Name);
                    if (isWorkspace) changedFiles = true;
                    var callArgs = WorkspaceTools.Args(call);
                    var step = new ToolActivity
                    {
                        Label = agent != null ? agent.Label(call) : isWorkspace ? WorkspaceTools.Label(call) : isSystem ? SystemTools.Label(call) : isSkill ? Skills.Label(call)
                            : isApp ? AppTools.Label(call)
                            : isSteps ? WorkSteps.Label(WorkSteps.Parse(callArgs))
                            : isSchedule ? L($"Taak plannen: {WorkspaceTools.Str(callArgs, "title") ?? ""}")
                            : isLoadConnector ? L($"Connector laden: {WorkspaceTools.Str(callArgs, "name") ?? ""}")
                            : connectorTool is { } r ? McpManager.Label(r) : WebTools.Label(call),
                    };
                    activities.Add(step);
                    store.Update(id, _ => reply.Activities = Copy(activities));
                    string text;
                    List<Attachment>? shots = null;
                    if (agent != null)
                    {
                        var output = await RunAgent(agent, call, id, ct);
                        text = output.Text;
                        if (output.Denied) step.Denied = true;
                    }
                    else if (isWorkspace)
                    {
                        var kind = WorkspaceTools.RightFor(call.Name);
                        var right = Settings.Shared.Right(kind);
                        if (right == "deny")
                        {
                            text = $"The user's settings do not allow this kind of action ({kind.Title()}). Do not try it again; tell the user what you wanted to do, or continue without it.";
                            step.Denied = true;
                        }
                        else if (right == "ask" && !await AskApproval(call, id, ct))
                        {
                            text = "The user did not allow this. Ask what they want instead, or continue without it.";
                            step.Denied = true;
                        }
                        else if (call.Name == WorkspaceTools.ImageTool)
                        {
                            try
                            {
                                var picture = await WorkspaceTools.ImageAsync(call);
                                if (SeesImages())
                                {
                                    looked.Add(picture);
                                    text = "The image follows in the next message.";
                                }
                                else text = "The model runs without vision now, so it cannot see images. Check the result another way (text output, a log).";
                            }
                            catch (ToolError e) { text = "Error: " + e.Message; }
                        }
                        else
                        {
                            var commandStart = DateTime.UtcNow;
                            text = await WorkspaceTools.RunAsync(call, ct);
                            // the pictures the step makes (an emulator screenshot): small, beside the step, so the user watches along
                            if (call.Name == "run_command")
                                foreach (var file in WorkspaceTools.NewImages(commandStart, (WorkspaceTools.Str(callArgs, "command") ?? "") + "\n" + text))
                                    if (await ImageScaler.ThumbnailAsync(file) is { } shot) (shots ??= []).Add(shot);
                        }
                    }
                    else if (isSystem)
                    {
                        var output = await RunSystem(call, id, store, ct);
                        text = output.Text;
                        if (output.Denied) step.Denied = true;
                    }
                    else if (isSkill)
                    {
                        text = Skills.Run(call);
                    }
                    else if (isSteps)
                    {
                        if (WorkSteps.Parse(callArgs) is { } steps)
                        {
                            store.Update(id, c => c.Steps = steps);
                            text = WorkSteps.Summary(steps);
                        }
                        else text = "Error: give steps as a list of {title, status} with status todo, doing or done.";
                    }
                    else if (isSchedule)
                    {
                        var output = await ScheduleTask(call, id, ct);
                        text = output.Text;
                        if (output.Denied) step.Denied = true;
                    }
                    else if (isLoadConnector && Connectors is { } mcp)
                    {
                        var output = await mcp.LoadAsync(WorkspaceTools.Str(callArgs, "name") ?? "", ct);
                        text = output.Text;
                        if (output.Name is { } name && !LoadedConnectors.Contains(name))
                        {
                            var now = LoadedConnectors;
                            now.Add(name);
                            LoadedConnectors = now;
                            store.Update(id, c => c.Connectors = now);
                        }
                    }
                    else if (connectorTool is { } route)
                    {
                        var output = await RunConnector(call, route, id, store, ct);
                        text = output.Text;
                        if (output.Denied) step.Denied = true;
                    }
                    else if (isApp)
                    {
                        var output = await RunApp(call, id, ct);
                        text = output.Text;
                        if (output.Denied) step.Denied = true;
                    }
                    else
                    {
                        var found = await WebTools.RunAsync(call, ct);
                        text = found.Text;
                        foreach (var s in found.Sources)
                            if (!sources.Exists(x => x.Url == s.Url)) sources.Add(s);
                        if (found.Notice is { } notice) Notice?.Invoke(notice);
                    }
                    if (ct.IsCancellationRequested) return;
                    // one result must not fill a small context: the start and the end, which usually matter most
                    int room = Math.Max(8_000, ContextLimit * 3 / 4);
                    if (text.Length > room)
                        text = TextLimit.Prefix(text, room * 3 / 4) + $"\n… (shortened, the output was {text.Length} characters) …\n" + TextLimit.Suffix(text, room / 4);
                    step.Done = true;
                    step.Output = TextLimit.Prefix(text, 4000);
                    if (shots != null) step.Images = shots;
                    // the reminder rides on the round's last result: a message of its own would break the tool turn, and on the last
                    // one the engine's cache of the conversation stays valid
                    var withNudge = place == result.ToolCalls.Count - 1 && stepsNudge != null ? text + "\n\n" + stepsNudge : text;
                    store.Update(id, c =>
                    {
                        // the step is done (or denied) in its own round
                        reply.Activities = Copy(activities);
                        reply.Sources = [];
                        c.Messages.Add(new ChatMessage { Role = ChatRole.Tool, Text = withNudge, ToolCallId = call.Id });
                    });
                }
                if (looked.Count > 0)
                {
                    var names = string.Join(", ", looked.Select(a => a.Filename));
                    store.Update(id, c => c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = L($"Hier is de afbeelding die je opvroeg: {names}."), Attachments = looked, Automatic = true }));
                }
            }
            // the steps ran out: say so, in a message the user sees (a tool result is folded away)
            store.Update(id, c => c.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Sources = [.. sources],
                Error = L($"Het model heeft {rounds} stappen gezet en pauzeert. Zeg \"ga door\" om verder te gaan."),
            }));
        }
        finally
        {
            if (ct.IsCancellationRequested) FillStopped(id, store);
        }
    }

    /// <summary>A question clearly about a skill's subject gets that skill before the first answer, as if the model had called
    /// use_skill: a small model often answers or searches the web without loading it. Once per skill per conversation.</summary>
    internal static void LoadMatchingSkills(Guid id, ConversationStore store, IReadOnlyList<Skill>? skills = null)
    {
        if (!Settings.Shared.Skills || store.Find(id) is not { } c) return;
        if (c.Messages.LastOrDefault(m => m.Role == ChatRole.User && m.Automatic != true)?.Text is not { } question) return;
        var loaded = c.Messages.SelectMany(m => m.ToolCalls).Where(t => t.Name == Skills.ToolName)
            .Select(t => (WorkspaceTools.Str(WorkspaceTools.Args(t), "name") ?? "").ToLowerInvariant()).ToHashSet();
        foreach (var skill in Skills.Matching(question, skills))
        {
            if (loaded.Contains(skill.Name.ToLowerInvariant())) continue;
            var call = new ToolCall("auto_skill_" + Guid.NewGuid().ToString("N")[..8], Skills.ToolName, new JsonObject { ["name"] = skill.Name }.ToJsonString());
            var step = new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [call],
                Activities = [new ToolActivity { Label = Skills.AutoLabel(skill.Name), Done = true }],
                Automatic = true,
            };
            store.Update(id, conversation =>
            {
                conversation.Messages.Add(step);
                conversation.Messages.Add(new ChatMessage { Role = ChatRole.Tool, Text = Skills.Run(call, [skill]), ToolCallId = call.Id });
            });
        }
    }

    // MARK: - system management

    /// <summary>system_read, system_change and system_undo, with the user's rights; a change always shows its plan first.</summary>
    private async Task<(string Text, bool Denied)> RunSystem(ToolCall call, Guid id, ConversationStore store, CancellationToken ct)
    {
        var s = Settings.Shared;
        var a = WorkspaceTools.Args(call);
        switch (call.Name)
        {
            case "system_read":
                {
                    var command = (WorkspaceTools.Str(a, "command") ?? "").Trim();
                    if (!SystemTools.IsReadOnly(command))
                        return ("This command is not on the list of read-only commands (one program, without pipes, ;, &, redirection, $, (, ), {, } or [ ]). Read with a simpler command and sort the result yourself, or use system_change if it changes something.", false);
                    switch (s.SystemRight(false))
                    {
                        case "deny":
                            return ("The user's settings do not allow reading the system. Tell the user what you wanted to look at.", true);
                        case "ask":
                            if (!await Ask(new Approval(id, call, L("Systeem lezen"), command), ct))
                                return ("The user did not allow this. Ask what they want instead, or continue without it.", true);
                            break;
                    }
                    var output = await SystemTools.RunUser(command, 120, ct);
                    return ((output.Output.Length == 0 ? "(no output)" : output.Output) + (output.Ok ? "" : "\n[the command failed]"), false);
                }
            case "system_change":
                {
                    (string Summary, List<SystemTools.Step> Steps) plan;
                    try { plan = SystemTools.Plan(call); }
                    catch (SystemTools.PlanException e) { return ("Error: " + e.Message, false); }
                    bool admin = plan.Steps.Exists(x => x.Admin);
                    var right = s.SystemRight(true);
                    if (right == "deny")
                        return ("The user's settings do not allow changing the system. Tell the user what you would change and how they can do it themselves.", true);
                    if (right == "ask" || admin)
                    {
                        var current = await SystemTools.Preview(plan.Steps, ct);
                        var approval = new Approval(id, call, plan.Summary, string.Join("\n", plan.Steps.Select(x => (x.Admin ? "[admin] " : "") + x.Apply)))
                        {
                            Warnings = SystemTools.Warnings(plan.Steps, ReadWeb(id, store)),
                            Steps = plan.Steps.Select((x, i) => new PlanStep(x.Title, x.Apply, current[i], x.Admin)).ToList(),
                            Admin = admin,
                        };
                        // AutoApprove (tests) skips the card, but never the UAC prompt of Windows
                        if (!(s.AutoApprove && right == "allow") && !await Ask(approval, ct))
                            return ("The user did not approve this plan. Ask what they want instead.", true);
                    }
                    var change = await SystemTools.Apply(plan.Summary, plan.Steps, admin && Snapshot, ct: ct);
                    return (SystemTools.Report(change), false);
                }
            case "system_undo":
                {
                    var wanted = (WorkspaceTools.Str(a, "id") ?? "").Trim();
                    var all = SystemTools.Load();
                    if (all.Find(c => c.Id == wanted) is not { } change)
                        return ($"There is no change with id {wanted}. Recent: " + string.Join(", ", all.Take(5).Select(c => $"{c.Id} ({c.Summary})")), false);
                    if (change.Undone != null) return ($"Change {wanted} was already undone.", false);
                    if (s.SystemRight(true) == "deny") return ("The user's settings do not allow changing the system.", true);
                    var undoSteps = Enumerable.Range(0, change.Steps.Count).Reverse().Where(i => change.Results[i].Ran).ToList();
                    var approval = new Approval(id, call, L($"Ongedaan maken: {change.Summary}"), string.Join("\n", undoSteps.Select(i => change.Steps[i].Undo)))
                    {
                        Warnings = SystemTools.Warnings(undoSteps.Select(i => change.Steps[i]).ToList(), false),
                        Steps = undoSteps.Select(i => new PlanStep(L($"Terugzetten: {change.Steps[i].Title}"), change.Steps[i].Undo, null, change.Steps[i].Admin)).ToList(),
                        Admin = change.Admin,
                    };
                    if (!await Ask(approval, ct)) return ("The user did not approve undoing it.", true);
                    var done = await SystemTools.Undo(change, ct: ct);
                    return ($"Undone {done.Id}:\n" + done.UndoReport, false);
                }
            default:
                return ($"Unknown tool {call.Name}", false);
        }
    }

    /// <summary>app_help and app_status only read; create_skill writes a skill of the user's own after their yes.</summary>
    private async Task<(string Text, bool Denied)> RunApp(ToolCall call, Guid id, CancellationToken ct)
    {
        switch (call.Name)
        {
            case "app_help": return (AppTools.Help(call), false);
            case "app_status": return (AppTools.RunStatus(), false);
            case "app_action":
                {
                    var args = WorkspaceTools.Args(call);
                    var action = (WorkspaceTools.Str(args, "action") ?? "").Trim();
                    if (!AppTools.Actions.Contains(action)) return ($"Unknown action \"{action}\". Use one of: {string.Join(", ", AppTools.Actions)}.", false);
                    if (AppTools.Control is not { } control) return ("The app cannot do that from here.", false);
                    var plan = control.Plan(action, args);
                    if (plan.Error != null) return ("Error: " + plan.Error, false);
                    var right = Settings.Shared.SystemRight(true);
                    if (!plan.ReadOnly)
                    {
                        if (right == "deny") return ("The user's settings do not allow changing the app or the system (Systeembeheer). Tell the user how to do it themselves.", true);
                        if ((right == "ask" || plan.Ask) && !Settings.Shared.AutoApprove
                            && !await Ask(new Approval(id, call, plan.Title ?? action, plan.Detail ?? "") { Warnings = plan.AfterAnswer ? [L("Dit gebeurt als het antwoord klaar is.")] : [] }, ct))
                            return ("The user did not approve this. Ask what they want instead.", true);
                    }
                    return (await control.Run(action, args), false);
                }
            case "create_skill":
                {
                    if (!Settings.Shared.Skills) return ("Skills are off in the user's settings (Systeembeheer). Tell the user they can turn them on.", true);
                    if (AppTools.Skill(call) is not { } skill) return ("Error: give a name (letters, digits, hyphens), a description and the instructions.", false);
                    var replaces = AppTools.Replaces(skill.Name);
                    var title = replaces == "own" ? L($"Eigen skill vervangen: {skill.Name}")
                        : replaces == "builtin" ? L($"Eigen versie van de ingebouwde skill: {skill.Name}") : L($"Skill maken: {skill.Name}");
                    if (!Settings.Shared.AutoApprove && !await Ask(new Approval(id, call, title, skill.Text), ct))
                        return ("The user did not approve this skill. Ask what they want changed.", true);
                    var file = AppTools.WriteSkill(skill.Name, skill.Text);
                    return ($"Skill {skill.Name} saved as {file}. It is in the skill list from the next message on and loads with use_skill. Tell the user where it is (Settings, Systeembeheer, Open map) and that they can change it there.", false);
                }
            default:
                return ($"Unknown tool {call.Name}", false);
        }
    }

    /// <summary>A connector's tool, with the connector's right: read-only tools run, others ask first unless the right says allow.</summary>
    private async Task<(string Text, bool Denied)> RunConnector(ToolCall call, McpRoute route, Guid id, ConversationStore store, CancellationToken ct)
    {
        var args = WorkspaceTools.Args(call);
        bool afterWeb = ReadWeb(id, store);
        var decision = McpManager.Decision(route.Server.RightValue, route.Tool, Settings.Shared.AutoApprove);
        // after reading the web a page could have chosen this call: then even "allow" asks
        if (decision == "allow" && afterWeb && route.Tool.ReadOnly != true && !Settings.Shared.AutoApprove) decision = "ask";
        switch (decision)
        {
            case "deny":
                return ($"The user's settings do not allow the connector {route.Server.Name}. Tell the user what you wanted to do.", true);
            case "ask":
                var warnings = new List<string>();
                var name = route.Server.Name;
                if (route.Tool.Destructive == true) warnings.Add(L($"Deze tool kan gegevens wijzigen of verwijderen bij {name}."));
                if (route.Server.IsRemote) warnings.Add(L($"Wat hieronder staat gaat naar {name} op internet."));
                if (afterWeb) warnings.Add(L("Dit volgt op het lezen van het web. Een webpagina kan het model instructies geven: controleer of dit is wat jij wilde."));
                var approval = new Approval(id, call, $"{name}: {route.Tool.Title ?? route.Tool.Name}", McpManager.Pretty(args)) { Warnings = warnings };
                if (!await Ask(approval, ct)) return ("The user did not allow this. Ask what they want instead, or continue without it.", true);
                break;
        }
        return (await Connectors!.CallAsync(route, args, ct), false);
    }

    /// <summary>Mail, the browser and controlling programs: each call says whether it runs, asks (a card, perhaps every time) or is
    /// refused.</summary>
    private async Task<(string Text, bool Denied)> RunAgent(IAgentToolSet agent, ToolCall call, Guid id, CancellationToken ct)
    {
        switch (await agent.Check(call))
        {
            case AgentStep.Refuse refuse:
                return (refuse.Reason, true);
            case AgentStep.Ask ask:
                var approval = new Approval(id, call, ask.Title, ask.Detail) { Warnings = ask.Warnings ?? [], EveryTime = ask.EveryTime };
                if (!await Ask(approval, ct)) return ("The user did not allow this. Ask what they want instead, or continue without it.", true);
                break;
        }
        return (await agent.Run(call, ct), false);
    }

    /// <summary>schedule_task: always with the card first; the task gets this conversation's folder.</summary>
    private async Task<(string Text, bool Denied)> ScheduleTask(ToolCall call, Guid id, CancellationToken ct)
    {
        if (Tasks is not { } store) return ("Tasks are not available here.", true);
        ScheduledTask task;
        try { task = TaskTool.Parse(WorkspaceTools.Args(call), DateTimeOffset.Now); }
        catch (TaskTool.Problem e) { return ($"Error: {e.Message}. Call schedule_task again with that fixed.", false); }
        if (Settings.Shared.WorkspaceTools && WorkspaceTools.Workspace is { } ws) task.Workspace = ws;
        var approval = new Approval(id, call, L($"Taak plannen: {task.Title}"), task.Schedule.Description + "\n\n" + task.Prompt);
        if (!await Ask(approval, ct)) return ("The user did not want this task. Ask what they want instead.", true);
        store.Upsert(task);
        var next = store.Task(task.Id)?.NextRun is { } n ? n.ToLocalTime().ToString("f", AppLanguages.Current.Culture()) : "-";
        return ($"Planned \"{task.Title}\" ({task.Schedule.Description}); the first run is {next}. The user finds it under Taken in the sidebar.", false);
    }

    /// <summary>The current task (since the user's last question) read the web: a page could have put the plan in the model's mind.</summary>
    private static bool ReadWeb(Guid id, ConversationStore store)
    {
        if (store.Find(id) is not { } c) return false;
        int start = c.Messages.FindLastIndex(m => m.Role == ChatRole.User && m.Automatic != true);
        return start >= 0 && c.Messages.Skip(start).Any(m => m.ToolCalls.Exists(t => WebTools.Names.Contains(t.Name)));
    }

    private static List<ToolActivity> Copy(List<ToolActivity> activities) => [.. activities.Select(a => a with { })];

    /// <summary>A tool call without a result breaks the next request, and its step would keep spinning: stopping answers it.</summary>
    private static void FillStopped(Guid id, ConversationStore store)
    {
        var c = store.Find(id);
        if (c == null) return;
        int at = c.Messages.FindLastIndex(m => m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0);
        var answered = at < 0 ? [] : c.Messages.Skip(at + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToHashSet();
        var open = at < 0 ? [] : c.Messages[at].ToolCalls.Where(call => !answered.Contains(call.Id)).ToList();
        bool spinning = c.Messages.Exists(m => m.Activities.Exists(a => !a.Done));
        if (open.Count == 0 && !spinning) return;
        store.Update(id, conversation =>
        {
            foreach (var m in conversation.Messages)
                foreach (var a in m.Activities)
                    if (!a.Done) { a.Done = true; a.Output ??= StoppedResult; }
            int place = at + 1;
            while (place < conversation.Messages.Count && conversation.Messages[place].Role == ChatRole.Tool) place++;
            foreach (var call in open)
                conversation.Messages.Insert(place++, new ChatMessage { Role = ChatRole.Tool, Text = StoppedResult, ToolCallId = call.Id });
        });
    }

    internal const string StoppedResult = "gestopt door de gebruiker";

    // MARK: - learning

    /// <summary>The app keeps WINAIMAGIC.md for this work folder: the setting is on, and the user allows writing there.</summary>
    private static bool KeepsNotes => WorkspaceActive && Settings.Shared.LearnFromChats && Settings.Shared.Right(WorkspaceRight.Write) != "deny";

    /// <summary>After a task of some steps the model says what it learned, and that goes in WINAIMAGIC.md. It answers after the
    /// conversation itself, so the engine reuses everything it already has in memory, whatever the model's speed.
    /// Not after a task that read the web: a page must not be able to put instructions in the notes.</summary>
    private async Task Learn(Guid id, ConversationStore store, Uri baseUrl, CancellationToken ct)
    {
        if (!KeepsNotes || ct.IsCancellationRequested || WorkspaceTools.Workspace is not { } ws) return;
        var c = store.Find(id);
        if (c == null || c.Messages.Count == 0 || c.Messages[^1].Error != null) return;
        int start = c.Messages.FindLastIndex(m => m.Role == ChatRole.User && m.Automatic != true);
        if (start < 0) return;
        var calls = c.Messages.Skip(start).SelectMany(m => m.ToolCalls).ToList();
        if (calls.Count < 3 || !calls.TrueForAll(x => WorkspaceTools.Names.Contains(x.Name))) return;

        Learning = true;
        StateChanged?.Invoke();
        try
        {
            var messages = RequestMessages(c.Unsummarized, c.Summary);
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = ChatTexts.LearnRequest });
            var body = new JsonObject
            {
                ["model"] = ModelName,
                ["messages"] = messages,
                ["stream"] = false,
                ["temperature"] = 0.2,
                ["max_tokens"] = 400,
                // the same template settings as the conversation's own requests: with thinking off DeepSeek's template drops
                // "Reasoning Effort" from the start of the system prompt and the old thoughts, so the engine read the whole
                // conversation again after every task (the Mac measured 52,232 tokens, 383 s). Thinking is stopped by the
                // budget instead, which does not change the prompt.
                ["chat_template_kwargs"] = TemplateKwargs(),
                ["thinking_budget_tokens"] = 0,
            };
            // the same tools as the conversation, so its start stays the same for the engine; it must only write here
            var tools = RunTools();
            if (tools.Count > 0) { body["tools"] = tools; body["tool_choice"] = "none"; }
            var answer = await CompleteAsync(body, baseUrl, 900, ct);
            if (answer == null || ct.IsCancellationRequested) return;
            var added = WorkspaceNotes.Add(WorkspaceNotes.Lessons(answer), ws);
            if (added.Count == 0) return;
            store.Update(id, conversation =>
            {
                if (conversation.Messages.Count == 0) return;
                conversation.Messages[^1].Activities.Add(new ToolActivity { Label = ChatTexts.NotesUpdated(added.Count), Done = true, Output = string.Join("\n", added) });
            });
        }
        finally
        {
            Learning = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Notes (and the skill list) as this conversation first saw them (Conversation.Notes). The model and the app write
    /// lessons into WINAIMAGIC.md while a conversation goes on; reading the file anew each time changed the start of the system
    /// prompt, and the engine read the whole conversation again (seen on the Mac with DeepSeek: 50,353 tokens, 357 s, after
    /// a restore). A change counts for new conversations, and for this one after its summary.</summary>
    private string? FrozenNotes(string key, Func<string?> read)
    {
        if (RunConversation is not { } id || runStore is not { } store || store.Find(id) is not { } c) return read();
        if (c.Notes is { } notes && notes.TryGetValue(key, out var kept)) return kept.Length == 0 ? null : kept;
        var now = read();
        store.Remember(id, x => { x.Notes ??= []; x.Notes[key] = now ?? ""; });
        return now;
    }

    // MARK: - stopping too early

    private static readonly string[] CallMarks = ["<tool_call>", "</tool_call>", "<function=", "</function>", "</parameter>"];
    private static readonly string[] EndWords = ["klaar", "done", "fertig", "terminé", "termine"];
    private static readonly string[] Invitations = ["let me know", "laat me weten", "laat het me weten", "lass es mich wissen", "lass mich wissen", "faites-moi savoir", "dites-moi"];
    private static readonly string[] Announcements =
    [
        " laat me ", " laten we ", " ik ga ", " ik maak nu", " ik zal ", " nu ga ik", " nu maak ik", " ga ik nu", " ik probeer ",
        " let me ", " let's ", " i will ", " i'll ", " i am going to", " i'm going to", " now i ", " next, i ",
        " lass mich ", " lassen sie mich ", " ich werde ", " jetzt werde ", " ich versuche ",
        " je vais ", " laisse-moi ", " laissez-moi ", " je vais maintenant",
    ];
    private static readonly Regex SentenceEnd = new(@"[.!?]\s+", RegexOptions.Compiled);

    /// <summary>A local model often ends its turn while the work is not done: it says what it will do and stops, thinks without
    /// answering, or its tool call stays behind in the thinking. Then the turn has no tool calls, and without this the
    /// conversation would just stop. working: tools are in use, so an announcement is meant to be followed by a step.
    /// tookSteps: this task used tools, and then only the model's own word ends it (KLAAR, or a question to the user):
    /// a remark like "dit werkt niet lekker" is no ending.</summary>
    public static bool LooksUnfinished(string text, string reasoning, bool cutOff, bool working, bool tookSteps = false)
    {
        if (cutOff) return true;
        var answer = text.Trim();
        if (answer.Length == 0) return true;
        if (!working) return false;
        var thought = TextLimit.Suffix(reasoning, 300);
        if (CallMarks.Any(m => answer.Contains(m, StringComparison.Ordinal) || thought.Contains(m, StringComparison.Ordinal))) return true;
        if (answer.EndsWith('?')) return false;
        if (answer.EndsWith(':')) return true;
        // the last sentence
        var parts = SentenceEnd.Replace(answer, "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length == 0 ? "" : " " + parts[^1].ToLowerInvariant();
        // the model waits for the user ("laat me weten zodra …"): that is no stop halfway, also after tool use
        if (Invitations.Any(i => last.Contains(i, StringComparison.Ordinal))) return false;
        if (tookSteps)
        {
            int first = 0;
            while (first < answer.Length && !char.IsLetter(answer[first])) first++;
            int end = first;
            while (end < answer.Length && char.IsLetter(answer[end])) end++;
            return !EndWords.Contains(answer[first..end].ToLowerInvariant());
        }
        // does the last sentence announce a next step?
        return Announcements.Any(a => last.Contains(a, StringComparison.Ordinal));
    }

    private static readonly string[] DoneWords = ["KLAAR", "DONE", "FERTIG", "TERMINÉ", "TERMINE"];
    private static readonly Regex DoneAtEnd = new(@"[\s*]*\b(KLAAR|DONE|FERTIG|TERMINÉ|TERMINE)[\s*.!]*$", RegexOptions.Compiled);

    /// <summary>The answer without the done-word the model was asked to start with: "KLAAR — morgen staat er niets" becomes
    /// "Morgen staat er niets". Only the word in capitals counts: "Klaar! Het bestand is er" is ordinary language.</summary>
    public static string WithoutDoneWord(string text)
    {
        // also at the very end: "… verder zoek. KLAAR" (in capitals only: "Ik ben klaar" stays)
        if (DoneAtEnd.Match(text) is { Success: true, Index: > 0 } tail) text = text[..tail.Index];
        int i = 0;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '*')) i++;
        int end = i;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        if (!DoneWords.Contains(text[i..end])) return text;
        while (end < text.Length && (char.IsWhiteSpace(text[end]) || "*.:!—–-,".Contains(text[end]))) end++;
        if (end >= text.Length) return text;
        return char.ToUpper(text[end], AppLanguages.Current.Culture()) + text[(end + 1)..];
    }

    // MARK: - compacting

    /// <summary>Summarize the start of a conversation, so it fits in the context again: everything before the last two questions
    /// becomes a summary that goes with the system prompt; the messages stay visible in the chat.</summary>
    /// <summary>The transcript's own labels at the start of a line: no summary but more of the conversation.</summary>
    internal static bool LooksLikeTranscript(string text)
    {
        string result = L("Resultaat:"), user = L("Gebruiker:");
        return text.Split('\n').Any(l => l.StartsWith(result, StringComparison.Ordinal) || l.StartsWith(user, StringComparison.Ordinal));
    }

    public async Task CompactAsync(Guid id, ConversationStore store, Uri baseUrl, Guid? excluding = null, CancellationToken ct = default)
    {
        var c = store.Find(id);
        if (c == null) return;
        var open = c.Unsummarized.Where(m => m.Id != excluding).ToList();
        var users = Enumerable.Range(0, open.Count).Where(i => open[i].Role == ChatRole.User && open[i].Automatic != true).ToList();
        int cut;
        if (users.Count > 2)
        {
            cut = users[^2];   // keep the last two questions and what came after them
        }
        else
        {
            // one long task: keep its latest steps, and the summary carries the question (a tool result stays with its call)
            if (open.Count <= 8) return;
            cut = open.Count - 6;
            while (cut < open.Count && open[cut].Role == ChatRole.Tool) cut++;
            if (cut >= open.Count) return;
        }
        var old = open.GetRange(0, cut);
        if (old.Count == 0) return;
        var last = old[^1];

        Compacting = true;
        StateChanged?.Invoke();
        try
        {
            // a transcript without images and with long tool results shortened, so the request itself fits
            int budget = (int)(ContextLimit * 2.5);   // characters, about 0.7 of the context in tokens
            var lines = new List<string>();
            if (c.Summary is { } previous) lines.Add(L("Eerdere samenvatting:") + "\n" + previous);
            foreach (var m in old)
            {
                switch (m.Role)
                {
                    case ChatRole.User:
                        if (m.Automatic == true) continue;
                        lines.Add(L("Gebruiker:") + " " + m.Text + (m.Attachments.Count == 0 ? "" : " [" + L("afbeelding") + "]"));
                        break;
                    case ChatRole.Assistant:
                        if (m.Text.Length > 0) lines.Add(L("Assistent:") + " " + (m.CutOff == true ? Shortened(m.Text) : m.Text));
                        foreach (var call in m.ToolCalls) lines.Add(L($"Assistent gebruikt {call.Name}:") + " " + TextLimit.Prefix(call.Arguments, 600));
                        break;
                    case ChatRole.Tool:
                        lines.Add(L("Resultaat:") + " " + TextLimit.Prefix(m.Text, 1500));
                        break;
                }
            }
            var transcript = string.Join("\n\n", lines);
            if (transcript.Length > budget) transcript = "[" + L("begin weggelaten") + "]\n" + TextLimit.Suffix(transcript, budget);

            var body = new JsonObject
            {
                ["model"] = ModelName,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = L("Je vat een gesprek samen zodat het verder kan zonder de oorspronkelijke berichten. Houd vast: het doel van de gebruiker, beslissingen, namen en paden van bestanden, commando's die werkten of faalden, de stand van het werk en open vragen of taken. Schrijf beknopt, in het Nederlands, als lijst met kopjes.") },
                    new JsonObject { ["role"] = "user", ["content"] = L("Vat dit gesprek samen:") + "\n\n" + transcript + "\n\n" + L("Dit was het gesprek. Ga het niet voortzetten: schrijf nu de samenvatting.") }),
                ["stream"] = false,
                ["temperature"] = 0.3,
                ["max_tokens"] = 2000,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
                ["thinking_budget_tokens"] = 0,    // GLM's template ignores enable_thinking and always opens with <think>
            };
            var summary = await CompleteAsync(body, baseUrl, 1800, ct);
            // on a long transcript a model can carry on with the conversation instead (seen on GLM in a 64K session): once more
            if (summary != null && LooksLikeTranscript(summary)) summary = await CompleteAsync(body, baseUrl, 1800, ct);
            if (string.IsNullOrEmpty(summary)) return;
            store.Update(id, conversation =>
            {
                conversation.Summary = summary;
                // the whole conversation is read anew after a summary anyway: the notes may be brought up to date
                conversation.Notes = null;
                conversation.SummaryUpTo = last.Id;
                conversation.ContextTokens = null;
            });
            store.Save();
        }
        finally
        {
            Compacting = false;
            StateChanged?.Invoke();
        }
    }

    // MARK: - request

    /// <summary>The tools for the next request, from the settings and the folder alone.</summary>
    public static JsonArray CurrentTools()
    {
        var s = Settings.Shared;
        var tools = new JsonArray();
        if (s.WebSearch) foreach (var t in WebTools.Definitions) tools.Add(t!.DeepClone());
        if (s.WorkspaceTools && WorkspaceTools.Workspace != null)
        {
            foreach (var t in WorkspaceTools.Definitions) tools.Add(t!.DeepClone());
            if (SeesImages()) tools.Add(WorkspaceTools.ImageDefinition);
        }
        // the app's own tools, skills and system management come last: with them off the request starts the same as before
        if (s.AppTools) foreach (var t in AppTools.Definitions(s.Skills)) tools.Add(t!.DeepClone());
        if (s.Skills && Skills.All.Count > 0) tools.Add(Skills.Definition);
        if (s.SystemManagement) foreach (var t in SystemTools.Definitions) tools.Add(t!.DeepClone());
        foreach (var set in AgentTools.All.Where(x => x.IsOn))
            foreach (var t in set.Definitions) tools.Add(t!.DeepClone());
        if (OffersSteps) tools.Add(WorkSteps.Definition);
        return tools;
    }

    /// <summary>The step list is there when the model can do work of several steps: in a folder, on the system, with connectors.</summary>
    private static bool OffersSteps =>
        WorkspaceActive || Settings.Shared.SystemManagement || Connectors?.Enabled.Count > 0 || AgentOn;

    /// <summary>This run's tools: the settings' ones, planning a task, and the connectors this conversation loaded.</summary>
    internal JsonArray RunTools()
    {
        var tools = CurrentTools();
        if (Tasks != null) tools.Add(TaskTool.Definition);
        // connectors: only when one is switched on, and then the tools of those this conversation loaded
        if (Connectors is { } mcp && mcp.Enabled.Count > 0)
        {
            tools.Add(McpManager.LoadDefinition);
            foreach (var t in mcp.Definitions(LoadedConnectors)) tools.Add(t);
        }
        return tools;
    }

    internal JsonArray RequestMessages(IEnumerable<ChatMessage> history, string? summary = null)
    {
        var s = Settings.Shared;
        var date = DateTime.Now.ToString("D", AppLanguages.Current.Culture());
        var system = ChatTexts.Identity(ModelName, date);
        if (s.WebSearch)
            system += " " + L("Je kunt het web doorzoeken met web_search en pagina's lezen met fetch_url. Doe dat voor actuele informatie en noem je bronnen met hun URL.");
        // the user's general notes, for every conversation (the folder's notes come after them)
        if (FrozenNotes("general", () => AtomicFile.ReadUtf8(Paths.GeneralNotes) is { Length: > 0 } g ? g : null) is { } general)
            system += "\n\n" + ChatTexts.GeneralNotes + "\n" + TextLimit.Prefix(general, 8000);
        if (s.WorkspaceTools && WorkspaceTools.Workspace is { } ws)
        {
            system += "\n\n" + ChatTexts.Workspace(ws);
            system += " " + ChatTexts.Outside;
            // the user's own notes for this folder and what was learned here, like CLAUDE.md for Claude Code
            if (FrozenNotes("folder:" + ws, () => WorkspaceNotes.ForPrompt(ws)) is { } text) system += "\n\n" + ChatTexts.FolderNotes + "\n" + text;
            var noTools = DeveloperTools.HasDeveloperTools ? "" : " " + ChatTexts.NoDeveloperTools(DeveloperTools.Missing);
            var look = SeesImages() ? " " + L("Wat een programma met een venster laat zien (een spel, een emulator), controleer je zelf: laat het een screenshot als bestand opslaan en bekijk dat met view_image. Zeg pas dat het werkt als je het resultaat hebt gezien.") : "";
            system += "\n\n" + ChatTexts.KeepGoing + look + noTools;
        }
        // Instellingen → Systeembeheer: nothing is added while they are off
        if (s.AppTools) system += "\n\n" + AppTools.PromptSection(s.Skills);
        if (s.SystemManagement) system += "\n\n" + SystemTools.PromptSection();
        // also as this conversation first saw it: a skill the model rewrites must not change the start of the prompt
        if (s.Skills && FrozenNotes("skills", () => Skills.PromptSection(Skills.All) is { Length: > 0 } t ? t : null) is { } section) system += "\n\n" + section;
        if (OffersSteps)
            system += "\n\n" + L("Werk je aan een taak van drie stappen of meer, zet die dan eerst op met task_list en werk de lijst bij als een stap begint of klaar is. De gebruiker ziet de lijst boven het invoerveld.");
        foreach (var set in AgentTools.All.Where(x => x.IsOn)) system += "\n\n" + set.PromptSection;
        if (Connectors?.PromptSection() is { Length: > 0 } connectors) system += "\n\n" + connectors;
        if (s.SystemPrompt.Length > 0) system += "\n\n" + s.SystemPrompt;
        if (summary != null)
            system += "\n\n" + L("Samenvatting van het begin van dit gesprek (die berichten zijn weggelaten om ruimte te maken in je context):") + "\n" + summary;

        var o = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        // older pictures the model looked at itself go as a line of text; the user's own pictures always go
        var list = history as IList<ChatMessage> ?? history.ToList();
        var keptLooks = list.Where(m => m.Automatic == true && m.Attachments.Count > 0).TakeLast(ImagesKept).Select(m => m.Id).ToHashSet();
        bool sees = SeesImages();
        foreach (var m in list)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    if (m.Attachments.Count > 0 && !sees)
                    {
                        // the engine runs without vision: it refuses every request with a picture, and the picture stays in the
                        // conversation, so each later question failed too. It goes as a line of text instead.
                        var names = string.Join(", ", m.Attachments.Select(a => a.Filename));
                        var note = L($"(Hier stond een afbeelding: {names}. Het model draait nu zonder afbeeldingen (vision); zeg dat als het ertoe doet.)");
                        o.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text.Length == 0 ? note : note + "\n\n" + m.Text });
                    }
                    else if (m.Automatic == true && m.Attachments.Count > 0 && !keptLooks.Contains(m.Id))
                    {
                        var seen = string.Join(", ", m.Attachments.Select(a => a.Filename));
                        o.Add(new JsonObject { ["role"] = "user", ["content"] = L($"(Hier stond een afbeelding die je eerder bekeek: {seen}.)") });
                    }
                    else if (m.Attachments.Count == 0)
                    {
                        o.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    }
                    else
                    {
                        var parts = new JsonArray();
                        foreach (var a in m.Attachments)
                            parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(a.Jpeg) } });
                        if (m.Text.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                        o.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    }
                    break;
                case ChatRole.Assistant:
                    if (m.Text.Length == 0 && m.ToolCalls.Count == 0) continue;
                    // the thinking stays here: reasoning never goes back to the model
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.CutOff == true ? Shortened(m.Text) : m.Text };
                    if (m.ToolCalls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var call in m.ToolCalls)
                            calls.Add(new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
                            });
                        msg["tool_calls"] = calls;
                    }
                    o.Add(msg);
                    break;
                case ChatRole.Tool:
                    o.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId ?? "", ["content"] = m.Text });
                    break;
            }
        }
        // after summarizing one long task the rest starts with a step of the model; every template wants a question first
        if (o.Count > 1 && o[1]?["role"]?.GetValue<string>() != "user")
            o.Insert(1, new JsonObject { ["role"] = "user", ["content"] = L("Ga verder met de taak uit de samenvatting.") });
        return o;
    }

    /// <summary>A cut-off answer as the model gets it back: its start and its end, so it knows where it was without the monologue in
    /// between filling the context.</summary>
    public static string Shortened(string text, int keep = 3000)
    {
        if (text.Length <= keep * 2 + 200) return text;
        var left = (text.Length - keep * 2).ToString("N0", AppLanguages.Current.Culture());
        return TextLimit.Prefix(text, keep) + "\n\n[" + L($"… {left} tekens weggelaten …") + "]\n\n" + TextLimit.Suffix(text, keep);
    }

    /// <summary>Thinking in the later rounds of one answer. The first round plans the work; after that a round only takes the next
    /// step, and on the Mac DeepSeek-V4.1-Flash used the full 16,000 tokens in nearly every round (14 minutes a step). So later rounds
    /// get at most this much, also when the setting has no limit.</summary>
    public const int FollowUpThinking = 4_000;

    /// <summary>The user only says to go on ("ga door", "verder", "continue") with work the model was doing: its last answer took
    /// steps. Such a round needs no new plan; on the Mac its first round thought 16,328 tokens, 14 minutes (DeepSeek-V4.1-Flash).</summary>
    public static bool IsGoOn(Conversation c)
    {
        int i = c.Messages.FindLastIndex(m => m.Role == ChatRole.User && m.Automatic != true);
        if (i < 0 || !IsGoOnText(c.Messages[i].Text)) return false;
        for (int j = i - 1; j >= 0; j--)
        {
            var m = c.Messages[j];
            if (m.Role == ChatRole.User && m.Automatic != true) break;
            if (m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0) return true;
        }
        return false;
    }

    private static readonly string[] GoOnWords = ["ga door", "ga verder", "ga maar door", "doorgaan", "verder", "door", "ga door aub", "ga door graag",
        "go on", "continue", "keep going", "carry on", "weiter", "mach weiter", "continue s'il te plaît", "continuez", "vas-y"];

    public static bool IsGoOnText(string text)
    {
        var t = text.ToLowerInvariant().Trim().Trim(GoOnTrim);
        if (t.Length > 40) return false;
        // "ga door graag" yes, "door welke deur?" no: only a phrase of two words or more may have a few after it
        return GoOnWords.Contains(t) || GoOnWords.Any(w => w.Contains(' ') && t.StartsWith(w + " ", StringComparison.Ordinal) && t.Length <= w.Length + 12);
    }

    private static readonly char[] GoOnTrim = [.. " \t\r\n.,!?;:'\"()-…".ToCharArray()];

    /// <summary>The thinking budget of round <paramref name="turn"/> (0 is the first) of an answer; 0 means no limit.</summary>
    public static int ThinkingBudget(int setting, int turn) =>
        turn == 0 ? setting : setting == 0 ? FollowUpThinking : Math.Min(setting, FollowUpThinking);

    /// <summary>What the chat template is rendered with, the same for every request about a conversation (its answers and
    /// the lessons after them): these change the start of the prompt (DeepSeek writes "Reasoning Effort" into the system
    /// part and drops old thoughts without thinking), and then the engine reads the whole conversation again.</summary>
    public static JsonObject TemplateKwargs() =>
        new() { ["enable_thinking"] = Settings.Shared.Thinking, ["reasoning_effort"] = Settings.Shared.ReasoningEffort };

    internal JsonObject StreamBody(IEnumerable<ChatMessage> history, string? summary, int? thinkingBudget = null)
    {
        var s = Settings.Shared;
        int budget = thinkingBudget ?? s.ThinkingBudgetNow;
        var body = new JsonObject
        {
            ["model"] = ModelName,
            ["messages"] = RequestMessages(history, summary),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = s.Temperature,
            ["top_p"] = 1.0,
            ["max_tokens"] = MaxAnswerTokens,
            // enable_thinking is understood by both templates (DeepSeek maps it to thinking_mode)
            ["chat_template_kwargs"] = TemplateKwargs(),
        };
        // without a limit a model can keep doubting on a hard question and never get to its answer
        // off means off: GLM's template ignores enable_thinking and always opens with <think>
        if (!s.Thinking) body["thinking_budget_tokens"] = 0;
        else if (budget > 0)
        {
            body["thinking_budget_tokens"] = budget;
            body["reasoning_budget_message"] = "\n\n" + L("Genoeg nagedacht; nu schrijf ik het antwoord.") + "\n";
        }
        // no limit asked: said out loud, because the tiered server otherwise applies its own default
        else body["thinking_budget_tokens"] = -1;
        var tools = RunTools();
        if (tools.Count > 0) body["tools"] = tools;
        return body;
    }

    private static HttpRequestMessage Post(JsonObject body, Uri baseUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.ToString().TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(ChatJson.Options), Encoding.UTF8, "application/json"),
        };
        var key = Settings.Shared.ApiKey;
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        return req;
    }

    /// <summary>The model is this run's now: the conversation that had the engine goes to the SSD, and this conversation's state
    /// comes back from it (SlotKeeper). A run without a conversation (a task step, the heartbeat) only saves the other one.</summary>
    private static Task EnterEngine(Uri baseUrl) =>
        SlotKeeper.At(baseUrl) is not { } keeper ? Task.CompletedTask
        : RunConversation is { } id ? keeper.EnterAsync(id) : keeper.YieldAsync();

    /// <summary>A request without streaming; the answer's text, or null when anything went wrong: the callers go on without it.</summary>
    private async Task<string?> CompleteAsync(JsonObject body, Uri baseUrl, int timeoutSeconds, CancellationToken ct)
    {
        var gate = Gate(baseUrl);
        if (!await gate.AcquireAsync(RunConversation ?? Guid.NewGuid(), ct).ConfigureAwait(false)) return null;
        await EnterEngine(baseUrl).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var req = Post(body, baseUrl);
            using var resp = await http.SendAsync(req, limit.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200) return null;
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            return json?["choices"]?[0]?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException) { return null; }
        finally { gate.Release(); }
    }

    internal sealed class StreamResult
    {
        public List<ToolCall> ToolCalls = [];
        /// <summary>"length": the context was full, the answer is cut off.</summary>
        public string? FinishReason;
        public double? TokensPerSecond, PromptTokensPerSecond;
        /// <summary>Prompt and answer together: what the conversation takes in the context now.</summary>
        public int? ContextTokens;
    }

    /// <summary>The JSON of an event-stream line; null for what is not data: comments and keep-alives during a long prompt,
    /// blank lines, event names.</summary>
    internal static string? DataOf(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line[5..].TrimStart(' ');
        return payload.Length == 0 ? null : payload;
    }

    private async Task<StreamResult> StreamAsync(List<ChatMessage> history, string? summary, Uri baseUrl, StreamThrottle throttle, int thinkingBudget, CancellationToken ct)
    {
        // the model writes for one conversation at a time; the others wait here (and a stop while waiting is a stop)
        var body = StreamBody(history, summary, thinkingBudget);
        var gate = Gate(baseUrl);
        if (!await gate.AcquireAsync(RunConversation ?? Guid.NewGuid(), ct).ConfigureAwait(false)) throw new OperationCanceledException(ct);
        await EnterEngine(baseUrl).ConfigureAwait(false);
        StateChanged?.Invoke();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(RequestTimeout);
        try
        {
            using var req = Post(body, baseUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200)
            {
                var msg = (await resp.Content.ReadAsStringAsync(idle.Token).ConfigureAwait(false)).Replace("\r", "").Replace("\n", "");
                throw new ChatEngineException((int)resp.StatusCode, $"Engine gaf {(int)resp.StatusCode}: {TextLimit.Prefix(msg, 400)}");
            }

            var result = new StreamResult();
            var calls = new SortedDictionary<int, (string Id, StringBuilder Name, StringBuilder Arguments)>();
            int callChunks = 0;
            await using var stream = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, AtomicFile.Utf8);
            while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
            {
                idle.CancelAfter(RequestTimeout);
                ct.ThrowIfCancellationRequested();
                if (DataOf(line) is not { } payload) continue;
                if (payload == "[DONE]") break;
                JsonDocument json;
                try { json = JsonDocument.Parse(payload); }
                catch (JsonException) { continue; }
                using (json)
                {
                    if (json.RootElement.ValueKind == JsonValueKind.Object) ReadChunk(json.RootElement, result, calls, throttle, ref callChunks);
                }
            }
            result.ToolCalls = [.. calls.Select(kv =>
            {
                var id = kv.Value.Id.Length > 0 ? kv.Value.Id : $"call_{kv.Key}_{Guid.NewGuid().ToString("N")[..8]}";
                return new ToolCall(id, kv.Value.Name.ToString(), kv.Value.Arguments.ToString());
            })];
            // a tool call cut off halfway has broken arguments: the model writes it again after the context has room
            if (result.FinishReason == "length") result.ToolCalls = [];
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The engine did not answer for {RequestTimeout.TotalSeconds:0} s.");
        }
        finally { gate.Release(); }
    }

    /// <summary>What a tool call that is still being written will do, from its name and the arguments so far (no whole JSON yet):
    /// "Schrijft bestand demo.bas · 1.240 tekens".</summary>
    public static string PreparingText(string name, string arguments)
    {
        var m = Regex.Match(arguments, "\"(path|file|name|url|query|command|app)\"\\s*:\\s*\"([^\"]{1,80})");
        string what = name switch
        {
            "write_file" => L("Schrijft bestand"),
            "edit_file" => L("Past bestand aan"),
            "run_command" => L("Bereidt een commando voor"),
            "system_change" => L("Maakt een plan"),
            "create_skill" => L("Schrijft een skill"),
            "" => L("Bereidt een stap voor"),
            _ => L($"Bereidt voor: {name}"),
        };
        var size = arguments.Length > 200 ? " · " + L($"{arguments.Length.ToString("N0", AppLanguages.Current.Culture())} tekens") : "";
        return what + (m.Success ? " " + m.Groups[2].Value : "") + size;
    }

    private void ReadChunk(JsonElement chunk, StreamResult result, SortedDictionary<int, (string Id, StringBuilder Name, StringBuilder Arguments)> calls, StreamThrottle throttle, ref int callChunks)
    {
        if (chunk.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var count) && count > 0)
            result.ContextTokens = count;
        if (chunk.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            result.TokensPerSecond = Number(t, "predicted_per_second");
            result.PromptTokensPerSecond = Number(t, "prompt_per_second");
        }
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object) return;
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) result.FinishReason = reason.GetString();
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return;
        var content = Text(delta, "content");
        var reasoning = Text(delta, "reasoning_content");
        if (content.Length > 0 || reasoning.Length > 0)
        {
            throttle.Add(content, reasoning);
            TokensStreamed?.Invoke(1);   // the server streams one token per chunk
        }
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array) return;
        foreach (var tc in toolCalls.EnumerateArray())
        {
            int index = tc.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
            if (!calls.TryGetValue(index, out var call)) call = ("", new StringBuilder(), new StringBuilder());
            if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } newId) call.Id = newId;
            if (tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                call.Name.Append(Text(f, "name"));
                call.Arguments.Append(Text(f, "arguments"));
            }
            calls[index] = call;
            // the arguments of a tool call stream too (a whole program for write_file): say what is coming, now and then,
            // so the chat does not look idle while tokens come in; they count in the speed too
            callChunks++;
            if (callChunks == 1 || callChunks % 25 == 0) throttle.Prepare(PreparingText(call.Name.ToString(), call.Arguments.ToString()));
            TokensStreamed?.Invoke(1);
        }
    }

    private static string Text(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Number(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
