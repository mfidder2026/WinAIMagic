using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;
using Automation = Microsoft.UI.Xaml.Automation.AutomationProperties;

namespace WinAIMagic.App.Views;

/// <summary>The input: text, images (button, drag and drop, paste), the toggles and the send button.</summary>
public sealed partial class Composer : Grid
{
    private const double Ring = 1000;
    private readonly AppState app;
    private readonly Action sent;
    private readonly TextBox box = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 218, FontSize = 14, FontFamily = Theme.Text, BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 2, 0, 4), MinHeight = 28, AllowDrop = false, VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly List<Attachment> attachments = [];
    private readonly StackPanel thumbs = Ui.H(8);
    private readonly ScrollViewer thumbsHost;
    private readonly Border glass;
    private readonly Grid ringHost = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Storyboard turn = new();
    private readonly Button image, send, stop;
    private readonly ToggleButton think, web, folder;
    private readonly TextBlock folderName = Ui.Label("", 12, null, FontWeights.Medium);
    private readonly RightsButton rights;
    private Guid? conversation;
    private string? workspace;
    private bool busy, target, updating;

    public Composer(AppState app, Action sent)
    {
        this.app = app;
        this.sent = sent;
        MaxWidth = 860;
        Margin = new Thickness(24, 8, 24, 16);

        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                                  "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            box.Resources[k] = clear;
        box.Resources["TextControlForegroundFocused"] = Theme.Primary;
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Automation.SetName(box, L("Bericht"));
        box.TextChanged += (_, _) => Refresh();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || Down(Windows.System.VirtualKey.Shift)) return;
            e.Handled = true;
            Send();
        };
        box.Paste += (_, e) => { if (Paste()) e.Handled = true; };

        thumbsHost = new ScrollViewer
        {
            Content = thumbs, Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ZoomMode = ZoomMode.Disabled,
        };

        image = Small(Ui.Icon(Glyph.Image, 14), () => _ = PickImages(), L("Afbeelding toevoegen"));
        Automation.SetName(image, L("Afbeelding toevoegen"));
        var s = Settings.Shared;
        think = Toggle(Glyph.Think, L("Denken"), L("Laat het model eerst nadenken (beter, maar trager)"), on => s.Thinking = on);
        web = Toggle(Glyph.Globe, L("Web"), L("Het model mag het web doorzoeken en pagina's lezen"), on => s.WebSearch = on);
        folder = Toggle(Glyph.Folder, folderName, on =>
        {
            if (on && workspace == null) _ = ChooseFolder();
            else s.WorkspaceTools = on;
        });
        Automation.SetName(folder, L("Werkmap"));
        rights = new RightsButton();

        send = Ui.Btn("", Send, Glyph.Up, Theme.Accent);
        stop = Ui.Btn("", app.Chat.Stop, Glyph.Stop, Colors.White);
        foreach (var (b, name) in new[] { (send, L("Versturen")), (stop, L("Stop")) })
        {
            b.Width = b.Height = 32;
            b.Padding = new Thickness(0);
            b.CornerRadius = new CornerRadius(16);
            b.Tip(name);
            Automation.SetName(b, name);
        }

        var tools = Ui.H(6, image, Pair(think, ThinkMenu()), web, Pair(folder, FolderMenu()), rights);
        var sendHost = new Grid { Children = { send, stop } };
        var content = Ui.V(8, thumbsHost, box, Ui.Row("*,auto", 8, tools, sendHost));

        // a turning gradient edge while the model writes, a calm one otherwise: the storyboard only runs while it writes
        var rotate = new RotateTransform { CenterX = Ring / 2, CenterY = Ring / 2 };
        var spinner = new Rectangle
        {
            Width = Ring, Height = Ring, RenderTransform = rotate,
            Fill = Theme.Gradient(new Point(0, 0.5), new Point(1, 0.5), Theme.Cyan, Theme.Accent, Theme.Violet, Theme.Pink, Theme.Cyan),
        };
        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(spinner);
        ringHost.Children.Add(canvas);
        var a = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(4), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, rotate);
        Storyboard.SetTargetProperty(a, "Angle");
        turn.Children.Add(a);

        var visual = ElementCompositionPreview.GetElementVisual(ringHost);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(18);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
        ringHost.SizeChanged += (_, e) =>
        {
            geometry.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            Canvas.SetLeft(spinner, (e.NewSize.Width - Ring) / 2);
            Canvas.SetTop(spinner, (e.NewSize.Height - Ring) / 2);
        };

        glass = new Border
        {
            Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Theme.Rgb(0x15, 0x17, 0x2B)), BorderBrush = Theme.Solid(Colors.White, 0.12),
        };
        Children.Add(ringHost);
        Children.Add(glass);
        Loaded += (_, _) => { box.Focus(FocusState.Programmatic); if (busy) turn.Begin(); };
        Unloaded += (_, _) => turn.Stop();
    }

    public bool TextFocused => ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot), box);
    public bool HasDraft => box.Text.Length > 0;
    public void Focus() => box.Focus(FocusState.Programmatic);

    public void SetDraft(string text)
    {
        box.Text = text;
        box.SelectionStart = text.Length;
        Focus();
    }

    /// <summary>Another conversation: its own empty field.</summary>
    public void Reset(Guid? id)
    {
        conversation = id;
        box.Text = "";
        attachments.Clear();
        ShowAttachments();
    }

    private bool CanSend => app.EngineState.IsRunning && !app.Chat.Busy && (box.Text.Trim().Length > 0 || attachments.Count > 0);

    /// <summary>The state of the buttons.</summary>
    public void Refresh()
    {
        var s = Settings.Shared;
        var c = conversation is { } id ? app.Store.Find(id) : null;
        workspace = c?.Workspace;
        updating = true;
        if (think.IsChecked != s.Thinking) think.IsChecked = s.Thinking;
        if (web.IsChecked != s.WebSearch) web.IsChecked = s.WebSearch;
        bool active = s.WorkspaceTools && workspace != null;
        if (folder.IsChecked != active) folder.IsChecked = active;
        updating = false;
        folderName.SetIfChanged(workspace == null ? L("Werkmap") : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(workspace)));
        folder.Tip(workspace == null ? L("Kies een map waarin het model in dit gesprek bestanden mag lezen, maken en scripts mag starten") : L($"Het model werkt in {workspace} (rechten: knop L S U)"));
        rights.Refresh();

        if (image.IsEnabled != s.Vision) image.IsEnabled = s.Vision;
        bool working = app.Chat.Busy;
        send.Show(!working);
        stop.Show(working);
        bool can = CanSend;
        if (send.IsEnabled != can) send.IsEnabled = can;
        if (working != busy)
        {
            busy = working;
            ringHost.Show(working);
            glass.Margin = new Thickness(working ? 2 : 0);
            glass.CornerRadius = new CornerRadius(working ? 16 : 18);
            glass.BorderThickness = new Thickness(working ? 0 : 1);
            glass.Padding = new Thickness(working ? 13 : 14);
            if (working) turn.Begin();
            else turn.Stop();
        }
        var model = app.ChatModel;
        var hint = L($"Vraag {model?.ShortName ?? "DeepSeek"} iets…");
        if (box.PlaceholderText != hint) box.PlaceholderText = hint;
    }

    private void Send()
    {
        if (!CanSend || conversation is not { } id) return;
        app.Send(box.Text.Trim(), [.. attachments], id);
        box.Text = "";
        attachments.Clear();
        ShowAttachments();
        sent();
    }

    // MARK: - images

    private async Task PickImages()
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var e in ImageTypes) picker.FileTypeFilter.Add(e);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(app.Window));
            foreach (var file in await picker.PickMultipleFilesAsync()) await Add(file.Path);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { App.Log(e); }
    }

    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".ico"];

    public async Task Add(string path)
    {
        if (await ImageScaler.MakeAsync(path) is { } a) Attach(a);
    }

    private void Attach(Attachment a)
    {
        attachments.Add(a);
        ShowAttachments();
        Refresh();
    }

    private void ShowAttachments()
    {
        thumbs.Children.Clear();
        foreach (var a in attachments)
        {
            var close = ChatUi.Plain(Ui.Icon(Glyph.Close, 9, new SolidColorBrush(Colors.White)), () => { attachments.Remove(a); ShowAttachments(); Refresh(); }, L("Verwijder afbeelding"));
            close.Width = close.Height = 18;
            close.CornerRadius = new CornerRadius(9);
            close.Background = Theme.Solid(Colors.Black, 0.6);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(3);
            Automation.SetName(close, L("Verwijder afbeelding"));
            thumbs.Children.Add(new Grid { Children = { ChatUi.Thumb(a.Jpeg, 64, 8), close } });
        }
        thumbsHost.Show(attachments.Count > 0);
    }

    /// <summary>The images in a clipboard or a drop: image files first, a bitmap only when it comes without text (copied text often
    /// carries a picture of itself too). False when there are none, so text pastes as usual.</summary>
    public async Task<bool> TakeImages(DataPackageView v)
    {
        if (!Settings.Shared.Vision) return false;
        if (v.Contains(StandardDataFormats.StorageItems))
        {
            var files = (await v.GetStorageItemsAsync()).OfType<StorageFile>().Where(f => ImageTypes.Contains(f.FileType.ToLowerInvariant())).ToList();
            if (files.Count > 0)
            {
                foreach (var f in files) await Add(f.Path);
                return true;
            }
        }
        if (v.Contains(StandardDataFormats.Text) || !v.Contains(StandardDataFormats.Bitmap)) return false;
        var reference = await v.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        if (await ImageScaler.MakeAsync(bytes, L("Geplakt")) is not { } a) return false;
        Attach(a);
        return true;
    }

    public static bool HasImages(DataPackageView v) =>
        Settings.Shared.Vision && (v.Contains(StandardDataFormats.StorageItems) || (!v.Contains(StandardDataFormats.Text) && v.Contains(StandardDataFormats.Bitmap)));

    /// <summary>Ctrl+V: true when the clipboard is taken here, so the field does not paste it too.</summary>
    public bool Paste()
    {
        DataPackageView v;
        try { v = Clipboard.GetContent(); }
        catch (COMException) { return false; }
        if (!HasImages(v)) return false;
        _ = PasteFrom(v);
        return true;
    }

    private async Task PasteFrom(DataPackageView v)
    {
        try
        {
            if (!await TakeImages(v) && v.Contains(StandardDataFormats.Text)) box.SelectedText = await v.GetTextAsync();
        }
        catch (COMException e) { App.Log(e); }
    }

    /// <summary>Something can be dropped here.</summary>
    public void DropTarget(bool on)
    {
        if (on == target) return;
        target = on;
        glass.BorderBrush = on ? new SolidColorBrush(Theme.Accent) : Theme.Solid(Colors.White, 0.12);
        glass.BorderThickness = new Thickness(on ? 2 : busy ? 0 : 1);
    }

    private static bool Down(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // MARK: - the toggles

    private static void Dress(FrameworkElement b)
    {
        b.Resources["ToggleButtonBackground"] = b.Resources["ToggleButtonBackgroundDisabled"] = Theme.Solid(Colors.White, 0.07);
        b.Resources["ToggleButtonBackgroundPointerOver"] = Theme.Solid(Colors.White, 0.13);
        b.Resources["ToggleButtonBackgroundPressed"] = Theme.Solid(Colors.White, 0.05);
        b.Resources["ToggleButtonBackgroundChecked"] = Theme.Solid(Theme.Accent, 0.42);
        b.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Theme.Solid(Theme.Accent, 0.55);
        b.Resources["ToggleButtonBackgroundCheckedPressed"] = Theme.Solid(Theme.Accent, 0.35);
        foreach (var k in new[] { "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed" })
            b.Resources[k] = new SolidColorBrush(Colors.White);
        foreach (var k in new[] { "ToggleButtonBorderBrush", "ToggleButtonBorderBrushPointerOver", "ToggleButtonBorderBrushPressed", "ToggleButtonBorderBrushChecked", "ToggleButtonBorderBrushCheckedPointerOver", "ToggleButtonBorderBrushCheckedPressed" })
            b.Resources[k] = new SolidColorBrush(Colors.Transparent);
    }

    private ToggleButton Toggle(string glyph, string text, string tip, Action<bool> set)
    {
        var t = Toggle(glyph, Ui.Label(text, 12, null, FontWeights.Medium), set).Tip(tip);
        Automation.SetName(t, text);
        return t;
    }

    private ToggleButton Toggle(string glyph, TextBlock text, Action<bool> set)
    {
        var t = new ToggleButton { Content = Ui.H(5, Ui.Icon(glyph, 13), text), Padding = new Thickness(9, 4, 9, 5), CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
        Dress(t);
        t.Click += (_, _) => { if (!updating) set(t.IsChecked == true); Refresh(); };
        return t;
    }

    private static Button Small(UIElement content, Action click, string tip)
    {
        var b = ChatUi.Plain(content, click, tip);
        b.Width = 32;
        b.Height = 28;
        b.Background = Theme.Solid(Colors.White, 0.07);
        return b;
    }

    /// <summary>A toggle with the menu of its choices stuck to it.</summary>
    private static StackPanel Pair(ToggleButton toggle, MenuFlyout menu)
    {
        toggle.CornerRadius = new CornerRadius(7, 0, 0, 7);
        var more = new Button { Content = Ui.Icon(Glyph.ChevronDown, 8), Width = 20, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0, 7, 7, 0), Background = Theme.Solid(Colors.White, 0.07), Flyout = menu, VerticalAlignment = VerticalAlignment.Stretch };
        menu.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;
        var name = Automation.GetName(toggle);
        if (name.Length > 0) Automation.SetName(more, name + " …");
        return Ui.H(1, toggle, more);
    }

    private static MenuFlyout ThinkMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var s = Settings.Shared;
            menu.Items.Add(new MenuFlyoutItem { Text = L("Maximaal nadenken"), IsEnabled = false });
            foreach (var (label, n) in new[] { ("4K", 4_000), ("8K", 8_000), ("16K", 16_000), ("32K", 32_000), (L("Onbeperkt"), 0) })
            {
                var item = new RadioMenuFlyoutItem { Text = label, GroupName = "think", IsChecked = s.ThinkingBudget == n };
                item.Click += (_, _) => s.ThinkingBudget = n;
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    private MenuFlyout FolderMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var choose = new MenuFlyoutItem { Text = L("Kies map voor dit gesprek…") };
            choose.Click += (_, _) => _ = ChooseFolder();
            menu.Items.Add(choose);
            if (workspace is not { } now) return;
            var show = new MenuFlyoutItem { Text = L("Toon in Verkenner") };
            show.Click += (_, _) => OpenFolder(now);
            menu.Items.Add(show);
            var none = new MenuFlyoutItem { Text = L("Geen werkmap in dit gesprek") };
            none.Click += (_, _) =>
            {
                if (conversation is not { } id) return;
                app.Store.Update(id, c => c.Workspace = null);
                app.Store.Save();
            };
            menu.Items.Add(none);
        };
        return menu;
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { App.Log(e); }
    }

    private async Task ChooseFolder()
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(app.Window));
            if (await picker.PickSingleFolderAsync() is { } picked) SetFolder(picked.Path);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { App.Log(e); }
        Refresh();
    }

    /// <summary>This chat's folder; new chats start here.</summary>
    public void SetFolder(string path)
    {
        if (conversation is not { } id) return;
        app.Store.Update(id, c => c.Workspace = path);
        app.Store.Save();
        Settings.Shared.Workspace = path;
        Settings.Shared.WorkspaceTools = true;
    }
}

