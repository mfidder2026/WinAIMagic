using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.App.Views.SettingsDialog;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Instellingen → Agent: mail, the browser, controlling other programs, and speech. All off by default.</summary>
public sealed class AgentTab
{
    private readonly AppState app;
    private readonly StackPanel apps = Ui.V(8);
    private readonly TextBlock refused = Note("", Theme.Solid(Theme.Amber));

    public FrameworkElement View { get; }

    public AgentTab(AppState app)
    {
        this.app = app;
        var s = Settings.Shared;
        var help = Ui.IconBtn(Glyph.Help, () => app.OpenHelp("agent"), L("Hulp bij dit onderdeel"));
        var outlook = OutlookMail.Installed
            ? Note(L("Het model leest, zoekt en vat je mail samen in de accounts van Outlook; WinAIMagic krijgt geen wachtwoord. Een concept opent in Outlook na een kaart; versturen vraagt altijd, ook na 'Altijd in dit gesprek'."))
            : Note(L("Klassiek Outlook staat niet op deze pc. Zonder Outlook kan het model alleen een concept klaarzetten in je standaard mailprogramma; lezen en versturen gaan via klassiek Outlook (de nieuwe Outlook en de Mail-app hebben er geen koppeling voor)."), Theme.Solid(Theme.Amber));
        var voices = Speech.Voices;
        var voicePick = Pick(new[] { (L("Standaard"), "") }.Concat(voices.Select(v => (v.DisplayName, v.Id))), s.VoiceId, v => s.VoiceId = v);
        refused.Show(false);
        RefreshApps();
        View = Tab(
            Ui.Row("*,auto", 12, Note(L("Laat het model ook buiten de chat werken: je e-mail lezen, op websites werken, andere programma's bedienen en praten. Alles staat standaard uit, en elke wijziging komt eerst op een kaart.")), help),
            Section(L("E-mail"),
                Toggle(L("E-mail via Outlook"), s.FeatureMail, v => s.FeatureMail = v),
                outlook),
            Section(L("Browser"),
                Toggle(L("Browser in een eigen venster"), s.FeatureBrowser, v => s.FeatureBrowser = v),
                Note(L("Het model surft in een eigen venster van WinAIMagic dat je kunt meekijken, met eigen cookies, los van Edge en Chrome. Inloggen doe je zelf in dat venster. Een klik op een knop vraagt eerst; versturen, kopen, plaatsen of verwijderen vraagt elke keer. Wachtwoord- en betaalvelden vult het model nooit in."))),
            Section(L("Programma's besturen"),
                Toggle(L("Het model mag deze programma's besturen"), s.FeatureComputerUse, v => s.FeatureComputerUse = v),
                apps,
                Ui.Btn(L("Programma toevoegen…"), AddApp),
                refused,
                Note(L("Het model ziet de knoppen, velden en menu's van het venster en drukt, typt of geeft toetsen, elke keer na een kaart. Wachtwoordvelden, opdrachtvensters en PowerShell, de Register-editor, Windows-instellingen en wachtwoordmanagers zijn nooit toegestaan."))),
            Section(L("Spraak"),
                Toggle(L("Spraak (inspreken en voorlezen)"), s.FeatureVoice, v => s.FeatureVoice = v),
                Toggle(L("Lees antwoorden vanzelf voor"), s.SpeakAnswers, v => s.SpeakAnswers = v),
                Row(L("Stem"), voicePick),
                SpeechModelRow(),
                Note(L("Met de microfoon naast het invoerveld spreek je een vraag in; na twee seconden stilte gaat hij vanzelf. Het spraakmodel verstaat je op deze pc, ook spraakberichten uit Telegram: wat je zegt gaat nergens heen. De luidspreker onder een antwoord leest het voor, met de stemmen van Windows, ook op deze pc. Meer stemmen komen met een taal in Windows Instellingen → Tijd en taal → Spraak."))));
    }

    /// <summary>The speech model: what it is, and downloading or removing it.</summary>
    private FrameworkElement SpeechModelRow()
    {
        var store = app.Host.Store;
        var model = app.SpeechModel;
        if (model == null) return new Grid();
        var state = Ui.Caption("");
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 160 };
        var download = Ui.Btn(L("Download"), () => store.StartDownload(model.Id), Glyph.Download, Theme.Violet);
        var pause = Ui.Btn(L("Pauzeer"), () => store.StopDownload(model.Id));
        var remove = Ui.Btn(L("Verwijder"), () => { if (store.DeleteModel(model.Id) is { } why) state.Text = why; });
        var row = Ui.Row("*,auto", 12, Ui.V(2, Ui.Label(L($"Spraakmodel: {model.Name}"), 14), state), Ui.H(8, bar, download, pause, remove));
        void Refresh()
        {
            bool ready = app.SpeechModelReady;
            var fraction = app.SpeechDownload;
            string? error = store.LastError.TryGetValue(model.Id, out var e) ? e : null;
            state.Text = error ?? (ready ? L($"Gedownload, {model.TotalSize.Gb()}. Verstaat 99 talen, op deze pc.")
                : fraction is { } f ? L($"Downloaden… {f:P0} van {model.TotalSize.Gb()}")
                : L($"Nodig om in te spreken, {model.TotalSize.Gb()}. Zonder dit model werkt alleen voorlezen."));
            bar.Show(fraction != null);
            if (fraction is { } p) bar.Value = p;
            download.Show(!ready && fraction == null);
            pause.Show(fraction != null);
            remove.Show(ready);
        }
        Refresh();
        app.Watch(row, Refresh);
        return row;
    }

    private void RefreshApps()
    {
        apps.Children.Clear();
        foreach (var path in ComputerTools.Shared.Allowed)
        {
            var remove = Ui.Btn(L("Verwijder"), () => { ComputerTools.Shared.Disallow(path); RefreshApps(); });
            apps.Children.Add(Ui.Row("*,auto", 12, Ui.V(2, Ui.Label(ComputerTools.NameOf(path), 14), Ui.Caption(path)), remove));
        }
    }

    private void AddApp()
    {
        var chosen = NativePickers.Files(WinRT.Interop.WindowNative.GetWindowHandle(app.Window), true, [".exe"]);
        var no = new List<string>();
        foreach (var path in chosen)
            if (!ComputerTools.Shared.Allow(path)) no.Add(Path.GetFileName(path));
        refused.Text = no.Count == 0 ? "" : L($"Niet toegestaan: deze programma's voeren opdrachten uit, bevatten wachtwoorden of beheren Windows, en het model mag ze nooit besturen: {string.Join(", ", no)}.");
        refused.Show(no.Count > 0);
        RefreshApps();
    }
}
