using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Hulp: the topics on the left with a search field, the chosen topic on the right. Dutch, or English for the other languages.</summary>
public sealed class HelpPage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly IReadOnlyList<HelpTopic> topics = HelpCenter.Topics();
    private readonly TextBox search = new() { PlaceholderText = L("Zoek in de hulp…") };
    private readonly StackPanel list = Ui.V(2);
    private readonly TextBlock nothing = Ui.Caption(L("Niets gevonden. Probeer een ander woord."));
    private readonly StackPanel body = Ui.V(14);
    private readonly ScrollViewer scroll;
    private string current = "start";

    public HelpPage(AppState app)
    {
        this.app = app;
        current = app.HelpTopic;
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            // the best hit opens by itself
            if (q.Length > 0 && HelpCenter.Search(q, topics).FirstOrDefault() is { } best) current = best.Id;
            Fill();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, L("Zoek in de hulp…"));
        var left = Ui.V(10, Ui.H(8, Ui.Icon(Glyph.Help, 22, Theme.Brand), Ui.Label(L("Hulp"), 26, null, FontWeights.Bold, display: true)), search,
            new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, nothing);
        left.Width = 300;
        left.Padding = new Thickness(Theme.PagePadding, 4, 16, Theme.PagePadding);
        body.Padding = new Thickness(28, 4, Theme.PagePadding, Theme.PagePadding);
        body.MaxWidth = 860;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var line = new Border { Width = 1, Background = Theme.Solid(Microsoft.UI.Colors.White, 0.08) };
        View = Ui.Row("auto,auto,*", 0, left, line, scroll);
        app.HelpRequested += () =>
        {
            current = app.HelpTopic;
            search.Text = "";
            Fill();
        };
        Fill();
    }

    private void Fill()
    {
        var shown = HelpCenter.Search(search.Text, topics);
        list.Children.Clear();
        foreach (var t in shown)
        {
            var id = t.Id;
            bool on = id == current;
            var b = new Button
            {
                Content = Ui.Row("auto,*", 10, Ui.Icon(IconOf(t.Icon), 15, on ? Theme.Brand : Theme.Secondary), Ui.Label(t.Title, 14, null, on ? FontWeights.SemiBold : null, wrap: true)),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = on ? Theme.Solid(Microsoft.UI.Colors.White, 0.10) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, t.Title);
            b.Click += (_, _) => { current = id; Fill(); };
            list.Children.Add(b);
        }
        nothing.Show(shown.Count == 0);

        body.Children.Clear();
        var topic = topics.FirstOrDefault(t => t.Id == current) ?? topics.FirstOrDefault();
        if (topic == null)
        {
            body.Children.Add(Ui.Label(L("De hulpteksten ontbreken in deze versie van de app."), 15, wrap: true));
            return;
        }
        body.Children.Add(Ui.H(10, Ui.Icon(IconOf(topic.Icon), 26, Theme.Brand), Ui.Label(topic.Title, 28, null, FontWeights.Bold, wrap: true, display: true)));
        foreach (var block in HelpCenter.Blocks(topic.Body))
        {
            switch (block.Kind)
            {
                case HelpCenter.BlockKind.Heading:
                    var h = Ui.Label(block.Text, 19, null, FontWeights.SemiBold, wrap: true, display: true);
                    h.Margin = new Thickness(0, 6, 0, 0);
                    body.Children.Add(h);
                    break;
                case HelpCenter.BlockKind.Tip:
                    var tip = Ui.Rounded(Theme.Solid(Theme.Amber, 0.10), 8, Ui.Row("auto,*", 8, Ui.Icon(Glyph.Lightbulb, 15, Theme.Solid(Theme.Amber)), Paragraph(block.Text)));
                    tip.Padding = new Thickness(10);
                    body.Children.Add(tip);
                    break;
                default:
                    foreach (var part in Parts(block.Text)) body.Children.Add(part);
                    break;
            }
        }
        scroll.ChangeView(null, 0, null, true);
    }

    /// <summary>Paragraphs (split at blank lines) and fenced code blocks.</summary>
    private static IEnumerable<UIElement> Parts(string text)
    {
        var lines = new List<string>();
        List<string>? code = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (code == null)
                {
                    if (lines.Count > 0) { yield return Paragraph(string.Join("\n", lines)); lines.Clear(); }
                    code = [];
                }
                else
                {
                    var box = Ui.Rounded(Theme.Solid(Microsoft.UI.Colors.Black, 0.35), 8,
                        new TextBlock { Text = string.Join("\n", code), FontFamily = Theme.Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                    box.Padding = new Thickness(10);
                    yield return box;
                    code = null;
                }
                continue;
            }
            if (code != null) { code.Add(line); continue; }
            if (line.Trim().Length == 0)
            {
                if (lines.Count > 0) { yield return Paragraph(string.Join("\n", lines)); lines.Clear(); }
                continue;
            }
            lines.Add(line);
        }
        if (lines.Count > 0) yield return Paragraph(string.Join("\n", lines));
    }

    private static TextBlock Paragraph(string text)
    {
        var t = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 23, Foreground = Theme.Primary };
        foreach (var r in MarkdownLite.Inline(text))
        {
            var run = new Run { Text = r.Text };
            if (r.Bold) run.FontWeight = FontWeights.SemiBold;
            if (r.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (r.Code) { run.FontFamily = Theme.Mono; run.Foreground = new SolidColorBrush(Theme.Cyan); }
            t.Inlines.Add(run);
        }
        return t;
    }

    /// <summary>The icon a topic names in its front matter.</summary>
    private static string IconOf(string name) => name switch
    {
        "Home" => Glyph.Home,
        "Chat" => Glyph.Chat,
        "Folder" => Glyph.Folder,
        "Globe" => Glyph.Globe,
        "Library" => Glyph.Library,
        "Speed" => Glyph.Speed,
        "Image" => Glyph.Image,
        "Document" => Glyph.Document,
        "Compare" => Glyph.Compare,
        "Network" => Glyph.Network,
        "Info" => Glyph.Info,
        "Lightbulb" => Glyph.Lightbulb,
        "Shield" => Glyph.Shield,
        "Code" => Glyph.Code,
        "Settings" => Glyph.Settings,
        "Keyboard" => Glyph.Keyboard,
        "Lock" => Glyph.Lock,
        "Repair" => Glyph.Repair,
        "Connect" => Glyph.Connect,
        "Tasks" => Glyph.Tasks,
        _ => Glyph.Help,
    };
}
