using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;
using FontWeight = Windows.UI.Text.FontWeight;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinAIMagic.App.Views;

/// <summary>Drawing helpers.</summary>
internal static class Draw
{
    /// <summary>A circle's arc between two fractions, from the top, clockwise.</summary>
    public static PathGeometry Arc(double cx, double cy, double r, double from, double to)
    {
        Point At(double f) => new(cx + r * Math.Sin(f * 2 * Math.PI), cy - r * Math.Cos(f * 2 * Math.PI));
        var fig = new PathFigure { StartPoint = At(from), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment { Point = At(to), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, IsLargeArc = to - from > 0.5 });
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>The sparkles of the logo: a large four-pointed star with two small ones, in a square of `s`.</summary>
    public static PathGeometry Sparkles(double s)
    {
        var g = new PathGeometry();
        void Star(double cx, double cy, double r)
        {
            Point P(double dx, double dy) => new(cx + dx * r, cy + dy * r);
            var fig = new PathFigure { StartPoint = P(0, -1), IsClosed = true, IsFilled = true };
            foreach (var (c, e) in new[] { (P(0.14, -0.14), P(1, 0)), (P(0.14, 0.14), P(0, 1)), (P(-0.14, 0.14), P(-1, 0)), (P(-0.14, -0.14), P(0, -1)) })
                fig.Segments.Add(new QuadraticBezierSegment { Point1 = c, Point2 = e });
            g.Figures.Add(fig);
        }
        Star(s * 0.44, s * 0.56, s * 0.42);
        Star(s * 0.82, s * 0.20, s * 0.20);
        Star(s * 0.86, s * 0.84, s * 0.13);
        return g;
    }
}

/// <summary>Soft colour blobs behind everything; still by default. "Bewegende achtergrond" lets them drift and light up while the
/// model writes: only then a timer runs, so only then it costs anything.</summary>
public sealed partial class Aurora : Grid
{
    private static readonly (Color C, double Phase, double Radius, double Strength)[] Blobs =
    [
        (Theme.Accent, 0.0, 0.55, 0.9), (Theme.Violet, 2.1, 0.45, 0.8),
        (Theme.Cyan, 4.2, 0.35, 0.6), (Theme.Pink, 5.3, 0.30, 0.5),
    ];

    private readonly Canvas canvas = new();
    private readonly Ellipse[] ellipses;
    private readonly AppState app;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? timer;
    private double time;
    private DateTime lastTick;

    public Aurora(AppState app)
    {
        this.app = app;
        Background = new SolidColorBrush(Theme.Night);
        IsHitTestVisible = false;
        ellipses = Blobs.Select(b =>
        {
            var brush = new RadialGradientBrush();
            // Theme.swift blurs the blobs by 12% of the short side; here that is a soft edge
            foreach (var (a, at) in new[] { (1.0, 0.0), (0.82, 0.45), (0.38, 0.72), (0.08, 0.92), (0.0, 1.0) })
                brush.GradientStops.Add(new GradientStop { Color = Theme.Alpha(b.C, a), Offset = at });
            var e = new Ellipse { Fill = brush };
            canvas.Children.Add(e);
            return e;
        }).ToArray();
        Children.Add(canvas);
        SizeChanged += (_, _) => Lay();
        Loaded += (_, _) => { Settings.Shared.PropertyChanged += OnSetting; Apply(); };
        Unloaded += (_, _) => { Settings.Shared.PropertyChanged -= OnSetting; timer?.Stop(); timer = null; };
    }

