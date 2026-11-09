using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Small parts of the chat.</summary>
internal static class ChatUi
{
    public static Button Plain(UIElement content, Action click, string? tip = null)
    {
        var b = new Button { Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return b.Tip(tip);
    }

    public static Border Capsule(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(10, 5, 10, 6), CornerRadius = new CornerRadius(14), Background = Theme.Solid(Colors.White, 0.07), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Only what a browser should open: a page or a text from the web cannot start anything else.</summary>
    public static Uri? WebLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto) ? u : null;

    /// <summary>A flyout with room for its content, in the colours of the app.</summary>
    public static Flyout Popover(FrameworkElement content, double maxWidth = 460)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, maxWidth));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Dark));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Theme.Rgb(0x16, 0x18, 0x2A))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Solid(Theme.Accent, 0.35)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(14)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        return new Flyout { Content = content, FlyoutPresenterStyle = style };
    }

    /// <summary>A menu in the colours of the app.</summary>
    public static void Dress(MenuFlyout menu)
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Dark));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Theme.Rgb(0x16, 0x18, 0x2A))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Solid(Theme.Accent, 0.35)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
        menu.MenuFlyoutPresenterStyle = style;
    }

    /// <summary>A picture from JPEG bytes, cut to the rounded square of `size`.</summary>
    public static Border Thumb(byte[] jpeg, double size, double radius)
    {
        var image = new BitmapImage { DecodePixelWidth = (int)(size * 2), DecodePixelType = DecodePixelType.Logical };
        var fill = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill } };
        Load(image, jpeg);
        return fill;
    }

    private static async void Load(BitmapImage image, byte[] jpeg)
    {
        try
        {
            using var s = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await s.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(jpeg));
            s.Seek(0);
            await image.SetSourceAsync(s);
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { App.Log(e); }
    }
}

/// <summary>What a row keeps when it leaves the screen and comes back.</summary>
internal sealed class RowMemory
{
    /// <summary>Steps: the earlier parts of a working answer are shown; Work: the folded work of a finished answer is open.</summary>
    public bool Steps, Work, Summary;
    public readonly HashSet<Guid> OpenSteps = [];
    public readonly HashSet<string> OpenThoughts = [];
}

/// <summary>"Hierboven samengevat": where a conversation was compacted, an amber line that opens.</summary>
public sealed partial class SummaryRow : StackPanel
{
    public SummaryRow(string summary, Action<double>? shifted, Func<bool> isOpen, Action<bool> setOpen)
    {
        Spacing = 8;
        Brush amber = new SolidColorBrush(Theme.Amber);
        Border Rule() => new() { Height = 1, Background = Theme.Solid(Theme.Amber, 0.4), VerticalAlignment = VerticalAlignment.Center };
        var open = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = Theme.Solid(Colors.White, 0.05), Visibility = Visibility.Collapsed };
        void Show(bool on)
        {
            open.Show(on);
            if (on && open.Child == null)
            {
                var md = new MarkdownView(shifted, false, 13);
                md.Set(summary);
                open.Child = md;
            }
        }
        var button = ChatUi.Plain(Ui.H(6, Ui.Icon(Glyph.Compress, 12, amber), Ui.Label(L("Hierboven samengevat om context vrij te maken"), 12, amber, FontWeights.SemiBold)), () =>
        {
            setOpen(!isOpen());
            Show(isOpen());
        });
        button.Padding = new Thickness(6, 2, 6, 3);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L("Hierboven samengevat om context vrij te maken"));
        Children.Add(Ui.Row("*,auto,*", 8, Rule(), button, Rule()));
        Children.Add(open);
        Show(isOpen());
    }
}

/// <summary>A question of the user (MessageRow in ChatView.swift). The model's answer is an <see cref="AnswerRow"/>.</summary>
public sealed partial class MessageRow : Grid
{
    internal MessageRow(ChatMessage m, Action<double>? shifted)
    {
        var body = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(80, 0, 0, 0) };
        if (m.Attachments.Count > 0)
        {
            var pics = Ui.H(8);
            pics.HorizontalAlignment = HorizontalAlignment.Right;
            foreach (var a in m.Attachments) pics.Children.Add(ChatUi.Thumb(a.Jpeg, 140, 10));
            body.Children.Add(pics);
        }
        if (m.Text.Length > 0)
        {
            // a very long paste goes in pages, like an answer
            UIElement text;
            if (m.Text.Length < 4000) text = new TextBlock { Text = m.Text, FontSize = 14, FontFamily = Theme.Text, LineHeight = 22, Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            else
            {
                var pages = new MarkdownView(shifted, true, 14, new SolidColorBrush(Colors.White));
                pages.Set(m.Text);
                text = pages;
            }
            body.Children.Add(new Border
            {
                Child = text, Padding = new Thickness(16, 11, 16, 11), CornerRadius = new CornerRadius(18), HorizontalAlignment = HorizontalAlignment.Right,
                Background = Theme.UserBubble, BorderBrush = Theme.Solid(Colors.White, 0.15), BorderThickness = new Thickness(1),
            });
        }
        Children.Add(body);
    }
}

/// <summary>A small turning arc; its storyboard only runs while it is active.</summary>
public sealed partial class Spinner : Grid
{
    private readonly Storyboard turn = new();
    private bool active;

    public Spinner(double size, bool start = true)
    {
        Width = Height = size;
        var rotate = new RotateTransform { CenterX = size / 2, CenterY = size / 2 };
        Children.Add(new Ellipse { Stroke = Theme.Solid(Colors.White, 0.15), StrokeThickness = 1.6 });
        Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Draw.Arc(size / 2, size / 2, size / 2 - 0.8, 0, 0.3), Stroke = new SolidColorBrush(Theme.Cyan), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = rotate });
        var a = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(0.9), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, rotate);
        Storyboard.SetTargetProperty(a, "Angle");
        turn.Children.Add(a);
        active = start;
        this.Show(start);
        Loaded += (_, _) => { if (active) turn.Begin(); };
        Unloaded += (_, _) => turn.Stop();
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            this.Show(value);
            if (value) turn.Begin();
            else turn.Stop();
        }
    }
}

