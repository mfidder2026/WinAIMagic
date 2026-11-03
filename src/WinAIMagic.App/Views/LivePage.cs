using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>The live dashboard: a big speed gauge and real-time charts of what the PC is doing, on three tiers.</summary>
public sealed class LivePage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly BrandMark mark = new(44);
    private readonly TextBlock model = Ui.Label("", 17, Theme.Secondary);
    private readonly GaugeRing gauge = new(30, L("SCHRIJFSNELHEID"), L("tokens / s"), 210);
    private readonly TextBlock gpu = Stat(Theme.Accent), cpu = Stat(Theme.Cyan), ssd = Stat(Theme.Amber), hit = Stat(Theme.Mint), spread = Stat(Theme.Violet), heat = Stat(Theme.Pink);
    private readonly LineChart tokens = new(130, new LineChart.Series(Theme.Violet, s => s.TokensPerSec));
    private readonly LineChart load = new(130, new LineChart.Series(Theme.Accent, s => s.Gpu), new LineChart.Series(Theme.Cyan, s => s.Cpu, 1.6, false, true));
    private readonly LineChart vram = new(62, new LineChart.Series(Theme.Violet, s => s.VramUsedGB * ByteFormat.GibPerGb));
    private readonly LineChart ram = new(62, new LineChart.Series(Theme.Pink, s => s.RamUsedGB * ByteFormat.GibPerGb));
    private readonly LineChart disk = new(148, new LineChart.Series(Theme.Amber, s => s.SsdMBs));
    private readonly TextBlock tokensValue = Value(), loadValue = Value(), vramValue = Value(), ramValue = Value(), diskValue = Value();
    private readonly PlanStrip strip = new();

    public LivePage(AppState app)
    {
        this.app = app;
        var head = Ui.PageHead(mark, L("Live"), model, new StatusPill(app), new EngineButton(app));

        var stats = new Grid { RowSpacing = 12, ColumnSpacing = 6 };
        for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(int row, int col, TextBlock value, string label) => stats.Children.Add(MiniStat(value, label).At(row, col));
        Put(0, 0, gpu, "GPU %");
        Put(0, 1, cpu, "CPU %");
        Put(0, 2, ssd, "SSD MB/s");
        Put(1, 0, hit, L("Experts in VRAM"));
        Put(1, 1, spread, "VRAM/RAM/SSD");
        Put(1, 2, heat, L("GPU °C"));
        gauge.HorizontalAlignment = HorizontalAlignment.Center;
        var gaugeBody = Ui.V(16, gauge, stats);
        gaugeBody.VerticalAlignment = VerticalAlignment.Center;
        var gaugeCard = Ui.Card(null, glow: Theme.Violet);
        gaugeCard.Child = gaugeBody;

        var right = Ui.V(Theme.SectionSpacing,
            ChartCard(L("Tokens per seconde"), Glyph.Bolt, Theme.Violet, tokensValue, tokens),
            ChartCard(L("GPU en CPU"), Glyph.Cpu, Theme.Accent, loadValue, load));
        var top = Ui.Row("300,*", Theme.SectionSpacing, gaugeCard, right);

        var memory = Ui.V(6,
            Ui.Row("auto,*", 8, Ui.Label("VRAM", 11, Theme.Solid(Theme.Violet), FontWeights.SemiBold), vramValue), vram,
            Ui.Row("auto,*", 8, Ui.Label("RAM", 11, Theme.Solid(Theme.Pink), FontWeights.SemiBold), ramValue), ram);
        var bottom = new Grid { ColumnSpacing = Theme.SectionSpacing };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.Children.Add(ChartCard(L("Geheugen"), Glyph.Memory, Theme.Pink, null, memory).At(0, 0));
        bottom.Children.Add(ChartCard(L("SSD lezen"), Glyph.Drive, Theme.Amber, diskValue, disk).At(0, 1));

        View = Ui.Page(head, top, bottom, strip.View);
        app.WatchSamples(View, Update);
        app.Watch(View, UpdatePlan);
    }

    private static TextBlock Stat(Color c)
    {
        var t = Ui.Label("0", 18, Theme.Solid(c), FontWeights.Bold, display: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }

    private static TextBlock Value()
    {
        var t = Ui.Label("", 13, Theme.Secondary);
        t.HorizontalAlignment = HorizontalAlignment.Right;
        return t;
    }

    private static StackPanel MiniStat(TextBlock value, string label)
    {
        var l = Ui.Label(label, 10, Theme.Secondary, FontWeights.SemiBold);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.TextAlignment = TextAlignment.Center;
        return Ui.V(2, value, l);
    }

    private static Border ChartCard(string title, string icon, Color color, TextBlock? value, UIElement chart)
    {
        var head = Ui.Row("auto,auto,*", 8, Ui.Icon(icon, 14, Theme.Solid(color)), Ui.Label(title, 14, Theme.Solid(color), FontWeights.SemiBold, display: true), value);
        return Ui.Card(Ui.V(12, head, chart), glow: color);
    }

    private void Update()
    {
        var samples = app.Host.Metrics.Samples;
        var now = samples.Count > 0 ? samples[^1] : null;
        double rate = now?.TokensPerSec ?? 0;
        mark.Active = rate > 0;
        gauge.Set(rate);
        gpu.SetIfChanged($"{now?.Gpu ?? 0:0}");
        cpu.SetIfChanged($"{now?.Cpu ?? 0:0}");
        ssd.SetIfChanged($"{now?.SsdMBs ?? 0:0}");
        hit.SetIfChanged(now?.VramHit is { } h ? $"{h * 100:0}%" : "–");
        heat.SetIfChanged(now?.GpuTemperature is { } t ? $"{t:0}" : "–");

        var culture = AppLanguages.Current.Culture();
        string F(double v, string format) => v.ToString(format, culture);
        tokensValue.SetIfChanged(F(rate, "0.0") + " tok/s");
        loadValue.SetIfChanged($"GPU {F(now?.Gpu ?? 0, "0")}% · CPU {F(now?.Cpu ?? 0, "0")}%");
        double k = ByteFormat.GibPerGb;
        vramValue.SetIfChanged($"{F((now?.VramUsedGB ?? 0) * k, "0.0")} / {F((now?.VramTotalGB ?? 0) * k, "0")} GB");
        ramValue.SetIfChanged($"{F((now?.RamUsedGB ?? 0) * k, "0")} / {F((now?.RamTotalGB ?? 0) * k, "0")} GB");
        diskValue.SetIfChanged(MiniDashboard.Disk(now?.SsdMBs ?? 0));

        tokens.Set(samples, 30);
        load.Set(samples, 100);
        double vramTotal = (now?.VramTotalGB ?? 0) * k, ramTotal = (now?.RamTotalGB ?? 0) * k;
        vram.Set(samples, vramTotal > 0 ? vramTotal * 1.08 : null, vramTotal > 0 ? (vramTotal, $"{vramTotal:0} GB") : null);
        // what the engine pins: its plan, as the sample has no figure for it; only while it runs
        var plan = app.EngineState.IsActive ? app.Host.Plan : null;
        ram.Set(samples, ramTotal > 0 ? ramTotal * 1.08 : null, ramTotal > 0 ? (ramTotal, $"{ramTotal:0} GB") : null,
            plan is { RamExperts: > 0 } ? ((plan.RamExperts + plan.KvRam) / 1e9 * k, L($"gepind volgens plan {(plan.RamExperts + plan.KvRam).GbShort()}")) : null);
        disk.Set(samples, null);
    }

    private void UpdatePlan()
    {
        var plan = app.Host.Plan;
        model.SetIfChanged(app.Host.Engine?.Model?.Name ?? app.Host.SelectedModel?.Name ?? L("Geen model"));
        strip.Set(plan);
        if (plan is { Experts: > 0 })
        {
            double all = plan.Experts, v = (plan.GpuExperts + plan.ExpertCache) / all * 100, r = plan.RamExperts / all * 100, s = Math.Max(100 - v - r, 0);
            spread.SetIfChanged($"{v:0}/{r:0}/{s:0}");
        }
        else spread.SetIfChanged("–");
    }
}