    private void OnSetting(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.AnimatedBackground)) DispatcherQueue.TryEnqueue(() => Apply());
    }

    private void Apply()
    {
        timer?.Stop();
        timer = null;
        if (Settings.Shared.AnimatedBackground)
        {
            lastTick = DateTime.UtcNow;
            timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1 / 30.0);
            timer.Tick += (_, _) => Lay();
            timer.Start();
        }
        Lay();
    }

    private void Lay()
    {
        double w = ActualWidth, h = ActualHeight, side = Math.Min(w, h);
        if (side <= 0) return;
        double energy = timer == null ? 0 : Math.Min(app.TokensPerSec / 20, 1);
        if (timer != null)
        {
            var now = DateTime.UtcNow;
            time += (now - lastTick).TotalSeconds * (0.05 + 0.10 * energy);
            lastTick = now;
        }
        double blur = side * 0.12, t = timer == null ? 0 : time;
        for (int i = 0; i < Blobs.Length; i++)
        {
            var b = Blobs[i];
            double x = w * (0.5 + 0.38 * Math.Sin(t * 0.9 + b.Phase));
            double y = h * (0.5 + 0.34 * Math.Cos(t * 0.7 + b.Phase * 1.3));
            double r = side * b.Radius + blur;
            var e = ellipses[i];
            e.Width = e.Height = 2 * r;
            Canvas.SetLeft(e, x - r);
            Canvas.SetTop(e, y - r);
            e.Opacity = (0.16 + 0.14 * energy) * b.Strength;
        }
    }
}

/// <summary>The app mark: a gradient orb with sparkles. It only turns while the model writes, on the composition thread.</summary>
public sealed partial class BrandMark : Grid
{
    private readonly Storyboard spin = new();
    private bool active;

    public BrandMark(double size)
    {
        Width = Height = size;
        var orb = new Canvas { Width = size, Height = size };
        const int parts = 48;
        for (int i = 0; i < parts; i++)
        {
            double f = (double)i / parts;
            orb.Children.Add(new Path
            {
                Data = Draw.Arc(size / 2, size / 2, size / 4, f, f + 1.3 / parts),
                Stroke = new SolidColorBrush(Theme.Along(f, Theme.Accent, Theme.Violet, Theme.Pink, Theme.Cyan, Theme.Accent)),
                StrokeThickness = size / 2,
            });
        }
        var rotate = new RotateTransform { CenterX = size / 2, CenterY = size / 2 };
        orb.RenderTransform = rotate;
        Children.Add(orb);
        Children.Add(new Ellipse { Margin = new Thickness(size * 0.08), Fill = Theme.Solid(Theme.Night, 0.35) });
        double s = size * 0.60;
        Children.Add(new Path { Data = Draw.Sparkles(s), Fill = new SolidColorBrush(Colors.White), Width = s, Height = s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var a = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(3), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, rotate);
        Storyboard.SetTargetProperty(a, "Angle");
        spin.Children.Add(a);
        Unloaded += (_, _) => Active = false;
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value) spin.Begin(); else spin.Stop();
        }
    }
}

/// <summary>A ring gauge whose colour runs around the circle: the number in the very middle, what belongs to it below that.</summary>
public sealed partial class GaugeRing : Grid
{
    private readonly Path[] parts;
    private readonly TextBlock number;
    private readonly TextBlock? labelBlock;
    private double maximum, last;
    private int shown;

    /// <summary>The top of the scale; image tokens come far faster than words.</summary>
    public double Maximum
    {
        get => maximum;
        set
        {
            if (maximum == value) return;
            maximum = value;
            Set(last);
        }
    }

    public void SetLabel(string text) => labelBlock?.SetIfChanged(text);

