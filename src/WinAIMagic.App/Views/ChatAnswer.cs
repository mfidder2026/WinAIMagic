using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>One answer of the model: thinking, text and steps of all its rounds in one card, in the order they came (AnswerView in
/// ChatView.swift). While the model works you see the latest parts as they come; when it is done the work folds into one line above
/// the answer, since the chat is about the answer.</summary>
public sealed partial class AnswerRow : Grid
{
    /// <summary>While working, at most this many of the latest parts.</summary>
    private const int Live = 14;
    private readonly RowMemory memory;
    private readonly Action<double>? shifted;
    private readonly BrandMark mark;
    private readonly PulseRing pulse;
    private readonly Border card;
    private readonly StackPanel parts = new() { Spacing = 10 };
    private readonly Button earlier;
    private readonly TextBlock earlierText = Ui.Label("", 13, Theme.Tertiary);
    private readonly Disclosure work;
    private readonly StackPanel workParts = new() { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Spinner spinner = new(16, false) { VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Beside the spinner: what the model prepares (a file, a command), or "Bezig…".</summary>
    private readonly TextBlock busyText = Ui.Label("", 13, Theme.Secondary);
    private readonly StackPanel busyRow;
    private readonly Border sources = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock stats = Ui.Label("", 12, Theme.Tertiary);
    private readonly Button copy, speak;
    private readonly FontIcon speakIcon = Ui.Icon(Glyph.Speaker, 12, Theme.Tertiary);
    private readonly FrameworkElement statsRow;
    private readonly Dictionary<string, PartView> views = [];
    private List<ChatMessage> rounds = [];
    private string layout = "", copyText = "", sourcesShown = "";
    private Guid? messageId;
    private bool streaming, wasStreaming, wasReading;

    internal AnswerRow(List<ChatMessage> rounds, RowMemory memory, Action<double>? shifted, bool streaming)
    {
        this.memory = memory;
        this.shifted = shifted;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnSpacing = 12;
        mark = new BrandMark(30) { VerticalAlignment = VerticalAlignment.Top };
        pulse = new PulseRing(30) { VerticalAlignment = VerticalAlignment.Top };
        Children.Add(pulse);
        Children.Add(mark);

        earlier = ChatUi.Plain(Ui.H(6, Ui.Icon(Glyph.Bullets, 13, Theme.Tertiary), earlierText), () => { memory.Steps = true; Relayout(); });
        earlier.Padding = new Thickness(2, 1, 6, 2);
        earlier.HorizontalAlignment = HorizontalAlignment.Left;
        work = new Disclosure(Glyph.Bullets, memory.Work, on =>
        {
            if (memory.Work == on) return;
            memory.Work = on;
            DispatcherQueue.TryEnqueue(Relayout);
        }) { HorizontalAlignment = HorizontalAlignment.Stretch };
        work.Content = workParts;
        busyRow = Ui.H(8, spinner, busyText);
        busyRow.HorizontalAlignment = HorizontalAlignment.Left;
        busyText.VerticalAlignment = VerticalAlignment.Center;

        copy = ChatUi.Plain(Ui.Icon(Glyph.Copy, 12, Theme.Tertiary), () => Ui.Copy(copyText), L("Kopieer"));
        copy.Width = 26;
        copy.Height = 22;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, L("Kopieer"));
        speak = ChatUi.Plain(speakIcon, () => { if (messageId is { } mid) Speech.Shared.Speak(copyText, mid); }, L("Lees voor"));
        speak.Width = 26;
        speak.Height = 22;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(speak, L("Lees voor"));
        statsRow = Ui.H(10, stats, copy, speak);

        card = new Border
        {
            Child = Ui.V(10, parts, busyRow, sources, statsRow), Padding = new Thickness(16), CornerRadius = new CornerRadius(18), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Theme.Solid(Colors.White, 0.05), BorderBrush = Theme.Solid(Colors.White, 0.07), BorderThickness = new Thickness(1),
        };
        Grid.SetColumn(card, 1);
        Children.Add(card);
        Update(rounds, streaming);
    }

    private void Relayout()
    {
        layout = "";
        Update(rounds, streaming);
    }

    /// <summary>Show the answer as it is now; cheap when nothing changed.</summary>
    internal void Update(List<ChatMessage> rounds, bool streaming)
    {
        this.rounds = rounds;
        this.streaming = streaming;
        var all = AnswerPart.Of(rounds, streaming);
        foreach (var p in all)
        {
            if (!views.TryGetValue(p.Id, out var v)) views[p.Id] = v = new PartView(p, memory, shifted);
            v.Update(p);
        }

        // what stands in the card, and what in the fold of a finished answer
        List<AnswerPart> folded = [], main;
        int hidden = 0;
        if (streaming)
        {
            hidden = memory.Steps ? 0 : Math.Max(0, all.Count - Live);
            main = all.GetRange(hidden, all.Count - hidden);
        }
        else
        {
            int tail = AnswerPart.TailStart(all);
            var before = all.GetRange(0, tail);
            bool simple = before.Count == 1 && before[0].Kind == PartKind.Thought
                || before.Count is > 0 and <= 3 && !before.Exists(p => p.Kind == PartKind.Thought);
            if (simple || before.Count == 0) main = all;
            else
            {
                folded = before;
                main = all.GetRange(tail, all.Count - tail);
            }
        }
        var key = $"{streaming}|{hidden}|{memory.Work}|{string.Join(",", folded.Select(p => p.Id))}|{string.Join(",", main.Select(p => p.Id))}";
        if (key != layout)
        {
            layout = key;
            parts.Children.Clear();
            workParts.Children.Clear();
            if (hidden > 0)
            {
                earlierText.SetIfChanged(L($"{hidden} eerdere stappen"));
                parts.Children.Add(earlier);
            }
            if (folded.Count > 0)
            {
                int steps = folded.Count(p => p.Kind == PartKind.Step);
                work.SetTitle(steps == 0 ? L("Gedachtegang") : L($"Gedachtegang en {steps} stappen"), false);
                parts.Children.Add(work);
                if (memory.Work) foreach (var p in folded) workParts.Children.Add(views[p.Id]);
            }
            foreach (var p in main) parts.Children.Add(views[p.Id]);
        }

        // what the model does right now, when the parts do not show it yet
        var last = rounds[^1];
        bool working = streaming && last.Role == ChatRole.Assistant && last.Error == null;
        bool preparing = working && last.Preparing != null;
        bool spin = preparing || working && last.Text.Length == 0 && last.Reasoning.Length == 0 && last.Activities.Count == 0;
        spinner.Active = spin;
        busyRow.Show(spin);
        if (spin) busyText.SetIfChanged(preparing ? last.Preparing! : L("Bezig…"));

        var answer = rounds.LastOrDefault(m => m.Role == ChatRole.Assistant);
        UpdateSources(answer?.Sources ?? []);
        statsRow.Show(answer?.TokensPerSecond != null && !streaming);
        if (answer?.TokensPerSecond is { } tps && !streaming)
        {
            var culture = AppLanguages.Current.Culture();
            var line = tps.ToString("0.0", culture) + " tok/s";
            if (answer.PromptTokensPerSecond is { } p) line += "   prompt " + p.ToString("0", culture) + " tok/s";
            stats.SetIfChanged(line);
            copyText = answer.Text;
        }
        messageId = answer?.Id;
        speak.Show(Settings.Shared.FeatureVoice);
        bool reading = answer != null && Speech.Shared.Speaking == answer.Id;
        if (reading != wasReading)
        {
            wasReading = reading;
            speakIcon.Glyph = reading ? Glyph.SpeakerOff : Glyph.Speaker;
            speak.Tip(reading ? L("Stop met voorlezen") : L("Lees voor"));
        }
        if (streaming != wasStreaming)
        {
            wasStreaming = streaming;
            mark.Active = streaming;
            pulse.Active = streaming;
            card.BorderBrush = streaming
                ? Theme.Gradient(new Point(0, 0), new Point(1, 1), Theme.Alpha(Theme.Accent, 0.6), Theme.Alpha(Theme.Violet, 0.6), Theme.Alpha(Theme.Pink, 0.6))
                : Theme.Solid(Colors.White, 0.07);
        }
    }

    private void UpdateSources(List<Source> list)
    {
        var key = string.Join("\n", list.Take(10).Select(s => s.Url));
        sources.Show(list.Count > 0);
        if (key == sourcesShown) return;
        sourcesShown = key;
        var row = Ui.H(8);
        foreach (var s in list.Take(10))
        {
            if (ChatUi.WebLink(s.Url) is not { } url) continue;
            var host = url.Host.Replace("www.", "");
            var chip = ChatUi.Plain(Ui.H(4, Ui.Icon(Glyph.Globe, 11, Theme.Secondary), Ui.Label(host.Length > 0 ? host : s.Title, 12, Theme.Secondary)),
                () => _ = Windows.System.Launcher.LaunchUriAsync(url), s.Title);
            chip.Padding = new Thickness(8, 3, 8, 4);
            chip.CornerRadius = new CornerRadius(11);
            chip.Background = Theme.Solid(Colors.White, 0.08);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, host);
            row.Children.Add(chip);
        }
        sources.Child = new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled,
        };
    }
}

