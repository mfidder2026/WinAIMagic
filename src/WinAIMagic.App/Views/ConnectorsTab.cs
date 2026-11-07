using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinAIMagic.Chat;
using static WinAIMagic.App.Views.SettingsDialog;
using static WinAIMagic.Core.Support.Lang;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace WinAIMagic.App.Views;

/// <summary>Instellingen → Connectors: the MCP servers the model may use, each with its own right. The editor and the log open in
/// the tab itself: a second dialog cannot open over Instellingen.</summary>
public sealed class ConnectorsTab
{
    private readonly AppState app;
    private readonly Border host = new();
    // made again for every list: an element can stand in one place of the tree only
    private StackPanel list = Ui.V(10);
    private TextBlock notice = Ui.Caption("");
    private string shown = "?";

    public FrameworkElement View => host;
    private McpManager Mcp => app.Connectors;

    public ConnectorsTab(AppState app)
    {
        this.app = app;
        ShowList();
        host.Loaded += (_, _) => Mcp.Changed += OnChanged;
        host.Unloaded += (_, _) => Mcp.Changed -= OnChanged;
    }

    private void OnChanged() => app.Ui.TryEnqueue(() => { if (host.Child is StackPanel) Refresh(); });

    private void ShowList()
    {
        var help = Ui.IconBtn(Glyph.Help, () => app.OpenHelp("connectors"), L("Hulp bij dit onderdeel"));
        var presets = new MenuFlyout();
        foreach (var p in McpManager.Presets())
        {
            var item = new MenuFlyoutItem { Text = p.Config.Name, IsEnabled = Mcp.Config(p.Config.Name) == null };
            item.Click += (_, _) => AddPreset(p);
            presets.Items.Add(item);
        }
        presets.Items.Add(new MenuFlyoutItem { Text = L("Agenda en Herinneringen: niet beschikbaar op Windows"), IsEnabled = false });
        var example = new DropDownButton { Content = L("Voorbeeld toevoegen"), Flyout = presets };
        var add = Ui.Btn(L("Toevoegen…"), () => ShowEditor(null, new McpServerConfig { Right = "ask" }));
        var import = Ui.Btn(L("Importeer"), Import).Tip(L("Neemt connectors over uit Claude Desktop en Claude Code; ze komen uitgeschakeld binnen."));
        shown = "?";
        var said = notice.Text;
        list = Ui.V(10);
        notice = Ui.Caption(said);
        host.Child = Tab(
            Ui.Row("*,auto", 12, Note(L("Connectors geven het model gereedschap van andere programma's en diensten (MCP). Het laadt een connector pas als het hem nodig heeft. Tools die alleen lezen mogen standaard; voor de rest kies je per connector.")), help),
            Ui.V(6, Ui.H(8, example, add, import), notice),
            Section(L("Connectors"), list));
        Refresh();
    }

    private void Refresh()
    {
        var servers = Mcp.Servers;
        var key = string.Join("|", servers.Select(s => $"{s.Name}{s.IsEnabled}{s.RightValue}{s.Description}{Mcp.StateOf(s.Name)}"));
        if (key == shown) return;
        shown = key;
        list.Children.Clear();
        if (servers.Count == 0) list.Children.Add(Ui.Label(L("Nog geen connectors. Begin met een voorbeeld, zoals Bestanden."), 14, Theme.Secondary, wrap: true));
        foreach (var s in servers) list.Children.Add(Row(s));
    }