    public GaugeRing(double maximum, string label, string unit, double size)
    {
        this.maximum = maximum;
        Width = Height = size;
        double thick = size * 0.075, r = size / 2 - thick / 2 - 1;
        var canvas = new Canvas { Width = size, Height = size };
        var track = new Ellipse { Width = 2 * r + thick, Height = 2 * r + thick, Stroke = Theme.Solid(Colors.White, 0.06), StrokeThickness = thick };
        Canvas.SetLeft(track, size / 2 - r - thick / 2);
        Canvas.SetTop(track, size / 2 - r - thick / 2);
        canvas.Children.Add(track);
        int count = size > 100 ? 120 : 48;
        parts = Enumerable.Range(0, count).Select(i =>
        {
            double f = (double)i / count;
            var p = new Path
            {
                Data = Draw.Arc(size / 2, size / 2, r, f, f + 1.0 / count),
                Stroke = new SolidColorBrush(Theme.Along(f + 0.5 / count, Theme.Cyan, Theme.Accent, Theme.Violet, Theme.Pink, Theme.Cyan)),
                StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(p);
            return p;
        }).ToArray();
        Children.Add(canvas);
        double big = size * 0.26;
        number = new TextBlock
        {
            FontFamily = Theme.Display, FontWeight = FontWeights.Bold, FontSize = big, Foreground = new SolidColorBrush(Theme.Cyan),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(number);
        var below = Ui.V(1, Small(unit, Math.Max(size * 0.07, 8), Theme.Secondary, FontWeights.Medium), label.Length > 0 ? Small(label, size * 0.058, Theme.Tertiary, FontWeights.SemiBold) : null);
        below.HorizontalAlignment = HorizontalAlignment.Center;
        below.VerticalAlignment = VerticalAlignment.Top;
        below.Margin = new Thickness(0, size / 2 + big * 0.64, 0, 0);
        Children.Add(below);
        labelBlock = below.Children.Count > 1 ? below.Children[1] as TextBlock : null;
        Set(0);
    }

    private static TextBlock Small(string t, double size, Brush c, FontWeight w) => new()
    {
        Text = t, FontSize = size, Foreground = c, FontWeight = w, FontFamily = Theme.Display, HorizontalAlignment = HorizontalAlignment.Center,
    };

    public void Set(double value)
    {
        last = value;
        number.SetIfChanged(value.ToString("0.0", AppLanguages.Current.Culture()));
        int n = (int)Math.Round(Math.Clamp(value / maximum, 0, 1) * parts.Length);
        if (n == shown) return;
        for (int i = Math.Min(n, shown); i < Math.Max(n, shown); i++) parts[i].Visibility = i < n ? Visibility.Visible : Visibility.Collapsed;
        shown = n;
    }
}

/// <summary>The progress ring of a model card.</summary>
public sealed partial class ArcRing : Grid
{
    private readonly Path arc;
    private readonly double size, thickness;
    public FontIcon Mark { get; }
    private double last = -1;

    public ArcRing(double size, double thickness, Brush stroke, Brush markColor)
    {
        this.size = size;
        this.thickness = thickness;
        Width = Height = size;
        Children.Add(new Ellipse { Stroke = Theme.Solid(Colors.White, 0.08), StrokeThickness = thickness });
        arc = new Path { Stroke = stroke, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        Children.Add(arc);
        Mark = Ui.Icon(Glyph.CloudDown, size * 0.38, markColor);
        Children.Add(Mark);
    }

    public void Set(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.9995);
        if (Math.Abs(fraction - last) < 0.001) return;
        last = fraction;
        arc.Data = fraction <= 0 ? null : Draw.Arc(size / 2, size / 2, size / 2 - thickness / 2, 0, fraction);
    }
}

/// <summary>Lays its children out in rows; XAML has no such panel of its own.</summary>
public sealed partial class WrapPanel : Panel
{
    public double Gap { get; set; } = 8;
    public double RowGap { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > available.Width) { y += row + RowGap; x = 0; row = 0; }
            x += s.Width + Gap;
            row = Math.Max(row, s.Height);
            width = Math.Max(width, x - Gap);
        }
        return new Size(width, y + row);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, row = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += row + RowGap; x = 0; row = 0; }
            c.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Gap;
            row = Math.Max(row, s.Height);
        }
        return final;
    }
}

/// <summary>A tiny area chart: gradient fill under a line, scaled to maxY or to the largest value.</summary>
public sealed partial class MiniSpark : Grid
{
    private readonly Polyline line;
    private readonly Polygon area;
    private IReadOnlyList<double> values = [];
    private double? maxY;

    public MiniSpark(Color color, double height)
    {
        Height = height;
        area = new Polygon { Fill = Theme.Gradient(new Point(0, 0), new Point(0, 1), Theme.Alpha(color, 0.45), Theme.Alpha(color, 0.02)) };
        line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
        Children.Add(area);
        Children.Add(line);
        SizeChanged += (_, _) => Redraw();
    }