/// <summary>The rights of the model in the work folder at a glance: three bold letters in green (allowed), orange (asks first) or
/// red (denied). A click opens the matrix.</summary>
public sealed partial class RightsButton : Button
{
    private readonly (WorkspaceRight Kind, TextBlock Letter)[] letters;

    public RightsButton()
    {
        letters = [.. Enum.GetValues<WorkspaceRight>().Select(k => (k, Ui.Label(k.Letter(), 12, null, FontWeights.ExtraBold)))];
        Content = Ui.H(4, [.. letters.Select(l => (UIElement)l.Letter)]);
        Padding = new Thickness(9, 4, 9, 5);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(0);
        Background = Theme.Solid(Colors.White, 0.08);
        VerticalAlignment = VerticalAlignment.Center;
        this.Tip(L("Rechten van het model in de werkmap: Lezen, Schrijven, Uitvoeren. Groen = mag, oranje = eerst vragen, rood = nee."));
        Automation.SetName(this, string.Join(" ", letters.Select(l => l.Letter.Text)));
        Flyout = ChatUi.Popover(new RightsMatrix(Refresh), 620);
        Refresh();
    }

    public static string Value(WorkspaceRight k)
    {
        var s = Settings.Shared;
        return k == WorkspaceRight.Read ? s.RightRead : k == WorkspaceRight.Write ? s.RightWrite : s.RightRun;
    }

