using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Instellingen: the tabs Chat and Engine (560 x 520). A choice counts at once.</summary>
public static class SettingsDialog
{
    private static bool open;

    public static async void Show(AppState app, int tab = 0, double scrollTo = 0)
    {
        if (open) return;
        open = true;
        var host = new Grid { Width = 560, Height = 520 };
        var dialog = Ui.Dialog(app);
        dialog.CloseButtonText = L("Sluiten");
        dialog.Content = host;
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        dialog.Resources["ContentDialogMaxHeight"] = 760.0;
        void Fill(int t) => BuildInto(host, app, t, Fill);
        Fill(tab);
        // a new language shows here at once too
        void OnLanguage()
        {
            dialog.CloseButtonText = L("Sluiten");
            Fill(0);
        }
        app.LanguageChanged += OnLanguage;
        if (scrollTo > 0) host.Loaded += (_, _) => host.Children.OfType<ScrollViewer>().First().ChangeView(null, scrollTo, null, true);
        await dialog.ShowAsync();
        app.LanguageChanged -= OnLanguage;
        open = false;
    }

    private static void BuildInto(Grid host, AppState app, int tab, Action<int> refill)
    {
        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition());
        var bar = new SelectorBar();
        var chat = new SelectorBarItem { Text = L("Chat"), Icon = Ui.Icon(Glyph.Chat) };
        var engine = new SelectorBarItem { Text = L("Engine"), Icon = Ui.Icon(Glyph.Cpu) };
        bar.Items.Add(chat);
        bar.Items.Add(engine);
        bar.SelectedItem = tab == 1 ? engine : chat;
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 8, 14, 0) }.At(1);
        scroll.Content = tab == 1 ? EngineTab(app) : ChatTab();
        bar.SelectionChanged += (_, _) =>
        {
            scroll.Content = bar.SelectedItem == engine ? EngineTab(app) : ChatTab();
            scroll.ChangeView(null, 0, null, true);
        };
        host.Children.Add(bar.At(0));
        host.Children.Add(scroll);
    }

    // MARK: - the tabs

    private static StackPanel ChatTab()
    {
        var s = Settings.Shared;
        var warnWrite = Note(L("Let op: het model kan dan zonder te vragen bestanden in de werkmap wijzigen of commando's uitvoeren."), Theme.Solid(Theme.Amber));
        void Warn() => warnWrite.Show(s.RightWrite == "allow" || s.RightRun == "allow");
        Warn();

        var temperature = Ui.Label(L($"Temperatuur {s.Temperature:0.0}"), 14);
        var slider = new Slider { Minimum = 0, Maximum = 1.5, StepFrequency = 0.1, Value = s.Temperature, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            s.Temperature = Math.Round(e.NewValue, 1);
            temperature.Text = L($"Temperatuur {s.Temperature:0.0}");
        };
        var prompt = new TextBox { Text = s.SystemPrompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 170, PlaceholderText = L("Systeemprompt") };
        ScrollViewer.SetVerticalScrollBarVisibility(prompt, ScrollBarVisibility.Auto);
        prompt.TextChanged += (_, _) => s.SystemPrompt = prompt.Text;

        return Tab(
            Section(L("Taal"),
                Row(L("Taal van de app"), Pick(Enum.GetValues<AppLanguage>().Select(l => (l.Name(), l.Code())), s.Language, v => s.Language = v)),
                Note(L("Ook het model krijgt zijn instructies in deze taal."))),
            Section(L("Antwoorden"),
                Row(L("Denkniveau"), Pick([(L("Laag"), "low"), (L("Gemiddeld"), "medium"), (L("Hoog"), "high"), (L("Maximaal"), "max")], s.ReasoningEffort, v => s.ReasoningEffort = v)),
                Row(L("Maximaal nadenken"), Pick(new[] { 4_000, 8_000, 16_000, 32_000 }.Select(n => (L($"{n:N0} tokens"), n)).Append((L("Onbeperkt"), 0)), s.ThinkingBudget, v => s.ThinkingBudget = v)),
                Note(L("Daarna schrijft het model zijn antwoord. Zonder grens kan een model bij een lastige vraag blijven twijfelen en nooit antwoorden.")),
                Ui.Row("*,auto", 8, temperature, slider),
                Stacked(L("Systeemprompt"), prompt),
                Ui.Row("*,auto", 12,
                    Note(L("Algemene notities voor het model (WINAIMAGIC.md), in elk gesprek. Een werkmap kan er een eigen WINAIMAGIC.md bij hebben.")),
                    Ui.Btn(L("Open bestand"), OpenNotes))),
            Section(L("Werkmap"),
                Toggle(L("Bestanden en commando's in de werkmap"), s.WorkspaceTools, v => s.WorkspaceTools = v),
                Toggle(L("Bewaar wat het model leert in WINAIMAGIC.md, zodat het fouten niet twee keer maakt"), s.LearnFromChats, v => s.LearnFromChats = v),
                Row(L("Lezen (mappen en bestanden)"), Pick(Rights(), s.RightRead, v => s.RightRead = v)),
                Row(L("Schrijven (bestanden maken en aanpassen)"), Pick(Rights(), s.RightWrite, v => { s.RightWrite = v; Warn(); })),
                Row(L("Uitvoeren (commando's, scripts, apps)"), Pick(Rights(), s.RightRun, v => { s.RightRun = v; Warn(); })),
                warnWrite,
                Note(s.Workspace.Length == 0 ? L("Nog geen werkmap gekozen (knop in de chat).") : s.Workspace)),
            Section(L("Web zoeken"),
                Secret(L("Brave Search API-sleutel (optioneel)"), s.BraveKey, v => s.BraveKey = v),
                Note(L("Zonder sleutel zoekt WinAIMagic via DuckDuckGo."))));
    }

    private static StackPanel EngineTab(AppState app)
    {
        var s = Settings.Shared;
        var host = new TextBox { Text = s.Host, Width = 200 };
        host.TextChanged += (_, _) => s.Host = host.Text.Trim();
        var port = new NumberBox { Value = s.Port, Minimum = 1, Maximum = 65535, Width = 200 };
        port.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) s.Port = (int)e.NewValue; };

        ulong profile = app.Host.Backend.VramReserve(-1) >> 20;
        var reserves = new[] { 500, 700, 1000, 1500, 2000, 3000, 4000 }.Select(n => (L($"{n} MiB"), n)).Prepend((L($"Uit het profiel ({profile} MiB)"), -1));
        if (s.VramReserveMib >= 0 && !new[] { 500, 700, 1000, 1500, 2000, 3000, 4000 }.Contains(s.VramReserveMib))
            reserves = reserves.Append((L($"{s.VramReserveMib} MiB"), s.VramReserveMib));

        var sys = app.Host.System;
        bool shortPage = sys.RequiredPageFile > 0 && sys.PageFile < sys.RequiredPageFile;
        UIElement pageFile = Ui.Row("*,auto", 12,
            Note(shortPage
                ? L($"Het wisselbestand is {sys.PageFile.Gib()} GB; de engine heeft minstens {sys.RequiredPageFile.Gib()} GB nodig (VRAM + 9 GB), anders zet hij te weinig vast en wordt hij veel trager. Vraagt om toestemming en werkt na een herstart.")
                : L($"Het wisselbestand is {sys.PageFile.Gib()} GB, genoeg voor het vastzetten van het model.")),
            shortPage ? Ui.Btn(L("Stel in"), () => app.Perform(SystemCheck.CheckAction.SetPageFile), null, Theme.Accent) : null);

        return Tab(
            Section(L("Model"),
                Row(L("Context"), Pick([("32K", 32_768), ("64K", 65_536), ("128K", 131_072), ("256K", 262_144), ("512K", 524_288), ("1M", 1_048_576)], s.ContextSize, v => s.ContextSize = v)),
                Note(L("Meer context kost geheugen (KV-cache): bij 1M ~6 GB voor DeepSeek, ~20 GB voor GLM. Qwen gaat tot 256K. Een vol gesprek wordt automatisch samengevat.")),
                Toggle(L("Afbeeldingen (vision, 1 GB extra)"), s.Vision, v => s.Vision = v),
                Toggle(L("Sneller schrijven met MTP (GLM en Qwen)"), s.Mtp, v => s.Mtp = v),
                Toggle(L("Engine starten bij openen app"), s.AutoStart, v => s.AutoStart = v),
                Toggle(L("Bewegende achtergrond"), s.AnimatedBackground, v => s.AnimatedBackground = v)),
            Section(L("Videokaart"),
                Row(L("VRAM-reserve"), Pick(reserves, s.VramReserveMib, v => s.VramReserveMib = v)),
                Note(L("Een volle kaart geeft op Windows geen fout, maar de snelheid zakt dan naar enkele tokens per seconde. Deze ruimte blijft vrij naast de engine.")),
                Stacked(L("Wisselbestand"), pageFile),
                Row(L("Engine-backend"), Pick([(L("Automatisch"), ""), .. Engine.GpuProfiles.Choices.Select(c => (c.Label, c.Id))], s.GpuBackendOverride, v => s.GpuBackendOverride = v)),
                Note(app.Host.Choice.Reason + " " + L("Een andere keuze geldt na het herstarten van de app."))),
            Section(L("Server"),
                Row(L("Adres"), host),
                Row(L("Poort"), port),
                Secret(L("API-sleutel (leeg = alleen deze pc)"), s.ApiKey, v => s.ApiKey = v),
                Note(L("Wijzigingen gelden na het herstarten van de engine. Gebruik 0.0.0.0 alleen met een API-sleutel."))));
    }

    private static (string, string)[] Rights() => [(L("Toestaan"), "allow"), (L("Eerst vragen"), "ask"), (L("Weigeren"), "deny")];

    private static void OpenNotes()
    {
        var file = Paths.GeneralNotes;
        if (!File.Exists(file)) File.WriteAllText(file, L("# Notities voor het model\n\nSchrijf hier wat het model in elk gesprek moet weten.\n"));
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    // MARK: - the parts of a form

    private static StackPanel Tab(params UIElement[] sections) => Ui.V(14, sections);

    private static StackPanel Section(string title, params UIElement[] rows)
    {
        var body = Ui.V(10, rows);
        var card = Ui.Rounded(Theme.Solid(Microsoft.UI.Colors.White, 0.05), 12, body);
        card.Padding = new Thickness(14);
        return Ui.V(6, Ui.Bold(title, 13, Theme.Secondary), card);
    }

    private static Grid Row(string label, FrameworkElement control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row("*,auto", 12, Ui.Label(label, 14, wrap: true), control);
    }

    private static StackPanel Stacked(string label, UIElement control) => Ui.V(5, Ui.Label(label, 14), control);

    private static TextBlock Note(string text, Microsoft.UI.Xaml.Media.Brush? color = null) => Ui.Caption(text, color);

    private static Grid Toggle(string label, bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        t.Toggled += (_, _) => set(t.IsOn);
        return Ui.Row("*,44", 12, Ui.Label(label, 14, wrap: true), t);
    }

    private static Grid Secret(string label, string value, Action<string> set)
    {
        var p = new PasswordBox { Password = value, Width = 200 };
        p.PasswordChanged += (_, _) => set(p.Password);
        return Row(label, p);
    }

    private static ComboBox Pick<T>(IEnumerable<(string Label, T Value)> options, T current, Action<T> set) where T : notnull
    {
        var list = options.ToList();
        var c = new ComboBox { MinWidth = 200, ItemsSource = list.Select(o => o.Label).ToList() };
        c.SelectedIndex = Math.Max(list.FindIndex(o => o.Value.Equals(current)), 0);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(list[c.SelectedIndex].Value); };
        return c;
    }
}
