using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>The chat of the chosen conversation: the bar on top, the messages, what the engine or the model asks of the user, and
/// the input. It follows the stream through AppState.Changed, so at most every 0.1 s and on the UI thread; only rows on screen exist.</summary>
public sealed class ChatPage : IPage
{
    private sealed record SummaryKey(Guid Conversation);

    private readonly AppState app;
    private readonly Grid root = new() { AllowDrop = true, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollViewer scroller;
    private readonly VirtualList list;
    private readonly FrameworkElement empty;
    private readonly TextBlock modelName = Ui.Label("", 13, null, FontWeights.SemiBold), folderName = Ui.Label("", 13);
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse modelDot = new() { Width = 9, Height = 9 };
    private readonly Button folderChip;
    private readonly ContextMeter meter;
    private readonly MiniSpark spark = new(Theme.Violet, 22) { Width = 160 };
    private readonly TextBlock speed = Ui.Label("", 13, new SolidColorBrush(Theme.Cyan), FontWeights.SemiBold, display: true);
    private readonly Border engineBanner = new() { Background = Theme.Solid(Colors.White, 0.05), BorderBrush = Theme.Hairline, BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly Border otherBanner, noticeBanner;
    private readonly TextBlock noticeText = Ui.Caption("", new SolidColorBrush(Theme.Amber), 13);
    private readonly Border approvalHost = new() { MaxWidth = 860, Margin = new Thickness(24, 4, 24, 0), Visibility = Visibility.Collapsed };
    private readonly Border gateBanner;
    private readonly TextBlock gateText = Ui.Caption("", new SolidColorBrush(Theme.Amber), 13);
    private readonly Border stepsHost = new() { MaxWidth = 860, Margin = new Thickness(24, 4, 24, 0), Visibility = Visibility.Collapsed };
    private string stepsKey = "";
    private bool stepsOpen = true;
    private readonly Dictionary<Guid, ChatMessage> visible = new();
    private readonly Dictionary<Guid, RowMemory> memory = new();
    private readonly TextBlock emptyTitle = Ui.Label("", 36, Theme.Brand, FontWeights.Bold, display: true);
    private Guid? shown, lastMessage;
    private Approval? approvalShown;
    private string bannerKey = "";
    private string? summaryText;
    private bool pinned = true, jumpQueued;
    private double lastOffset;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? noticeTimer;

    public FrameworkElement View => root;
    public Composer Composer { get; }

    public ChatPage(AppState app)
    {
        this.app = app;
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });

        // the bar on top
        folderChip = ChatUi.Plain(ChatUi.Capsule(Ui.H(6, Ui.Icon(Glyph.Folder, 13, new SolidColorBrush(Theme.Amber)), folderName)), OpenFolder, L("Werkmap van dit gesprek: klik om hem in de Verkenner te openen"));
        folderChip.CornerRadius = new CornerRadius(14);
        folderName.MaxWidth = 380;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(folderChip, L("Werkmap"));
        meter = new ContextMeter(app);
        var top = Ui.Row("auto,auto,*,auto,auto,auto", 12, ChatUi.Capsule(Ui.H(6, modelDot, modelName)), folderChip, null, meter, spark, speed);
        top.Padding = new Thickness(20, 4, 20, 8);
        spark.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(top.At(0));

        // the messages
        list = new VirtualList(Build, Estimate, 20) { MaxWidth = 860, Margin = new Thickness(24, 12, 24, 20), Shifted = Shift, StickToEnd = true };
        scroller = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ZoomMode = ZoomMode.Disabled };
        scroller.ViewChanged += (_, _) => OnScrolled();
        list.SizeChanged += (_, _) => FollowEnd();
        scroller.SizeChanged += (_, _) => FollowEnd();
        root.Children.Add(scroller.At(1));
        empty = BuildEmpty();
        root.Children.Add(empty.At(1));

