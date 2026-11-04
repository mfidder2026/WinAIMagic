using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;
using State = WinAIMagic.Engine.SystemCheck.CheckState;

namespace WinAIMagic.App.Views;

/// <summary>The system check: what WinAIMagic needs on this PC, what it put right itself, and what it offers to do.</summary>
public sealed class SystemPage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly TextBlock summary = Ui.Label("", 14, Theme.Secondary, wrap: true);
    private readonly ProgressRing busy = new() { Width = 18, Height = 18, IsActive = false };
    private readonly Button again;
    private readonly StackPanel rows = Ui.V(0);
    private readonly Border card;
    private readonly TextBlock restart = Ui.Caption(color: new Microsoft.UI.Xaml.Media.SolidColorBrush(Theme.Amber), size: 13);
    private string shown = "";

    public SystemPage(AppState app)
    {
        this.app = app;
        again = Ui.Btn(L("Opnieuw controleren"), () => _ = app.RunCheck(), Glyph.Refresh);
        card = Ui.Card(rows, glow: Theme.Mint);
        View = Ui.Page(Ui.PageHead(Ui.PageIcon(Glyph.System), L("Systeem"), summary, busy, again), restart, card);
        app.Watch(View, Update);
    }

    private void Update()
    {
        var check = app.Host.Check;
        bool running = check.Running;
        busy.IsActive = running;
        busy.Show(running);
        again.IsEnabled = !running;
        summary.SetIfChanged(running && check.Items.Count == 0 ? L("Bezig met controleren…")
            : check.NeedsAttention ? L("Er is iets dat de app tegenhoudt; zie hieronder.")
            : check.Items.Any(i => i.State == State.Todo) ? L("Alles is in orde; alleen een model ontbreekt nog.")
            : L("Alles wat WinAIMagic nodig heeft is aanwezig."));
        restart.SetIfChanged(check.RestartNeeded ? L("Het wisselbestand is ingesteld. Het telt na een herstart van de pc.") : check.PageFileProblem ?? "");
        restart.Show(check.RestartNeeded || check.PageFileProblem != null);
        card.BorderBrush = Ui.CardEdge(check.NeedsAttention ? Theme.Amber : Theme.Mint);

        var key = string.Join("|", check.Items.Select(i => i.Id + i.Detail + i.State + i.ActionLabel));
        if (key == shown) return;
        shown = key;
        rows.Children.Clear();
        for (int i = 0; i < check.Items.Count; i++)
        {
            if (i > 0) rows.Children.Add(Ui.Divider());
            rows.Children.Add(Row(check.Items[i]));
        }
    }

    private FrameworkElement Row(SystemCheck.Item item)
    {
        var color = Color(item.State);
        var icon = Ui.Icon(Glyph_(item.State), 17, Theme.Solid(color));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        var text = Ui.V(3, Ui.Bold(item.Title), Ui.Caption(item.Detail, size: 13));
        text.Children.OfType<TextBlock>().Last().IsTextSelectionEnabled = true;
        UIElement? action = null;
        if (item.Action is { } a && item.ActionLabel is { } label)
            action = Ui.Btn(label, () => app.Perform(a), null, color);
        var row = Ui.Row("22,*,auto", 12, icon, text, action);
        row.Padding = new Thickness(0, 10, 0, 10);
        return row;
    }

    private static string Glyph_(State s) => s switch
    {
        State.Ok => Glyph.Done,
        State.Fixed => Glyph.Fixed,
        State.Todo => Glyph.Download,
        State.Optional => Glyph.Add,
        _ => Glyph.Warning,
    };

    private static Color Color(State s) => s switch
    {
        State.Ok or State.Fixed => Theme.Mint,
        State.Todo => Theme.Accent,
        State.Optional => Theme.Cyan,
        _ => Theme.Amber,
    };
}
