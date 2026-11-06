using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Measures every downloaded model the same way and puts them side by side.</summary>
public sealed class ComparePage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly HashSet<string> selection = [];
    private readonly StackPanel choices = Ui.V(8);
    private readonly TextBlock none = Ui.Label(L("Nog geen model gedownload."), 14, Theme.Secondary);
    private readonly Button start, clear, export, share;
    private const string ShareUrl = "https://github.com/mfidder2026/WinAIMagic/issues/new?template=meting.yml";
    private readonly FrameworkElement idle, working;
    private readonly ProgressRing ring = new() { Width = 16, Height = 16 };
    private readonly TextBlock step = Ui.Label("", 14, Theme.Secondary), saved = Ui.Caption();
    private readonly BarChart write, read, load, ssd, imageTime, imageTokens;
    private readonly List<FrameworkElement> textCards = new(), imageCards = new();
    private readonly FrameworkElement results;
    private readonly Grid table = new() { ColumnSpacing = 18, RowSpacing = 8 };
    private string shownReady = "?", shownResults = "?";

    public ComparePage(AppState app)
    {
        this.app = app;
        start = Ui.Btn(L("Start meting"), Run, Glyph.Play, Theme.Violet);
        clear = Ui.Btn(L("Wis resultaten"), () => app.Host.Bench.Clear(), Glyph.Trash);
        export = Ui.Btn(L("Exporteer meting"), () => _ = ChooseExport(), Glyph.Up);
        export.Tip(L("Eén bestand met hardware, profiel en resultaten, zonder persoonlijke gegevens. Niets gaat vanzelf naar buiten."));
        // a form on GitHub where colleagues drop that file: laptops without a large card are the ones we cannot measure ourselves
        share = Ui.Btn(L("Deel op GitHub"), () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ShareUrl)), Glyph.Globe);
        share.Tip(L("Opent een formulier op GitHub. Sleep daar het geëxporteerde bestand in; je ziet alles voordat je het verstuurt."));
        idle = Ui.H(10, start, clear, export, share);
        working = Ui.H(8, ring, step);

        var measure = Ui.Card(Ui.V(10, none, choices, idle, working, saved,
            Ui.Label(L("Elk model wordt om de beurt gestart; grote modellen laden de eerste keer enkele minuten."), 12, Theme.Tertiary, wrap: true)),
            L("Meten"), Glyph.Clock, Theme.Violet);

        write = new BarChart("tokens/s");
        read = new BarChart("tokens/s");
        load = new BarChart(L("seconden"));
        ssd = new BarChart("MB/s");
        var charts = new Grid { ColumnSpacing = Theme.SectionSpacing, RowSpacing = Theme.SectionSpacing };
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        charts.Children.Add(Ui.Card(write, L("Schrijven"), Glyph.Bolt).At(0, 0));
        charts.Children.Add(Ui.Card(read, L("Prompt lezen"), Glyph.Eye).At(0, 1));
        charts.Children.Add(Ui.Card(load, L("Laadtijd"), Glyph.Clock).At(1, 0));
        charts.Children.Add(Ui.Card(ssd, L("SSD tijdens schrijven"), Glyph.Drive).At(1, 1));
        textCards.AddRange(charts.Children.Cast<FrameworkElement>());
        // a model that makes images is measured by one image: charts of its own, the text charts do not fit it
        imageTime = new BarChart(L("seconden"));
        imageTokens = new BarChart("tokens/s");
        charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        imageCards.Add(Ui.Card(imageTime, L("Afbeelding van 1024 × 1024, met laden"), Glyph.Image).At(2, 0));
        imageCards.Add(Ui.Card(imageTokens, L("Beeldtokens"), Glyph.Bolt).At(2, 1));
        foreach (var c in imageCards) charts.Children.Add(c);

        var tableScroll = new ScrollViewer
        {
            Content = table,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
        tableScroll.SizeChanged += (_, e) => table.MinWidth = e.NewSize.Width;
        results = Ui.V(Theme.SectionSpacing, charts, Ui.Card(tableScroll, L("Alle cijfers"), Glyph.Bullets, Theme.Cyan));

        View = Ui.Page(
            Ui.PageHead(Ui.PageIcon(Glyph.Compare), L("Vergelijken"), Ui.Label(L("Dezelfde meting op elk model: een prompt van ~2.500 tokens lezen en 300 tokens schrijven, of een afbeelding van 1024 × 1024 maken."), 14, Theme.Secondary, wrap: true)),
            measure, results);
        app.Watch(View, Refresh);
        View.Loaded += (_, _) => Task.Run(app.Host.Store.Refresh);
    }

    private IReadOnlyList<ModelInfo> Ready => app.Host.Store.Catalog.All.Where(m => app.Host.Store.StatusOf(m.Id).IsComplete).ToList();

    private void Refresh()
    {
        var bench = app.Host.Bench;
        var ready = Ready;
        var key = string.Join("|", ready.Select(m => m.Id));
        if (key != shownReady)
        {
            bool first = shownReady == "?" || selection.Count == 0;
            shownReady = key;
            if (first) foreach (var m in ready) selection.Add(m.Id);
            selection.IntersectWith(ready.Select(m => m.Id));
            choices.Children.Clear();
            foreach (var m in ready) choices.Children.Add(Choice(m));
        }
        none.Show(ready.Count == 0);
        choices.Show(ready.Count > 0);

        bool running = bench.Running != null;
        idle.Show(!running);
        working.Show(running);
        ring.IsActive = running;
        if (running) step.SetIfChanged($"{bench.Running}: {LKey(bench.Step)}");
        if (start.IsEnabled != selection.Count > 0) start.IsEnabled = selection.Count > 0;

        var latest = bench.Latest(app.Host.Store.Catalog);
        clear.Show(bench.Results.Count > 0);
        export.Show(bench.Results.Count > 0);
        share.Show(bench.Results.Count > 0);
        results.Show(latest.Count > 0);
        var seen = string.Join("|", latest.Select(r => r.Id));
        if (seen == shownResults) return;
        shownResults = seen;
        string Family(BenchResult r) => app.Host.Store.Catalog.Model(r.ModelId)?.Family ?? "deepseek";
        var text = latest.Where(r => !r.MakesImage).ToList();
        var images = latest.Where(r => r.MakesImage).ToList();
        void Fill(BarChart c, IReadOnlyList<BenchResult> list, Func<BenchResult, double> value) => c.Set(list.Select(r => (Name(r), value(r), Family(r))).ToList());
        Fill(write, text, r => r.DecodeTps);
        Fill(read, text, r => r.PromptTps);
        Fill(load, text, r => r.LoadSeconds);
        Fill(ssd, text, r => r.SsdMBs);
        Fill(imageTime, images, r => r.ImageSeconds ?? 0);
        Fill(imageTokens, images, r => r.DecodeTps);
        foreach (var c in textCards) c.Show(text.Count > 0);
        foreach (var c in imageCards) c.Show(images.Count > 0);
        BuildTable(latest);
    }

    private FrameworkElement Choice(ModelInfo m)
    {
        var box = new CheckBox { IsChecked = selection.Contains(m.Id), MinWidth = 0, Padding = new Thickness(6, 0, 0, 0) };
        var dot = new Ellipse { Width = 10, Height = 10, Fill = Theme.FamilyGradient(m.Family) };
        box.Content = Ui.H(8, dot, Ui.Label(m.Name, 14), Ui.Label(LKey(m.Summary), 12, Theme.Secondary));
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true) selection.Add(m.Id);
            else selection.Remove(m.Id);
            Refresh();
        };
        return box;
    }

    private void Run()
    {
        var bench = app.Host.Bench;
        var ids = app.Host.Store.Catalog.All.Select(m => m.Id).Where(selection.Contains).ToList();
        Task.Run(() => bench.Run(ids, app.Host));
    }

    private void BuildTable(IReadOnlyList<BenchResult> latest)
    {
        table.Children.Clear();
        table.ColumnDefinitions.Clear();
        table.RowDefinitions.Clear();
        var culture = AppLanguages.Current.Culture();
        string N(double v, string format) => v.ToString(format, culture);
        double k = ByteFormat.GibPerGb;
        string[] heads = [L("Model"), L("Schrijven"), L("Lezen"), L("Laden"), "SSD", "VRAM", "RAM", L("Experts"), L("Afbeelding"), L("Gemeten")];
        for (int c = 0; c < heads.Length; c++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Row(int r, IEnumerable<FrameworkElement> cells)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int c = 0;
            foreach (var cell in cells) table.Children.Add(cell.At(r, c++));
        }
        Row(0, heads.Select(h => (FrameworkElement)Ui.Label(h, 12, Theme.Secondary, FontWeights.SemiBold)));
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.Children.Add(Ui.Divider().At(1, 0, 1, heads.Length));
        int row = 2;
        foreach (var r in latest)
        {
            FrameworkElement Cell(string text) => Ui.Label(text, 13);
            Row(row++,
            [
                Ui.Label(Name(r), 13, null, FontWeights.Medium),
                Cell($"{N(r.DecodeTps, "0.0")} tok/s"),
                Cell(r.MakesImage ? "–" : $"{N(r.PromptTps, "0")} tok/s"),
                Cell(r.MakesImage ? "–" : $"{N(r.LoadSeconds, "0")} s"),
                Cell($"{N(r.SsdMBs, "0")} MB/s"),
                Cell($"{N(r.VramGb * k, "0.0")} GB"),
                Cell($"{N(r.RamGb * k, "0")} GB"),
                Cell(r.MakesImage ? "–" : Experts(r)),
                Cell(r.ImageSeconds is double seconds ? $"{N(seconds, "0")} s" : "–"),
                Ui.Label(r.Date.ToLocalTime().ToString("d MMM yyyy", culture), 13, Theme.Secondary),
            ]);
        }
    }

    /// <summary>A measurement with experts skipped is not one of the full model, and says so first: the charts cut long names off.</summary>
    private static string Name(BenchResult r) => r.SkipExperts switch
    {
        "light" => L("Licht") + " · " + r.ModelName,
        "strong" => L("Sterk") + " · " + r.ModelName,
        _ => r.ModelName,
    };

    /// <summary>Where the experts were: all on the GPU, some layers on the CPU, or mapped from the SSD with a GPU cache.</summary>
    private static string Experts(BenchResult r)
    {
        if (r.ExpertCacheGb is > 0) return L($"cache {r.ExpertCacheGb * ByteFormat.GibPerGb:0} GB");
        return r.CpuMoELayers == 0 ? "GPU" : L($"{r.CpuMoELayers} lagen CPU");
    }

    private async Task ChooseExport()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "WinAIMagic-meting" };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(app.Window));
            if (await picker.PickSaveFileAsync() is { } file) Export(file.Path);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { App.Log(e); }
    }

    /// <summary>Exporteer meting: what the file says, and only after the user chose where it goes.</summary>
    public void Export(string path)
    {
        try
        {
            app.Host.Bench.Export(path, app.Host.System, app.Host.Backend);
            saved.Foreground = Theme.Secondary;
            saved.SetIfChanged(L($"Opgeslagen: {path}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            saved.Foreground = Theme.Solid(Theme.Amber);
            saved.SetIfChanged(e.Message);
        }
    }
}

/// <summary>Hand-drawn bars side by side: a name per row, a bar in the model's colours, its value, and the scale below.</summary>
public sealed partial class BarChart : Grid
{
    private const double RowHeight = 44, AxisHeight = 30, NameWidth = 132, ValueRoom = 52;
    private readonly string unit;
    private readonly Canvas canvas = new();
    private IReadOnlyList<(string Name, double Value, string Family)> rows = [];

    public BarChart(string unit)
    {
        this.unit = unit;
        Children.Add(canvas);
        SizeChanged += (_, _) => Redraw();
    }

    public void Set(IReadOnlyList<(string Name, double Value, string Family)> values)
    {
        rows = values;
        Height = Math.Max(rows.Count, 1) * RowHeight + AxisHeight;
        Redraw();
    }

    private static double Nice(double top)
    {
        if (top <= 0) return 1;
        double step = Math.Pow(10, Math.Floor(Math.Log10(top)));
        foreach (var f in new[] { 1, 2, 2.5, 5, 10 })
            if (f * step >= top) return f * step;
        return 10 * step;
    }

    private void Redraw()
    {
        canvas.Children.Clear();
        double w = ActualWidth, plot = w - NameWidth - ValueRoom, bottom = rows.Count * RowHeight;
        if (plot <= 20 || rows.Count == 0) return;
        double top = Nice(rows.Max(r => r.Value));
        var culture = AppLanguages.Current.Culture();

        TextBlock Add(string text, double x, double y, double size, Brush color, double? width = null, TextAlignment align = TextAlignment.Left, Windows.UI.Text.FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = color, FontFamily = Theme.Text, TextAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis };
            if (width != null) t.Width = width.Value;
            if (weight != null) t.FontWeight = weight.Value;
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            canvas.Children.Add(t);
            return t;
        }

        for (int i = 0; i <= 4; i++)
        {
            double value = top * i / 4, x = NameWidth + plot * i / 4;
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = bottom, Stroke = Theme.Solid(Colors.White, i == 0 ? 0.14 : 0.06), StrokeThickness = 1 });
            if (i % 2 == 0) Add(value.ToString(value >= 10 ? "0" : "0.#", culture), x - 24, bottom + 3, 10, Theme.Tertiary, 48, TextAlignment.Center);
        }
        Add(unit, NameWidth, bottom + 15, 10, Theme.Tertiary, plot, TextAlignment.Center);

        for (int i = 0; i < rows.Count; i++)
        {
            var (name, value, family) = rows[i];
            double y = i * RowHeight, barHeight = 22, barTop = y + (RowHeight - barHeight) / 2;
            Add(name, 0, barTop + 2, 12, Theme.Secondary, NameWidth - 10).Tip(name);
            double length = Math.Max(plot * Math.Min(value / top, 1), value > 0 ? 3 : 0);
            var bar = new Rectangle { Width = length, Height = barHeight, RadiusX = 6, RadiusY = 6, Fill = Theme.FamilyGradient(family) };
            Canvas.SetLeft(bar, NameWidth);
            Canvas.SetTop(bar, barTop);
            canvas.Children.Add(bar);
            Add(value.ToString("0.0", culture), NameWidth + length + 6, barTop + 3, 12, Theme.Secondary, ValueRoom - 6, weight: FontWeights.SemiBold);
        }
    }
}