    public void Set(IReadOnlyList<double> v, double? max)
    {
        values = v;
        maxY = max;
        Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || values.Count == 0) return;
        double top = Math.Max(maxY ?? values.Max() * 1.15, 0.001), step = w / Math.Max(values.Count - 1, 1);
        var pts = new PointCollection();
        var fill = new PointCollection { new Point(0, h) };
        for (int i = 0; i < values.Count; i++)
        {
            var p = new Point(i * step, Math.Round((h - 1) * (1 - Math.Min(values[i] / top, 1)) + 0.5, 1));
            pts.Add(p);
            fill.Add(p);
        }
        fill.Add(new Point((values.Count - 1) * step, h));
        line.Points = pts;
        area.Points = fill;
    }
}

/// <summary>Hand-drawn lines over time, in a window of 90 s that ends at the last sample: line and area per series, gridlines and
/// values at the right, and at most two marks (the total, the pinned part).</summary>
public sealed partial class LineChart : Grid
{
    public sealed record Series(Color Color, Func<LiveMetrics.Sample, double> Value, double Width = 2.2, bool Fill = true, bool Dash = false);

    private const double AxisWidth = 38;
    private readonly (Series S, Polyline Line, Polyline Glow, Polygon Area)[] series;
    private readonly Canvas canvas = new();
    private readonly Line[] grid = [new(), new(), new()];
    private readonly TextBlock[] labels = [new(), new(), new()];
    private readonly (Line Line, TextBlock Text)[] marks;
    private IReadOnlyList<LiveMetrics.Sample> samples = [];
    private double? maxY;
    private (double Value, string Text)?[] markValues = [null, null];

    public LineChart(double height, params Series[] list)
    {
        Height = height;
        Children.Add(canvas);
        foreach (var l in grid) { l.Stroke = Theme.Solid(Colors.White, 0.06); l.StrokeThickness = 1; canvas.Children.Add(l); }
        foreach (var t in labels) { t.FontSize = 10; t.Foreground = Theme.Tertiary; canvas.Children.Add(t); }
        series = list.Select(s =>
        {
            var area = new Polygon { Fill = Theme.Gradient(new Point(0, 0), new Point(0, 1), Theme.Alpha(s.Color, 0.5), Theme.Alpha(s.Color, 0.02)) };
            var glow = new Polyline { Stroke = Theme.Solid(s.Color, 0.22), StrokeThickness = s.Width + 3.5, StrokeLineJoin = PenLineJoin.Round };
            var line = new Polyline { Stroke = new SolidColorBrush(s.Color), StrokeThickness = s.Width, StrokeLineJoin = PenLineJoin.Round };
            if (s.Dash) line.StrokeDashArray = new DoubleCollection { 4, 3 };
            area.Show(s.Fill);
            glow.Show(!s.Dash);
            canvas.Children.Add(area);
            canvas.Children.Add(glow);
            canvas.Children.Add(line);
            return (s, line, glow, area);
        }).ToArray();
        marks = new[] { Theme.Solid(Colors.White, 0.40), Theme.Solid(Theme.Violet, 0.9) }.Select(b =>
        {
            var l = new Line { Stroke = b, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 5, 4 }, Visibility = Visibility.Collapsed };
            var t = new TextBlock { FontSize = 10, Foreground = b, Visibility = Visibility.Collapsed };
            canvas.Children.Add(l);
            canvas.Children.Add(t);
            return (l, t);
        }).ToArray();
        SizeChanged += (_, _) => Redraw();
    }

    /// <param name="max">The top of the scale; null: from the values.</param>
    public void Set(IReadOnlyList<LiveMetrics.Sample> s, double? max, (double Value, string Text)? total = null, (double Value, string Text)? pinned = null)
    {
        samples = s;
        maxY = max;
        markValues = [total, pinned];
        Redraw();
    }

    private void Redraw()
    {
        double W = ActualWidth, h = ActualHeight, w = W - AxisWidth;
        if (w <= 0 || h <= 0) return;
        canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, W, h) };
        double top = maxY ?? Math.Max(10, samples.Count == 0 ? 10 : samples.Max(x => series.Max(z => z.S.Value(x))) * 1.2);
        double Y(double v) => Math.Round(h - 1 - (h - 2) * Math.Min(Math.Max(v, 0) / top, 1), 1);

        for (int i = 0; i < 3; i++)
        {
            double value = top * i / 2, y = Y(value);
            grid[i].X1 = 0; grid[i].X2 = w; grid[i].Y1 = grid[i].Y2 = y;
            labels[i].Text = value >= 1000 ? (value / 1000).ToString("0.#") + "k" : value.ToString("0");
            Canvas.SetLeft(labels[i], w + 6);
            Canvas.SetTop(labels[i], Math.Clamp(y - 7, 0, h - 14));
        }
        for (int i = 0; i < marks.Length; i++)
        {
            var (l, t) = marks[i];
            l.Show(markValues[i] != null);
            t.Show(markValues[i] != null);
            if (markValues[i] is not { } m) continue;
            l.X1 = 0; l.X2 = w; l.Y1 = l.Y2 = Y(m.Value);
            t.Text = m.Text;
            Canvas.SetLeft(t, 4);
            Canvas.SetTop(t, Math.Clamp(Y(m.Value) + 1, 0, h - 14));
        }

        DateTime end = samples.Count > 0 ? samples[^1].T : DateTime.UtcNow;
        double X(DateTime t) => w * (1 - (end - t).TotalSeconds / LiveMetrics.WindowSeconds);
        foreach (var (s, line, glow, area) in series)
        {
            // a collection belongs to one shape
            PointCollection pts = new(), halo = new(), fill = new();
            if (samples.Count > 0) fill.Add(new Point(Math.Max(X(samples[0].T), 0), h));
            foreach (var x in samples)
            {
                var p = new Point(X(x.T), Y(s.Value(x)));
                pts.Add(p);
                halo.Add(p);
                fill.Add(p);
            }
            if (samples.Count > 0) fill.Add(new Point(w, h));
            line.Points = pts;
            if (!s.Dash) glow.Points = halo;
            if (s.Fill) area.Points = fill;
        }
    }
}

