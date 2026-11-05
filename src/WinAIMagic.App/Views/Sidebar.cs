using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Logo, name and chosen model; the screens; the conversations; the mini dashboard.</summary>
public sealed partial class Sidebar : Grid
{
    private readonly AppState app;
    private readonly BrandMark mark = new(30);
    private readonly TextBlock modelName = Ui.Label("", 12, Theme.Secondary);
    private readonly StackPanel chats = new() { Spacing = 2 };
    private readonly Dictionary<PaneKind, (Button Button, FontIcon Icon)> nav = new();
    private readonly Dictionary<Guid, (Button Button, TextBlock Title, TextBlock Time)> chatRows = new();
    private string chatOrder = "";
    private Pane pane;

    public Sidebar(AppState app)
    {
        this.app = app;
        Background = Theme.Solid(Theme.Night, 0.42);
        BorderBrush = Theme.Hairline;
        BorderThickness = new Thickness(0, 0, 1, 0);
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            RowDefinitions.Add(new RowDefinition { Height = h });

        var name = Ui.Label("WinAIMagic", 15, null, FontWeights.Bold, display: true);
        var version = Ui.Label("v" + Paths.AppVersion, 11, Theme.Secondary);
        version.VerticalAlignment = VerticalAlignment.Bottom;
        version.Margin = new Thickness(0, 0, 0, 2);
        var head = Ui.Row("auto,*", 10, mark, Ui.V(0, Ui.H(6, name, version), modelName));
        head.Margin = new Thickness(18, 10, 12, 10);
        Children.Add(head.At(0));

        var list = Ui.V(2);
        list.Margin = new Thickness(10, 2, 10, 8);
        foreach (var (kind, title, glyph) in new[]
        {
            (PaneKind.Live, L("Live"), Glyph.Live), (PaneKind.Models, L("Modellen"), Glyph.Models), (PaneKind.Documents, L("Documenten"), Glyph.Document),
            (PaneKind.Images, L("Beeld"), Glyph.Image),
            (PaneKind.Compare, L("Vergelijken"), Glyph.Compare), (PaneKind.Cluster, L("Cluster"), Glyph.Cluster), (PaneKind.System, L("Systeem"), Glyph.System),
        })
        {
            var icon = Ui.Icon(glyph, 15, Theme.Secondary);
            var b = Row(Ui.H(11, icon, Ui.Label(title, 14)), () => app.Pane = new Pane(kind));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title);
            nav[kind] = (b, icon);
            list.Children.Add(b);
        }
        Children.Add(list.At(1));

        var header = Ui.Label(L("Gesprekken"), 12, Theme.Tertiary, FontWeights.SemiBold);
        header.Margin = new Thickness(20, 6, 8, 6);
        Children.Add(header.At(2));
        chats.Margin = new Thickness(10, 0, 10, 8);
        Children.Add(new ScrollViewer { Content = chats, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.At(3));

        var dashboard = new MiniDashboard(app);
        dashboard.Margin = new Thickness(10, 4, 10, 10);
        Children.Add(dashboard.At(4));

        app.Watch(this, Update);
        app.WatchSamples(this, () => mark.Active = app.TokensPerSec > 0);
    }

    private static Button Row(UIElement content, Action click)
    {
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void Update()
    {
        modelName.SetIfChanged(app.Host.SelectedModel?.Name ?? "");
        var list = app.Store.Conversations;
        var order = string.Join(",", list.Select(c => c.Id));
        if (order != chatOrder)
        {
            chatOrder = order;
            chats.Children.Clear();
            chatRows.Clear();
            foreach (var c in list)
            {
                var id = c.Id;
                TextBlock title = Ui.Label("", 13), time = Ui.Label("", 11, Theme.Tertiary);
                var row = Row(Ui.V(1, title, time), () => app.Pane = new Pane(PaneKind.Chat, id));
                ((StackPanel)row.Content).MaxWidth = 210;
                var menu = new MenuFlyout();
                var delete = new MenuFlyoutItem { Text = L("Verwijderen"), Icon = Ui.Icon(Glyph.Trash) };
                delete.Click += (_, _) => Delete(id);
                menu.Items.Add(delete);
                row.ContextFlyout = menu;
                chatRows[id] = (row, title, time);
                chats.Children.Add(row);
            }
            Mark(pane);
        }
        foreach (var c in list)
        {
            if (!chatRows.TryGetValue(c.Id, out var row)) continue;
            if (row.Title.Text != c.DisplayTitle) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Button, c.DisplayTitle);
            row.Title.SetIfChanged(c.DisplayTitle);
            row.Time.SetIfChanged(Ago(c.Updated));
        }
    }

