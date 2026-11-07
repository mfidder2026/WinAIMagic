using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>Taken: what the model does on its own, when it runs next, and the conversations of the last runs.</summary>
public sealed class TasksPage : IPage
{
    public FrameworkElement View { get; }
    private readonly AppState app;
    private readonly StackPanel list = Ui.V(10), runs = Ui.V(4);
    private readonly TextBlock heartbeat = Ui.Label("", 13, Theme.Secondary, wrap: true);
    private string shownTasks = "?", shownRuns = "?";

    public TasksPage(AppState app)
    {
        this.app = app;
        var add = Ui.Btn(L("Nieuwe taak…"), () => _ = TaskEditor.Show(app, null), Glyph.Add, Theme.Accent);
        var help = Ui.IconBtn(Glyph.Help, () => app.OpenHelp("tasks"), L("Hulp bij dit onderdeel"));
        var intro = Ui.Card(Ui.V(10,
            Ui.Label(L("Een taak is een opdracht die het model zelf uitvoert, op een vast moment. Elke keer wordt het een gesprek dat je hieronder terugvindt. Een wijziging vraagt eerst jouw goedkeuring; je krijgt dan een melding."), 13, null, wrap: true),
            Ui.Row("auto,*", 12, add, Ui.Caption(L("Of vraag het in een gesprek: \"Geef me elke werkdag om 8 uur mijn agenda.\""))),
            heartbeat), null);
        View = Ui.Page(
            Ui.PageHead(Ui.PageIcon(Glyph.Tasks), L("Taken"), Ui.Label(L("Wat het model zelf doet, op een vast moment."), 14, Theme.Secondary, wrap: true), help),
            intro,
            Ui.Card(list, L("Taken"), Glyph.Tasks, Theme.Violet),
            Ui.Card(runs, L("Laatste uitvoeringen"), Glyph.Clock, Theme.Mint));
        app.Watch(View, Refresh);
    }

    private void Refresh()
    {
        var s = Settings.Shared;
        heartbeat.SetIfChanged(s.Heartbeat
            ? L("Heartbeat staat aan.") + (app.HeartbeatStatus is { } st ? " " + st : "")
            : L("Heartbeat staat uit (Instellingen, Taken)."));
        var tasks = app.Tasks.Tasks;
        var culture = AppLanguages.Current.Culture();
        var key = string.Join("|", tasks.Select(t => $"{t.Id}{t.Enabled}{t.Title}{t.NextRun}{t.LastConversation}{app.TasksStarting.Contains(t.Id)}{Running(t.Id)}")) + s.Language;
        if (key != shownTasks)
        {
            shownTasks = key;
            list.Children.Clear();
            if (tasks.Count == 0) list.Children.Add(Ui.Label(L("Nog geen taken."), 14, Theme.Secondary));
            foreach (var t in tasks) list.Children.Add(Row(t, culture));
        }
        var done = app.Store.Conversations.Where(c => c.Task != null && c.Hidden != true).Take(30).ToList();
        var runKey = string.Join("|", done.Select(c => $"{c.Id}{c.DisplayTitle}{app.Chat.IsBusy(c.Id)}{app.Chat.ApprovalFor(c.Id) != null}{c.Messages.LastOrDefault()?.Error != null}")) + s.Language;
        if (runKey == shownRuns) return;
        shownRuns = runKey;
        runs.Children.Clear();
        if (done.Count == 0) runs.Children.Add(Ui.Label(L("Nog niets uitgevoerd."), 14, Theme.Secondary));
        foreach (var c in done)
        {
            var id = c.Id;
            UIElement mark = app.Chat.ApprovalFor(id) != null ? Ui.Icon(Glyph.Warning, 13, Theme.Solid(Theme.Amber)).Tip(L("Wacht op jouw goedkeuring"))
                : app.Chat.IsBusy(id) ? new Spinner(13)
                : c.Messages.LastOrDefault()?.Error != null ? Ui.Icon(Glyph.Error, 13, Theme.Solid(Theme.Red))
                : Ui.Icon(Glyph.CircleDone, 13, Theme.Solid(Theme.Mint));
            var b = ChatUi.Plain(Ui.Row("auto,*,auto", 8, mark, Ui.Label(c.DisplayTitle, 13), Ui.Caption(c.Updated.ToLocalTime().ToString("g", culture))), () => app.Open(id));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            runs.Children.Add(b);
        }
    }

    private bool Running(Guid task)
    {
        lock (app.TaskRuns) return app.TaskRuns.ContainsValue(task);
    }

