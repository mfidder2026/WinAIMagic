using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinAIMagic.Engine;

namespace WinAIMagic.App.Views;

/// <summary>A round gauge for loading or unloading a model: an arc that fills up, with the percentage inside when there is room.
/// It reads the engine itself five times a second while it is on screen, because the engine says little while it reads 20 GB
/// (the owner found loading and unloading unclear). Amber while loading, grey-blue while unloading.</summary>
public sealed partial class LoadGauge : Grid
{
    private readonly AppState app;
    private readonly double size;
    private readonly Microsoft.UI.Xaml.Shapes.Path arc = new() { StrokeThickness = 0, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock percent;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private double shown = -1;
    private EngineStatus shownStatus;

    /// <summary>Each tick: the fraction (0..1), the seconds still to go, and the engine's status.</summary>
    public event Action<double, int?, EngineStatus>? Ticked;

    /// <summary>The chat model's engine: its state as the app sees it, its progress as the engine has it.</summary>
    public LoadGauge(AppState app, double size)
    {
        this.app = app;
        this.size = size;
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        double stroke = size >= 24 ? 3 : 2;
        arc.StrokeThickness = stroke;
        Children.Add(new Ellipse { Stroke = Theme.Solid(Colors.White, 0.15), StrokeThickness = stroke });
        Children.Add(arc);
        percent = new TextBlock
        {
            FontSize = Math.Max(8, size * 0.3), FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (size >= 24) Children.Add(percent);
        timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { Tick(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    private void Tick()
    {
        var e = app.Host.Engine;
        var status = app.EngineState.Status;
        double f = status is EngineStatus.Starting or EngineStatus.Stopping ? e?.LoadProgress ?? 0 : status == EngineStatus.Running ? 1 : 0;
        int? left = (e as EngineBase)?.SecondsLeft;
        Ticked?.Invoke(f, left, status);
        if (Math.Abs(f - shown) < 0.002 && status == shownStatus) return;
        shown = f;
        shownStatus = status;
        double r = size / 2 - arc.StrokeThickness / 2;
        // an arc of almost nothing draws nothing: show a sliver from the start
        arc.Data = Draw.Arc(size / 2, size / 2, r, 0, Math.Clamp(f, 0.02, 0.999));
        arc.Stroke = new SolidColorBrush(status == EngineStatus.Stopping ? Theme.Accent : Theme.Amber);
        percent.Text = $"{Math.Round(f * 100):0}%";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, percent.Text);
    }
}
