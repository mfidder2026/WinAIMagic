using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinAIMagic.Chat.Telegram;
using WinAIMagic.Core.Support;
using static WinAIMagic.App.Views.SettingsDialog;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Instellingen → Telegram: your own bot, the accounts that may talk to it, and the notifications.</summary>
public sealed class TelegramTab
{
    private readonly AppState app;
    private readonly StackPanel root = Ui.V(14);
    private readonly TextBlock status = Ui.Label("", 14);
    private readonly StackPanel bot = Ui.V(10);
    private readonly StackPanel accounts = Ui.V(10);
    private readonly TextBlock log = new() { FontFamily = Theme.Mono, FontSize = 11, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Secondary };
    private readonly StackPanel logSection;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? clock;
    private string shown = "?";

    public FrameworkElement View => root;
    private TelegramService Service => app.Telegram;

    public TelegramTab(AppState app)
    {
        this.app = app;
        var s = Settings.Shared;
        var help = Ui.IconBtn(Glyph.Help, () => app.OpenHelp("telegram"), L("Hulp bij dit onderdeel"));
        logSection = Section(L("Log"), new ScrollViewer { Content = log, MaxHeight = 110 });
        root.Children.Add(Ui.Row("*,auto", 12, Note(L("Praat via Telegram met het model op deze pc, ook als je er niet bent. Het model blijft op de pc; je berichten en de antwoorden gaan wel via de servers van Telegram.")), help));
        root.Children.Add(Section(L("Telegram"),
            Toggle(L("Telegram"), s.FeatureTelegram, v => { s.FeatureTelegram = v; Service.Restart(); app.Notify(); }),
            Row(L("Status"), status)));
        root.Children.Add(Section(L("Bot"), bot));
        root.Children.Add(Section(L("Wie mag praten"), accounts));
        root.Children.Add(Section(L("Meldingen"),
            Toggle(L("Taken en heartbeat ook naar Telegram"), s.TelegramNotify, v => s.TelegramNotify = v),
            Note(L("Naar het eerst gekoppelde account: een taak klaar of gestopt, wat de heartbeat vond, en een goedkeuring die een taak nodig heeft (met knoppen). Beheerdersrechten keur je altijd op de pc goed."))));
        root.Children.Add(logSection);
        Refresh();
        root.Loaded += (_, _) =>
        {
            Service.Changed += OnChanged;
            clock = app.Ui.CreateTimer();
            clock.Interval = TimeSpan.FromSeconds(1);
            clock.Tick += (_, _) => Refresh();
            clock.Start();
        };
        root.Unloaded += (_, _) =>
        {
            Service.Changed -= OnChanged;
            clock?.Stop();
        };
    }

    private void OnChanged() => app.Ui.TryEnqueue(Refresh);

    private string StatusText => Service.Current switch
    {
        { State: TelegramService.State.Off } => L("Uit"),
        { State: TelegramService.State.NoToken } => L("Nog geen token"),
        { State: TelegramService.State.Connecting } => L("Verbinden…"),
        { State: TelegramService.State.Online } on => L($"Verbonden als {on.Text}"),
        var problem => problem.Text,
    };

    private void Refresh()
    {
        status.SetIfChanged(StatusText);
        var lines = Service.Log;
        log.SetIfChanged(string.Join("\n", lines.TakeLast(20)));
        logSection.Show(lines.Count > 0);
        var code = Service.Code;
        var left = code is { } c ? (int)Math.Ceiling((c.Until - DateTimeOffset.Now).TotalMinutes) : 0;
        if (code != null && left <= 0) { Service.CancelCode(); code = null; }
        // the rest only changes with the token, the accounts, the code or going online
        var key = $"{Service.Token != null}|{string.Join(",", Service.Accounts.Select(a => a.Id))}|{code?.Code}|{left}|{Service.IsOnline}|{Settings.Shared.Language}";
        if (key == shown) return;
        shown = key;
        bot.Children.Clear();
        if (Service.Token != null)
        {
            bot.Children.Add(Ui.Row("*,auto", 12, Ui.Label(L("Token staat in Windows Referentiebeheer."), 14, wrap: true),
                Ui.Btn(L("Verwijder token"), () => { Service.SetToken(""); shown = "?"; Refresh(); })));
        }
        else
        {
            var token = new PasswordBox { PlaceholderText = L("Token") };
            var save = Ui.Btn(L("Bewaar"), () =>
            {
                if (token.Password.Trim().Length == 0) return;
                Service.SetToken(token.Password);
                shown = "?";
                Refresh();
            });
            bot.Children.Add(Note(L("1. Open Telegram en zoek @BotFather.\n2. Stuur /newbot en kies een naam.\n3. Plak hier de token die je krijgt.")));
            bot.Children.Add(Ui.Row("*,auto", 8, token, save));
        }
        accounts.Children.Clear();
        if (Service.Accounts.Count == 0)
            accounts.Children.Add(Note(L("Nog niemand. Alleen een gekoppeld account krijgt antwoord; berichten van anderen negeert de bot.")));
        foreach (var a in Service.Accounts)
            accounts.Children.Add(Ui.Row("*,auto", 12, Ui.H(8, Ui.Label(a.Name, 14), Ui.Caption(a.Id.ToString())), Ui.Btn(L("Ontkoppelen"), () => Service.Unpair(a.Id))));
        if (code is { } shownCode)
        {
            var big = new TextBlock { Text = shownCode.Code, FontFamily = Theme.Mono, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true };
            accounts.Children.Add(Ui.V(6, Ui.Label(L("Stuur deze code naar je bot in Telegram:"), 14), big,
                Note(L($"Nog {left} min geldig.")), Ui.Btn(L("Annuleer"), Service.CancelCode)));
        }
        else
        {
            var pair = Ui.Btn(L("Koppel een account"), () => Service.NewCode());
            pair.IsEnabled = Service.IsOnline;
            accounts.Children.Add(pair);
        }
    }
}
