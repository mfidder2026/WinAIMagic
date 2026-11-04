using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using WinAIMagic.Studio;

namespace WinAIMagic.App;

public enum PaneKind { Live, Models, Documents, Compare, Cluster, System, Chat }

public readonly record struct Pane(PaneKind Kind, Guid? Conversation = null);

/// <summary>Ties the parts together, like AppState.swift; the engine half lives in EngineHost. Everything that reaches the screens
/// comes through here on the UI thread, at most every 0.1 s: the parts' events fire on background threads.</summary>
public sealed class AppState : INotifyPropertyChanged, IDisposable
{
    public EngineHost Host { get; }
    public ConversationStore Store { get; } = new();
    public ChatClient Chat { get; } = new();
    public Settings Settings => Settings.Shared;
    public DispatcherQueue Ui { get; } = DispatcherQueue.GetForCurrentThread();
    /// <summary>The window, for pickers and dialogs.</summary>
    public Window Window { get; set; } = null!;
    public XamlRoot XamlRoot => Window.Content.XamlRoot;

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Something changed in the models, the engine, the conversations or the chat.</summary>
    public event Action? Changed;
    /// <summary>A new metrics sample (0.5 s with an engine, 2 s without).</summary>
    public event Action? Sampled;
    /// <summary>The app closes: save what is open.</summary>
    public event Action? Closing;
    /// <summary>A new language: the shell builds every screen again.</summary>
    public event Action? LanguageChanged;

    private readonly DispatcherQueueTimer throttle;
    private int pending, sampling;
    private Pane pane = new(PaneKind.Models);
    private DocumentStudio? studio;
    private DocumentEngines? documentEngines;
    private DocumentEngineHost? documentHost;

    public AppState()
    {
        Host = new EngineHost();
        Chat.TokensStreamed += Host.Metrics.AddTokens;
        LocalModel.TokensStreamed += Host.Metrics.AddTokens;
        throttle = Ui.CreateTimer();
        throttle.IsRepeating = false;
        throttle.Interval = TimeSpan.FromSeconds(0.1);
        throttle.Tick += (_, _) =>
        {
            Interlocked.Exchange(ref pending, 0);
            Guarded(() =>
            {
                Changed?.Invoke();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
            });
        };
        Host.Changed += Notify;
        Host.Check.Changed += Notify;
        Host.Bench.Changed += Notify;
        Store.Changed += _ => Notify();
        Chat.StateChanged += Notify;
        Host.Metrics.Sampled += () =>
        {
            if (Interlocked.Exchange(ref sampling, 1) == 0)
                Ui.TryEnqueue(() => { Interlocked.Exchange(ref sampling, 0); Guarded(() => Sampled?.Invoke()); });
        };
        Settings.PropertyChanged += OnSetting;
    }

    /// <summary>A screen that fails on one update must not take the downloads and the engine down with it: logged, and on with the rest.</summary>
    private static void Guarded(Action a)
    {
        try { a(); }
        catch (Exception e) { App.Log(e); }
    }

    public void Notify()
    {
        if (Interlocked.Exchange(ref pending, 1) == 0) Ui.TryEnqueue(() => throttle.Start());   // not the method group: CsWinRT tries to unwrap a delegate on a projected object
    }