    private UIElement Row(ScheduledTask t, System.Globalization.CultureInfo culture)
    {
        bool starting = app.TasksStarting.Contains(t.Id), running = starting || Running(t.Id);
        var on = new ToggleSwitch { IsOn = t.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
        on.Toggled += (_, _) =>
        {
            if (app.Tasks.Task(t.Id) is not { } now || now.Enabled == on.IsOn) return;
            now.Enabled = on.IsOn;
            app.Tasks.Upsert(now);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(on, t.Title);
        var parts = new List<string> { t.Schedule.Description };
        if (t.Enabled && t.NextRun is { } next) { var when = next.ToLocalTime().ToString("g", culture); parts.Add(L($"volgende: {when}")); }
        else if (!t.Enabled) parts.Add(L("staat uit"));
        if (t.Workspace is { } ws) parts.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(ws)));
        var title = Ui.H(6, Ui.Bold(t.Title, 14), running ? new Spinner(13) : null, starting ? Ui.Caption(L("Model laden…")) : null);
        var now = Ui.Btn(L("Nu uitvoeren"), () => app.RunTaskNow(t.Id), Glyph.Play);
        now.IsEnabled = !running;
        var more = Ui.IconBtn(Glyph.More, () => { }, L("Meer"));
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = L("Bewerken…") };
        edit.Click += (_, _) => _ = TaskEditor.Show(app, app.Tasks.Task(t.Id));
        menu.Items.Add(edit);
        if (t.LastConversation is { } last && app.Store.Find(last) != null)
        {
            var open = new MenuFlyoutItem { Text = L("Laatste uitvoering openen") };
            open.Click += (_, _) => app.Open(last);
            menu.Items.Add(open);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = L("Verwijderen"), Icon = Ui.Icon(Glyph.Trash) };
        delete.Click += async (_, _) =>
        {
            if (await Ui.Ask(app, L("Taak verwijderen"), L($"\"{t.Title}\" verwijderen? Zijn gesprekken blijven staan."), L("Verwijderen"), danger: true) == ContentDialogResult.Primary)
                app.Tasks.Remove(t.Id);
        };
        menu.Items.Add(delete);
        more.Flyout = menu;
        return Ui.Row("auto,*,auto,auto", 10, on, Ui.V(2, title, Ui.Caption(string.Join(" · ", parts))), now, more);
    }
}