        // what stands between the messages and the input
        noticeBanner = Banner(noticeText, Theme.Amber);
        otherBanner = Banner(Ui.H(8, Ui.Icon(Glyph.Clock, 13, new SolidColorBrush(Theme.Amber)), Ui.Caption(L("De engine werkt aan een ander verzoek (bijvoorbeeld via de API). Een nieuw bericht wacht tot dat klaar is."), new SolidColorBrush(Theme.Amber), 13)), Theme.Amber);
        root.Children.Add(noticeBanner.At(2));
        root.Children.Add(engineBanner.At(3));
        root.Children.Add(otherBanner.At(4));
        // this conversation waits while the model writes for another one of ours (a task, another chat)
        gateBanner = Banner(Ui.Row("auto,*", 8, Ui.Icon(Glyph.Hourglass, 13, new SolidColorBrush(Theme.Amber)), gateText), Theme.Amber);
        root.Children.Add(gateBanner.At(5));
        root.Children.Add(stepsHost.At(6));
        root.Children.Add(approvalHost.At(7));

        Composer = new Composer(app, () =>
        {
            pinned = true;
            list.StickToEnd = true;
            FollowEnd();
        });
        root.Children.Add(Composer.At(8));

        root.PreviewKeyDown += OnKey;
        root.DragOver += (_, e) =>
        {
            bool can = Composer.HasImages(e.DataView);
            e.AcceptedOperation = can ? DataPackageOperation.Copy : DataPackageOperation.None;
            Composer.DropTarget(can);
        };
        root.DragLeave += (_, _) => Composer.DropTarget(false);
        root.Drop += async (_, e) =>
        {
            Composer.DropTarget(false);
            var wait = e.GetDeferral();
            try { await Composer.TakeImages(e.DataView); }
            catch (System.Runtime.InteropServices.COMException x) { App.Log(x); }
            finally { wait.Complete(); }
        };

