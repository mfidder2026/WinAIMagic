using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>The models: one card each (download, select), then the selected model's plan, engine and API, and the folder.</summary>
public sealed class ModelsPage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly TextBlock subtitle = Ui.Label("", 14, Theme.Secondary);
    private readonly List<ModelCard> cards;
    private readonly PlanStrip strip = new();
    private readonly PlanDetails details;
    private readonly EngineCard engine;
    private readonly ApiCard api;
    private readonly FolderCard folder;
    private bool lastHeaders;

    public ModelsPage(AppState app)
    {
        this.app = app;
        cards = app.Host.Store.Catalog.Models.Select(m => new ModelCard(app, m)).ToList();
        var grid = new Grid { ColumnSpacing = Theme.SectionSpacing, RowSpacing = Theme.SectionSpacing };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cards.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(cards[i].View.At(i / 2, i % 2));
        }
        details = new PlanDetails(app);
        engine = new EngineCard(app);
        api = new ApiCard(app);
        folder = new FolderCard(app);
        View = Ui.Page(Ui.PageHead(Ui.PageIcon(Glyph.Models), L("Modellen"), subtitle), grid, strip.View, details.View, engine.View, api.View, folder.View);
        app.Watch(View, Refresh);
        View.Loaded += (_, _) => Task.Run(() => { app.Host.RefreshSystem(); app.Host.LoadIndex(); });
    }

    public void ChangeFolder(string to) => _ = folder.Change(to);

    private void Refresh()
    {
        var host = app.Host;
        var s = host.System;
        subtitle.SetIfChanged(L($"{s.VramTotal.Gib()} GB VRAM · {s.RamTotal.Gib()} GB RAM · {s.FreeDisk.GbShort()} vrij op schijf"));
        foreach (var c in cards) c.Update();
        bool headers = host.Store.StatusOf(host.SelectedModelId).HeadersAvailable;
        if (headers != lastHeaders)
        {
            lastHeaders = headers;
            if (headers && host.Index == null) Task.Run(host.LoadIndex);
        }
        var plan = host.Plan;
        strip.Set(plan);
        details.Set(plan);
        engine.Update();
        api.Update();
        folder.Update();
    }
}

public sealed class ModelCard
{
    public Border View { get; }
    private readonly AppState app;
    private readonly ModelInfo model;
    private readonly ModelStore store;
    private readonly ArcRing ring;
    private readonly TextBlock active;
    private readonly Border activeBadge;
    private readonly WrapPanel badges = new();
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock progress = Ui.Caption();
    private readonly StackPanel progressPart;
    private readonly TextBlock message = Ui.Caption();
    private readonly TextBlock note = Ui.Caption(color: Theme.Solid(Theme.Amber));
    private readonly TextBlock error = Ui.Caption(color: Theme.Solid(Theme.Pink));
    private readonly Button download, visionDownload, pause, delete, choose;
    private readonly UIElement chosen;
    private readonly Brush restEdge;
    private string badgeKey = "", noteKey = "";
    private static readonly Dictionary<string, string> DeleteErrors = new();

    public ModelCard(AppState app, ModelInfo model)
    {
        this.app = app;
        this.model = model;
        store = app.Host.Store;
        var family = Theme.Family(model.Family);
        ring = new ArcRing(46, 5, Theme.FamilyGradient(model.Family), Theme.Solid(family));
        ring.VerticalAlignment = VerticalAlignment.Top;
        active = Ui.Label(L("ACTIEF"), 10, Theme.Solid(Theme.Mint), FontWeights.Black);
        activeBadge = Ui.Rounded(Theme.Solid(Theme.Mint, 0.2), 10, active);
        activeBadge.Padding = new Thickness(8, 3, 8, 3);
        activeBadge.VerticalAlignment = VerticalAlignment.Top;
        var head = Ui.Row("auto,*,auto", 12, ring, Ui.V(3, Ui.Label(model.Name, 18, null, FontWeights.Bold, display: true), Ui.Caption(LKey(model.Summary))), activeBadge);

        progressPart = Ui.V(5, bar, progress, message);
        download = Ui.Btn(L("Download"), () => _ = Download(), Glyph.Download, family);
        visionDownload = Ui.Btn(L("Afbeeldingsbestanden"), () => store.StartVisionDownload(model.Id), Glyph.Image);
        pause = Ui.Btn(L("Pauzeer"), () => store.StopDownload(model.Id), Glyph.Pause);
        delete = Ui.Btn(L("Verwijder model"), () => _ = ConfirmDelete(app, model), Glyph.Trash);
        choose = Ui.Btn(L("Kies"), () => app.Select(model.Id));
        chosen = Ui.H(6, Ui.Icon(Glyph.Done, 14, Theme.Solid(family)), Ui.Label(L("Gekozen"), 13, Theme.Solid(family)));
        var left = Ui.H(8, download, visionDownload, pause, delete);
        var right = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { choose, chosen } };
        var foot = Ui.Row("*,auto", 8, left, right);
        foot.VerticalAlignment = VerticalAlignment.Bottom;