/// <summary>Nieuwe taak / Taak bewerken.</summary>
public static class TaskEditor
{
    public static async Task Show(AppState app, ScheduledTask? existing)
    {
        var task = existing?.Copy() ?? new ScheduledTask { Schedule = TaskSchedule.Daily(8, 0, true) };
        bool isNew = existing == null;
        var sch = task.Schedule;
        var culture = AppLanguages.Current.Culture();

        var name = new TextBox { Text = task.Title, PlaceholderText = L("Naam") };
        var prompt = new TextBox { Text = task.Prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96, PlaceholderText = L("Opdracht voor het model") };
        var kinds = new[] { ("once", L("Eenmalig")), ("daily", L("Dagelijks")), ("weekly", L("Wekelijks")), ("every", L("Elke paar uur")) };
        var kind = new ComboBox { ItemsSource = kinds.Select(k => k.Item2).ToList(), SelectedIndex = Array.FindIndex(kinds, k => k.Item1 == sch.Kind), MinWidth = 200 };
        var onceAt = (sch.At ?? DateTimeOffset.Now.AddHours(1)).ToLocalTime();
        var date = new CalendarDatePicker { Date = onceAt, MinDate = DateTimeOffset.Now.Date };
        var clock = new TimePicker { ClockIdentifier = "24HourClock", Time = sch.Kind == "once" ? onceAt.TimeOfDay : new TimeSpan(sch.Hour, sch.Minute, 0), MinuteIncrement = 5 };
        var weekdaysOnly = new CheckBox { Content = L("Alleen op werkdagen"), IsChecked = sch.Kind != "daily" || sch.WeekdaysOnly };
        var days = Enumerable.Range(1, 7).Select(d => culture.DateTimeFormat.DayNames[d - 1]).ToList();
        var weekday = new ComboBox { ItemsSource = days, SelectedIndex = sch.Kind == "weekly" ? sch.Weekday - 1 : 1, MinWidth = 200 };
        var hours = new NumberBox { Minimum = 1, Maximum = 12, Value = sch.Kind == "every" ? sch.Hours : 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140 };
        var hourList = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
        var from = new ComboBox { ItemsSource = hourList, SelectedIndex = sch.Kind == "every" ? sch.From : 8 };
        var to = new ComboBox { ItemsSource = hourList, SelectedIndex = sch.Kind == "every" ? sch.To : 22 };
        var folder = Ui.Caption(task.Workspace ?? L("geen"), Theme.Primary, 13);
        var choose = Ui.Btn(L("Kies…"), () =>
        {
            if (NativePickers.Folder(WinRT.Interop.WindowNative.GetWindowHandle(app.Window)) is { } f) { task.Workspace = f; folder.Text = f; }
        });
        var none = Ui.Btn(L("Geen"), () => { task.Workspace = null; folder.Text = L("geen"); });
        var note = Ui.Caption("");

        var onceRow = Ui.Row("*,auto,auto", 8, Ui.Label(L("Moment"), 14), date, null);
        var timeRow = Ui.Row("*,auto", 8, Ui.Label(L("Tijd"), 14), clock);
        var dayRow = Ui.Row("*,auto", 8, Ui.Label(L("Dag"), 14), weekday);
        var everyRow = Ui.Row("*,auto", 8, Ui.Label(L("Hoe vaak (uur)"), 14), hours);
        var spanRow = Ui.Row("*,auto,auto,auto", 8, Ui.Label(L("Tussen"), 14), from, Ui.Label("–", 14), to);

        TaskSchedule Built()
        {
            var t = clock.Time;
            return kinds[Math.Max(kind.SelectedIndex, 0)].Item1 switch
            {
                "once" => TaskSchedule.Once(new DateTimeOffset((date.Date ?? DateTimeOffset.Now).Date + t)),
                "weekly" => TaskSchedule.Weekly(Math.Max(weekday.SelectedIndex, 0) + 1, t.Hours, t.Minutes),
                "every" => TaskSchedule.Every(double.IsNaN(hours.Value) ? 1 : (int)hours.Value, Math.Max(from.SelectedIndex, 0), Math.Max(to.SelectedIndex, 0)),
                _ => TaskSchedule.Daily(t.Hours, t.Minutes, weekdaysOnly.IsChecked == true),
            };
        }
        string? Problem()
        {
            if (name.Text.Trim().Length == 0) return L("Geef de taak een naam.");
            if (prompt.Text.Trim().Length == 0) return L("Schrijf wat het model moet doen.");
            if (Built() is { Kind: "once", At: { } at } && at <= DateTimeOffset.Now) return L("Kies een moment in de toekomst.");
            return null;
        }

        var dialog = Ui.Dialog(app);
        void Update()
        {
            var k = kinds[Math.Max(kind.SelectedIndex, 0)].Item1;
            onceRow.Show(k == "once");
            timeRow.Show(k is "once" or "daily" or "weekly");
            weekdaysOnly.Show(k == "daily");
            dayRow.Show(k == "weekly");
            everyRow.Show(k == "every");
            spanRow.Show(k == "every");
            var p = Problem();
            note.Text = p ?? Built().Description;
            dialog.IsPrimaryButtonEnabled = p == null;
        }
        kind.SelectionChanged += (_, _) => Update();
        name.TextChanged += (_, _) => Update();
        prompt.TextChanged += (_, _) => Update();
        date.DateChanged += (_, _) => Update();
        clock.TimeChanged += (_, _) => Update();
        weekdaysOnly.Click += (_, _) => Update();
        weekday.SelectionChanged += (_, _) => Update();
        hours.ValueChanged += (_, _) => Update();
        from.SelectionChanged += (_, _) => Update();
        to.SelectionChanged += (_, _) => Update();

        var body = Ui.V(10, name, Ui.V(4, Ui.Label(L("Opdracht voor het model"), 14), prompt),
            Ui.Row("*,auto", 8, Ui.Label(L("Wanneer"), 14), kind), onceRow, dayRow, timeRow, weekdaysOnly, everyRow, spanRow,
            Ui.Row("auto,*,auto,auto", 8, Ui.Label(L("Werkmap"), 14), folder, choose, none), note);
        body.Width = 520;
        dialog.Title = isNew ? L("Nieuwe taak") : L("Taak bewerken");
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 560, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        dialog.PrimaryButtonText = L("Bewaar");
        dialog.CloseButtonText = L("Annuleren");
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        Update();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        task.Title = name.Text.Trim();
        task.Prompt = prompt.Text.Trim();
        task.Schedule = Built();
        if (isNew) task.Enabled = true;
        app.Tasks.Upsert(task);
    }
}