/// <summary>A pulsing ring around the assistant's mark while it writes; nothing at all otherwise.</summary>
public sealed partial class PulseRing : Grid
{
    private readonly Storyboard pulse = new();
    private readonly Ellipse ring;
    private bool active;

    public PulseRing(double size)
    {
        Width = Height = size;
        var scale = new ScaleTransform { CenterX = size / 2, CenterY = size / 2 };
        ring = new Ellipse { Stroke = Theme.Brand, StrokeThickness = 2, RenderTransform = scale, Opacity = 0, IsHitTestVisible = false };
        Children.Add(ring);
        void Animate(DependencyObject target, string property, double from, double to)
        {
            var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, property);
            pulse.Children.Add(a);
        }
        Animate(scale, "ScaleX", 1, 1.45);
        Animate(scale, "ScaleY", 1, 1.45);
        Animate(ring, "Opacity", 0.9, 0);
        Unloaded += (_, _) => Active = false;
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value) pulse.Begin();
            else
            {
                pulse.Stop();
                ring.Opacity = 0;
            }
        }
    }
}

/// <summary>A title with a chevron that opens what stands under it.</summary>
public sealed partial class Disclosure : StackPanel
{
    private readonly FontIcon chevron = Ui.Icon(Glyph.ChevronRight, 10, Theme.Tertiary);
    private readonly FontIcon icon;
    private readonly TextBlock title = Ui.Label("", 13, Theme.Secondary, FontWeights.Medium);
    private readonly Storyboard shimmer = new();
    private readonly Action<bool> changed;
    private readonly StackPanel titleRow;
    private UIElement? content;
    private bool open, shining;

    public Disclosure(string glyph, bool startOpen, Action<bool> changed)
    {
        this.changed = changed;
        icon = Ui.Icon(glyph, 13, Theme.Secondary);
        titleRow = Ui.H(6, chevron, icon, title);
        var button = ChatUi.Plain(titleRow, () => SetOpen(!open));
        button.Padding = new Thickness(2, 1, 6, 2);
        Children.Add(button);
        var a = new DoubleAnimation { From = 1, To = 0.45, Duration = TimeSpan.FromSeconds(0.7), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, titleRow);
        Storyboard.SetTargetProperty(a, "Opacity");
        shimmer.Children.Add(a);
        Unloaded += (_, _) => { if (shining) { shining = false; shimmer.Stop(); } };
        if (startOpen) SetOpen(true);
    }

    public UIElement? Content
    {
        get => content;
        set
        {
            if (content != null) Children.Remove(content);
            content = value;
            if (value != null && open) Children.Add(value);
        }
    }

    public void SetTitle(string text, bool shine)
    {
        title.SetIfChanged(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName((Button)Children[0], text);
        if (shine == shining) return;
        shining = shine;
        title.Foreground = shine ? new SolidColorBrush(Theme.Rgb(0xB9, 0xA6, 0xFF)) : Theme.Secondary;
        icon.Foreground = shine ? new SolidColorBrush(Theme.Rgb(0xB9, 0xA6, 0xFF)) : Theme.Secondary;
        if (shine) shimmer.Begin();
        else shimmer.Stop();
    }

    public void SetOpen(bool on)
    {
        open = on;
        chevron.Glyph = on ? Glyph.ChevronDown : Glyph.ChevronRight;
        changed(on);
        if (content == null) return;
        if (on && !Children.Contains(content)) Children.Add(content);
        else if (!on) Children.Remove(content);
    }
}

/// <summary>One tool step; opens to show what the tool returned.</summary>
public sealed partial class ActivityRow : StackPanel
{
    internal ActivityRow(ToolActivity a, RowMemory memory)
    {
        Spacing = 4;
        UIElement mark = a.Denied == true ? Ui.Icon(Glyph.Blocked, 13, new SolidColorBrush(Theme.Amber))
            : a.Done ? Ui.Icon(Glyph.Done, 13, new SolidColorBrush(Theme.Mint))
            : new Spinner(13);
        var chevron = Ui.Icon(Glyph.ChevronRight, 9, Theme.Tertiary).Show(a.Output != null);
        var label = Ui.Label(a.Label, 13, Theme.Secondary);
        var row = Ui.Row("auto,*,auto", 6, mark, label, chevron);
        Border? output = null;
        void Show(bool on)
        {
            chevron.Glyph = on ? Glyph.ChevronDown : Glyph.ChevronRight;
            if (on && output == null && a.Output != null)
            {
                output = new Border
                {
                    CornerRadius = new CornerRadius(8), Background = Theme.Solid(Colors.Black, 0.35),
                    Child = new ScrollViewer
                    {
                        MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, ZoomMode = ZoomMode.Disabled,
                        Content = new TextBlock { Text = a.Output, FontFamily = Theme.Mono, FontSize = 12, IsTextSelectionEnabled = true, Margin = new Thickness(8) },
                    },
                };
                Children.Add(output);
            }
            output?.Show(on);
        }
        var button = ChatUi.Plain(row, () =>
        {
            if (a.Output == null) return;
            bool on = !memory.OpenSteps.Remove(a.Id);
            if (on) memory.OpenSteps.Add(a.Id);
            Show(on);
        });
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(2, 1, 4, 2);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, a.Label);
        Children.Add(button);
        Show(memory.OpenSteps.Contains(a.Id));
    }
}
