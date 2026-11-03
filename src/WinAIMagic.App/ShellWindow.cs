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
        Closed += (_, _) =>
        {
            AppWindow.Hide();   // the rest of the closing may take a few seconds: the engine stops with the app
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
        double inset = AppWindow.TitleBar.RightInset / Math.Max(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0, 1);
        titleBar = Ui.Row($"{SidebarWidth + Theme.PagePadding},auto,*,auto,auto,{Math.Max(inset, 138) + 8}", 4, null, pill, null, add, gear).At(0, 0, 1, 2);
        titleBar.Height = BarHeight;
        titleBar.VerticalAlignment = VerticalAlignment.Top;
        titleBar.Background = new SolidColorBrush(Colors.Transparent);
        titleControls = [pill, add, gear];
        root.Children.Add(titleBar);
        SetTitleBar(titleBar);
        titleBar.Loaded += (_, _) => UpdateCaption();
        titleBar.SizeChanged += (_, _) => UpdateCaption();
        pill.SizeChanged += (_, _) => UpdateCaption();

        var shortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        shortcut.Invoked += (_, e) => { app.NewConversation(); e.Handled = true; };
        root.KeyboardAccelerators.Add(shortcut);
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
                PaneKind.Compare => new ComparePage(app),
                PaneKind.Cluster => new ClusterPage(app),
                PaneKind.System => new SystemPage(app),
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
        string? pageArg = Arg("page");
        Pane? forced = Enum.TryParse<PaneKind>(pageArg, true, out var kind) ? new Pane(kind, kind == PaneKind.Chat ? app.Store.Selection : null) : null;
        // first run: the models until one is there
        bool ready = app.Host.Store.StatusOf(app.Host.SelectedModelId).IsComplete;
        app.Pane = forced ?? (ready && app.Store.Selection is { } id ? new Pane(PaneKind.Chat, id) : new Pane(PaneKind.Models));
        ShowPane();
        RunTestHooks();

        // leftovers of a forced quit go, downloads go on, and then the start order of the original app
        await Task.Run(app.Host.OnAppStart);
        if (Settings.Shared.AutoStart && app.Host.CanStart) app.StartEngine();
        // what this PC needs is checked at every start; the very first start, or something in the way, shows it
        await app.RunCheck();
        if (forced == null && (app.Host.Check.NeedsAttention || !Settings.Shared.SystemCheckSeen)) app.Pane = new Pane(PaneKind.System);
        Settings.Shared.SystemCheckSeen = true;
        if (app.Store.LoadProblem is { } problem && Content.XamlRoot != null)
            await Ui.Ask(app, L("Gesprekken"), problem, null, close: L("Sluiten"));
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

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