    public static Windows.UI.Color Color(string value) => value == "allow" ? Theme.Mint : value == "deny" ? Theme.Red : Theme.Amber;

    public void Refresh()
    {
        foreach (var (kind, letter) in letters)
        {
            var c = Color(Value(kind));
            if (letter.Foreground is not SolidColorBrush b || b.Color != c) letter.Foreground = new SolidColorBrush(c);
        }
    }
}

/// <summary>Per kind of action: Toestaan, Eerst vragen or Weigeren.</summary>
public sealed partial class RightsMatrix : StackPanel
{
    private readonly Action changed;
    private readonly List<Action> refresh = [];

    public RightsMatrix(Action changed)
    {
        this.changed = changed;
        Spacing = 12;
        Children.Add(Ui.Label(L("Wat mag het model in de werkmap?"), 15, null, FontWeights.SemiBold, display: true));
        foreach (var kind in Enum.GetValues<WorkspaceRight>()) Children.Add(Line(kind));
        var s = Settings.Shared;
        var standard = Ui.Btn(L("Standaard"), () => Set("allow", "ask", "ask")).Tip(L("Lezen mag, schrijven en uitvoeren eerst vragen"));
        var all = Ui.Btn(L("Alles toestaan"), () => Set("allow", "allow", "allow"));
        Children.Add(Ui.Row("auto,auto,*,auto", 8, standard, all, null, Ui.Label(L("Geldt voor alle gesprekken"), 12, Theme.Secondary)));
        void Set(string read, string write, string run)
        {
            s.RightRead = read;
            s.RightWrite = write;
            s.RightRun = run;
            Show();
        }
        Show();
    }

