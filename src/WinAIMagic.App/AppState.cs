using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using WinAIMagic.Studio;

namespace WinAIMagic.App;

public enum PaneKind { Live, Models, Documents, Images, Tasks, Compare, Cluster, System, Help, Chat }

public readonly record struct Pane(PaneKind Kind, Guid? Conversation = null);

/// <summary>Ties the parts together, like AppState.swift; the engine half lives in EngineHost. Everything that reaches the screens
/// comes through here on the UI thread, at most every 0.1 s: the parts' events fire on background threads.</summary>
public sealed partial class AppState : INotifyPropertyChanged, IDisposable
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
        Host.Images.Root = Program.TestData ? Path.Combine(Paths.AppData, "Afbeeldingen") : null;
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
        // the model knows the app and can act in it (app_status, app_action)
        AppTools.Control = new AppControl(this);
        if (Settings.FastModel.Length > 0 && Settings.FastModel == Settings.SelectedModelId) Settings.FastModel = "";
    }

    /// <summary>A screen that fails on one update must not take the downloads and the engine down with it: logged, and on with the rest.</summary>
    private static void Guarded(Action a)
    {
        try { a(); }
        catch (Exception e) { App.Log(e); }
    }

    /// <summary>The help topic to show; set by a ? button or F1, read by the help page.</summary>
    public string HelpTopic { get; private set; } = "start";
    public event Action? HelpRequested;

    public void OpenHelp(string topic)
    {
        HelpTopic = topic;
        Pane = new Pane(PaneKind.Help);
        HelpRequested?.Invoke();
    }

    /// <summary>The help for the page on screen.</summary>
    public void OpenHelpHere() => OpenHelp(Core.Support.HelpCenter.TopicFor(Pane.Kind.ToString()));

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
            case nameof(Settings.SelectedModelId):
                // the heavy model is never the fast one too, however it was chosen (a card, the system check, Compare)
                if (Settings.FastModel == Settings.SelectedModelId) Settings.FastModel = "";
                Notify();
                break;
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
    /// <summary>The second engine (documents, a description for an image) when it was ever started.</summary>
    public IEngine? FastEngine => documentHost?.FastEngine;
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
        return Task.Run(async () =>
        {
            // the same model twice in memory helps nothing: the copy on the second engine makes room for the real one
            if (ChatModelOnFast != null && documentHost != null) await documentHost.StopFastAndWaitAsync();
            Host.StartEngine();
        });
    }

    /// <summary>The chat's model, when it runs only as the fast model on the second engine (on the processor, 16K): the chat does not
    /// talk to that one.</summary>
    public ModelInfo? ChatModelOnFast =>
        Host.Engine?.State.IsActive != true && documentHost?.FastEngine is { } f && f.State.IsActive && f.Model?.Id == Host.SelectedModel?.Id ? f.Model : null;

    public void StopEngine() => Task.Run(Host.StopEngine);

    public void Select(string id) => Task.Run(() => Host.Select(id));

    /// <summary>"Start dit model": choose it and start it in one go; another model that runs stops first. A model that was the fast one is
    /// that no longer: the heavy and the fast model are never the same.</summary>
    public Task StartModel(string id) => Task.Run(async () =>
    {
        if (Settings.FastModel == id) Settings.FastModel = "";
        if (!await Host.Choose(id) || !Host.CanStart) return;
        await StartEngineTask();
    });

    /// <summary>One image; the documents module goes first when it is at work, and its fast model makes room when it is not.</summary>
    public Task MakeImage(string prompt, ImageSize size) => Task.Run(() => Host.MakeImage(prompt, size, async () =>
    {
        if (studio?.Busy == true) return Lang.L("De documentmodule is bezig. Wacht tot die klaar is of stop hem.");
        await MakeRoomForImage(size);
        return null;
    }, improve: Settings.ImageImprove ? step => ImproveDescription(prompt, step) : null));

    /// <summary>Who writes the better description: the fast model (Snel), the heavy one only when there is no other.</summary>
    public ImageDescriber? Describer
    {
        get
        {
            var store = Host.Store;
            var downloaded = store.Catalog.Models.Where(m => store.StatusOf(m.Id).IsComplete && m.Tier?.Llama != false).ToList();
            var heavy = Host.SelectedModel is { } h && store.StatusOf(h.Id).IsComplete ? h : null;
            var engines = DocumentEngines;     // makes the host of the second engine when it is not there yet
            var fast = documentHost?.FastEngine;
            var fastRunning = fast?.State.IsRunning == true ? fast.Model : null;
            return ImageDescriber.Choose(heavy, downloaded, Settings.FastModel, Host.Engine?.State.IsRunning == true, fastRunning,
                                         documentHost?.CanRunFastBeside() ?? false);
        }
    }

    /// <summary>The fast model runs on the processor and may stay beside the image when the RAM has room for both; else it makes room.</summary>
    private async Task MakeRoomForImage(ImageSize size)
    {
        if (documentHost?.FastEngine is not { } fast || !fast.State.IsActive) return;
        var plan = Host.ImagePlanFor(size);
        // what the image engine holds of its own beside the weights it maps: measured 5 GB on the card's path, the decoder on the processor
        ulong need = (plan.OnCard ? 5_000_000_000 : ImagePlanner.DecoderBytes(size) + 2_500_000_000) + 2_000_000_000;
        if (SystemInfo.AvailableMemory() >= need) return;
        Host.Images.Step(Lang.L($"{fast.Model?.ShortName ?? Lang.L("Snel")} stopt om ruimte te maken voor de afbeelding"));
        await documentHost.StopFastAndWaitAsync();
    }

    /// <summary>The chat model writes a better description: started first when it does not run; the engine makes room afterwards, as for
    /// any image.</summary>
    private async Task<(string Prompt, IReadOnlyList<ImageSource> Sources)> ImproveDescription(string text, Action<string> onStep)
    {
        var writer = Describer ?? throw new InvalidOperationException(Lang.L("Er is geen chatmodel gedownload."));
        var model = writer.Model;
        Uri baseUrl;
        if (writer.OnFast)
        {
            var fast = documentHost!.FastEngine;
            if (!(fast?.State.IsRunning == true && fast.Model?.Id == model.Id))
            {
                onStep(Lang.L($"{model.ShortName} ({writer.Role}) laden…"));
                await documentHost.StartForImageAsync(model);
            }
            baseUrl = documentHost.FastBaseUrl;
        }
        else
        {
            if (Host.Engine?.State.IsRunning != true)
            {
                if (!Host.ChatModelReady) throw new InvalidOperationException(Lang.L($"{model.Name} kan nu niet starten."));
                onStep(Lang.L($"{model.ShortName} ({writer.Role}) laden…"));
                if (Host.Plan is { } plan) Chat.ContextLimit = plan.ContextSize;
                Host.StartEngine(forImage: true);
                var started = DateTime.UtcNow;
                while (Host.Engine?.State.IsRunning != true)
                {
                    if (Host.Engine?.State.Status == Engine.EngineStatus.Failed) throw new InvalidOperationException(Host.Engine.State.Message ?? Lang.L($"{model.Name} startte niet."));
                    if ((DateTime.UtcNow - started).TotalSeconds > 1800) throw new InvalidOperationException(Lang.L($"{model.Name} startte niet."));
                    await Task.Delay(500);
                }
            }
            baseUrl = BaseUrl;
        }
        onStep(Lang.L($"{model.ShortName} ({writer.Role}) schrijft de beschrijving…"));
        var result = await ImagePrompt.Improve(text, model.Id, ImagePrompt.Sender(baseUrl), s => onStep(model.ShortName + " " + s));
        return (result.Prompt, result.Sources.Select(s => new ImageSource(s.Title, s.Url)).ToList());
    }

    /// <summary>A button of the system check. The page file waits for Windows' UAC prompt, so that goes off the UI thread.</summary>
    public void Perform(SystemCheck.CheckAction action)
    {
        void Open(SystemCheck.CheckAction a) => Ui.TryEnqueue(() =>
        {
            if (a == SystemCheck.CheckAction.RestartApp) Restart();
            else Pane = new Pane(a == SystemCheck.CheckAction.OpenCluster ? PaneKind.Cluster : PaneKind.Models);
        });
        if (action == SystemCheck.CheckAction.SetPageFile) Task.Run(() => Host.Check.Perform(action, Host, Open));
        else Host.Check.Perform(action, Host, Open);
    }

    public Task RunCheck() => Task.Run(() => Host.Check.Run(Host));

    /// <summary>A new copy with the same arguments, which waits until this one has stopped its engines and gone (Program: --wait-for).</summary>
    public void Restart()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--wait-for=", StringComparison.Ordinal))
            .Append($"--wait-for={Environment.ProcessId}");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(start);
        // really quit, also with tasks on (closing would otherwise leave this copy in the notification area)
        if (Window is ShellWindow shell) shell.Quit();
        else Window.Close();
    }

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
        StopTasks();
        Chat.Stop();
        studio?.Stop();
        studio?.Save();
        documentHost?.Dispose();
        Settings.PropertyChanged -= OnSetting;
        Store.Save();
        Host.Dispose();
    }
}