        app.Watch(root, Refresh);
        app.WatchSamples(root, UpdateSpeed);
        PropertyChangedEventHandler onPane = (_, e) => { if (e.PropertyName == nameof(AppState.Pane) && app.Pane.Kind == PaneKind.Chat) Refresh(); };
        Action<string> onNotice = text => app.Ui.TryEnqueue(() => ShowNotice(text));
        root.Loaded += (_, _) => { app.PropertyChanged += onPane; app.Chat.Notice += onNotice; };
        root.Unloaded += (_, _) => { app.PropertyChanged -= onPane; app.Chat.Notice -= onNotice; };
    }

    private static Border Banner(UIElement content, Windows.UI.Color tint) => new()
    {
        Child = content, Padding = new Thickness(20, 6, 20, 6), Background = Theme.Solid(tint, 0.08), Visibility = Visibility.Collapsed,
    };

    private FrameworkElement BuildEmpty()
    {
        (string Glyph, string Text)[] suggestions =
        [
            (Glyph.Globe, L("Wat is het laatste nieuws over grafische kaarten?")),
            (Glyph.Code, L("Schrijf een C#-functie die priemgetallen zoekt")),
            (Glyph.Think, L("Leg uit hoe een mixture-of-experts model werkt")),
            (Glyph.Image, L("Sleep een afbeelding hierheen en vraag wat erop staat")),
        ];
        var grid = new Grid { RowSpacing = 12, ColumnSpacing = 12, MaxWidth = 640 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < suggestions.Length; i++)
        {
            var (glyph, text) = suggestions[i];
            var label = Ui.Label(text, 14, null, wrap: true);
            var button = ChatUi.Plain(Ui.Row("20,*", 10, Ui.Icon(glyph, 15, new SolidColorBrush(Theme.Cyan)), label), () => Composer.SetDraft(text));
            button.Padding = new Thickness(14);
            button.CornerRadius = new CornerRadius(14);
            button.BorderThickness = new Thickness(1);
            button.BorderBrush = Theme.Hairline;
            button.Background = Theme.Solid(Colors.White, 0.05);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
            grid.Children.Add(button.At(i / 2, i % 2));
        }
        var line = Ui.Caption(L("Volledig lokaal op deze pc. Niets verlaat je computer, behalve zoekopdrachten als je web zoeken aanzet."), Theme.Secondary, 14);
        line.TextAlignment = TextAlignment.Center;
        line.MaxWidth = 480;
        var mark = new BrandMark(84) { HorizontalAlignment = HorizontalAlignment.Center };
        emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var body = Ui.V(18, mark, emptyTitle, line, grid);
        body.VerticalAlignment = VerticalAlignment.Center;
        body.Margin = new Thickness(24);
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Center };
    }

    // MARK: - the list

    private FrameworkElement Build(object key)
    {
        if (key is SummaryKey s)
            return new SummaryRow(summaryText ?? "", Shift, () => Memory(s.Conversation).Summary, on => Memory(s.Conversation).Summary = on);
        var id = (Guid)key;
        return new MessageRow(visible[id], Memory(id), Shift, shown is { } c && app.Chat.IsBusy(c) && id == lastMessage);
    }

    private RowMemory Memory(Guid id)
    {
        if (!memory.TryGetValue(id, out var m)) memory[id] = m = new RowMemory();
        return m;
    }

    /// <summary>The height of a row that was not on screen yet.</summary>
    private double Estimate(object key, double width)
    {
        if (key is SummaryKey || !visible.TryGetValue((Guid)key, out var m)) return 22;
        if (m.Automatic == true) return 20;
        double perLine = Math.Max((width - (m.Role == ChatRole.User ? 112 : 74)) / 7.4, 20);
        int breaks = 0;
        foreach (char c in m.Text) if (c == '\n') breaks++;
        double h = (breaks + 1 + m.Text.Length / perLine) * 22 + 34;
        if (m.Attachments.Count > 0) h += 148;
        if (m.Reasoning.Length > 0) h += 30;
        if (m.Activities.Count > 0) h += 30;
        if (m.Sources.Count > 0) h += 34;
        if (m.TokensPerSecond != null) h += 26;
        return h;
    }

    private void OnScrolled()
    {
        double offset = scroller.VerticalOffset, left = scroller.ScrollableHeight - offset;
        if (left <= 24) SetPinned(true);
        else if (offset < lastOffset - 1) SetPinned(false);
        lastOffset = offset;
    }

    private void SetPinned(bool on)
    {
        pinned = on;
        list.StickToEnd = on;
    }

    private void FollowEnd()
    {
        if (!pinned || jumpQueued) return;
        jumpQueued = true;
        // after the scroller has taken the new height
        app.Ui.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            jumpQueued = false;
            if (pinned) scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        });
    }

    private void Shift(double by)
    {
        if (!pinned) scroller.ChangeView(null, scroller.VerticalOffset + by, null, true);
    }

    // MARK: - keeping up

    private void Refresh()
    {
        var id = app.Pane.Conversation ?? app.Store.Selection;
        var c = id is { } g ? app.Store.Find(g) : null;
        if (c?.Id != shown)
        {
            shown = c?.Id;
            memory.Clear();
            visible.Clear();
            list.Reset();
            SetPinned(true);
            lastOffset = 0;
            Composer.Reset(c?.Id);
            noticeBanner.Show(false);
        }
        bool has = c is { Messages.Count: > 0 };
        empty.Show(!has);
        scroller.Show(has);
        if (has) UpdateList(c!);
        else emptyTitle.SetIfChanged(app.Host.SelectedModel?.Name ?? "WinAIMagic");

        var model = app.ChatModel;
        modelName.SetIfChanged(model?.Name ?? L("Geen model"));
        modelDot.Fill = Theme.FamilyGradient(model?.Family ?? "deepseek");
        bool hasFolder = Settings.Shared.WorkspaceTools && c?.Workspace != null;
        folderChip.Show(hasFolder);
        if (hasFolder) folderName.SetIfChanged(Middle(c!.Workspace!));
        meter.Update(c);
        UpdateSpeed();
        UpdateEngineBanner();
        otherBanner.Show(app.EngineState.IsRunning && app.Host.Metrics.EngineBusy && !app.Chat.Busy);
        UpdateGate(c);
        UpdateSteps(c);
        UpdateApproval(c);
        Composer.Refresh();
    }

    /// <summary>A path cut in the middle: its end says the most.</summary>
    private static string Middle(string path) => path.Length <= 48 ? path : path[..18] + "…" + path[^29..];

    private void UpdateList(Conversation c)
    {
        var last = c.Messages[^1].Id;
        lastMessage = last;
        bool busy = app.Chat.IsBusy(c.Id);
        var keys = new List<object>(c.Messages.Count);
        visible.Clear();
        summaryText = c.Summary;
        bool nudged = false;
        foreach (var m in c.Messages)
        {
            bool blank = m.Role == ChatRole.Assistant && m.Text.Length == 0 && m.Reasoning.Length == 0 && m.Error == null && m.Activities.Count == 0 && !(busy && m.Id == last);
            if (!m.IsHidden && !blank && !(m.Automatic == true && nudged))
            {
                keys.Add(m.Id);
                visible[m.Id] = m;
                nudged = m.Automatic == true;
            }
            if (m.Id == c.SummaryUpTo && c.Summary != null) keys.Add(new SummaryKey(c.Id));
        }
        list.SetKeys(keys);
        foreach (var (key, row) in list.Live)
            if (key is Guid id && row is MessageRow message && visible.TryGetValue(id, out var m)) message.Update(m, busy && id == last);
    }

    private void UpdateSpeed()
    {
        double now = app.TokensPerSec;
        bool show = app.Chat.Busy || now > 0;
        spark.Show(show);
        speed.Show(show);
        if (!show) return;
        var samples = app.Host.Metrics.Samples;
        spark.Set([.. samples.Skip(Math.Max(samples.Count - 60, 0)).Select(s => s.TokensPerSec)], null);
        speed.SetIfChanged(now.ToString("0.0", AppLanguages.Current.Culture()) + " tok/s");
    }

    private void UpdateEngineBanner()
    {
        var s = app.EngineState;
        engineBanner.Show(!s.IsRunning);
        if (s.IsRunning) return;
        bool can = app.Host.CanStart;
        // the chat's model may run already, as the fast model on the processor: that one does not chat here, and says so
        var onFast = app.ChatModelOnFast;
        var key = $"{s.Status}|{s.Message}|{can}|{app.Host.WhyNotStartable}|{Settings.Shared.Language}|{onFast?.Id}";
        if (key == bannerKey) return;
        bannerKey = key;
        UIElement content;
        switch (s.Status)
        {
            case EngineStatus.Starting:
                // only a large model takes minutes; a small one is there in seconds
                var gb = (long)((app.ChatModel?.TotalSize ?? 0) / 1_000_000_000);
                content = Ui.Row("auto,*", 8, new Spinner(16), Ui.Caption(gb >= 50 ? L($"{s.Message} Het eerste laden van {gb} GB duurt enkele minuten.") : s.Message, Theme.Primary, 13));
                break;
            case EngineStatus.Failed:
                var again = Ui.Btn(L("Opnieuw"), app.StartEngine);
                again.IsEnabled = can;
                content = Ui.Row("auto,*,auto", 8, Ui.Icon(Glyph.Warning, 14, new SolidColorBrush(Theme.Red)), Ui.Caption(s.Message, Theme.Primary, 13), again);
                break;
            default:
                var start = Ui.Btn(L($"Start {app.Host.SelectedModel?.ShortName ?? L("model")}"), app.StartEngine, null, Theme.Accent);
                start.IsEnabled = can;
                start.Tip(can ? null : app.Host.WhyNotStartable);
                string said = !can ? app.Host.WhyNotStartable ?? L("Het gekozen model staat er nog niet: kies of download er een bij Modellen.")
                    : onFast != null ? L($"{onFast.ShortName} draait nu alleen als snel model, op de processor, voor Documenten en Beeld. Start het om ermee te chatten; dan stopt het snelle exemplaar.")
                    : L("Er is geen model gestart. Je vraag start het vanzelf, of klik op Start.");
                content = Ui.Row("auto,*,auto", 8, Ui.Icon(Glyph.Power, 14, Theme.Secondary), Ui.Caption(said, Theme.Primary, 13), start);
                break;
        }
        ((FrameworkElement)content).Margin = new Thickness(16, 7, 16, 7);
        engineBanner.Child = content;
    }

    private void ShowNotice(string text)
    {
        noticeText.Text = text;
        noticeBanner.Show(true);
        noticeTimer?.Stop();
        noticeTimer = app.Ui.CreateTimer();
        noticeTimer.IsRepeating = false;
        noticeTimer.Interval = TimeSpan.FromSeconds(12);
        noticeTimer.Tick += (_, _) => noticeBanner.Show(false);
        noticeTimer.Start();
    }

    private void OpenFolder()
    {
        if (shown is { } id && app.Store.Find(id)?.Workspace is { } folder) Composer.OpenFolder(folder);
    }

    // MARK: - the model's question

    private void UpdateGate(Conversation? c)
    {
        var other = c == null ? null : app.Chat.WaitingBehind(c.Id);
        gateBanner.Show(other != null);
        if (other is not { } o) return;
        var title = app.Store.Find(o)?.DisplayTitle ?? "";
        gateText.SetIfChanged(L($"Wacht op het model: het schrijft nu in '{title}'. Daarna is dit gesprek aan de beurt."));
    }

    /// <summary>The model's step list for this conversation: what it does now and what is done. A finished list goes with the next question.</summary>
    private void UpdateSteps(Conversation? c)
    {
        var steps = c?.Steps;
        bool busy = c != null && app.Chat.IsBusy(c.Id);
        var key = steps == null ? "" : $"{c!.Id}|{busy}|{stepsOpen}|{Settings.Shared.Language}|" + string.Join("|", steps.Select(x => x.Status + x.Title));
        if (key == stepsKey) return;
        stepsKey = key;
        stepsHost.Show(steps is { Count: > 0 });
        if (steps is not { Count: > 0 }) { stepsHost.Child = null; return; }
        int done = steps.Count(x => x.IsDone), all = steps.Count;
        var now = steps.FirstOrDefault(x => x.Status == "doing");
        var head = Ui.Row("auto,auto,auto,*,auto", 6, Ui.Icon(Glyph.Steps, 14, new SolidColorBrush(Theme.Cyan)), Ui.Label(L("Stappen"), 13, null, FontWeights.SemiBold),
            Ui.Label($"{done}/{all}", 12, Theme.Secondary), !stepsOpen && now != null ? Ui.Label(now.Title, 12, Theme.Secondary) : null,
            Ui.Icon(stepsOpen ? Glyph.ChevronDown : Glyph.ChevronRight, 11, Theme.Secondary));
        var toggle = ChatUi.Plain(head, () => { stepsOpen = !stepsOpen; UpdateSteps(app.Store.Find(c.Id)); }, L($"Stappen: {done} van {all} klaar"));
        toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
        toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var body = Ui.V(4, toggle);
        if (stepsOpen)
            foreach (var st in steps)
            {
                UIElement mark = st.Status switch
                {
                    "done" => Ui.Icon(Glyph.CircleDone, 13, new SolidColorBrush(Theme.Mint)),
                    "doing" when busy => new Spinner(13),
                    "doing" => Ui.Icon(Glyph.Circle, 13, new SolidColorBrush(Theme.Amber)),
                    _ => Ui.Icon(Glyph.Circle, 13, Theme.Tertiary),
                };
                var text = Ui.Label(st.Title, 13, st.IsDone ? Theme.Secondary : null, wrap: true);
                if (st.IsDone) text.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                body.Children.Add(Ui.Row("auto,*", 8, mark, text));
            }
        stepsHost.Child = new Border
        {
            Child = body, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(12),
            Background = Theme.Solid(Colors.White, 0.06), BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1),
        };
    }

    private void UpdateApproval(Conversation? c)
    {
        var a = c == null ? null : app.Chat.ApprovalFor(c.Id);
        if (a == approvalShown) return;
        bool wasOpen = approvalShown != null;
        approvalShown = a;
        approvalHost.Show(a != null);
        approvalHost.Child = a == null ? null : ApprovalCard(a);
        if (a == null && wasOpen) Composer.Focus();
        // typing goes on undisturbed; otherwise Enter and Esc are at hand
        else if (a != null && !Composer.HasDraft && approvalHost.Child is Border { Child: StackPanel body } && body.Children[^1] is Grid buttons && buttons.Children[^1] is Button allow)
            allow.Focus(FocusState.Programmatic);
    }

    private Border ApprovalCard(Approval a)
    {
        var amber = new SolidColorBrush(Theme.Amber);
        bool warned = a.Warnings.Count > 0;
        var edge = warned ? Theme.Red : Theme.Amber;
        var icon = a.Call.Name.StartsWith("system_", StringComparison.Ordinal) ? (a.Admin ? Glyph.Shield : Glyph.Gears)
            : a.Call.Name == "run_command" ? Glyph.Terminal : a.Call.Name == TaskTool.ToolName ? Glyph.Tasks
            : a.Call.Name.Contains("__", StringComparison.Ordinal) ? Glyph.Connect
            : WinAIMagic.Chat.Agent.AgentTools.Owner(a.Call.Name)?.Icon switch { "mail" => Glyph.Mail, "browser" => Glyph.Globe, "computer" => Glyph.Pointer, _ => Glyph.Document };
        var head = Ui.Row("auto,*,auto", 8, Ui.Icon(icon, 15, amber), Ui.Label(a.Title, 15, null, FontWeights.SemiBold, wrap: true, display: true),
            a.Admin ? Ui.H(4, Ui.Icon(Glyph.Key, 12, Theme.Solid(Theme.Amber)), Ui.Label(L("Beheerder"), 12, Theme.Solid(Theme.Amber), FontWeights.Bold)) : null);
        var body = Ui.V(10, head);
        if (warned)
        {
            var list = Ui.V(4, [.. a.Warnings.Select(w => (UIElement)Ui.Row("auto,*", 6, Ui.Icon(Glyph.Warning, 13, Theme.Solid(Theme.Red)), Ui.Label(w, 13, Theme.Solid(Theme.Red), wrap: true)))]);
            var box = Ui.Rounded(Theme.Solid(Theme.Red, 0.12), 8, list);
            box.Padding = new Thickness(10);
            body.Children.Add(box);
        }
        UIElement content;
        if (a.Steps.Count == 0)
            content = new TextBlock { Text = a.Detail, FontFamily = Theme.Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(10) };
        else
        {
            // a plan: each step with what is there now, where the app could read it first
            var steps = Ui.V(8);
            steps.Margin = new Thickness(10);
            for (int i = 0; i < a.Steps.Count; i++)
            {
                var st = a.Steps[i];
                var one = Ui.V(2, Ui.Label($"{i + 1}. {st.Title}" + (st.Admin ? "  🔑" : ""), 13, null, FontWeights.SemiBold, wrap: true));
                if (st.Current is { } now) one.Children.Add(new TextBlock { Text = L($"Nu: {now}"), FontSize = 12, Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
                one.Children.Add(new TextBlock { Text = st.Command, FontFamily = Theme.Mono, FontSize = 12, Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                steps.Children.Add(one);
            }
            content = steps;
        }
        // as high as what it shows, scrolling from 160 (a command) or 240 (a plan) on
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Background = Theme.Solid(Colors.Black, 0.35),
            Child = new ScrollViewer { MaxHeight = a.Steps.Count == 0 ? 160 : 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, ZoomMode = ZoomMode.Disabled, Content = content },
        });
        if (a.Admin)
        {
            var snapshot = new CheckBox { Content = L("Maak eerst een herstelpunt, zodat je altijd terug kunt"), IsChecked = app.Chat.Snapshot };
            snapshot.Checked += (_, _) => app.Chat.Snapshot = true;
            snapshot.Unchecked += (_, _) => app.Chat.Snapshot = false;
            body.Children.Add(snapshot);
            body.Children.Add(Ui.Caption(L("Na Toestaan vraagt Windows om toestemming (UAC). Windows maakt hoogstens één herstelpunt per 24 uur.")));
        }
        // "always" never covers administrator rights
        var buttons = Ui.Row("auto,*,auto,auto", 8,
            Ui.Btn(L("Weigeren"), () => app.Chat.Answer(a, false)), null,
            a.Admin || a.EveryTime ? null : Ui.Btn(L("Altijd in dit gesprek"), () => app.Chat.Answer(a, true, true)),
            Ui.Btn(L("Toestaan"), () => app.Chat.Answer(a, true), Glyph.Check, edge));
        body.Children.Add(buttons);
        return new Border
        {
            Child = body, Padding = new Thickness(14), CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Theme.Rgb(0x1D, 0x1B, 0x26)), BorderBrush = Theme.Solid(edge, 0.6), BorderThickness = new Thickness(1.5),
        };
    }

    private void OnKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.V && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = Composer.Paste();
            return;
        }
        if (approvalShown is not { } a) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            app.Chat.Answer(a, false);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && !(Composer.TextFocused && Composer.HasDraft))
        {
            // the buttons on the card answer to Enter themselves
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is Button focused && approvalHost.Child is { } card && IsInside(focused, card) && focused.Content is not null && !IsAllow(focused)) return;
            app.Chat.Answer(a, true);
            e.Handled = true;
        }
    }

    private static bool IsAllow(Button b) => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b) == L("Toestaan");

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var p = child; p != null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, parent)) return true;
        return false;
    }
}