/// <summary>The engine's state as a pill.</summary>
public sealed partial class StatusPill : Grid
{
    private readonly Ellipse dot = new() { Width = 8, Height = 8 };
    private readonly TextBlock text = Ui.Label("", 13, null, FontWeights.Medium);
    private readonly AppState app;
    private readonly bool all;

    /// <summary>`all` false: only the app's own engine, beside its Start and Stop.</summary>
    public StatusPill(AppState app, bool all = true)
    {
        this.app = app;
        this.all = all;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(11, 5, 11, 6);
        BorderThickness = new Thickness(1);
        Background = Theme.Solid(Colors.White, 0.06);
        VerticalAlignment = VerticalAlignment.Center;
        text.MaxWidth = 300;
        Children.Add(Ui.H(7, dot, text));
        this.Tip(all ? L("Welk model er draait. Klik om een model te starten of te stoppen.") : L("Engine-status: klik voor het live dashboard"));
        // at the top: a menu to start or stop a model on every page
        if (all) Tapped += (_, _) => Menu().ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });
        else Tapped += (_, _) => app.Pane = new Pane(PaneKind.Live);
        app.Watch(this, Update);
    }

    /// <summary>Every downloaded model to start with one click, Stop model, and the pages Modellen and Live.</summary>
    private MenuFlyout Menu()
    {
        var menu = new MenuFlyout();
        var store = app.Host.Store;
        var ready = store.Catalog.Models.Where(m => store.StatusOf(m.Id).IsComplete).ToList();
        bool active = app.EngineState.IsActive;
        if (ready.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = L("Nog geen model gedownload"), IsEnabled = false });
        else
        {
            menu.Items.Add(new MenuFlyoutItem { Text = L("Start een model"), IsEnabled = false });
            foreach (var m in ready)
            {
                bool on = active && app.Host.Engine?.Model?.Id == m.Id;
                var item = new MenuFlyoutItem { Text = m.Name + "  " + m.TotalSize.GbShort(), Icon = Ui.Icon(on ? Glyph.Done : Glyph.Play), IsEnabled = !on };
                var id = m.Id;
                item.Click += (_, _) => _ = app.StartModel(id);
                menu.Items.Add(item);
            }
        }
        if (active)
        {
            var stop = new MenuFlyoutItem { Text = L("Stop model"), Icon = Ui.Icon(Glyph.Stop) };
            stop.Click += (_, _) => app.StopEngine();
            menu.Items.Add(stop);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var models = new MenuFlyoutItem { Text = L("Modellen…") };
        models.Click += (_, _) => app.Pane = new Pane(PaneKind.Models);
        var live = new MenuFlyoutItem { Text = L("Live-dashboard") };
        live.Click += (_, _) => app.Pane = new Pane(PaneKind.Live);
        menu.Items.Add(models);
        menu.Items.Add(live);
        return menu;
    }

    /// <summary>Every model in memory with what it does: the chat engine (Zwaar), the second engine (Snel) and the image model while it
    /// draws. Green: runs; amber: loading; red: failed; grey: nothing in memory.</summary>
    private void Update()
    {
        if (all) { UpdateAll(); return; }
        UpdateOwn();
    }

    /// <summary>`role` null: only one model runs, and its role says nothing.</summary>
    private static (string Text, Windows.UI.Color Color, int Rank)? Part(EngineState s, string? name, string? role) => s.Status switch
    {
        EngineStatus.Running => (role == null ? L($"{name ?? "Model"} actief") : L($"{name ?? "Model"} actief ({role})"), Theme.Mint, 1),
        EngineStatus.Starting => (role == null ? L($"{name ?? "Model"} laden…") : L($"{name ?? "Model"} laden… ({role})"), Theme.Amber, 2),
        EngineStatus.Failed => (L($"{name ?? "Model"}: fout"), Theme.Pink, 3),
        _ => null,
    };

    private void UpdateAll()
    {
        var parts = new List<(string Text, Windows.UI.Color Color, int Rank)>();
        // the role only matters when a fast model runs beside the chat model
        bool both = app.FastEngine?.State.Status is EngineStatus.Running or EngineStatus.Starting;
        if (Part(app.EngineState, app.Host.Engine?.Model?.ShortName ?? app.Host.SelectedModel?.ShortName, both ? L("Zwaar") : null) is { } heavy) parts.Add(heavy);
        if (app.FastEngine is { } fast && Part(fast.State, fast.Model?.ShortName, L("Snel")) is { } quick) parts.Add(quick);
        if (app.Host.Images.Busy) parts.Add((L($"{app.Host.ImageModel?.ShortName ?? L("Beeld")} tekent"), Theme.Mint, 1));
        var color = parts.Count == 0 ? Theme.Rgb(140, 145, 160) : parts.MaxBy(p => p.Rank).Color;
        text.MaxWidth = 520;
        text.SetIfChanged(parts.Count == 0 ? L("Geen model · klik om te starten") : string.Join(" · ", parts.Select(p => p.Text)));
        dot.Fill = new SolidColorBrush(color);
        BorderBrush = Theme.Solid(color, 0.4);
    }

    private void UpdateOwn()
    {
        var s = app.EngineState;
        var color = s.Status switch
        {
            EngineStatus.Running => Theme.Mint,
            EngineStatus.Starting => Theme.Amber,
            EngineStatus.Failed => Theme.Pink,
            _ => Theme.Rgb(140, 145, 160),
        };
        text.SetIfChanged(s.Status switch
        {
            EngineStatus.Running => L($"{app.Host.Engine?.Model?.ShortName ?? "Model"} actief"),
            EngineStatus.Starting => s.Message,
            EngineStatus.Failed => L("Engine fout"),
            _ => L("Engine uit"),
        });
        dot.Fill = new SolidColorBrush(color);
        BorderBrush = Theme.Solid(color, 0.4);
    }
}

