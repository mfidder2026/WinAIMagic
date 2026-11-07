using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinAIMagic.Chat;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.App.Views;

/// <summary>A vertical list that only keeps the rows near the viewport in the visual tree; a row that leaves it goes away and
/// its measured height stays. Used for the messages of a chat and for the pages of one long message.</summary>
public sealed partial class VirtualList : Panel
{
    private const double Slack = 900;
    private readonly Func<object, FrameworkElement> build;
    private readonly Func<object, double, double> estimate;
    private readonly double gap;
    private readonly Dictionary<object, double> known = new();
    private readonly Dictionary<object, FrameworkElement> live = new();
    private List<object> keys = [];
    private Rect view = new(0, 0, 0, 1000);
    private double coveredFrom = double.MinValue, coveredTo = double.MaxValue, width;
    private bool stick;

    public VirtualList(Func<object, FrameworkElement> build, Func<object, double, double> estimate, double gap = 0)
    {
        this.build = build;
        this.estimate = estimate;
        this.gap = gap;
        EffectiveViewportChanged += (_, e) =>
        {
            var v = e.EffectiveViewport;
            if (v.Height <= 0) return;
            bool taller = v.Height != view.Height;
            view = v;
            if (stick ? taller : v.Y < coveredFrom || v.Y + v.Height > coveredTo) InvalidateMeasure();
        };
    }

    /// <summary>Rows above the viewport changed height by this: the scroller keeps what is on screen where it is.</summary>
    public Action<double>? Shifted { get; set; }
    public IReadOnlyDictionary<object, FrameworkElement> Live => live;

    /// <summary>Fill from the end, for a chat that follows its last message.</summary>
    public bool StickToEnd
    {
        get => stick;
        set
        {
            if (stick == value) return;
            stick = value;
            InvalidateMeasure();
        }
    }

    public void SetKeys(List<object> next)
    {
        if (keys.SequenceEqual(next)) return;
        keys = next;
        InvalidateMeasure();
    }

    public void Reset()
    {
        Children.Clear();
        live.Clear();
        known.Clear();
        keys = [];
        InvalidateMeasure();
    }

    private double Realize(object key, double w, HashSet<object> wanted)
    {
        if (!live.TryGetValue(key, out var e))
        {
            e = build(key);
            live[key] = e;
            Children.Add(e);
        }
        e.Measure(new Size(w, double.PositiveInfinity));
        wanted.Add(key);
        return known[key] = e.DesiredSize.Height;
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? Math.Max(width, 100) : available.Width;
        if (w != width)
        {
            width = w;
            foreach (var k in known.Keys.Where(k => !live.ContainsKey(k)).ToList()) known.Remove(k);
        }
        double Known(object k) => known.TryGetValue(k, out var h) ? h : estimate(k, w);

        var wanted = new HashSet<object>();
        double total = 0, shift = 0;
        if (stick)
        {
            double need = view.Height + Slack, have = 0;
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                double h = have < need ? Realize(keys[i], w, wanted) : Known(keys[i]);
                have += h + gap;
            }
            total = have;
            coveredFrom = double.MinValue;
            coveredTo = double.MaxValue;
        }
        else
        {
            double top = view.Y - Slack, bottom = view.Y + view.Height + Slack;
            foreach (var key in keys)
            {
                double h = Known(key);
                if (total + h >= top && total <= bottom)
                {
                    double old = h;
                    h = Realize(key, w, wanted);
                    if (total + old <= view.Y) shift += h - old;
                }
                total += h + gap;
            }
            coveredFrom = view.Y - Slack / 2;
            coveredTo = view.Y + view.Height + Slack / 2;
        }
        foreach (var k in live.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            Children.Remove(live[k]);
            live.Remove(k);
        }
        if (Math.Abs(shift) > 0.5) DispatcherQueue.TryEnqueue(() => Shifted?.Invoke(shift));
        // a short text keeps its own width: the card around it then hugs it
        double wide = keys.Count == 1 && live.Count == 1 ? Math.Min(w, live.Values.First().DesiredSize.Width) : w;
        return new Size(wide, Math.Max(total - gap, 0));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (var key in keys)
        {
            if (!known.TryGetValue(key, out var h)) h = estimate(key, width);
            if (live.TryGetValue(key, out var e)) e.Arrange(new Rect(0, y, final.Width, h));
            y += h + gap;
        }
        return final;
    }
}