/// <summary>How full the model's context is for this conversation, with a button to compact it.</summary>
public sealed partial class ContextMeter : Button
{
    private readonly AppState app;
    private readonly Spinner ring = new(12, false);
    private readonly TextBlock text = Ui.Label("", 12, null, FontWeights.SemiBold);
    private readonly Border fill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock counts = Ui.Caption("", Theme.Primary, 13);
    private readonly Button compact;
    private Conversation? current;
    private Windows.UI.Color shownColor;

    public ContextMeter(AppState app)
    {
        this.app = app;
        var bar = new Grid { Width = 46, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Theme.Solid(Colors.White, 0.12), Children = { fill } };
        Content = Ui.H(6, ring, text, bar);
        Padding = new Thickness(10, 5, 10, 6);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(0);
        Background = Theme.Solid(Colors.White, 0.07);
        VerticalAlignment = VerticalAlignment.Center;
        this.Tip(L("Context van het model in dit gesprek"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, L("Context"));

        compact = Ui.Btn(L("Nu samenvatten"), Compact);
        compact.HorizontalAlignment = HorizontalAlignment.Left;
        var body = Ui.V(8, Ui.Label(L("Context"), 15, null, FontWeights.SemiBold, display: true), counts,
            Ui.Caption(L($"Bij {(int)Math.Round(ChatClient.CompactAt * 100)}% wordt het begin automatisch samengevat; de berichten blijven zichtbaar."), Theme.Secondary), compact);
        body.Width = 280;
        var flyout = ChatUi.Popover(body);
        flyout.Opening += (_, _) => Fill();
        Flyout = flyout;
    }

    private void Fill()
    {
        counts.Text = L($"{current?.ContextTokens ?? 0} van {Math.Max(app.Chat.ContextLimit, 1)} tokens gebruikt in dit gesprek");
        compact.IsEnabled = !(current != null && app.Chat.IsBusy(current.Id)) && !app.Chat.Compacting && app.EngineState.IsRunning;
    }

    private async void Compact()
    {
        Flyout.Hide();
        if (current is not { } c) return;
        try { await app.Chat.CompactAsync(c.Id, app.Store, app.BaseUrl); }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or IOException) { App.Log(e); }
    }

    private static string Short(int n) => n >= 1_048_576 ? $"{n / 1_048_576}M" : n >= 1000 ? $"{n / 1024}K" : $"{n}";

    public void Update(Conversation? c)
    {
        current = c;
        int used = c?.ContextTokens ?? 0, limit = Math.Max(app.Chat.ContextLimit, 1);
        double share = Math.Min((double)used / limit, 1);
        var color = share > 0.9 ? Theme.Red : share > ChatClient.CompactAt * 0.85 ? Theme.Amber : Theme.Mint;
        bool working = app.Chat.Compacting || app.Chat.Learning;
        ring.Active = working;
        text.SetIfChanged(app.Chat.Compacting ? L("Samenvatten…") : app.Chat.Learning ? L("Noteert wat het geleerd heeft…") : $"{Short(used)} / {Short(limit)}");
        fill.Width = 46 * share;
        if (color != shownColor)
        {
            shownColor = color;
            fill.Background = new SolidColorBrush(color);
        }
    }
}
