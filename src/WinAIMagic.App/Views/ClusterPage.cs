using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace WinAIMagic.App.Views;

/// <summary>Several PCs as one memory: choose this PC's role, see the helpers, and what the model gains from them. Only the second engine
/// can spread a model over PCs: for a model on the main engine this screen says so and offers the models that can.</summary>
public sealed class ClusterPage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly RadioButton mainRole, helperRole;
    private readonly TextBlock roleText = Ui.Label("", 13, Theme.Secondary, wrap: true);
    private readonly FrameworkElement mainSide, helperSide;

    // the main PC
    private readonly Border tieredCard;
    private readonly TextBlock tieredText = Ui.Label("", 13, Theme.Solid(Theme.Amber), wrap: true);
    private readonly StackPanel offers = Ui.V(8), helpers = Ui.V(10);
    private readonly FrameworkElement searching;
    private readonly TextBox manual = new() { PlaceholderText = L("Helper handmatig toevoegen (adres:poort)") };
    private readonly Button addManual;
    private readonly TextBlock localStat = Stat(), helperStat = Stat(), totalStat = Stat();
    private readonly TextBlock fit = Ui.Label("", 13, null, wrap: true), split = Ui.Caption();
    /// <summary>How far the model parts are on their way to the helpers, and what went wrong with a helper.</summary>
    private readonly TextBlock transfer = Ui.Label("", 13, Theme.Secondary, wrap: true), problem = Ui.Label("", 13, Theme.Solid(Theme.Amber), wrap: true);
    private string shownHelpers = "?", shownOffers = "?";

    // a helper
    private readonly Ellipse helperDot = new() { Width = 9, Height = 9 };
    private readonly TextBlock helperState = Ui.Bold("", 14);
    private readonly Button startHelper, stopHelper;
    private readonly FrameworkElement pageFileRow;
    private readonly TextBlock pageFileText = Ui.Label("", 13, null, wrap: true);
    private readonly ToggleSwitch lan = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly TextBlock addresses = Ui.Label("", 12, Theme.Secondary, wrap: true, select: true);
    private readonly Border logBox;
    private readonly TextBlock log = Ui.Label("", 12, Theme.Secondary, wrap: true, mono: true, select: true);
    private readonly ScrollViewer logScroll;

    public ClusterPage(AppState app)
    {
        this.app = app;
        var settings = app.Settings;

        mainRole = new RadioButton { Content = L("Hoofd-pc: draait de modellen"), GroupName = "cluster-role", MinWidth = 0 };
        helperRole = new RadioButton { Content = L("Helper: leent zijn GPU en geheugen uit"), GroupName = "cluster-role", MinWidth = 0 };
        mainRole.Checked += (_, _) => { if (settings.ClusterRole != "main") settings.ClusterRole = "main"; };
        helperRole.Checked += (_, _) => { if (settings.ClusterRole != "helper") settings.ClusterRole = "helper"; };
        var role = Ui.Card(Ui.V(6, Ui.V(0, mainRole, helperRole), roleText), L("Rol van deze pc"), "\uE7F4", Theme.Violet);

        // main PC
        var offerBody = Ui.V(10, tieredText, offers);
        tieredCard = Ui.Card(offerBody, L("Dit model kan niet in een cluster"), Glyph.Warning, Theme.Amber);

        var ring = new ProgressRing { Width = 16, Height = 16, IsActive = true };
        searching = Ui.H(8, ring, Ui.Label(L("Zoeken naar helpers op het netwerk…"), 14, Theme.Secondary));
        addManual = Ui.Btn(L("Voeg toe"), AddManual);
        addManual.IsEnabled = false;
        manual.TextChanged += (_, _) => addManual.IsEnabled = manual.Text.Trim().Length > 0;
        var helperCard = Ui.Card(Ui.V(12, searching, helpers, Ui.Row("*,auto", 8, manual, addManual)), L("Helpers"), Glyph.Network, Theme.Mint);

        var stats = Ui.H(24, StatBlock(localStat, L("Deze pc")), StatBlock(helperStat, L("Helpers")), StatBlock(totalStat, L("Totaal voor de GPU's")));
        var together = Ui.Card(Ui.V(10, stats, fit, split, transfer, problem, Ui.Caption(L("Wijzigingen gelden bij de volgende start van de engine."))), L("Samen"), Glyph.Memory, Theme.Violet);
        mainSide = Ui.V(Theme.SectionSpacing, tieredCard, helperCard, together);

        // a helper
        startHelper = Ui.Btn(L("Start helper"), () => Task.Run(() => app.Host.Cluster.StartHelper()), Glyph.Play, Theme.Mint);
        stopHelper = Ui.Btn(L("Stop helper"), () => app.Host.Cluster.StopHelper(), Glyph.Stop, Theme.Pink);
        var setPageFile = Ui.Btn(L("Stel in"), () => app.Perform(SystemCheck.CheckAction.SetPageFile), Glyph.Settings).Tip(L("Vraagt om toestemming en werkt na een herstart."));
        pageFileRow = Ui.Row("*,auto", 10, pageFileText, setPageFile);
        lan.IsOn = settings.ClusterAllowLan;
        lan.Toggled += (_, _) => settings.ClusterAllowLan = lan.IsOn;
        var warnIcon = Ui.Icon(Glyph.Warning, 13, Theme.Solid(Theme.Amber));
        warnIcon.VerticalAlignment = VerticalAlignment.Top;
        warnIcon.Margin = new Thickness(0, 2, 0, 0);
        logScroll = new ScrollViewer { Content = log, Height = 140, Padding = new Thickness(10, 8, 10, 8), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        logBox = Ui.Rounded(Theme.Solid(Windows.UI.Color.FromArgb(255, 0, 0, 0), 0.3), 8, logScroll);
        var helper = Ui.Card(Ui.V(12,
            Ui.Row("auto,*,auto,auto", 8, helperDot, helperState, startHelper, stopHelper),
            pageFileRow,
            Ui.Row("*,44", 12, Ui.Label(L("Ook bereikbaar via het lokale netwerk (niet alleen via een directe kabel)"), 14, wrap: true), lan),
            Ui.Row("auto,*", 8, warnIcon, Ui.Label(L("Let op: de helper heeft geen wachtwoord. Gebruik hem alleen op een directe kabel of een vertrouwd netwerk."), 13, Theme.Solid(Theme.Amber), wrap: true)),
            addresses, logBox), L("Helper"), Glyph.Cpu, Theme.Mint);
        helperSide = helper;

        var connect = Ui.Card(Ui.V(8,
            Ui.V(6,
                Ui.Label(L("1. Verbind elke helper met de hoofd-pc: een USB4- of Thunderbolt-kabel, of anders ethernet."), 13, null, wrap: true),
                Ui.Label(L("2. Een directe kabel verschijnt in Windows als netwerkverbinding en krijgt vanzelf een adres (169.254.x.x). Dat hoef je niet in te stellen."), 13, null, wrap: true),
                Ui.Label(L("3. Installeer WinAIMagic op de helpers, kies daar 'Helper' en druk op Start helper."), 13, null, wrap: true),
                Ui.Label(L("4. Op de hoofd-pc verschijnen de helpers hier vanzelf. Vink ze aan en start het model onder Modellen."), 13, null, wrap: true)),
            Ui.Caption(L("De helper luistert alleen op een directe kabel, tenzij je het lokale netwerk toestaat. Per woord gaat er maar enkele tientallen KB over de verbinding, dus USB4 is ruim genoeg."))),
            L("Zo sluit je de pc's aan"), Glyph.Network, Theme.Cyan);

        View = Ui.Page(
            Ui.PageHead(Ui.PageIcon(Glyph.Cluster), L("Cluster"), Ui.Label(L("Verbind meerdere pc's en gebruik hun geheugen samen voor één groot model."), 14, Theme.Secondary, wrap: true)),
            role, mainSide, helperSide, connect);
        app.Watch(View, Refresh);
        View.Loaded += (_, _) => app.Host.Cluster.RecheckManual();
        View.Loaded += (_, _) => addresses.SetIfChanged(L("Adressen van deze pc: ") + string.Join(" · ", ClusterController.Addresses().Select(a => $"{a.Interface} {a.Ip}")));
    }

    private static TextBlock Stat()
    {
        var t = Ui.Label("", 22, Theme.Cool, FontWeights.Bold, display: true);
        return t;
    }

    private static StackPanel StatBlock(TextBlock value, string label) => Ui.V(2, value, Ui.Caption(label));

    private void AddManual()
    {
        app.Host.Cluster.AddManual(manual.Text);
        manual.Text = "";
    }

    private void Refresh()
    {
        bool isMain = app.Settings.ClusterRole != "helper";
        if (mainRole.IsChecked != isMain) mainRole.IsChecked = isMain;
        if (helperRole.IsChecked == isMain) helperRole.IsChecked = !isMain;
        roleText.SetIfChanged(isMain
            ? L("Op deze pc chat je. Helpers die je aanvinkt, nemen een deel van het model op hun GPU.")
            : L("Deze pc rekent mee voor een hoofd-pc. Laat hem aan staan en aangesloten zolang het model draait."));
        mainSide.Show(isMain);
        helperSide.Show(!isMain);
        if (isMain) RefreshMain();
        else RefreshHelper();
    }

    private void RefreshMain()
    {
        var c = app.Host.Cluster;
        var culture = AppLanguages.Current.Culture();
        if (c.Transfer is { } t)
        {
            string sent = (t.Sent / 1e9).ToString("0.0", culture), whole = (t.Expected / 1e9).ToString("0", culture), rate = (t.BytesPerSecond / 1e6).ToString("0", culture);
            transfer.SetIfChanged(L($"Naar de helpers gestuurd: {sent} van ongeveer {whole} GB, {rate} MB/s. De eerste start stuurt alles; de helper bewaart het, dus een volgende start gaat sneller."));
        }
        transfer.Show(c.Transfer != null);
        problem.SetIfChanged(c.Problem ?? "");
        problem.Show(c.Problem != null);
        var host = app.Host;
        var cluster = host.Cluster;

        // a model on the main engine: helpers do nothing for it
        bool tiered = host.EngineForSelected == EngineKind.Tiered;
        tieredCard.Show(tiered);
        if (tiered)
        {
            tieredText.SetIfChanged(L($"{host.SelectedModel?.Name ?? ""} draait op de hoofdengine, en die kan een model niet over meerdere pc's verdelen. Alleen de tweede engine kan dat. Kies een model dat wel in een cluster kan:"));
            var models = host.ClusterModels;
            var key = string.Join("|", models.Select(m => m.Id + host.Store.StatusOf(m.Id).HeadersAvailable));
            if (key != shownOffers)
            {
                shownOffers = key;
                offers.Children.Clear();
                foreach (var m in models) offers.Children.Add(Offer(m));
            }
        }

        var all = cluster.Helpers;
        var chosen = cluster.Selected;
        searching.Show(all.Count == 0);
        var shown = string.Join("|", all.Select(h => h + chosen.Contains(h.Id).ToString()));
        if (shown != shownHelpers)
        {
            shownHelpers = shown;
            helpers.Children.Clear();
            foreach (var h in all) helpers.Children.Add(HelperRow(h, chosen.Contains(h.Id)));
        }
        helpers.Show(all.Count > 0);

        // (culture: above)
        double local = MemoryPlanner.LlamaVramLimit(host.System) / 1e9, extra = cluster.HelperBudgets.Aggregate(0UL, (n, b) => n + b) / 1e9;
        localStat.SetIfChanged($"{local.ToString("0", culture)} GB");
        helperStat.SetIfChanged($"+{extra.ToString("0", culture)} GB");
        totalStat.SetIfChanged($"{(local + extra).ToString("0", culture)} GB");

        var plan = host.ClusterApplies ? host.Plan : null;
        fit.Show(plan != null);
        split.Show(plan is { TensorSplit.Count: > 0 });
        if (plan == null) return;
        var name = host.SelectedModel?.Name ?? L("Het model");
        fit.SetIfChanged(plan.FitsInMemory
            ? L($"{name} past zo helemaal in het geheugen van het cluster.")
            : L($"{name} past nog niet helemaal: de meest gebruikte experts ({plan.ExpertCache.GbShort()}) staan in de GPU-cache van deze pc, de rest ({plan.SsdExperts.GbShort()}) komt van de SSD."));
        fit.Foreground = Theme.Solid(plan.FitsInMemory ? Theme.Mint : Theme.Amber);
        if (plan.TensorSplit.Count > 0)
        {
            var parts = cluster.Active.Zip(plan.TensorSplit).Select(p => $"{p.First.Name} {p.Second}");
            int own = Math.Max(plan.TensorSplit[^1], 1) - 1;
            split.SetIfChanged(L("Lagen: ") + string.Join(" · ", parts) + L($" · deze pc {own}"));
        }
    }

    private FrameworkElement Offer(ModelInfo m)
    {
        var ready = app.Host.Store.StatusOf(m.Id).HeadersAvailable;
        var dot = new Ellipse { Width = 10, Height = 10, Fill = Theme.FamilyGradient(m.Family) };
        Button action = ready
            ? Ui.Btn(L("Kies"), () => app.Select(m.Id), null, Theme.Accent)
            : Ui.Btn(L("Naar Modellen"), () => app.Pane = new Pane(PaneKind.Models));
        return Ui.Row("auto,*,auto", 10, dot, Ui.V(1, Ui.Label(m.Name, 14, Theme.Primary, FontWeights.SemiBold), Ui.Caption(LKey(m.Summary))), action);
    }

    private FrameworkElement HelperRow(ClusterController.Helper h, bool on)
    {
        var cluster = app.Host.Cluster;
        var box = new CheckBox { IsChecked = on, MinWidth = 0, Padding = new Thickness(0) };
        box.Click += (_, _) =>
        {
            var next = new HashSet<string>(cluster.Selected);
            if (box.IsChecked == true) next.Add(h.Id);
            else next.Remove(h.Id);
            cluster.Selected = next;
        };
        var line = h.Unreachable
            ? L($"{h.Host}:{h.Port} · niet bereikbaar: staat de helper aan?")
            : h.Manual
            ? L($"{h.Host}:{h.Port} · handmatig toegevoegd · {(int)h.GpuGb} GB")
            : L($"{h.Chip} · {(int)h.MemoryGb} GB · GPU {(int)h.GpuGb} GB · {h.Host}");
        var badge = h.ViaDirectLink ? Ui.Badge(L("Directe kabel"), Glyph.Bolt, Theme.Mint) : Ui.Badge(L("Netwerk"), Glyph.Globe, Theme.Amber);
        return Ui.Row("auto,auto,*,auto,auto", 10, box, Ui.Icon("\uE7F4", 16, Theme.Solid(Theme.Mint)),
            Ui.V(2, Ui.Bold(h.Name), Ui.Caption(line)), badge,
            h.Manual ? Ui.IconBtn(Glyph.Close, () => cluster.Remove(h), L("Verwijderen")) : null);
    }

    private void RefreshHelper()
    {
        var host = app.Host;
        var cluster = host.Cluster;
        bool running = cluster.HelperRunning;
        helperDot.Fill = running ? Theme.Solid(Theme.Mint) : Theme.Solid(Theme.Rgb(140, 145, 160));
        helperState.SetIfChanged(running ? L($"Helper actief op {cluster.HelperAddress}") : L("Helper uit"));
        startHelper.Show(!running);
        stopHelper.Show(running);
        if (lan.IsOn != app.Settings.ClusterAllowLan) lan.IsOn = app.Settings.ClusterAllowLan;

        // in place of the Mac's GPU limit: what a too small page file keeps from this PC
        var s = host.System;
        pageFileRow.Show(s.PageFile < s.RequiredPageFile);
        pageFileText.SetIfChanged(L($"Het wisselbestand is {s.PageFile.Gib()} GB; de engine heeft minstens {s.RequiredPageFile.Gib()} GB nodig (VRAM + 9 GB), anders zet hij te weinig vast en wordt hij veel trager. Vraagt om toestemming en werkt na een herstart."));

        var lines = cluster.HelperLog;
        logBox.Show(lines.Count > 0);
        var text = string.Join("\n", lines.TakeLast(60));
        if (log.Text == text) return;
        log.Text = text;
        logScroll.UpdateLayout();
        logScroll.ChangeView(null, logScroll.ScrollableHeight, null, true);
    }
}