    private void Delete(Guid id)
    {
        bool open = pane.Conversation == id;
        app.Store.Delete(id);
        if (open && app.Store.Selection is { } next) app.Pane = new Pane(PaneKind.Chat, next);
    }

    /// <summary>Which row is lit.</summary>
    public void Mark(Pane now)
    {
        pane = now;
        Brush on = Theme.Solid(Theme.Accent, 0.24), off = new SolidColorBrush(Colors.Transparent);
        foreach (var (kind, (button, icon)) in nav)
        {
            bool lit = now.Kind == kind;
            button.Background = lit ? on : off;
            icon.Foreground = lit ? new SolidColorBrush(Theme.Rgb(0x9F, 0xB1, 0xFF)) : Theme.Secondary;
        }
        foreach (var (id, row) in chatRows)
            row.Button.Background = now.Kind == PaneKind.Chat && now.Conversation == id ? on : off;
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalMinutes < 1) return L("zojuist");
        if (d.TotalHours < 1) return L($"{(int)d.TotalMinutes} min geleden");
        if (d.TotalDays < 1) return L($"{(int)d.TotalHours} uur geleden");
        if (d.TotalDays < 2) return L("gisteren");
        return d.TotalDays < 30 ? L($"{(int)d.TotalDays} dagen geleden") : t.LocalDateTime.ToString("d", AppLanguages.Current.Culture());
    }
}

/// <summary>The live dashboard in miniature: speed gauge and a sparkline of every metric.</summary>
public sealed partial class MiniDashboard : Grid
{
    private readonly AppState app;
    private readonly GaugeRing gauge = new(60, "", "tok/s", 54);
    private readonly MiniSpark speed = new(Theme.Violet, 36);
    private readonly (MiniSpark Spark, TextBlock Value)[] rows;

    public MiniDashboard(AppState app)
    {
        this.app = app;
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Brush rest = Ui.CardEdge(Theme.Violet), hover = Theme.Solid(Theme.Violet, 0.8);
        BorderBrush = rest;
        Background = Theme.Solid(Colors.White, 0.05);
        (string Label, Color C)[] metrics = [("GPU", Theme.Accent), ("VRAM", Theme.Violet), ("CPU", Theme.Cyan), ("RAM", Theme.Pink), ("SSD", Theme.Amber)];
        var body = Ui.V(5, Ui.Row("auto,*", 10, gauge, speed));
        rows = metrics.Select(m =>
        {
            var spark = new MiniSpark(m.C, 15);
            var value = Ui.Label("", 10, Theme.Primary, FontWeights.SemiBold);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.TextAlignment = TextAlignment.Right;
            value.FontFamily = Theme.Display;
            body.Children.Add(Ui.Row("30,*,48", 6, Ui.Label(m.Label, 9, Theme.Solid(m.C, 0.9), FontWeights.Bold), spark, value));
            return (spark, value);
        }).ToArray();
        speed.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(body);
        this.Tip(L("Klik voor het volledige live dashboard"));
        Tapped += (_, _) => app.Pane = new Pane(PaneKind.Live);
        PointerEntered += (_, _) => BorderBrush = hover;
        PointerExited += (_, _) => BorderBrush = rest;
        app.WatchSamples(this, Update);
    }

    private void Update()
    {
        var all = app.Host.Metrics.Samples;
        var recent = all.Skip(Math.Max(all.Count - 80, 0)).ToList();
        var now = recent.LastOrDefault();
        gauge.Set(now?.TokensPerSec ?? 0);
        speed.Set(recent.Select(s => s.TokensPerSec).ToList(), null);
        Row(0, recent.Select(s => s.Gpu), 100, $"{now?.Gpu ?? 0:0}%");
        double k = ByteFormat.GibPerGb;
        Row(1, recent.Select(s => s.VramUsedGB), now?.VramTotalGB is > 0 ? now.VramTotalGB : null, $"{(now?.VramUsedGB ?? 0) * k:0} GB");
        Row(2, recent.Select(s => s.Cpu), 100, $"{now?.Cpu ?? 0:0}%");
        Row(3, recent.Select(s => s.RamUsedGB), now?.RamTotalGB is > 0 ? now.RamTotalGB : null, $"{(now?.RamUsedGB ?? 0) * k:0} GB");
        Row(4, recent.Select(s => s.SsdMBs), null, Disk(now?.SsdMBs ?? 0));
    }

    private void Row(int i, IEnumerable<double> values, double? max, string text)
    {
        rows[i].Spark.Set(values.ToList(), max);
        rows[i].Value.SetIfChanged(text);
    }

    public static string Disk(double mbs) => mbs >= 1000 ? $"{mbs / 1000:0.0} GB/s" : $"{mbs:0} MB/s";
}
