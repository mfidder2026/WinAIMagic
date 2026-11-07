using WinAIMagic.Chat;
using WinAIMagic.Chat.Telegram;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>Telegram as a way into the chat (Instellingen → Telegram): the service polls the bot, the bridge turns messages into
/// questions in the account's conversation, and approvals, task results and the heartbeat go there too.</summary>
public sealed partial class AppState
{
    public TelegramService Telegram { get; } = TelegramService.Shared ??= new TelegramService();
    private TelegramBridge? telegramBridge;
    private BrowserWindow? browser;

    public TelegramBridge TelegramBridge => telegramBridge ??= new TelegramBridge(Telegram, Chat, Store,
        () => BaseUrl, () => EngineState.IsRunning, EnsureModelForChat, ShortStatus)
    {
        AdminNotice = (title, body, conversation) => Notifier.Post(title, body, conversation),
    };

    /// <summary>A question (typed, spoken or from Telegram) loads the model: it counts as the user's, a task does not stop it.</summary>
    public async Task<bool> EnsureModelForChat()
    {
        bool ready = await EnsureModel();
        lock (TaskRuns) if (TaskRuns.Count == 0) modelStartedByTasks = false;
        if (ready && ChatModel is { } m) Chat.ModelName = m.Name;
        return ready;
    }

    /// <summary>Started with the tasks, once the window is there: messages are handled on the UI thread.</summary>
    private void StartTelegram()
    {
        // the browser agent's window lives on the UI thread too
        WinAIMagic.Chat.Agent.BrowserTools.Shared.Host ??= browser = new BrowserWindow(Ui);
        Telegram.Owner = SynchronizationContext.Current;
        _ = TelegramBridge;
        Telegram.Changed += () => Ui.TryEnqueue(Notify);
        Telegram.Start();
    }

    /// <summary>On quitting: the model's browser window goes too.</summary>
    private void StopAgent() => browser?.Close();

    /// <summary>For /status in Telegram: the model, what runs, the next task, the free disk space.</summary>
    public string ShortStatus()
    {
        var o = new List<string>();
        if (Host.Engine?.Model is { } m && EngineState.IsRunning) o.Add(L($"Model: {m.Name}, geladen."));
        else o.Add(L("Model: niet geladen (laadt bij je volgende vraag)."));
        if (Chat.Busy) o.Add(L($"Bezig in {Chat.Running.Count} gesprek(ken)."));
        var next = Tasks.Tasks.Where(t => t.Enabled && t.NextRun != null).OrderBy(t => t.NextRun).FirstOrDefault();
        if (next?.NextRun is { } at) o.Add(L($"Volgende taak: {next.Title}, {at.ToLocalTime().ToString("g", AppLanguages.Current.Culture())}."));
        o.Add(L($"Vrij op schijf: {Host.System.FreeDisk / 1_000_000_000} GB."));
        return string.Join("\n", o);
    }
}