    private void Show()
    {
        foreach (var r in refresh) r();
        changed();
    }

    private Grid Line(WorkspaceRight kind)
    {
        var letter = Ui.Label(kind.Letter(), 18, null, FontWeights.ExtraBold, display: true);
        letter.Width = 18;
        var text = Ui.V(1, Ui.Label(kind.Title(), 14, null, FontWeights.SemiBold), Ui.Label(kind.Detail(), 12, Theme.Secondary));
        var parts = new[] { (L("Toestaan"), "allow"), (L("Eerst vragen"), "ask"), (L("Weigeren"), "deny") }.Select(o =>
        {
            var b = new Button { Content = o.Item1, FontSize = 12, Padding = new Thickness(10, 4, 10, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch };
            Automation.SetName(b, $"{kind.Title()}: {o.Item1}");
            b.Click += (_, _) =>
            {
                var s = Settings.Shared;
                if (kind == WorkspaceRight.Read) s.RightRead = o.Item2;
                else if (kind == WorkspaceRight.Write) s.RightWrite = o.Item2;
                else s.RightRun = o.Item2;
                Show();
            };
            return (Button: b, Value: o.Item2);
        }).ToList();
        refresh.Add(() =>
        {
            var now = RightsButton.Value(kind);
            letter.Foreground = new SolidColorBrush(RightsButton.Color(now));
            foreach (var (b, value) in parts)
            {
                bool on = value == now;
                b.Background = on ? Theme.Solid(RightsButton.Color(value), 0.28) : Theme.Solid(Colors.White, 0.06);
                b.Foreground = on ? new SolidColorBrush(RightsButton.Color(value)) : Theme.Secondary;
            }
        });
        var segment = Ui.Row("auto,auto,auto", 2, parts[0].Button, parts[1].Button, parts[2].Button);
        return Ui.Row("auto,*,auto", 14, letter, text, segment);
    }
}
