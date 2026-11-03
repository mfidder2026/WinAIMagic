using System.Text;
using System.Text.RegularExpressions;

namespace WinAIMagic.Documents;

public record struct MDInline()
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Code { get; set; }
    public bool Strike { get; set; }
    public string? Link { get; set; }
    public bool IsBreak { get; set; }
    /// An image in the text: `Text` is its description.
    public string? ImageSource { get; set; }
}

public enum MDKind { Heading, Paragraph, List, Table, Code, Quote, Image, PageBreak, Caption }

/// One block of a Markdown document, ready to become OOXML.
public sealed class MDBlock(MDKind kind)
{
    public MDKind Kind { get; set; } = kind;
    public List<MDInline> Inlines { get; set; } = [];
    public int Level { get; set; }
    public bool Ordered { get; set; }
    public int ListID { get; set; }
    public List<List<MDInline>> Header { get; set; } = [];
    public List<List<List<MDInline>>> Rows { get; set; } = [];
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";

    public string Plain => string.Concat(Inlines.Select(i => i.Text));
}

/// The Markdown models and people write: headings, lists, tables, quotes, code, images, links and emphasis.
public static class MarkdownBlocks
{
    private static readonly Rx PageBreak = new("<!--\\s*(pagebreak|page-break|nieuwe pagina)\\s*-->|\\\\(newpage|pagebreak)", RegexOptions.IgnoreCase);
    private static readonly Rx Heading = new("^ {0,3}(#{1,6})[ \\t]+(.*?)[ \\t]*#*[ \\t]*$");
    private static readonly Rx Fence = new("^ {0,3}(```+|~~~+)");
    private static readonly Rx Rule = new("^ {0,3}([-*_])[ \\t]*(\\1[ \\t]*){2,}$");
    private static readonly Rx ListItem = new("^([ \\t]*)([-*+]|\\d{1,9}[.)])[ \\t]+(.*)$");
    private static readonly Rx TableDelimiter = new("^[ \\t]*\\|?[ \\t]*:?-+:?[ \\t]*(\\|[ \\t]*:?-+:?[ \\t]*)*\\|?[ \\t]*$");
    private static readonly Rx CaptionLine = new("^(figuur|figure|afbeelding|tabel|table)\\s+\\d+[:.]", RegexOptions.IgnoreCase);