/// <summary>Start / stop for the selected model, used in several places.</summary>
public sealed partial class EngineButton : Grid
{
    private readonly Button start, stop;
    private readonly AppState app;

    public EngineButton(AppState app)
    {
        this.app = app;
        VerticalAlignment = VerticalAlignment.Center;
        start = Ui.Btn(L("Start"), app.StartEngine, Glyph.Play, Theme.Accent);
        stop = Ui.Btn(L("Stop model"), app.StopEngine, Glyph.Stop);
        Children.Add(start);
        Children.Add(stop);
        app.Watch(this, Update);
    }

    private void Update()
    {
        bool active = app.EngineState.IsActive;
        start.Show(!active);
        stop.Show(active);
        if (active) return;
        bool can = app.Host.CanStart;
        // "Start <model>": which one is said on the button
        var title = L($"Start {app.Host.SelectedModel?.ShortName ?? L("model")}");
        if (start.Content is Panel content && content.Children.OfType<TextBlock>().FirstOrDefault() is { } label && label.Text != title)
        {
            label.Text = title;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(start, title);
        }
        if (start.IsEnabled != can) start.IsEnabled = can;
        start.Tip(can ? null : app.Host.WhyNotStartable);
    }
}

/// <summary>The memory plan as one bar: what is fixed on the card, the experts in VRAM and RAM, what comes from the SSD, what stays free.</summary>
public sealed class PlanStrip
{
    public Border View { get; }
    private readonly Grid bar = new() { Height = 18, ColumnSpacing = 2 };
    private readonly WrapPanel legend = new() { Gap = 16 };
    private string shown = "";

