using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using WinAIMagic.App.Views;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App;

/// <summary>The one window: own title bar, Mica under the sidebar, the aurora under the pages. Always dark, at least 1000 x 680.</summary>
public sealed partial class ShellWindow : Window
{
    private const double BarHeight = 48, SidebarWidth = 260;
    private readonly AppState app;
    private readonly string[] args;
    private readonly Dictionary<PaneKind, IPage> pages = new();
    private readonly Grid pageHost = new();
    private readonly Storyboard fade = new();
    private Sidebar sidebar = null!;
    private Grid titleBar = null!;
    private FrameworkElement[] titleControls = [];
    private FrameworkElement? shown;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? hookTimer, tokenTimer;
    private bool quitting;

    /// <summary>The icon in the notification area (tasks and the heartbeat go on with the window closed).</summary>
    public TrayIcon Tray { get; }

    public ShellWindow(AppState app, string[] args)
    {
        this.app = app;
        this.args = args;
        app.Window = this;
        Title = "WinAIMagic";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "WinAIMagic.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var bar = AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Colors.White;
        bar.ButtonInactiveForegroundColor = Theme.Alpha(Colors.White, 0.45);
        bar.ButtonHoverBackgroundColor = Theme.Alpha(Colors.White, 0.10);
        bar.ButtonHoverForegroundColor = Colors.White;
        bar.ButtonPressedBackgroundColor = Theme.Alpha(Colors.White, 0.06);
        bar.ButtonPressedForegroundColor = Colors.White;

        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(1000 * scale);
            p.PreferredMinimumHeight = (int)(680 * scale);
        }
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1320 * scale), work.Width), h = Math.Min((int)(860 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));

        var a = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.18) };
        Storyboard.SetTarget(a, pageHost);
        Storyboard.SetTargetProperty(a, "Opacity");
        fade.Children.Add(a);

        Content = Build();
        app.LanguageChanged += Rebuild;
        app.PropertyChanged += OnApp;
        Tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), icon) { Activated = ShowFromTray, Menu = TrayMenu };
        Activated += (_, e) => app.WindowActive = e.WindowActivationState != WindowActivationState.Deactivated && AppWindow.IsVisible;
        // with tasks or a heartbeat on, closing the window leaves the app in the notification area; without, it quits as before
        AppWindow.Closing += (_, e) =>
        {
            if (quitting || !app.HasBackgroundWork || !Settings.Shared.StayInTray) return;
            e.Cancel = true;
            HideToTray(stopModel: Settings.Shared.StopModelOnClose);
        };
        app.Changed += UpdateTray;
        Closed += (_, _) =>
        {
            AppWindow.Hide();   // the rest of the closing may take a few seconds: the engine stops with the app
            Tray.Dispose();
            Notifier.Shutdown();
            app.Dispose();
        };
        Start();
    }

    private FrameworkElement Build()
    {
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        sidebar = new Sidebar(app);
        root.Children.Add(sidebar);

        var detail = new Grid().At(0, 1);
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });
        detail.RowDefinitions.Add(new RowDefinition());
        detail.Children.Add(new Aurora(app).At(0, 0, 2));
        (pageHost.Parent as Panel)?.Children.Remove(pageHost);
        detail.Children.Add(pageHost.At(1));
        root.Children.Add(detail);

        // the bar lies over both columns; Windows treats all of it as caption, except the controls in it
        var pill = new StatusPill(app);
        var add = Ui.IconBtn(Glyph.NewChat, app.NewConversation, L("Nieuw gesprek"));
        var gear = Ui.IconBtn(Glyph.Settings, () => SettingsDialog.Show(app), L("Instellingen"));
        var help = Ui.IconBtn(Glyph.Help, app.OpenHelpHere, L("Hulp bij dit onderdeel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(help, L("Hulp bij dit onderdeel"));
        double inset = AppWindow.TitleBar.RightInset / Math.Max(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0, 1);
        titleBar = Ui.Row($"{SidebarWidth + Theme.PagePadding},auto,*,auto,auto,auto,{Math.Max(inset, 138) + 8}", 4, null, pill, null, add, help, gear).At(0, 0, 1, 2);
        titleBar.Height = BarHeight;
        titleBar.VerticalAlignment = VerticalAlignment.Top;
        titleBar.Background = new SolidColorBrush(Colors.Transparent);
        titleControls = [pill, add, help, gear];
        root.Children.Add(titleBar);
        SetTitleBar(titleBar);
        titleBar.Loaded += (_, _) => UpdateCaption();
        titleBar.SizeChanged += (_, _) => UpdateCaption();
        pill.SizeChanged += (_, _) => UpdateCaption();

        var shortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        shortcut.Invoked += (_, e) => { app.NewConversation(); e.Handled = true; };
        root.KeyboardAccelerators.Add(shortcut);
        // F1: the help for the page on screen
        var f1 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F1 };
        f1.Invoked += (_, e) => { app.OpenHelpHere(); e.Handled = true; };
        root.KeyboardAccelerators.Add(f1);
        return root;
    }

    /// <summary>What lies over the title bar takes clicks itself.</summary>
    private void UpdateCaption()
    {
        if (Content?.XamlRoot is not { } xaml) return;
        double s = xaml.RasterizationScale;
        var rects = titleControls.Where(e => e.ActualWidth > 0).Select(e =>
        {
            var b = e.TransformToVisual(null).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            return new RectInt32((int)(b.X * s), (int)(b.Y * s), (int)(b.Width * s), (int)(b.Height * s));
        }).ToArray();
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects);
    }

    /// <summary>A new language: every screen again, in that language.</summary>
    private void Rebuild()
    {
        pages.Clear();
        pageHost.Children.Clear();
        shown = null;
        Content = Build();
        ShowPane();
    }

    private void OnApp(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Pane)) ShowPane();
    }

    private void ShowPane()
    {
        var pane = app.Pane;
        sidebar.Mark(pane);
        if (!pages.TryGetValue(pane.Kind, out var page))
        {
            page = pane.Kind switch
            {
                PaneKind.Live => new LivePage(app),
                PaneKind.Documents => new DocumentsPage(app),
                PaneKind.Images => new ImagePage(app),
                PaneKind.Compare => new ComparePage(app),
                PaneKind.Cluster => new ClusterPage(app),
                PaneKind.System => new SystemPage(app),
                PaneKind.Help => new HelpPage(app),
                PaneKind.Tasks => new TasksPage(app),
                PaneKind.Chat => new ChatPage(app),
                _ => new ModelsPage(app),
            };
            pages[pane.Kind] = page;
        }
        if (shown == page.View) return;
        shown = page.View;
        pageHost.Children.Clear();
        pageHost.Children.Add(page.View);
        fade.Stop();
        fade.Begin();
    }

    private async void Start()
    {
        try { await StartUp(); }
        catch (Exception e) { App.Log(e); }
    }

    private async Task StartUp()
    {
        string? pageArg = Arg("page") switch { "Documenten" => "Documents", "Beeld" => "Images", "Vergelijken" => "Compare", "Modellen" => "Models", "Systeem" => "System", "Hulp" => "Help", "Taken" => "Tasks", var p => p };
        Pane? forced = Enum.TryParse<PaneKind>(pageArg, true, out var kind) ? new Pane(kind, kind == PaneKind.Chat ? app.Store.Selection : null) : null;
        // first run: the models until one is there
        bool ready = app.Host.Store.StatusOf(app.Host.SelectedModelId).IsComplete;
        app.Pane = forced ?? (ready && app.Store.Selection is { } id ? new Pane(PaneKind.Chat, id) : new Pane(PaneKind.Models));
        ShowPane();
        RunTestHooks();

        // leftovers of a forced quit go, downloads go on, and then the start order of the original app
        await Task.Run(app.Host.OnAppStart);
        if (Settings.Shared.AutoStart && app.Host.CanStart) app.StartEngine();
        // the scheduled tasks and the heartbeat, and the icon in the notification area when they are on
        app.StartTasks();
        UpdateTray();
        // what this PC needs is checked at every start; the very first start, or something in the way, shows it
        await app.RunCheck();
        if (forced == null && (app.Host.Check.NeedsAttention || !Settings.Shared.SystemCheckSeen)) app.Pane = new Pane(PaneKind.System);
        Settings.Shared.SystemCheckSeen = true;
        if (app.Store.LoadProblem is { } problem && Content.XamlRoot != null)
            await Ui.Ask(app, L("Gesprekken"), problem, null, close: L("Sluiten"));
    }

    /// <summary>The window goes, the app stays in the notification area; the model stops unless something runs, so its memory is free again.</summary>
    public void HideToTray(bool stopModel)
    {
        AppWindow.Hide();
        app.WindowActive = false;
        Tray.Show(TrayTip());
        bool running;
        lock (app.TaskRuns) running = app.TaskRuns.Count > 0;
        if (stopModel && !app.Chat.Busy && !running && app.EngineState.IsActive) app.StopEngine();
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        BringToFront();
        app.WindowActive = true;
        UpdateTray();
    }

    /// <summary>Quits for real (the tray's Afsluiten).</summary>
    public void Quit()
    {
        quitting = true;
        Close();
    }

    /// <summary>The icon is there while tasks or a heartbeat are on, or while the window is hidden.</summary>
    private void UpdateTray()
    {
        bool want = !AppWindow.IsVisible || app.HasBackgroundWork;
        if (want) Tray.Show(TrayTip());
        else Tray.Hide();
    }

    private string TrayTip()
    {
        int runs;
        lock (app.TaskRuns) runs = app.TaskRuns.Count;
        return "WinAIMagic" + (runs > 0 ? " · " + L($"Taken bezig: {runs}") : app.EngineState.IsRunning ? " · " + L("Model geladen") : "");
    }

    private List<TrayIcon.Item> TrayMenu()
    {
        var items = new List<TrayIcon.Item>();
        var model = app.Host.Engine?.Model?.Name ?? "";
        items.Add(new(app.EngineState.IsRunning ? L($"Model geladen: {model}") : L("Model niet geladen")));
        int runs;
        lock (app.TaskRuns) runs = app.TaskRuns.Count;
        if (runs > 0) items.Add(new(L($"Taken bezig: {runs}")));
        else if (app.Chat.Busy) items.Add(new(L("Bezig met een gesprek")));
        if (app.Tasks.Tasks.Where(t => t.Enabled && t.NextRun != null).OrderBy(t => t.NextRun).FirstOrDefault() is { } next)
        {
            var when = next.NextRun!.Value.ToLocalTime().ToString("g", AppLanguages.Current.Culture());
            items.Add(new(L($"Volgende taak: {next.Title}, {when}")));
        }
        if (Settings.Shared.Heartbeat && app.HeartbeatStatus is { } status) items.Add(new(status));
        items.Add(TrayIcon.Item.Separator);
        items.Add(new(L("Open WinAIMagic"), ShowFromTray));
        items.Add(new(L("Taken"), () => { ShowFromTray(); app.Pane = new Pane(PaneKind.Tasks); }));
        if (Settings.Shared.Heartbeat) items.Add(new(L("Nu controleren"), () => _ = app.RunHeartbeat(forced: true)));
        if (app.Chat.Busy) items.Add(new(L("Alles stoppen"), app.Chat.Stop));
        items.Add(TrayIcon.Item.Separator);
        items.Add(new(L("WinAIMagic afsluiten"), Quit));
        return items;
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 3)..];

    /// <summary>Hidden, for screenshots: --scroll=N, --tokens=N (tokens per second as if a model wrote),
    /// --dialog=settings:Chat|settings:Engine[:scroll]|license:id|delete:id|folder:path.</summary>
    private void RunTestHooks()
    {
#if DEBUG
        RunChatHooks();
#endif
        if (double.TryParse(Arg("tokens"), out var rate))
        {
            tokenTimer = DispatcherQueue.CreateTimer();
            tokenTimer.Interval = TimeSpan.FromSeconds(0.5);
            tokenTimer.Tick += (_, _) => app.Host.Metrics.AddTokens((int)(rate / 2));
            tokenTimer.Start();
        }
        if (!args.Any(a => a.StartsWith("--scroll=") || a.StartsWith("--dialog="))) return;
        hookTimer = DispatcherQueue.CreateTimer();
        hookTimer.IsRepeating = false;
        hookTimer.Interval = TimeSpan.FromSeconds(1.2);
        hookTimer.Tick += (_, _) =>
        {
            if (double.TryParse(Arg("scroll"), out var y) && shown is ScrollViewer sv) sv.ChangeView(null, y, null, true);
            var dialog = Arg("dialog")?.Split(':');
            switch (dialog?[0])
            {
                case "settings": SettingsDialog.Show(app, dialog.ElementAtOrDefault(1) == "Engine" ? 1 : 0, double.TryParse(dialog.ElementAtOrDefault(2), out var at) ? at : 0); break;
                case "folder" when shown != null && pages.GetValueOrDefault(PaneKind.Models) is ModelsPage models: models.ChangeFolder(string.Join(":", dialog.Skip(1))); break;
                case "license" when app.Host.Store.Catalog.Model(dialog[1]) is { } m: _ = ModelCard.AcceptLicense(app, m); break;
                case "delete" when app.Host.Store.Catalog.Model(dialog[1]) is { } m: _ = ModelCard.ConfirmDelete(app, m); break;
            }
        };
        hookTimer.Start();
    }