    private UIElement Row(McpServerConfig s)
    {
        var state = Mcp.StateOf(s.Name);
        var on = new ToggleSwitch { IsOn = s.IsEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        on.Toggled += (_, _) => { if (Mcp.Config(s.Name) is { } c && c.IsEnabled != on.IsOn) Mcp.Upsert(c with { Enabled = on.IsOn }); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(on, s.Name);
        var dot = new Ellipse
        {
            Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
            Fill = state.Phase switch
            {
                McpManager.Phase.Ready => Theme.Solid(Theme.Mint),
                McpManager.Phase.Starting => Theme.Solid(Theme.Amber),
                McpManager.Phase.Failed => Theme.Solid(Theme.Red),
                _ => Theme.Solid(Microsoft.UI.Colors.White, 0.3),
            },
        };
        var what = s.Description ?? s.Url ?? string.Join(" ", new[] { s.Command ?? "" }.Concat(s.Args ?? []));
        var status = state.Phase switch
        {
            McpManager.Phase.Ready => L($"Actief, {state.Tools} tools") + " · " + what,
            McpManager.Phase.Starting => L("Starten…"),
            McpManager.Phase.Failed => L($"Fout: {state.Error}"),
            _ => what,
        };
        var title = Ui.H(6, dot, Ui.Bold(s.Name, 14), s.IsRemote ? Ui.Icon(Glyph.Globe, 12, Theme.Secondary) : null);
        var right = Pick(Rights(), s.RightValue, v => { if (Mcp.Config(s.Name) is { } c && c.RightValue != v) Mcp.Upsert(c with { Right = v }); });
        right.MinWidth = 140;
        var menu = new MenuFlyout();
        void Item(string text, Action click)
        {
            var i = new MenuFlyoutItem { Text = text };
            i.Click += (_, _) => click();
            menu.Items.Add(i);
        }
        Item(L("Test verbinding"), () => Test(s));
        if (s.Builtin == null) Item(L("Bewerken…"), () => ShowEditor(s.Name, s));
        Item(L("Log"), () => ShowLog(s.Name));
        if (s.IsRemote && s.Oauth != false && Uri.TryCreate(s.Url, UriKind.Absolute, out var u))
            Item(L("Uitloggen"), () => { McpOAuth.LogOut(u); Mcp.Stop(s.Name); notice.Text = L($"Uitgelogd bij {s.Name}."); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(L("Verwijderen"), () => Mcp.Remove(s.Name));
        var more = new Button { Content = Ui.Icon(Glyph.More, 14), Flyout = menu, Padding = new Thickness(8, 6, 8, 6) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, L("Meer"));
        var text = Ui.Caption(status);
        text.MaxLines = 2;
        return Ui.Row("auto,*,auto,auto", 10, on, Ui.V(2, title, text), right, more);
    }

    private async void Test(McpServerConfig s)
    {
        notice.Text = L($"{s.Name} testen…");
        var (tools, error) = await Mcp.TestAsync(s);
        var names = tools == null ? "" : string.Join(", ", tools.Take(6).Select(t => t.Name));
        notice.Text = tools != null ? L($"{s.Name} werkt: {tools.Count} tools ({names}).") : L($"{s.Name} werkt niet: {error}");
    }

    private void AddPreset(McpManager.Preset p)
    {
        if (p.Needs is { } missing && !McpManager.HasTool(missing))
        {
            var install = McpManager.Install(missing);
            notice.Text = L($"Voor {p.Config.Name} is {missing} nodig. Installeer het eerst, bijvoorbeeld met: {install}");
            return;
        }
        if (p.AskToken) ShowEditor(null, p.Config);
        else
        {
            Mcp.Upsert(p.Config);
            notice.Text = L($"{p.Config.Name} is toegevoegd en staat aan.");
        }
    }

    private void Import()
    {
        var found = Mcp.Importable();
        foreach (var c in found) Mcp.Upsert(c);
        int n = found.Count;
        var names = string.Join(", ", found.Select(c => c.Name));
        notice.Text = n == 0 ? L("Geen nieuwe connectors gevonden in Claude Desktop of Claude Code.") : L($"{n} connector(s) overgenomen, uitgeschakeld: {names}.");
    }

    private void ShowLog(string name)
    {
        var log = Mcp.LogOf(name);
        var text = Ui.Label(log.Length == 0 ? L("(leeg)") : log, 12, Theme.Secondary, wrap: true, mono: true, select: true);
        var box = Ui.Rounded(Theme.Solid(Microsoft.UI.Colors.Black, 0.35), 8, new ScrollViewer { Content = text, Height = 340, Padding = new Thickness(10) });
        host.Child = Tab(Ui.Bold(L($"Log van {name}"), 15), box, Ui.Btn(L("Terug"), ShowList));
    }

    /// <summary>Add or change one connector. The arguments and the environment are text with a line each, so Enter starts a new
    /// line instead of saving; Bewaar waits for a name, a command or an https address, and a name that is not taken.</summary>
    private void ShowEditor(string? original, McpServerConfig config)
    {
        var name = new TextBox { Text = config.Name, PlaceholderText = L("Naam") };
        var description = new TextBox { Text = config.Description ?? "", PlaceholderText = L("Waarvoor (één regel voor het model)") };
        var program = new RadioButton { Content = L("Programma op deze pc"), GroupName = "mcp-kind", IsChecked = config.Url == null };
        var address = new RadioButton { Content = L("Internetadres"), GroupName = "mcp-kind", IsChecked = config.Url != null };
        var command = new TextBox { Text = config.Command ?? "", PlaceholderText = L("Commando, bijvoorbeeld npx") };
        var args = new TextBox { Text = string.Join("\n", config.Args ?? []), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 76, FontFamily = Theme.Mono, PlaceholderText = L("Argumenten, één per regel") };
        var env = new TextBox { Text = string.Join("\n", (config.Env ?? []).OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 56, FontFamily = Theme.Mono, PlaceholderText = L("Omgeving, NAAM=waarde per regel") };
        var url = new TextBox { Text = config.Url ?? "", PlaceholderText = L("Adres (URL)") };
        var oauth = new CheckBox { Content = L("Inloggen in de browser als de dienst daarom vraagt (OAuth)"), IsChecked = config.Oauth ?? true };
        var token = new PasswordBox { PlaceholderText = L("Token (optioneel, komt in Windows Referentiebeheer)") };
        var always = new CheckBox { Content = L("Altijd geladen (tools staan in elk gesprek klaar; kost leestijd)"), IsChecked = config.AlwaysLoaded ?? false };
        var programPart = Ui.V(8, command, args, env);
        var addressPart = Ui.V(8, url, oauth, token, Note(L("Wat je met deze connector deelt, gaat naar die dienst op internet.")));
        var problemText = Note("", Theme.Solid(Theme.Amber));
        var result = Note("");
        var save = Ui.Btn(L("Bewaar"), () => { }, Glyph.Check, Theme.Accent);

        McpServerConfig Built(bool keepToken)
        {
            var c = config with { Name = name.Text.Trim(), Description = description.Text.Trim().Length == 0 ? null : description.Text.Trim(), AlwaysLoaded = always.IsChecked == true ? true : null };
            if (program.IsChecked == true)
            {
                var e = new Dictionary<string, string>();
                foreach (var line in env.Text.Split('\r', '\n'))
                    if (line.Split('=', 2) is [var k, var v] && k.Trim().Length > 0) e[k.Trim()] = v;
                return c with
                {
                    Url = null, Headers = null, Oauth = null, Command = command.Text.Trim(),
                    Args = [.. args.Text.Split('\r', '\n').Select(a => a.Trim()).Where(a => a.Length > 0)],
                    Env = e.Count == 0 ? null : e,
                };
            }
            c = c with { Command = null, Args = null, Env = null, Url = url.Text.Trim(), Oauth = oauth.IsChecked == true };
            return keepToken ? McpManager.WithToken(c, token.Password) : c;
        }
        string? Problem()
        {
            var n = name.Text.Trim();
            if (n.Length == 0) return L("Geef de connector een naam.");
            if (!n.Equals(original, StringComparison.OrdinalIgnoreCase) && Mcp.Config(n) != null) return L("Er is al een connector met deze naam.");
            if (program.IsChecked == true && command.Text.Trim().Length == 0) return L("Vul het commando in.");
            if (address.IsChecked == true && !(Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0))
                return L("Vul een adres in dat begint met https://.");
            return null;
        }
        void Update()
        {
            programPart.Show(program.IsChecked == true);
            addressPart.Show(address.IsChecked == true);
            var p = Problem();
            problemText.Text = p ?? "";
            problemText.Show(p != null);
            save.IsEnabled = p == null;
        }
        foreach (var t in new[] { name, command, url }) t.TextChanged += (_, _) => Update();
        program.Checked += (_, _) => Update();
        address.Checked += (_, _) => Update();
        save.Click += (_, _) =>
        {
            if (Problem() != null) return;
            Mcp.Upsert(Built(true), original);
            ShowList();
        };
        var test = Ui.Btn(L("Test verbinding"), async () =>
        {
            result.Text = L("Testen…");
            var (tools, error) = await Mcp.TestAsync(Built(false));
            var names = tools == null ? "" : string.Join(", ", tools.Take(6).Select(t => t.Name));
            result.Text = tools != null ? L($"Werkt: {tools.Count} tools ({names}).") : L($"Werkt niet: {error}");
        });
        // the buttons on top: the form is longer than the dialog shows at once
        host.Child = Tab(
            Ui.Row("auto,*,auto,auto,auto", 8, Ui.Bold(original == null ? L("Connector toevoegen") : L("Connector bewerken"), 15), null, test, Ui.Btn(L("Annuleren"), ShowList), save),
            problemText, result,
            name, description, Ui.H(16, program, address), programPart, addressPart, always);
        Update();
    }
}