    public PlanStrip()
    {
        View = Ui.Card(Ui.V(10, bar, legend), L("Waar het model staat"), Glyph.Models, Theme.Cyan);
    }

    public void Set(MemoryPlan? plan)
    {
        View.Show(plan != null);
        if (plan == null) return;
        string fixedName = plan.VramTotal == 0 ? L("Vast (RAM)") : plan.GpuLayers < plan.Layers ? L("Vast (VRAM + RAM)") : L("Vast (VRAM)");
        var parts = new List<(string Name, ulong Bytes, Color C)> { (fixedName, plan.Dense + plan.Overhead, Theme.Violet) };
        if (plan.ExpertCache > 0) parts.Add((L("Expert-cache VRAM"), plan.ExpertCache, Theme.Accent));
        else
        {
            if (plan.GpuExperts > 0) parts.Add((L("Experts VRAM"), plan.GpuExperts, Theme.Accent));
            if (plan.RamExperts > 0) parts.Add((L("Experts RAM"), plan.RamExperts, Theme.Cyan));
        }
        if (plan.KvRam > 0) parts.Add((L("KV-cache RAM"), plan.KvRam, Theme.Pink));
        var key = string.Join("|", parts.Select(p => p.Name + p.Bytes)) + plan.SsdExperts + plan.Engram + plan.VramTotal + plan.RamTotal;
        if (key == shown) return;
        shown = key;

        ulong capacity = Math.Max(plan.VramTotal + plan.RamTotal, 1) + plan.SsdExperts;
        ulong used = parts.Aggregate(plan.SsdExperts, (n, p) => n + p.Bytes);
        bar.Children.Clear();
        bar.ColumnDefinitions.Clear();
        void Add(double weight, Brush fill)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(weight, 0.0001), GridUnitType.Star) });
            bar.Children.Add(Ui.Rounded(fill, 4).At(0, bar.ColumnDefinitions.Count - 1));
        }
        foreach (var p in parts) Add(p.Bytes, Theme.Gradient(new Point(0, 0), new Point(1, 0), p.C, Theme.Along(0.25, p.C, Colors.White)));
        if (plan.SsdExperts > 0) Add(plan.SsdExperts, Theme.Gradient(new Point(0, 0), new Point(1, 0), Theme.Amber, Theme.Along(0.25, Theme.Amber, Colors.White)));
        if (capacity > used) Add(capacity - used, Theme.Solid(Colors.White, 0.06));

        legend.Children.Clear();
        void Legend(string text, Brush color)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = color };
            legend.Children.Add(Ui.H(5, dot, Ui.Label(text, 12, Theme.Secondary)));
        }
        foreach (var p in parts) Legend($"{p.Name} {p.Bytes.GbShort()}", new SolidColorBrush(p.C));
        if (plan.SsdExperts > 0) Legend(L($"Van SSD {plan.SsdExperts.GbShort()}"), new SolidColorBrush(Theme.Amber));
        if (plan.Engram > 0) Legend(L($"Engram (SSD) {plan.Engram.GbShort()}"), Theme.Solid(Theme.Amber, 0.5));
    }
}
