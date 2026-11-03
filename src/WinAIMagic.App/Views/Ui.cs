using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using static WinAIMagic.Core.Support.Lang;
using FontWeight = Windows.UI.Text.FontWeight;

namespace WinAIMagic.App.Views;

/// <summary>A screen of the app: one constructor argument (AppState), and what it shows.</summary>
public interface IPage
{
    FrameworkElement View { get; }
}

/// <summary>The small factories every screen is built from; no XAML.</summary>
public static class Ui
{
    public static TextBlock Label(string text = "", double size = 14, Brush? color = null, FontWeight? weight = null, bool wrap = false, bool display = false, bool mono = false, bool select = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = mono ? Theme.Mono : display ? Theme.Display : Theme.Text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = select,
        };
        if (color != null) t.Foreground = color;
        if (weight != null) t.FontWeight = weight.Value;
        return t;
    }

    public static TextBlock Caption(string text = "", Brush? color = null, double size = 12) => Label(text, size, color ?? Theme.Secondary, wrap: true);
    public static TextBlock Bold(string text = "", double size = 14, Brush? color = null) => Label(text, size, color, FontWeights.SemiBold);

    public static FontIcon Icon(string glyph, double size = 14, Brush? color = null)
    {
        var i = new FontIcon { Glyph = glyph, FontSize = size, FontFamily = Theme.Icons };
        if (color != null) i.Foreground = color;
        return i;
    }

    public static StackPanel V(double spacing, params UIElement?[] kids) => Stack(Orientation.Vertical, spacing, kids);
    public static StackPanel H(double spacing, params UIElement?[] kids) => Stack(Orientation.Horizontal, spacing, kids);

    private static StackPanel Stack(Orientation o, double spacing, UIElement?[] kids)
    {
        var s = new StackPanel { Orientation = o, Spacing = spacing };
        foreach (var k in kids) if (k != null) s.Children.Add(k);
        return s;
    }

    /// <summary>A row with its columns as "auto,*,auto" (or a number of pixels); the kids go in the columns in turn.</summary>
    public static Grid Row(string columns, double spacing, params UIElement?[] kids)
    {
        var g = new Grid { ColumnSpacing = spacing };
        foreach (var c in columns.Split(','))
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c == "auto" ? GridLength.Auto : c == "*" ? new GridLength(1, GridUnitType.Star) : new GridLength(double.Parse(c)) });
        for (int i = 0; i < kids.Length; i++)
        {
            if (kids[i] is not { } kid) continue;
            Grid.SetColumn((FrameworkElement)kid, i);
            g.Children.Add(kid);
        }
        return g;
    }

    public static T At<T>(this T e, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : FrameworkElement
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        Grid.SetRowSpan(e, rowSpan);
        Grid.SetColumnSpan(e, columnSpan);
        return e;
    }

    public static T Show<T>(this T e, bool visible) where T : UIElement
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (e.Visibility != v) e.Visibility = v;
        return e;
    }

    public static T Tip<T>(this T e, string? text) where T : DependencyObject
    {
        ToolTipService.SetToolTip(e, string.IsNullOrEmpty(text) ? null : text);
        return e;
    }

    public static void SetIfChanged(this TextBlock t, string text)
    {
        if (t.Text != text) t.Text = text;
    }

    public static LinearGradientBrush CardEdge(Color glow) =>
        Theme.Gradient(new Point(0, 0), new Point(1, 1), Theme.Alpha(glow, 0.55), Theme.Alpha(Colors.White, 0.06), Theme.Alpha(glow, 0.15));

    private static LinearGradientBrush CardFill(Color glow) =>
        Theme.Gradient(new Point(0, 0), new Point(1, 1), Theme.Alpha(glow, 0.09), Theme.Alpha(Colors.White, 0.045));

    /// <summary>GlassCard: a translucent card with a soft gradient edge; radius 18, padding 18.</summary>
    public static Border Card(UIElement? content, string? title = null, string? icon = null, Color? glow = null, double spacing = 12)
    {
        var color = glow ?? Theme.Accent;
        var body = new StackPanel { Spacing = spacing };
        if (title != null)
        {
            var head = H(8);
            if (icon != null) head.Children.Add(IconTile(icon, color));
            head.Children.Add(Label(title, 15, null, FontWeights.SemiBold, display: true));
            body.Children.Add(head);
        }
        if (content != null) body.Children.Add(content);
        return new Border
        {
            Child = body,
            Padding = new Thickness(Theme.CardPadding),
            CornerRadius = new CornerRadius(Theme.CardRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = CardEdge(color),
            Background = CardFill(color),
        };
    }

    public static Border IconTile(string glyph, Color color, double size = 24) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(7),
        Background = Theme.Solid(color, 0.15),
        Child = Icon(glyph, size * 0.54, Theme.Solid(color)),
    };

    /// <summary>A button: plain, or filled in `tint`.</summary>
    public static Button Btn(string text, Action click, string? icon = null, Color? tint = null)
    {
        var b = new Button { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        var content = H(7);
        if (icon != null) content.Children.Add(Icon(icon, 13));
        if (text.Length > 0) content.Children.Add(new TextBlock { Text = text, FontSize = 13, FontFamily = Theme.Text });
        b.Content = content;
        if (tint is { } c)
        {
            Color on = Theme.OnColor(c), hover = Theme.Along(0.18, c, Colors.White), down = Theme.Along(0.18, c, Theme.Night);
            b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            b.Resources["AccentButtonBackground"] = new SolidColorBrush(c);
            b.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
            b.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(down);
            b.Resources["AccentButtonForeground"] = b.Resources["AccentButtonForegroundPointerOver"] = b.Resources["AccentButtonForegroundPressed"] = new SolidColorBrush(on);
            b.Background = new SolidColorBrush(c);
            b.Foreground = new SolidColorBrush(on);
        }
        b.Click += (_, _) => click();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    public static Button IconBtn(string glyph, Action click, string? tip = null)
    {
        var b = new Button { Content = Icon(glyph, 14), Width = 34, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip ?? "");
        return b.Tip(tip);
    }

    public static Border Badge(string text, string? icon = null, Color? color = null)
    {
        Brush fg = color is { } c ? Theme.Solid(c) : Theme.Secondary;
        var row = H(4);
        if (icon != null) row.Children.Add(Icon(icon, 11, fg));
        row.Children.Add(Label(text, 12, fg, FontWeights.Medium));
        return new Border { Child = row, Padding = new Thickness(8, 3, 8, 4), CornerRadius = new CornerRadius(11), Background = Theme.Solid(color ?? Colors.White, 0.14) };
    }

    /// <summary>A page: padding 28, sections 18. The title bar above it already gives room at the top.</summary>
    public static ScrollViewer Page(params UIElement?[] sections)
    {
        var body = V(Theme.SectionSpacing, sections);
        body.Padding = new Thickness(Theme.PagePadding, 4, Theme.PagePadding, Theme.PagePadding);
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>The head of a page: mark, big title, the line under it, and what stands right.</summary>
    public static Grid PageHead(UIElement mark, string title, UIElement subtitle, params UIElement?[] right)
    {
        var r = H(10, right);
        r.VerticalAlignment = VerticalAlignment.Center;
        return Row("auto,*,auto", 14, mark, V(0, Label(title, 30, null, FontWeights.Bold, display: true), subtitle), r);
    }

    public static FontIcon PageIcon(string glyph) => Icon(glyph, 28, Theme.Brand);

    public static Border Rounded(Brush fill, double radius, UIElement? child = null) => new() { Background = fill, CornerRadius = new CornerRadius(radius), Child = child };

    public static Border Divider() => new() { Height = 1, Background = Theme.Hairline };

    /// <summary>A question in a dialog; the window has at most one open at a time.</summary>
    public static async Task<ContentDialogResult> Ask(AppState app, string title, object body, string? primary, string? secondary = null, string? close = null, bool danger = false)
    {
        var dialog = Dialog(app);
        dialog.Title = title;
        dialog.Content = body is string s ? Caption(s, Theme.Primary, 14) : body;
        dialog.PrimaryButtonText = primary ?? "";
        dialog.SecondaryButtonText = secondary ?? "";
        dialog.CloseButtonText = close ?? L("Annuleren");
        dialog.DefaultButton = danger ? ContentDialogButton.Close : ContentDialogButton.Primary;
        return await dialog.ShowAsync();
    }

    /// <summary>A dialog in the colours of the app.</summary>
    public static ContentDialog Dialog(AppState app)
    {
        var d = new ContentDialog
        {
            XamlRoot = app.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Theme.Rgb(0x16, 0x18, 0x2A)),
            BorderBrush = Theme.Solid(Theme.Accent, 0.35),
        };
        d.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Theme.Rgb(0x16, 0x18, 0x2A));
        d.Resources["ContentDialogSeparatorBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        return d;
    }

    public static string Duration(double seconds) => seconds > 3600
        ? L($"{Math.Floor(seconds / 3600):0} u {seconds % 3600 / 60:0} min")
        : L($"{seconds / 60:0} min");

    public static void Copy(string text)
    {
        var p = new Windows.ApplicationModel.DataTransfer.DataPackage();
        p.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(p);
    }
}