#if DEBUG
    /// <summary>Hidden, debug builds: --fake-engine (a scripted stream), --fake-state=starting|failed, --attach=file;file,
    /// --paste=file:path|bitmap:path|textbitmap:path (the paste logic on a local data package), --folder=path, --lag-log=file (the
    /// longest wait of the UI thread per second).</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? chatHook, documentHook;

    /// <summary>Hidden: --project, --huisstijl, --attach-doc, --doc=busy (see DocumentHooks), --export=file (Vergelijken), --sim-gpu=VRAM in GB
    /// (a PC with the main engine, for the cluster screen; 0: no card), --sim-ram=RAM in GB beside it (the plans of a laptop).</summary>
    private void RunDocumentHooks()
    {
        if (int.TryParse(Arg("sim-gpu"), out var vram))
            app.Host.SimulatedSystem = Engine.SystemInfo.Simulated(vram > 0 ? Engine.GpuProfiles.SimulatedMainEngineCard(vram) : Engine.FakeGpuBackend.Cpu(), int.TryParse(Arg("sim-ram"), out var simRam) ? simRam : 128);
        if (Arg("project") is { } folder) app.Studio.Choose(folder);
        if (args.Contains("--fake-engine")) DocumentHooks.Offline(app.Studio);
        if (Arg("huisstijl") is { } style) app.Studio.UseHouseStyle(style);
        if (Arg("attach-doc") is { } file) app.Studio.AddAttachment(file);
        documentHook = DispatcherQueue.CreateTimer();
        documentHook.IsRepeating = false;
        documentHook.Interval = TimeSpan.FromSeconds(1.2);
        documentHook.Tick += async (_, _) =>
        {
            if (Arg("export") is { } path && pages.GetValueOrDefault(PaneKind.Compare) is ComparePage compare) compare.Export(path);
            if (pages.GetValueOrDefault(PaneKind.Documents) is not DocumentsPage page) return;
            DocumentHooks.LogLayout("start", page, this);
            if (Arg("doc") == "busy") await DocumentHooks.Busy(app, page, this);
        };
        documentHook.Start();
    }

    private void RunChatHooks()
    {
        if (args.Contains("--fake-engine")) app.TestUrl = FakeEngine.Start();
        app.TestState = Arg("fake-state") switch
        {
            "starting" => Engine.EngineState.Starting(L("Model laden…")),
            "failed" => Engine.EngineState.Failed(L($"Engine gestopt (code {1}): {"fake"}")),
            _ => args.Contains("--fake-engine") ? Engine.EngineState.Running : null,
        };
        if (args.Contains("--fake-engine")) app.Host.Metrics.Fast = true;
        RunDocumentHooks();
        if (Arg("lag-log") is { } lagFile)
        {
            var tick = DispatcherQueue.CreateTimer();
            tick.Interval = TimeSpan.FromMilliseconds(16);
            long last = Environment.TickCount64, nextWrite = last + 1000, worst = 0;
            tick.Tick += (_, _) =>
            {
                long now = Environment.TickCount64;
                worst = Math.Max(worst, now - last);
                last = now;
                if (now < nextWrite) return;
                File.AppendAllText(lagFile, $"{DateTime.Now:HH:mm:ss} worst {worst} ms\n");
                worst = 0;
                nextWrite = now + 1000;
            };
            tick.Start();
            tokenTimer = tick;
        }
        chatHook = DispatcherQueue.CreateTimer();
        chatHook.IsRepeating = false;
        chatHook.Interval = TimeSpan.FromSeconds(1);
        chatHook.Tick += async (_, _) =>
        {
            if (pages.GetValueOrDefault(PaneKind.Chat) is not ChatPage chat) return;
            if (Arg("folder") is { } folder) chat.Composer.SetFolder(folder);
            foreach (var file in Arg("attach")?.Split(';') ?? []) await chat.Composer.Add(file);
            if (Arg("paste")?.Split(':', 2) is [var kind, var path])
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                if (kind == "file") package.SetStorageItems([await Windows.Storage.StorageFile.GetFileFromPathAsync(path)]);
                else
                {
                    package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(await Windows.Storage.StorageFile.GetFileFromPathAsync(path)));
                    if (kind == "textbitmap") package.SetText("tekst wint");
                }
                File.AppendAllText(Path.Combine(Paths.AppData, "paste.log"), $"{kind}: took={await chat.Composer.TakeImages(package.GetView())}\n");
            }
        };
        chatHook.Start();
    }
#endif

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