/// <summary>One part of an answer: a thought, a step, text, a picture the model looked at, the app's own "go on", or an error.</summary>
public sealed partial class PartView : StackPanel
{
    private readonly RowMemory memory;
    private readonly PartKind kind;
    // a thought
    private Disclosure? thought;
    private MarkdownView? thoughts;
    private TextBlock? gist;
    private string reasoning = "";
    // a step
    private string stepKey = "";
    // text
    private MarkdownView? text, tail;
    private Button? showAll;
    private TextBlock? showAllText;
    private bool showWhole;
    private string lastText = "";
    // an error
    private TextBlock? error;

    internal PartView(AnswerPart p, RowMemory memory, Action<double>? shifted)
    {
        this.memory = memory;
        kind = p.Kind;
        Spacing = 4;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        switch (p.Kind)
        {
            case PartKind.Thought:
                var id = p.Id;
                thought = new Disclosure(Glyph.Think, false, on =>
                {
                    if (on) memory.OpenThoughts.Add(id); else memory.OpenThoughts.Remove(id);
                    gist?.Show(!on);
                    if (on && thoughts == null)
                    {
                        thoughts = new MarkdownView(shifted, true, 13, Theme.Secondary);
                        thought!.Content = new Border { Child = thoughts, BorderBrush = Theme.Solid(Colors.White, 0.18), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 4, 0, 0) };
                        thoughts.Set(LongText.ThinkingEnd(reasoning));
                    }
                });
                gist = new TextBlock { FontSize = 12, Foreground = Theme.Tertiary, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(24, 0, 0, 0) };
                Children.Add(thought);
                Children.Add(gist);
                reasoning = p.Text;
                if (memory.OpenThoughts.Contains(id)) thought.SetOpen(true);
                break;
            case PartKind.Text:
                text = new MarkdownView(shifted);
                tail = new MarkdownView(shifted);
                showAllText = Ui.Label("", 13, new SolidColorBrush(Theme.Accent));
                showAll = ChatUi.Plain(showAllText, () => { showWhole = true; ShowText(lastText); }, L("Toon alles"));
                showAll.HorizontalAlignment = HorizontalAlignment.Left;
                Spacing = 10;
                Children.Add(text);
                Children.Add(showAll);
                Children.Add(tail);
                break;
            case PartKind.Looked:
                // a picture the model asked to look at (view_image): small, so the user sees what it saw
                var row = Ui.H(8);
                foreach (var a in p.Attachments ?? []) row.Children.Add(ChatUi.Thumb(a.Jpeg, 160, 6));
                var names = string.Join(", ", (p.Attachments ?? []).Select(a => a.Filename));
                row.Children.Add(Ui.H(6, Ui.Icon(Glyph.Eye, 12, Theme.Secondary), Ui.Label(L($"Het model bekijkt {names}"), 12, Theme.Secondary)));
                Children.Add(row);
                break;
            case PartKind.Resumed:
                Children.Add(Ui.H(6, Ui.Icon(Glyph.Refresh, 12, Theme.Secondary), Ui.Label(L("Het model was nog niet klaar en gaat automatisch door"), 12, Theme.Secondary)));
                break;
            case PartKind.Error:
                error = Ui.Caption("", new SolidColorBrush(Theme.Red), 13);
                error.IsTextSelectionEnabled = true;
                Children.Add(error);
                break;
        }
    }

    internal void Update(AnswerPart p)
    {
        switch (kind)
        {
            case PartKind.Thought:
                thought!.SetTitle(p.Live ? L("Denkt na…") : L("Gedachte"), p.Live);
                reasoning = p.Text;
                thoughts?.Set(LongText.ThinkingEnd(reasoning));
                // what it thinks now, or what it thought: the gist without opening it
                gist!.MaxLines = p.Live ? 3 : 1;
                gist.SetIfChanged(AnswerPart.Gist(p.Text, p.Live));
                gist.Show(!memory.OpenThoughts.Contains(p.Id));
                break;
            case PartKind.Step when p.Step is { } a:
                var key = $"{a.Label}{a.Done}{a.Denied}{a.Output?.Length}";
                if (key == stepKey) break;
                stepKey = key;
                Children.Clear();
                var step = new ActivityRow(a, memory) { HorizontalAlignment = HorizontalAlignment.Left };
                Children.Add(step);
                break;
            case PartKind.Text:
                ShowText(p.Text);
                break;
            case PartKind.Error:
                error!.SetIfChanged(p.Text);
                break;
        }
    }

    /// <summary>The text; a very long one as its start and end with "Toon alles" between them.</summary>
    private void ShowText(string value)
    {
        lastText = value;
        var folded = showWhole ? null : LongText.Folded(value);
        showAll!.Show(folded != null);
        tail!.Show(folded != null);
        if (folded is { } f)
        {
            text!.Set(f.Head);
            tail.Set(f.Tail);
            var hidden = f.Hidden.ToString("N0", AppLanguages.Current.Culture());
            showAllText!.SetIfChanged(L($"… {hidden} tekens verborgen · Toon alles"));
        }
        else text!.Set(value);
    }
}