/// <summary>Markdown from MarkdownLite, or plain text, in pages: only the pages near the viewport exist, and while a message grows
/// only its tail is touched. A long text is parsed off the UI thread, one parse at a time.</summary>
public sealed partial class MarkdownView : Grid
{
    private enum Kind { Text, Code, Heading, Table }

    /// <param name="Joined">Goes on from the piece before it, without the space between blocks.</param>
    private sealed record Piece(Kind Kind, string Text, string Language = "", int Level = 0, bool Joined = false, int Lines = 1);

    private const int ChunkLines = 40, ChunkChars = 3000, PageCost = 120, SyncParse = 16_000;

    private readonly VirtualList list;
    private readonly bool plain;
    private readonly double size;
    private readonly Brush? color;
    private List<List<Piece>> pages = [];
    private string text = "";
    private bool parsing;

    public MarkdownView(Action<double>? shifted = null, bool plain = false, double size = 14, Brush? color = null)
    {
        this.plain = plain;
        this.size = size;
        this.color = color;
        list = new VirtualList(k => new PageView(this, pages[(int)k]), (k, w) => Estimate(pages[(int)k], w)) { Shifted = shifted };
        Children.Add(list);
    }

    private double LineHeight => Math.Round(size * 1.55);

    private double Estimate(List<Piece> page, double w)
    {
        double perLine = Math.Max(w / (size * 0.52), 20), sum = 0;
        foreach (var p in page)
            sum += p.Kind == Kind.Code ? p.Lines * CodeLines.LineHeight + 48
                : p.Kind == Kind.Table ? p.Lines * (LineHeight + 6) + 28
                : p.Kind == Kind.Heading ? 36
                : (p.Lines + p.Text.Length / perLine) * LineHeight + (p.Joined ? 0 : 8);
        return sum;
    }

    /// <summary>The text as it is now; call it as often as it changes.</summary>
    public async void Set(string value)
    {
        if (value == text) return;
        text = value;
        if (parsing) return;
        if (value.Length < SyncParse)
        {
            Apply(Paginate(value, plain));
            return;
        }
        parsing = true;
        try
        {
            while (true)
            {
                string snapshot = text;
                bool isPlain = plain;
                var started = Environment.TickCount64;
                var parsed = await Task.Run(() => Paginate(snapshot, isPlain));
                Apply(parsed);
                if (snapshot == text) break;
                await Task.Delay((int)Math.Clamp((Environment.TickCount64 - started) * 3, 100, 1000));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or OutOfMemoryException) { App.Log(e); }
        finally { parsing = false; }
    }

    private void Apply(List<List<Piece>> next)
    {
        for (int i = 0; i < next.Count; i++)
        {
            if (i < pages.Count && pages[i].SequenceEqual(next[i])) next[i] = pages[i];
            else if (list.Live.TryGetValue(i, out var view)) ((PageView)view).Apply(next[i]);
        }
        pages = next;
        list.SetKeys([.. Enumerable.Range(0, pages.Count).Select(i => (object)i)]);
    }

    private static List<List<Piece>> Paginate(string text, bool plain)
    {
        var all = new List<List<Piece>>();
        var page = new List<Piece>();
        int cost = 0;
        void Add(Piece p)
        {
            if (cost >= PageCost)
            {
                all.Add(page);
                page = [];
                cost = 0;
            }
            page.Add(p);
            cost += p.Lines + p.Text.Length / 120;
        }
        if (plain) Chunks(text, Add);
        else
            foreach (var block in MarkdownLite.Blocks(text))
                switch (block)
                {
                    case MarkdownBlock.Code c: Add(new Piece(Kind.Code, c.Content, c.Language, 0, false, c.Content.Count(x => x == '\n') + 1)); break;
                    case MarkdownBlock.Heading h: Add(new Piece(Kind.Heading, h.Value, "", h.Level)); break;
                    case MarkdownBlock.Table tb: Add(new Piece(Kind.Table, tb.Raw, "", 0, false, tb.Rows.Count + (tb.Header == null ? 0 : 1))); break;
                    case MarkdownBlock.Text t: Chunks(t.Value, Add); break;
                }
        if (page.Count > 0) all.Add(page);
        return all;
    }

    /// <summary>One paragraph as pieces of a limited size; a growing text then only changes its last piece.</summary>
    private static void Chunks(string s, Action<Piece> add)
    {
        var part = new StringBuilder();
        int lines = 0;
        bool joined = false;
        void Flush()
        {
            if (lines == 0) return;
            add(new Piece(Kind.Text, part.ToString(), "", 0, joined, lines));
            part.Clear();
            lines = 0;
            joined = true;
        }
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.AsSpan();
            do
            {
                int take = line.Length;
                if (take > ChunkChars)
                {
                    take = line[..ChunkChars].LastIndexOf(' ') + 1;
                    if (take <= 0) take = ChunkChars;
                }
                if (lines >= ChunkLines || part.Length + take > ChunkChars) Flush();
                if (lines > 0) part.Append('\n');
                part.Append(line[..take]);
                lines++;
                line = line[take..];
            }
            while (line.Length > 0);
        }
        Flush();
    }