    private void OnSetting(object? _, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.Language): Ui.TryEnqueue(() => LanguageChanged?.Invoke()); break;
            case nameof(Settings.VramReserveMib): Task.Run(Host.RefreshSystem); break;
            case nameof(Settings.ModelFolder): Task.Run(() => { Host.LoadIndex(); Host.RefreshSystem(); }); break;
            default: Notify(); break;
        }
    }

    // MARK: - the screen

    public Pane Pane
    {
        get => pane;
        set
        {
            if (pane == value) return;
            pane = value;
            if (value.Conversation is { } id) Store.Selection = id;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pane)));
        }
    }

    public void NewConversation()
    {
        Store.NewConversation();
        if (Store.Selection is { } id) Pane = new Pane(PaneKind.Chat, id);
    }

    // MARK: - the engine

    /// <summary>Hidden flags: the state and address of a stand-in for the engine.</summary>
    internal EngineState? TestState { get; set; }
    internal Uri? TestUrl { get; set; }

    public EngineState EngineState => TestState ?? Host.Engine?.State ?? EngineState.Stopped;
    public double TokensPerSec => Host.Metrics.Current?.TokensPerSec ?? 0;
    public Uri BaseUrl => TestUrl ?? Host.Engine?.BaseUrl ?? new Uri($"http://{(Settings.Host == "0.0.0.0" ? "127.0.0.1" : Settings.Host)}:{Settings.Port}");
    /// <summary>The model the chat talks to.</summary>
    public ModelInfo? ChatModel => Host.Engine?.Model ?? Host.SelectedModel;

    /// <summary>Sends a question in a conversation; on the UI thread, where the chat's loop then stays.</summary>
    public void Send(string text, IReadOnlyList<Attachment> attachments, Guid conversation)
    {
        if (ChatModel is { } m) Chat.ModelName = m.Name;
        Chat.Send(text, attachments, conversation, Store, BaseUrl);
    }

    public void StartEngine() => _ = StartEngineTask();

    public Task StartEngineTask()
    {
        if (Host.Plan is { } plan) Chat.ContextLimit = plan.ContextSize;
        if (Host.SelectedModel is { } m) Chat.ModelName = m.Name;
        return Task.Run(Host.StartEngine);
    }

    public void StopEngine() => Task.Run(Host.StopEngine);

    public void Select(string id) => Task.Run(() => Host.Select(id));

    /// <summary>A button of the system check. The page file waits for Windows' UAC prompt, so that goes off the UI thread.</summary>
    public void Perform(SystemCheck.CheckAction action)
    {
        void Open(SystemCheck.CheckAction a) => Ui.TryEnqueue(() => Pane = new Pane(a == SystemCheck.CheckAction.OpenCluster ? PaneKind.Cluster : PaneKind.Models));
        if (action == SystemCheck.CheckAction.SetPageFile) Task.Run(() => Host.Check.Perform(action, Host, Open));
        else Host.Check.Perform(action, Host, Open);
    }

    public Task RunCheck() => Task.Run(() => Host.Check.Run(Host));

    // MARK: - the documents module

    /// <summary>Made when the module is first used: it makes its folder under Documents. A test copy of the app stays inside its data folder.</summary>
    public DocumentStudio Studio => studio ??= new DocumentStudio(root: Program.TestData ? Path.Combine(Paths.AppData, "Documenten") : null)
    {
        Endpoint = (role, ct) => DocumentEngines.EndpointAsync(role, ct),
        ContextLimit = () => Host.Plan?.ContextSize ?? 32_768,
    };

    public DocumentEngines DocumentEngines => documentEngines ??= new DocumentEngines(documentHost = new DocumentEngineHost(this));

    // MARK: - screens subscribe while they are on screen

    /// <summary>`refresh` at once when `owner` appears and at every change for as long as it stays.</summary>
    public void Watch(FrameworkElement owner, Action refresh) => While(owner, refresh, r => Changed += r, r => Changed -= r);

    /// <summary>`refresh` at once when `owner` appears and at every metrics sample for as long as it stays.</summary>
    public void WatchSamples(FrameworkElement owner, Action refresh) => While(owner, refresh, r => Sampled += r, r => Sampled -= r);

    private static void While(FrameworkElement owner, Action refresh, Action<Action> on, Action<Action> off)
    {
        bool active = false;
        owner.Loaded += (_, _) => { if (!active) { active = true; on(refresh); } refresh(); };
        owner.Unloaded += (_, _) => { if (active) { active = false; off(refresh); } };
    }

    public void Dispose()
    {
        Closing?.Invoke();
        Chat.Stop();
        studio?.Stop();
        studio?.Save();
        documentHost?.Dispose();
        Settings.PropertyChanged -= OnSetting;
        Store.Save();
        Host.Dispose();
    }
}