    public static List<MDBlock> Parse(string source)
    {
        var text = PageBreak.Replace(source.Replace("\r\n", "\n"), _ => "\n\n<!-- pagebreak -->\n\n");
        var lines = text.Split('\n');
        var output = new List<MDBlock>();
        int listCount = 0;
        int i = 0;

        static bool StartsBlock(string line) =>
            Heading.Test(line) || Fence.Test(line) || ListItem.Test(line) || line.HasPrefix(">") || line.HasPrefix("<!--");

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimWS();
            if (trimmed.Length == 0) { i++; continue; }

            if (Fence.First(line) is Rx.Match fence && fence.Groups[0] is string marker)
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimWS().HasPrefix(marker))
                {
                    code.Add(lines[i]);
                    i++;
                }
                i++;
                output.Add(new MDBlock(MDKind.Code) { Text = string.Join("\n", code) });
                continue;
            }
            if (trimmed.HasPrefix("<!--"))
            {
                var comment = trimmed;
                while (!comment.Contains("-->") && i + 1 < lines.Length)
                {
                    i++;
                    comment += "\n" + lines[i];
                }
                i++;
                if (comment.Contains("pagebreak")) output.Add(new MDBlock(MDKind.PageBreak));
                continue;
            }
            if (Heading.First(line) is Rx.Match heading)
            {
                output.Add(new MDBlock(MDKind.Heading) { Inlines = Inlines(heading.Groups[1] ?? ""), Level = heading.Groups[0]?.Length ?? 1 });
                i++;
                continue;
            }
            if (Rule.Test(line)) { i++; continue; }
            if (line.HasPrefix(">"))
            {
                var quoted = new List<string>();
                while (i < lines.Length && lines[i].HasPrefix(">"))
                {
                    quoted.Add(lines[i][1..].TrimWS());
                    i++;
                }
                var part = new List<string>();
                foreach (var q in quoted.Append(""))
                {
                    if (q.Length > 0) { part.Add(q); continue; }
                    if (part.Count > 0) output.Add(new MDBlock(MDKind.Quote) { Inlines = Inlines(string.Join(" ", part)) });
                    part = [];
                }
                continue;
            }
            if (i + 1 < lines.Length && line.Contains('|') && lines[i + 1].Contains('-') && TableDelimiter.Test(lines[i + 1]))
            {
                var table = new MDBlock(MDKind.Table) { Header = Cells(line).Select(Inlines).ToList() };
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && lines[i].TrimWS().Length > 0)
                {
                    table.Rows.Add(Cells(lines[i]).Select(Inlines).ToList());
                    i++;
                }
                output.Add(table);
                continue;
            }
            if (ListItem.Test(line))
            {
                ParseList(lines, ref i, output, ref listCount);
                continue;
            }
            var para = new List<string> { line.TrimWS() };
            var hardBreaks = new List<bool> { line.HasSuffix("  ") || line.HasSuffix("\\") };
            i++;
            int setext = 0;
            while (i < lines.Length)
            {
                var next = lines[i];
                var t = next.TrimWS();
                if (t.Length == 0 || StartsBlock(next)) break;
                if (para.Count == 1 && t.All(c => c == '=')) { setext = 1; i++; break; }
                if (para.Count == 1 && t.Length >= 2 && t.All(c => c == '-')) { setext = 2; i++; break; }
                if (Rule.Test(next)) break;
                para.Add(t);
                hardBreaks.Add(next.HasSuffix("  ") || next.HasSuffix("\\"));
                i++;
            }
            if (setext > 0)
            {
                output.Add(new MDBlock(MDKind.Heading) { Inlines = Inlines(para[0]), Level = setext });
                continue;
            }
            var parts = new List<MDInline>();
            for (int n = 0; n < para.Count; n++)
            {
                var p = para[n];
                var clean = p.HasSuffix("\\") ? p[..^1] : p;
                parts.AddRange(Inlines(clean));
                if (n < para.Count - 1) parts.Add(hardBreaks[n] ? new MDInline { IsBreak = true } : new MDInline { Text = " " });
            }
            AddParagraph(Merge(parts), output);
        }
        return output;
    }

    private static void AddParagraph(List<MDInline> parts, List<MDBlock> output)
    {
        var images = parts.Where(p => p.ImageSource != null).ToList();
        var rest = parts.Where(p => p.ImageSource == null).ToList();
        var plain = string.Concat(rest.Select(p => p.Text)).TrimWSNL();
        if (plain.Length > 0)
        {
            bool isCaption = CaptionLine.Test(plain) && output.Count > 0 && output[^1].Kind is MDKind.Image or MDKind.Table;
            output.Add(new MDBlock(isCaption ? MDKind.Caption : MDKind.Paragraph) { Inlines = rest });
        }
        foreach (var im in images) output.Add(new MDBlock(MDKind.Image) { Text = im.Text, Source = im.ImageSource ?? "" });
    }

    private static void ParseList(string[] lines, ref int i, List<MDBlock> output, ref int listCount)
    {
        var stack = new List<(int Indent, bool Ordered, int Id)>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (ListItem.First(line) is Rx.Match m)
            {
                int indent = (m.Groups[0] ?? "").Replace("\t", "    ").Length;
                var marker = m.Groups[1] ?? "-";
                bool ordered = Str.CharIsNumber(marker.Graphemes()[0]);
                while (stack.Count > 0 && indent < stack[^1].Indent) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0 && indent == stack[^1].Indent && stack[^1].Ordered != ordered) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0 || indent > stack[^1].Indent + 1)
                {
                    listCount++;
                    stack.Add((indent, ordered, listCount));
                }
                var top = stack[^1];
                output.Add(new MDBlock(MDKind.List) { Inlines = Inlines(m.Groups[2] ?? ""), Level = stack.Count - 1, Ordered = top.Ordered, ListID = top.Id });
                i++;
                continue;
            }
            var t = line.TrimWS();
            if (t.Length == 0)
            {
                // a blank line ends the list unless more of it follows
                int next = i + 1;
                while (next < lines.Length && lines[next].TrimWS().Length == 0) next++;
                if (next >= lines.Length) { i = next; return; }
                var after = lines[next];
                if (ListItem.Test(after)) { i = next; continue; }
                if (after.HasPrefix("  ") || after.HasPrefix("\t"))
                {
                    output[^1].Inlines.Add(new MDInline { IsBreak = true });
                    output[^1].Inlines.AddRange(Inlines(after.TrimWS()));
                    i = next + 1;
                    continue;
                }
                i = next;
                return;
            }
            // a continuation line of the item, unless another kind of block starts
            if (Heading.Test(line) || Fence.Test(line) || line.HasPrefix(">") || line.HasPrefix("<!--") || line.Contains('|')) return;
            output[^1].Inlines = Merge([.. output[^1].Inlines, new MDInline { Text = " " }, .. Inlines(t)]);
            i++;
        }
    }

    /// The cells of a table row; \| is a | in a cell.
    private static List<string> Cells(string line)
    {
        var t = line.TrimWS();
        if (t.HasPrefix("|")) t = t[1..];
        if (t.HasSuffix("|") && !t.HasSuffix("\\|")) t = t[..^1];
        var output = new List<string>();
        var cell = new StringBuilder();
        bool escaped = false;
        foreach (var c in t.Graphemes())
        {
            if (escaped)
            {
                cell.Append(c == "|" ? "|" : "\\" + c);
                escaped = false;
            }
            else if (c == "\\")
            {
                escaped = true;
            }
            else if (c == "|")
            {
                output.Add(cell.ToString().TrimWS());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }
        if (escaped) cell.Append('\\');
        output.Add(cell.ToString().TrimWS());
        return output;
    }

    // MARK: - inlines

    private static readonly Rx BreakTag = new("^<br\\s*/?>", RegexOptions.IgnoreCase);
    private static readonly Rx Autolink = new("^<(https?://[^>\\s]+)>");
    private static readonly Rx HtmlTag = new("^</?[a-zA-Z!][^>]*>");

    public static List<MDInline> Inlines(string text) => Merge(Scan(text.Graphemes(), new MDInline()));

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static List<MDInline> Scan(string[] c, MDInline style)
    {
        var output = new List<MDInline>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            output.Add(style with { Text = plain.ToString() });
            plain.Clear();
        }
        int i = 0;
        while (i < c.Length)
        {
            var ch = c[i];
            if (ch == "\\" && i + 1 < c.Length && (Str.CharIsPunctuation(c[i + 1]) || Str.CharIsSymbol(c[i + 1])))
            {
                plain.Append(c[i + 1]);
                i += 2;
                continue;
            }
            if (ch == "`")
            {
                int n = 0;
                while (i + n < c.Length && c[i + n] == "`") n++;
                if (Closing(c, i + n, Repeat("`", n)) is int end)
                {
                    Flush();
                    output.Add(style with { Text = string.Concat(c[(i + n)..end]).TrimWS(), Code = true });
                    i = end + n;
                    continue;
                }
                plain.Append(Repeat("`", n));
                i += n;
                continue;
            }
            if (ch == "<")
            {
                var tail = string.Concat(c[i..]);
                if (BreakTag.First(tail) is Rx.Match brk)
                {
                    Flush();
                    output.Add(new MDInline { IsBreak = true });
                    i += tail.Substring(brk.Range.Location, brk.Range.Length).GraphemeCount();
                    continue;
                }
                if (Autolink.First(tail) is Rx.Match auto && auto.Groups[0] is string url)
                {
                    Flush();
                    output.Add(style with { Text = url, Link = url });
                    i += url.GraphemeCount() + 2;
                    continue;
                }
                if (HtmlTag.First(tail) is Rx.Match tag)
                {
                    Flush();
                    i += tail.Substring(tag.Range.Location, tag.Range.Length).GraphemeCount();
                    continue;
                }
            }
            if (ch == "!" && i + 1 < c.Length && c[i + 1] == "[" && LinkParts(c, i + 1) is var (alt, source, imageEnd))
            {
                Flush();
                output.Add(new MDInline { Text = alt, ImageSource = source });
                i = imageEnd;
                continue;
            }
            if (ch == "[" && LinkParts(c, i) is var (label, target, linkEnd))
            {
                Flush();
                output.AddRange(Scan(label.Graphemes(), style with { Link = target }));
                i = linkEnd;
                continue;
            }
            if (ch is "*" or "_" or "~")
            {
                int n = 0;
                while (i + n < c.Length && c[i + n] == ch) n++;
                bool wordInside = ch == "_" && i > 0 && (Str.CharIsLetter(c[i - 1]) || Str.CharIsNumber(c[i - 1]));
                bool opens = i + n < c.Length && !Str.CharIsWhitespace(c[i + n]) && !wordInside;
                if (opens && (ch != "~" || n >= 2))
                {
                    int count = ch == "~" ? 2 : Math.Min(n, 3);
                    if (Closing(c, i + count, Repeat(ch, count)) is int end)
                    {
                        Flush();
                        var inner = style;
                        if (ch == "~") inner.Strike = true;
                        if (count == 3 || (count == 2 && ch != "~")) inner.Bold = true;
                        if (count == 3 || count == 1) inner.Italic = true;
                        output.AddRange(Scan(c[(i + count)..end], inner));
                        i = end + count;
                        continue;
                    }
                }
                plain.Append(Repeat(ch, n));
                i += n;
                continue;
            }
            plain.Append(ch);
            i++;
        }
        Flush();
        return output;
    }

    /// Where `marker` closes, not right after a space.
    private static int? Closing(string[] c, int from, string marker)
    {
        var m = marker.Graphemes();
        if (from >= c.Length || c.Length < m.Length) return null;
        int i = from;
        while (i + m.Length <= c.Length)
        {
            if (c[i] == "\\") { i += 2; continue; }
            if (c.AsSpan(i, m.Length).SequenceEqual(m))
            {
                bool single = m[0] != "`";
                int next = i + m.Length;
                // ** must not close on the first two stars of ***
                if (single && next < c.Length && c[next] == m[0] && m.Length < 3) { i++; continue; }
                if (!single || (i > from && !Str.CharIsWhitespace(c[i - 1]))) return i;
            }
            i++;
        }
        return null;
    }

    /// `[label](target "title")` starting at the `[`.
    private static (string Label, string Target, int End)? LinkParts(string[] c, int from)
    {
        int depth = 0;
        int i = from;
        while (i < c.Length)
        {
            if (c[i] == "\\") { i += 2; continue; }
            if (c[i] == "[") depth++;
            if (c[i] == "]")
            {
                depth--;
                if (depth == 0) break;
            }
            i++;
        }
        if (i + 1 >= c.Length || c[i] != "]" || c[i + 1] != "(") return null;
        var label = string.Concat(c[(from + 1)..i]);
        int j = i + 2;
        int parens = 1;
        var inner = new StringBuilder();
        while (j < c.Length)
        {
            if (c[j] == "(") parens++;
            if (c[j] == ")")
            {
                parens--;
                if (parens == 0) break;
            }
            inner.Append(c[j]);
            j++;
        }
        if (j >= c.Length) return null;
        var target = inner.ToString().TrimWS();
        int space = target.IndexOf(' ');
        if (space >= 0 && target[space..].Contains('"')) target = target[..space];
        if (target.HasPrefix("<") && target.HasSuffix(">")) target = target[1..^1];
        return (label, target, j + 1);
    }

    private static List<MDInline> Merge(List<MDInline> parts)
    {
        var output = new List<MDInline>();
        foreach (var p in parts)
        {
            if (output.Count > 0)
            {
                var last = output[^1];
                if (!last.IsBreak && !p.IsBreak && last.ImageSource == null && p.ImageSource == null
                    && last.Bold == p.Bold && last.Italic == p.Italic && last.Code == p.Code && last.Strike == p.Strike && last.Link == p.Link)
                {
                    output[^1] = last with { Text = last.Text + p.Text };
                    continue;
                }
            }
            output.Add(p);
        }
        return output;
    }
}