    private sealed partial class PageView : StackPanel
    {
        private readonly MarkdownView owner;
        private List<Piece> shown = [];

        public PageView(MarkdownView owner, List<Piece> page)
        {
            this.owner = owner;
            Apply(page);
        }

        public void Apply(List<Piece> page)
        {
            for (int i = 0; i < page.Count; i++)
            {
                var p = page[i];
                if (i < shown.Count && shown[i] == p) continue;
                if (i < shown.Count && shown[i].Kind == p.Kind) owner.Fill(Children[i], p, i == 0);
                else
                {
                    var e = owner.Make(p);
                    owner.Fill(e, p, i == 0);
                    if (i < Children.Count) Children[i] = e;
                    else Children.Add(e);
                }
            }
            while (Children.Count > page.Count) Children.RemoveAt(Children.Count - 1);
            shown = page;
        }
    }

    private UIElement Make(Piece p)
    {
        if (p.Kind == Kind.Code) return new CodeBox();
        if (p.Kind == Kind.Table) return new TableBox(size);
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = Theme.Text, FontSize = size, LineHeight = LineHeight };
        if (color != null) t.Foreground = color;
        return t;
    }

    private void Fill(UIElement e, Piece p, bool first)
    {
        if (e is CodeBox code)
        {
            code.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
            code.Set(p.Language, p.Text);
            return;
        }
        if (e is TableBox table)
        {
            table.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
            table.Set(MarkdownLite.Table(p.Text.Split('\n')), (tb, s) => Inlines(tb, s));
            return;
        }
        var t = (TextBlock)e;
        t.Margin = new Thickness(0, first || p.Joined ? 0 : p.Kind == Kind.Heading ? 12 : 8, 0, 0);
        if (p.Kind == Kind.Heading)
        {
            t.FontSize = p.Level <= 1 ? 22 : p.Level == 2 ? 18 : 15;
            t.FontWeight = p.Level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
            t.FontFamily = Theme.Display;
            t.LineHeight = 0;
        }
        if (plain) t.Text = p.Text;
        else Inlines(t, p.Text);
    }

    private void Inlines(TextBlock t, string s)
    {
        t.Inlines.Clear();
        foreach (var r in MarkdownLite.Inline(s))
        {
            var run = new Run { Text = r.Text };
            if (r.Bold) run.FontWeight = FontWeights.SemiBold;
            if (r.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (r.Strike) run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            if (r.Code)
            {
                run.FontFamily = Theme.Mono;
                run.FontSize = t.FontSize - 0.5;
                run.Foreground = new SolidColorBrush(Theme.Cyan);
            }
            if (r.Link is { } link && ChatUi.WebLink(link) is { } uri)
            {
                var h = new Hyperlink { NavigateUri = uri };
                h.Inlines.Add(run);
                t.Inlines.Add(h);
            }
            else t.Inlines.Add(run);
        }
    }
}

/// <summary>A Markdown table as a grid: the header bold with a line under it, columns as wide as they need, the text selectable.</summary>
public sealed partial class TableBox : Grid
{
    private readonly double size;
    private readonly Grid grid = new() { ColumnSpacing = 16, RowSpacing = 6 };

    public TableBox(double size)
    {
        this.size = size;
        Children.Add(new ScrollViewer
        {
            Content = grid,
            Padding = new Thickness(10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
        });
        Background = Theme.Solid(Microsoft.UI.Colors.Black, 0.18);
        BorderBrush = Theme.Solid(Theme.Accent, 0.18);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    public void Set(MarkdownBlock.Table table, Action<TextBlock, string> inlines)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        int columns = Math.Max(table.Header?.Count ?? 0, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        void Add(IReadOnlyList<string> cells, bool bold)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < columns; i++)
            {
                var t = new TextBlock { IsTextSelectionEnabled = true, FontFamily = Theme.Text, FontSize = size, TextWrapping = TextWrapping.NoWrap };
                inlines(t, i < cells.Count ? cells[i] : "");
                if (bold) t.FontWeight = FontWeights.SemiBold;
                Grid.SetRow(t, row);
                Grid.SetColumn(t, i);
                grid.Children.Add(t);
            }
            row++;
        }
        if (table.Header is { } header)
        {
            Add(header, true);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { Height = 1, Background = Theme.Solid(Theme.Accent, 0.25) };
            Grid.SetRow(line, row++);
            Grid.SetColumnSpan(line, Math.Max(columns, 1));
            grid.Children.Add(line);
        }
        foreach (var r in table.Rows) Add(r, false);
    }
}