        var body = new Grid { RowSpacing = 12 };
        for (int i = 0; i < 4; i++) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(head.At(0));
        body.Children.Add(badges.At(1));
        body.Children.Add(progressPart.At(2));
        body.Children.Add(Ui.V(4, note, error).At(3));
        body.Children.Add(foot.At(4));
        View = Ui.Card(null, glow: family);
        View.Child = body;      // a grid: the buttons stay at the bottom of a card as high as its neighbour
        restEdge = Theme.Solid(Colors.White, 0.08);
        View.BorderBrush = restEdge;
    }

    public void Update()
    {
        var st = store.StatusOf(model.Id);
        var host = app.Host;
        bool downloading = store.Downloading.Contains(model.Id);
        bool selected = host.SelectedModelId == model.Id;
        bool running = host.Engine?.Model?.Id == model.Id && app.EngineState.IsRunning;
        bool inUse = host.Engine?.Model?.Id == model.Id && app.EngineState.IsActive;
        var pack = store.PackProgress.TryGetValue(model.Id, out var p) ? p : -1;
        bool building = pack != -1;

        ring.Set(st.Fraction);
        ring.Mark.Glyph = st.IsComplete ? Glyph.Check : downloading ? Glyph.Download : Glyph.CloudDown;
        activeBadge.Show(running);
        View.BorderBrush = selected ? Theme.FamilyGradient(model.Family) : restEdge;
        View.BorderThickness = new Thickness(selected ? 2 : 1);
        View.Padding = new Thickness(Theme.CardPadding - (selected ? 1 : 0));

        // badges: size, the plan's label, a measured speed or that it is not measured, what it can do
        var fit = host.FitOf(model);
        var skip = host.SkipExpertsFor(model);
        var bench = host.Bench.Results.LastOrDefault(r => r.ModelId == model.Id && r.SkipExperts == skip && r.DecodeTps > 0);
        var visionFiles = store.VisionStatus(model);
        bool vision = store.VisionPaths(model).Count > 0;
        bool gettingVision = downloading && st.IsComplete && !vision;
        bool recommended = host.Recommended?.Id == model.Id;
        var key = $"{fit}|{bench?.Id}|{vision}|{recommended}";
        if (key != badgeKey)
        {
            badgeKey = key;
            badges.Children.Clear();
            badges.Children.Add(Ui.Badge(model.TotalSize.GbShort(), Glyph.Drive));
            badges.Children.Add(fit == MemoryPlanner.Fit.Fits
                ? Ui.Badge(L("Past in VRAM + RAM"), Glyph.Memory, Theme.Mint)
                : Ui.Badge(L("Streamt van SSD"), Glyph.Bolt, Theme.Amber));
            badges.Children.Add(bench != null
                ? Ui.Badge(L($"{bench.DecodeTps:0.0} tok/s gemeten op deze pc"), Glyph.Speed, fit == MemoryPlanner.Fit.Fits ? Theme.Mint : Theme.Amber)
                : fit == MemoryPlanner.Fit.Streams && MemoryPlanner.IsSmallPc(host.System)
                    ? Ui.Badge(L($"Op {host.System.RamTotal.Gib()} GB niet gemeten, kan traag zijn"), Glyph.Speed, Theme.Amber)
                    : Ui.Badge(L("Niet gemeten"), Glyph.Speed));
            if (vision) badges.Children.Add(Ui.Badge(L("Vision"), Glyph.Eye));
            if (model.HasMtp) badges.Children.Add(Ui.Badge("MTP", Glyph.Fast, Theme.Violet));
            if (recommended) badges.Children.Add(Ui.Badge(L("Aanbevolen"), Glyph.Star, Theme.Cyan));
        }

        // how far along, and what the download or the pack is doing
        progressPart.Show(!st.IsComplete || building || gettingVision);
        if (building)
        {
            bar.IsIndeterminate = pack == null;
            bar.Value = pack ?? 0;
            progress.SetIfChanged(L("Pack bouwen…"));
        }
        else if (gettingVision)
        {
            ulong have = visionFiles.Aggregate(0UL, (n, f) => n + f.Have), expected = visionFiles.Aggregate(0UL, (n, f) => n + f.Expected);
            bar.IsIndeterminate = false;
            bar.Value = expected == 0 ? 0 : (double)have / expected;
            progress.SetIfChanged(L($"Afbeeldingsbestanden: {have.GbShort()} van {expected.GbShort()}"));
        }
        else
        {
            bar.IsIndeterminate = false;
            bar.Value = st.Fraction;
            var eta = store.EtaSeconds(model.Id);
            progress.SetIfChanged(L($"{st.Have.GbShort()} van {st.Expected.GbShort()}") + (eta is { } e ? " · " + L($"{store.Speed / 1e6:0} MB/s · nog {Ui.Duration(e)}") : ""));
        }
        var said = (downloading || building) && store.LastMessage.TryGetValue(model.Id, out var m) ? m : "";
        message.SetIfChanged(said);
        message.Show(said.Length > 0);

        // what is in the way: a hard minimum, the limited mode, a drive that is too small
        var blocked = MemoryPlanner.Blocked(model, host.System);
        ulong missing = st.Expected > st.Have ? st.Expected - st.Have : 0;
        var noteId = $"{blocked}|{MemoryPlanner.EngineFor(model, host.System)}|{(st.IsComplete || downloading ? 0 : missing)}|{host.System.FreeDisk}";
        if (noteId != noteKey)
        {
            noteKey = noteId;
            var lines = new List<string>();
            if (blocked != null) lines.Add(blocked);
            else if (model.Engine == "tiered" && MemoryPlanner.EngineFor(model, host.System) == EngineKind.Llama)
                lines.Add(L("Op deze pc draait dit model in de beperkte modus, via de tweede engine: alles werkt, maar lager tempo en een kleinere context. Reden: ") + (host.System.TieredUnavailable ?? L("te weinig VRAM voor de hoofdengine.")));
            if (!st.IsComplete && !downloading && missing > host.System.FreeDisk)
            {
                lines.Add(L($"Op deze schijf is {host.System.FreeDisk.GbShort()} vrij; {model.ShortName} heeft nog {missing.GbShort()} nodig."));
                if (DriveReport.BetterDrive(Paths.ModelsRoot, missing) is { } better)
                    lines.Add(L($"Kies onder Modelmap een map op {better.Root}: daar is {better.FreeBytes.GbShort()} vrij."));
            }
            note.Text = string.Join("\n", lines);
            note.Show(lines.Count > 0);
        }
        var err = DeleteErrors.TryGetValue(model.Id, out var de) ? de : store.LastError.TryGetValue(model.Id, out var le) ? le : "";
        error.SetIfChanged(err);
        error.Show(err.Length > 0);

        download.Show(!st.IsComplete && !downloading);
        // the model is here and images are wanted, but their files are not: a clear action of its own, the same pinned download
        bool offerVision = st.IsComplete && !downloading && !building && !vision && visionFiles.Count > 0 && Settings.Shared.Vision;
        visionDownload.Show(offerVision);
        if (offerVision)
        {
            ulong size = visionFiles.Aggregate(0UL, (n, f) => n + f.Expected);
            visionDownload.Tip(L($"Download wat dit model nodig heeft om afbeeldingen te lezen ({size.GbShort()})."));
        }
        pause.Show(downloading);
        delete.Show(!downloading && !building && (st.Have > 0 || Directory.Exists(Paths.ModelDir(model.Id))));
        delete.IsEnabled = !inUse;
        delete.Tip(inUse ? L("Stop eerst de engine.") : null);
        chosen.Show(selected);
        choose.Show(!selected);
        choose.IsEnabled = st.HeadersAvailable;
    }

    private async Task Download()
    {
        if (model.License != null && store.StatusOf(model.Id).Have == 0 && !await AcceptLicense(app, model)) return;
        store.StartDownload(model.Id);
    }

    /// <summary>Models with a licence of their own show it before the download.</summary>
    public static async Task<bool> AcceptLicense(AppState app, ModelInfo model)
    {
        var name = model.License switch
        {
            "minimax-community" => "LICENSE.minimax-m31",
            "qwen-community" => "LICENSE.qwen-community-1.0",
            _ => "LICENSE." + model.License,
        };
        var file = Path.Combine(PackBuilder.TieredRoot, name);
        string? text = null;
        try { if (File.Exists(file)) text = await File.ReadAllTextAsync(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var body = Ui.V(10, Ui.Caption(L($"{model.Name} valt onder een eigen licentie ({model.License}). Lees die voordat je het model downloadt."), Theme.Primary, 13));
        if (text != null)
            body.Children.Add(new ScrollViewer
            {
                Content = Ui.Label(text, 12, Theme.Secondary, wrap: true, mono: true, select: true),
                Height = 340,
                Padding = new Thickness(0, 0, 14, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
        else
            body.Children.Add(Ui.Caption(L($"De licentietekst ontbreekt in deze installatie ({file}). Hij hoort bij het installatiepakket."), Theme.Solid(Theme.Amber), 13));
        return await Ui.Ask(app, L($"Licentie van {model.ShortName}"), body, L("Akkoord, download")) == ContentDialogResult.Primary;
    }

    /// <summary>Verwijder model: up to 460 GB, so it asks first.</summary>
    public static async Task ConfirmDelete(AppState app, ModelInfo model)
    {
        var have = app.Host.Store.StatusOf(model.Id).Have;
        var answer = await Ui.Ask(app, L($"{model.Name} verwijderen?"),
            L($"De map van dit model met alle bestanden ({have.Gb()}) wordt van de schijf verwijderd: {Paths.ModelDir(model.Id)}. Je kunt het model later opnieuw downloaden."),
            L("Verwijder model"), danger: true);
        if (answer != ContentDialogResult.Primary) return;
        var error = await Task.Run(() => app.Host.Store.DeleteModel(model.Id));
        if (error == null) DeleteErrors.Remove(model.Id); else DeleteErrors[model.Id] = error;
        _ = Task.Run(app.Host.LoadIndex);
    }
}

public sealed class PlanDetails
{
    public Border View { get; }
    private readonly AppState app;
    private readonly StackPanel body = Ui.V(8);
    private string key = "";

    public PlanDetails(AppState app)
    {
        this.app = app;
        View = Ui.Card(body, L("Geheugenplan"), Glyph.Memory, Theme.Violet);
    }

    public void Set(MemoryPlan? plan)
    {
        View.Show(plan != null);
        if (plan == null) return;
        var s = app.Host.System;
        bool shortPage = s.RequiredPageFile > 0 && s.PageFile < s.RequiredPageFile;
        var k = plan.ToString() + string.Join("|", plan.Warnings) + shortPage;
        if (k == key) return;
        key = k;

        var rows = new List<(string, string)> { (L("Vaste gewichten (VRAM)"), plan.Dense.Gb()) };
        if (plan.ExpertCache > 0)
        {
            rows.Add((L("Expert-cache (VRAM)"), plan.ExpertCache.Gb() + L(" · de experts die het gesprek het meest gebruikt")));
            rows.Add((L("Experts van de SSD"), plan.SsdExperts.Gb() + L(" · alleen als een woord ze nodig heeft")));
        }
        else
        {
            rows.Add((L("Experts in VRAM"), plan.GpuExperts.Gb()));
            rows.Add((L("Experts in RAM"), plan.RamExperts == 0 ? L("geen") : plan.RamExperts.Gb() + (plan.CpuMoELayers > 0 ? L($" · {plan.CpuMoELayers} lagen") : "")));
            if (plan.SsdExperts > 0) rows.Add((L("Experts van de SSD"), plan.SsdExperts.Gb() + L(" · alleen als een woord ze nodig heeft")));
        }
        if (plan.TensorSplit.Count > 0)
            rows.Add((L("Lagen per pc"), string.Join(" · ", plan.TensorSplit.SkipLast(1).Select(n => $"helper {n}")) + L($" · deze pc {Math.Max(plan.TensorSplit[^1], 1) - 1}")));
        if (plan.Engram > 0) rows.Add((L("Engram-tabellen (SSD)"), plan.Engram.Gb() + L(" · per token een paar rijen")));
        rows.Add((L("KV-cache + buffers"), plan.Overhead.Gb() + L($" · {plan.ContextSize / 1024}K context")));
        if (plan.KvRam > 0) rows.Add((L("KV-cache in RAM"), plan.KvRam.Gb()));
        rows.Add((L("VRAM-reserve"), plan.VramReserve.Gb()));

        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(Ui.Label(rows[i].Item1, 13, Theme.Secondary).At(i, 0));
            grid.Children.Add(Ui.Label(rows[i].Item2, 13, wrap: true).At(i, 1));
        }
        body.Children.Clear();
        var label = plan.Label == PlanLabel.FitsInVramRam ? Theme.Mint : Theme.Amber;
        body.Children.Add(Ui.Badge(plan.LabelText, plan.Label == PlanLabel.FitsInVramRam ? Glyph.Memory : Glyph.Bolt, label));
        ((FrameworkElement)body.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(grid);
        foreach (var w in plan.Warnings)
            body.Children.Add(Ui.Row("auto,*", 8, Ui.Icon(Glyph.Warning, 13, Theme.Solid(Theme.Amber)), Ui.Label(w, 13, Theme.Solid(Theme.Amber), wrap: true)));
        if (shortPage)
        {
            var b = Ui.Btn(L($"Wisselbestand instellen op {s.RequiredPageFile.Gib()} GB"), () => app.Perform(SystemCheck.CheckAction.SetPageFile), Glyph.Settings)
                .Tip(L("Vraagt om toestemming en werkt na een herstart."));
            b.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(b);
        }
    }
}

public sealed class EngineCard
{
    public Border View { get; }
    private readonly AppState app;
    private readonly TextBlock model = Ui.Label("", 14, Theme.Secondary);
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock failed = Ui.Caption(color: Theme.Solid(Theme.Pink), size: 13);
    private readonly TextBlock why = Ui.Caption(color: Theme.Solid(Theme.Amber), size: 13);
    private readonly TextBlock warnings = Ui.Caption(color: Theme.Solid(Theme.Amber), size: 13);
    private readonly Button buildPack;
    private readonly (string Label, string Value)[] skipOptions = SettingsDialog.SkipExpertsOptions();
    private readonly ComboBox skip;
    private readonly FrameworkElement skipPart;
    private readonly TextBlock log = Ui.Label("", 11, Theme.Secondary, wrap: true, mono: true, select: true);
    private readonly ScrollViewer logView;
    private bool logOpen;

    public EngineCard(AppState app)
    {
        this.app = app;
        var head = Ui.Row("auto,*,auto", 10, new StatusPill(app), model, new EngineButton(app));
        buildPack = Ui.Btn(L("Bouw het pack"), () => _ = app.Host.BuildPack(), Glyph.Fixed);
        buildPack.HorizontalAlignment = HorizontalAlignment.Left;
        skip = SettingsDialog.Pick(skipOptions, Settings.Shared.SkipExperts, v => Settings.Shared.SkipExperts = v);
        skip.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(skip, SettingsDialog.SkipExpertsTitle);
        skipPart = Ui.V(5, Ui.Row("*,auto", 12, Ui.Label(SettingsDialog.SkipExpertsTitle, 14, wrap: true), skip), Ui.Caption(SettingsDialog.SkipExpertsNote));
        logView = new ScrollViewer { Content = log, Height = 220, Padding = new Thickness(0, 0, 14, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        var chevron = Ui.Icon(Glyph.ChevronDown, 11, Theme.Secondary);
        var toggle = new Button { Content = Ui.H(7, chevron, Ui.Label(L("Log"), 13, Theme.Secondary)), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4, 3, 8, 3), CornerRadius = new CornerRadius(6) };
        toggle.Click += (_, _) =>
        {
            logOpen = !logOpen;
            chevron.Glyph = logOpen ? Glyph.ChevronUp : Glyph.ChevronDown;
            logView.Show(logOpen);
            Update();
        };
        var body = Ui.V(10, head, bar, failed, why, warnings, buildPack, skipPart, Ui.V(4, toggle, logView));
        View = Ui.Card(body, L("Engine"), Glyph.Bolt, Theme.Mint);
    }

    public void Update()
    {
        var host = app.Host;
        var state = app.EngineState;
        model.SetIfChanged(host.SelectedModel?.Name ?? "");
        bar.Show(state.Status == EngineStatus.Starting);
        bar.Value = host.Engine?.LoadProgress ?? 0;
        failed.SetIfChanged(state.Status == EngineStatus.Failed ? state.Message : "");
        failed.Show(failed.Text.Length > 0);
        var reason = state.IsActive ? null : host.WhyNotStartable;
        why.SetIfChanged(reason ?? "");
        why.Show(reason != null);
        var said = host.Engine?.Warnings is { Count: > 0 } w ? string.Join("\n", w) : "";
        warnings.SetIfChanged(said);
        warnings.Show(said.Length > 0);
        bool packMissing = host.SelectedModel is { } m && !state.IsActive && host.EngineForSelected == EngineKind.Tiered
            && host.Store.StatusOf(m.Id).IsComplete && !host.Store.PackProgress.ContainsKey(m.Id) && PackBuilder.WhyNotReady(m) != null;
        buildPack.Show(packMissing);
        bool offerSkip = host.SelectedModel is { } chosen && host.OffersSkipExperts(chosen);
        skipPart.Show(offerSkip);
        // also changed under Settings
        int skipIndex = Math.Max(Array.FindIndex(skipOptions, o => o.Value == Settings.Shared.SkipExperts), 0);
        if (offerSkip && skip.SelectedIndex != skipIndex) skip.SelectedIndex = skipIndex;
        if (logOpen)
        {
            var tail = host.Engine?.LogTail ?? [];
            log.SetIfChanged(tail.Count == 0 ? L("Nog geen log.") : string.Join("\n", tail.Skip(Math.Max(tail.Count - 200, 0))));
            logView.ChangeView(null, logView.ScrollableHeight + 1000, null, true);
        }
    }
}

public sealed class ApiCard
{
    public Border View { get; }
    private readonly AppState app;
    private readonly TextBlock openAi = Code(), anthropic = Code(), responses = Code();

    public ApiCard(AppState app)
    {
        this.app = app;
        View = Ui.Card(Ui.V(10,
            Ui.Caption(L("Zolang de engine draait, kunnen andere programma's het model gebruiken via dezelfde poort."), size: 13),
            Snippet(L("OpenAI-compatibel"), openAi),
            Snippet(L("Anthropic-compatibel (Claude Code)"), anthropic),
            Snippet("Responses API (Codex)", responses)), L("API voor andere apps"), Glyph.Network, Theme.Cyan);
    }

    private static TextBlock Code() => Ui.Label("", 13, null, mono: true, select: true);

    private static StackPanel Snippet(string title, TextBlock code)
    {
        var copy = Ui.IconBtn(Glyph.Copy, () => Ui.Copy(code.Text), L("Kopieer"));
        var box = Ui.Rounded(Theme.Solid(Colors.Black, 0.28), 8, Ui.Row("*,auto", 8, code, copy));
        box.Padding = new Thickness(10, 3, 4, 3);
        return Ui.V(4, Ui.Label(title, 12, Theme.Secondary), box);
    }

    public void Update()
    {
        var url = app.BaseUrl.ToString().TrimEnd('/');
        openAi.SetIfChanged($"$env:OPENAI_BASE_URL=\"{url}/v1\"");
        anthropic.SetIfChanged($"$env:ANTHROPIC_BASE_URL=\"{url}\"; claude");
        responses.SetIfChanged($"$env:OPENAI_BASE_URL=\"{url}/v1\"; codex   # {url}/v1/responses");
    }
}

/// <summary>Where the models live: any folder on any drive, with free space and NVMe per drive, a warning for a slow drive, a missing
/// drive as a message, and the offer to take the models along.</summary>
public sealed class FolderCard
{
    public Border View { get; }
    private readonly AppState app;
    private readonly TextBlock path = Ui.Label("", 12, null, wrap: true, mono: true, select: true);
    private readonly TextBlock missing = Ui.Caption(color: Theme.Solid(Theme.Pink), size: 13);
    private readonly TextBlock slow = Ui.Caption(color: Theme.Solid(Theme.Amber), size: 13);
    private readonly StackPanel drives = Ui.V(6);
    private readonly Button choose, standard, resume;
    private readonly StackPanel movePart;
    private readonly ProgressBar moveBar = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock moveText = Ui.Caption();
    private readonly TextBlock moveError = Ui.Caption(color: Theme.Solid(Theme.Pink), size: 13);
    private readonly TextBlock pendingText = Ui.Caption(color: Theme.Solid(Theme.Amber), size: 13);
    private CancellationTokenSource? moving;
    private string driveKey = "";
    private int progressPending;
    private ulong moveDone, moveTotal;
    private DateTime drivesRead = DateTime.MinValue;

    public FolderCard(AppState app)
    {
        this.app = app;
        choose = Ui.Btn(L("Kies map…"), () => _ = Choose());
        standard = Ui.Btn(L("Standaard"), () => _ = Change(Paths.DefaultModelsRoot));
        resume = Ui.Btn(L("Ga verder"), () =>
        {
            if (ModelFolderMover.Pending() is { } m) _ = MoveTo(m.To);
        }, Glyph.Play);
        var stop = Ui.Btn(L("Stop"), () => moving?.Cancel(), Glyph.Stop);
        movePart = Ui.V(6, moveBar, Ui.Row("*,auto", 8, moveText, stop)).Show(false);
        var pending = Ui.Row("*,auto", 8, pendingText, resume);
        View = Ui.Card(Ui.V(10,
            Ui.Caption(L("Elk model staat in een eigen submap; afbeeldingsmodellen in vision/."), size: 13),
            Ui.Row("*,auto,auto", 8, path, choose, standard),
            missing, drives, slow, pending, movePart, moveError), L("Modelmap"), Glyph.Folder, Theme.Amber);
        View.Tag = pending;
        View.Loaded += (_, _) => { driveKey = ""; drivesRead = DateTime.MinValue; };
    }

    public void Update()
    {
        var root = Paths.ModelsRoot;
        path.SetIfChanged(root);
        standard.Show(Settings.Shared.ModelFolder.Length > 0);
        bool gone = ModelStore.FolderMissing;
        missing.SetIfChanged(gone ? ModelStore.FolderMissingMessage : "");
        missing.Show(gone);

        // one drive query per half minute is plenty, unless the folder changed
        if (driveKey != root || (DateTime.UtcNow - drivesRead).TotalSeconds > 30)
        {
            driveKey = root;
            drivesRead = DateTime.UtcNow;
            var current = DriveReport.For(root);
            drives.Children.Clear();
            foreach (var d in DriveReport.All())
            {
                bool mine = string.Equals(d.Root, current.Root, StringComparison.OrdinalIgnoreCase);
                var kind = d.IsNvme ? Ui.Badge("NVMe", Glyph.Bolt, Theme.Mint)
                    : d.IsSsd == true ? Ui.Badge(L("SSD, geen NVMe"), Glyph.Drive, Theme.Amber)
                    : Ui.Badge(d.IsSsd == false ? L("Harde schijf") : L("Onbekend type"), Glyph.Drive, Theme.Amber);
                var row = Ui.Row("auto,*,auto", 10,
                    Ui.Label(d.Root, 13, null, mine ? FontWeights.SemiBold : FontWeights.Normal),
                    Ui.Label(L($"{d.FreeBytes.GbShort()} vrij van {d.TotalBytes.GbShort()}"), 13, Theme.Secondary),
                    Ui.H(8, mine ? Ui.Badge(L("In gebruik"), null, Theme.Accent) : null, kind));
                drives.Children.Add(row);
            }
            bool isSlow = current.Ready && !current.IsNvme;
            slow.Text = isSlow ? L($"{current.Root} is geen NVMe-schijf. Modellen die van de SSD streamen worden hierop trager. Kies een map op een NVMe-schijf als je die hebt.") : "";
            slow.Show(isSlow);
        }

        var pendingMove = moving == null ? ModelFolderMover.Pending() : null;
        bool canResume = pendingMove != null && string.Equals(Path.TrimEndingDirectorySeparator(pendingMove.From), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        ((FrameworkElement)View.Tag).Show(canResume);
        if (canResume) pendingText.SetIfChanged(L($"Een verplaatsing naar {pendingMove!.To} is niet afgemaakt. De originelen staan er nog."));
        bool free = moving == null;
        choose.IsEnabled = free;
        standard.IsEnabled = free;
    }

    private async Task Choose()
    {
        if (NativePickers.Folder(WinRT.Interop.WindowNative.GetWindowHandle(app.Window)) is { } folder) await Change(folder);
    }

    public async Task Change(string to)
    {
        var host = app.Host;
        var from = Paths.ModelsRoot;
        moveError.Show(false);
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase)) return;
        string? busy = app.EngineState.IsActive ? L("Stop eerst de engine; daarna kun je de map wijzigen.")
            : host.Store.Downloading.Count > 0 ? L("Er loopt nog een download. Pauzeer die eerst.") : null;
        if (busy != null)
        {
            await Ui.Ask(app, L("Modelmap"), busy, null, close: L("Sluiten"));
            return;
        }
        if (Directory.Exists(from) && Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Any())
        {
            var check = await Task.Run(() => ModelFolderMover.Prepare(from, to, false));
            var answer = check.Problem != null
                ? await Ui.Ask(app, L("Modellen verplaatsen?"), check.Problem, null, L("Alleen de map"))
                : await Ui.Ask(app, L("Modellen verplaatsen?"),
                    L($"In {from} staan modellen. Wil je ze meenemen naar {to}? Er is {check.Needed.Gb()} nodig en {check.Free.Gb()} vrij. Het origineel gaat pas weg als de kopie de juiste grootte heeft, en je kunt stoppen en later verder gaan."),
                    L("Verplaats"), L("Alleen de map"));
            if (answer == ContentDialogResult.None) return;
            if (answer == ContentDialogResult.Primary)
            {
                await MoveTo(to);
                return;
            }
        }
        Settings.Shared.ModelFolder = string.Equals(Path.GetFullPath(to), Path.GetFullPath(Paths.DefaultModelsRoot), StringComparison.OrdinalIgnoreCase) ? "" : to;
    }

    private async Task MoveTo(string to)
    {
        moving = new CancellationTokenSource();
        movePart.Show(true);
        moveError.Show(false);
        moveBar.Value = 0;
        moveText.Text = L("Verplaatsen…");
        var token = moving.Token;
        var error = await Task.Run(() => app.Host.MoveModels(to, (done, total) =>
        {
            moveDone = done;
            moveTotal = total;
            if (Interlocked.Exchange(ref progressPending, 1) == 0)
                app.Ui.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref progressPending, 0);
                    moveBar.Value = moveTotal == 0 ? 0 : (double)moveDone / moveTotal;
                    moveText.Text = L($"{moveDone.Gb()} van {moveTotal.Gb()} verplaatst");
                });
        }, token));
        moving = null;
        movePart.Show(false);
        moveError.SetIfChanged(error ?? "");
        moveError.Show(error != null);
        driveKey = "";
        app.Notify();
    }
}
