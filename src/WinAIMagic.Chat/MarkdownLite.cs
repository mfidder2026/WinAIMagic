using System.Text;
using System.Text.RegularExpressions;

namespace WinAIMagic.Chat;

/// <summary>What the chat renders: paragraphs, fenced code, headings and tables.</summary>
public abstract record MarkdownBlock
{
    public sealed record Text(string Value) : MarkdownBlock;
    public sealed record Code(string Language, string Content) : MarkdownBlock;
    public sealed record Heading(int Level, string Value) : MarkdownBlock;
    /// <summary>A Markdown table: the header (the row above the |---| line, unless empty) and the rows without empty ones.
    /// Raw holds the table's lines, so a growing answer only redraws a table that changed.</summary>
    public sealed record Table(IReadOnlyList<string>? Header, IReadOnlyList<IReadOnlyList<string>> Rows, string Raw) : MarkdownBlock;
}

/// <summary>A piece of a line with one style.</summary>
public sealed record InlineRun(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, string? Link = null);

/// <summary>Markdown with code blocks: fenced blocks become a code block, the rest inline markdown.</summary>
public static class MarkdownLite
{
    private static readonly Regex Bullet = new(@"^(\s*)[-*] ", RegexOptions.Compiled);

    public static List<MarkdownBlock> Blocks(string s)
    {
        var o = new List<MarkdownBlock>();
        var para = new List<string>();
        List<string>? code = null;
        var lang = "";
        var rows = new List<string>();
        void FlushTable()
        {
            if (rows.Count > 0) { o.Add(Table(rows)); rows = []; }
        }
        void Flush()
        {
            if (para.Count > 0) { o.Add(new MarkdownBlock.Text(string.Join("\n", para))); para = []; }
            FlushTable();
        }
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var trimmed = line.Trim();
            // a run of "| … |" lines is a table, outside code
            if (code == null && TableCells(line) != null)
            {
                if (para.Count > 0) { o.Add(new MarkdownBlock.Text(string.Join("\n", para))); para = []; }
                rows.Add(line);
                continue;
            }
            FlushTable();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (code != null)
                {
                    o.Add(new MarkdownBlock.Code(lang, string.Join("\n", code)));
                    code = null;
                }
                else
                {
                    Flush();
                    code = [];
                    lang = trimmed[3..].Trim();
                }
            }
            else if (code != null)
            {
                code.Add(line);
            }
            else if (line.StartsWith('#') && line.IndexOf(' ') is var sp && sp > 0 && line[..sp].All(c => c == '#'))
            {
                Flush();
                o.Add(new MarkdownBlock.Heading(sp, line[(sp + 1)..]));
            }
            else if (trimmed.Length == 0)
            {
                Flush();
            }
            else
            {
                // markdown bullets read better as real bullets
                para.Add(Bullet.Replace(line, "$1• ", 1));
            }
        }
        if (code != null) o.Add(new MarkdownBlock.Code(lang, string.Join("\n", code)));
        Flush();
        return o;
    }

    /// <summary>A Markdown table row: "| a | b |" → ["a", "b"]; null for any other line.</summary>
    public static List<string>? TableCells(string line)
    {
        var s = line.Trim();
        if (s.Length < 2 || s[0] != '|' || s[^1] != '|') return null;
        return [.. s[1..^1].Split('|').Select(c => c.Trim())];
    }

    /// <summary>The line under the header: |---|:--:|</summary>
    public static bool IsSeparator(IReadOnlyList<string> cells) =>
        cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' ')) && cells.Any(c => c.Contains('-'));

    /// <summary>The table of these lines: the row above the separator is the header, unless it is empty; empty rows go.</summary>
    public static MarkdownBlock.Table Table(IReadOnlyList<string> lines)
    {
        var all = lines.Select(l => TableCells(l) ?? []).ToList();
        List<string>? header = null;
        int sep = all.FindIndex(IsSeparator);
        if (sep == 1) { header = all[0]; all.RemoveRange(0, 2); }
        else if (sep >= 0) all.RemoveAt(sep);
        if (header != null && header.TrueForAll(c => c.Length == 0)) header = null;
        return new MarkdownBlock.Table(header, [.. all.Where(r => !r.TrueForAll(c => c.Length == 0))], string.Join("\n", lines));
    }

    /// <summary>Bold, italic, inline code, strikethrough and links in one paragraph; whitespace stays as it is.</summary>
    public static List<InlineRun> Inline(string s)
    {
        var o = new List<InlineRun>();
        Parse(s, new InlineRun(""), o);
        return o;
    }

    private static void Parse(string s, InlineRun style, List<InlineRun> o)
    {
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0) { o.Add(style with { Text = text.ToString() }); text.Clear(); }
        }
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && char.IsPunctuation(s[i + 1]) | char.IsSymbol(s[i + 1]))
            {
                text.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                int ticks = 0;
                while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                int close = s.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    Flush();
                    o.Add(style with { Text = s[(i + ticks)..close], Code = true });
                    i = close + ticks;
                    continue;
                }
            }
            if (c == '[' && LinkAt(s, i) is var (label, url, end))
            {
                Flush();
                Parse(label, style with { Link = url }, o);
                i = end;
                continue;
            }
            if (Delimited(s, i, "**", out var inner, out var after) || Delimited(s, i, "__", out inner, out after))
            {
                Flush();
                Parse(inner, style with { Bold = true }, o);
                i = after;
                continue;
            }
            if (Delimited(s, i, "~~", out inner, out after))
            {
                Flush();
                Parse(inner, style with { Strike = true }, o);
                i = after;
                continue;
            }
            if (Delimited(s, i, "*", out inner, out after) || Delimited(s, i, "_", out inner, out after))
            {
                Flush();
                Parse(inner, style with { Italic = true }, o);
                i = after;
                continue;
            }
            text.Append(c);
            i++;
        }
        Flush();
    }

    private static (string Label, string Url, int End)? LinkAt(string s, int i)
    {
        int depth = 0;
        for (int j = i; j < s.Length; j++)
        {
            if (s[j] == '[') depth++;
            else if (s[j] == ']' && --depth == 0)
            {
                if (j + 1 >= s.Length || s[j + 1] != '(') return null;
                int close = s.IndexOf(')', j + 2);
                return close < 0 ? null : (s[(i + 1)..j], s[(j + 2)..close], close + 1);
            }
        }
        return null;
    }

    /// <summary>An opening mark must touch its text, and the closing one too; _ inside a word (file_name_x) marks nothing.</summary>
    private static bool Delimited(string s, int i, string mark, out string inner, out int after)
    {
        inner = "";
        after = i;
        if (string.CompareOrdinal(s, i, mark, 0, mark.Length) != 0) return false;
        int start = i + mark.Length;
        if (start >= s.Length || char.IsWhiteSpace(s[start])) return false;
        if (mark[0] == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;
        for (int e = s.IndexOf(mark, start, StringComparison.Ordinal); e >= 0; e = s.IndexOf(mark, e + 1, StringComparison.Ordinal))
        {
            if (e == start || char.IsWhiteSpace(s[e - 1])) continue;
            if (mark.Length == 1 && e + 1 < s.Length && s[e + 1] == mark[0]) { e++; continue; }
            // in *** the last two close the bold, the first closes the italic inside it
            while (mark.Length == 2 && e + 2 < s.Length && s[e + 2] == mark[0]) e++;
            if (mark[0] == '_' && e + mark.Length < s.Length && char.IsLetterOrDigit(s[e + mark.Length])) continue;
            inner = s[start..e];
            after = e + mark.Length;
            return true;
        }
        return false;
    }
}