/// <summary>A fenced block: its language, a copy button, and the lines in a monospaced card.</summary>
public sealed partial class CodeBox : Grid
{
    private readonly TextBlock language = Ui.Label("", 12, Theme.Secondary);
    private readonly CodeLines lines = new();
    private string code = "";

    public CodeBox()
    {
        var copy = ChatUi.Plain(Ui.H(5, Ui.Icon(Glyph.Copy, 12, Theme.Secondary), Ui.Label(L("Kopieer"), 12, Theme.Secondary)), () => Ui.Copy(code));
        copy.Padding = new Thickness(6, 2, 6, 3);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, L("Kopieer"));
        var head = Ui.Row("*,auto", 8, language, copy);
        head.Padding = new Thickness(10, 4, 6, 4);
        var body = new ScrollViewer
        {
            Content = lines,
            Padding = new Thickness(10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
        };
        Children.Add(Ui.V(0, head, Ui.Divider(), body));
        Background = Theme.Solid(Microsoft.UI.Colors.Black, 0.35);
        BorderBrush = Theme.Solid(Theme.Accent, 0.25);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
    }

    public void Set(string lang, string content)
    {
        code = content;
        language.SetIfChanged(lang.Length == 0 ? "code" : lang);
        lines.Set(content);
    }
}

/// <summary>The lines of a code block. A long block only has the lines near the viewport in its text; every line is equally
/// high, so its size is known without laying out the rest.</summary>
public sealed partial class CodeLines : Grid
{
    public const double LineHeight = 19;
    private const double CharWidth = 7.83;
    private const int WindowFrom = 400, Around = 150, LongLine = 4000;
    private readonly TextBlock view = new()
    {
        FontFamily = Theme.Mono, FontSize = 13, LineHeight = LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };
    private string[] lines = [];
    private string shown = "";
    private bool windowed;
    private int first, last = -1;
    private double viewTop, viewHeight = 900;

    public CodeLines()
    {
        Children.Add(view);
        HorizontalAlignment = HorizontalAlignment.Left;
        EffectiveViewportChanged += (_, e) =>
        {
            if (e.EffectiveViewport.Height <= 0) return;
            viewTop = e.EffectiveViewport.Y;
            viewHeight = e.EffectiveViewport.Height;
            if (windowed) ShowWindow(false);
        };
    }

    public void Set(string code)
    {
        if (code == shown) return;
        shown = code;
        int before = lines.Length;
        lines = code.Split('\n');
        int widest = 0;
        foreach (var l in lines) widest = Math.Max(widest, l.Length);
        windowed = lines.Length > WindowFrom || widest > LongLine;
        if (!windowed)
        {
            view.Text = code.Replace("\t", "    ");
            view.Margin = new Thickness(0);
            Width = Height = double.NaN;
            return;
        }
        Width = Math.Min(widest, LongLine) * CharWidth + 8;
        Height = lines.Length * LineHeight;
        ShowWindow(last >= before - 1);
    }

    private void ShowWindow(bool force)
    {
        int from = Math.Max((int)(viewTop / LineHeight), 0), to = (int)((viewTop + viewHeight) / LineHeight);
        if (!force && from >= first && to <= last) return;
        first = Math.Clamp(from - Around, 0, Math.Max(lines.Length - 1, 0));
        last = Math.Clamp(to + Around, first, Math.Max(lines.Length - 1, 0));
        var o = new StringBuilder();
        for (int i = first; i <= last; i++)
        {
            var l = lines[i];
            if (i > first) o.Append('\n');
            if (l.Length > LongLine) o.Append(l.AsSpan(0, LongLine)).Append('…');
            else o.Append(l);
        }
        o.Replace("\t", "    ");
        view.Margin = new Thickness(0, first * LineHeight, 0, 0);
        view.Text = o.ToString();
    }
}
