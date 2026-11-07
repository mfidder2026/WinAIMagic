using System.Text;
using System.Text.RegularExpressions;

namespace WinAIMagic.Chat.Telegram;

/// <summary>The model writes Markdown; Telegram takes a little HTML (b, i, code, pre, a) and at most 4096 characters a message.</summary>
public static class TelegramFormat
{
    /// <summary>Room for the HTML the conversion adds (&amp;amp;, tags) under Telegram's 4096.</summary>
    public const int Limit = 3500;

    private static int Count(string s, string part)
    {
        int n = 0;
        for (int i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Splits at a blank line, else at a line end, else at a space; a code block that is split goes on in the next part.</summary>
    public static List<string> Chunks(string text, int limit = Limit)
    {
        var rest = text.Trim();
        var o = new List<string>();
        while (rest.Length > limit)
        {
            var window = rest[..limit];
            int cut = window.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (cut < 0) cut = window.LastIndexOf('\n');
            if (cut < 0) cut = window.LastIndexOf(' ');
            int end = cut > limit / 3 ? cut : limit;
            var part = rest[..end];
            rest = rest[end..].TrimStart('\n', ' ');
            // an open code block goes on in the next part
            if (Count(part, "```") % 2 == 1)
            {
                part += "\n```";
                rest = "```\n" + rest;
            }
            o.Add(part);
        }
        if (rest.Length > 0) o.Add(rest);
        return o.Count == 0 ? [""] : o;
    }

    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Markdown to Telegram's HTML: code blocks, inline code, bold, italic, links, headings as bold, tables as aligned
    /// columns. Everything else is escaped, so nothing the model writes can become a tag.</summary>
    public static string Html(string markdown)
    {
        var o = new StringBuilder();
        var parts = markdown.Split("```");
        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                // a code block: drop the language on the first line
                var code = parts[i];
                int nl = code.IndexOf('\n');
                if (nl >= 0 && !code[..nl].Contains(' ')) code = code[(nl + 1)..];
                o.Append("<pre>").Append(Escape(code.Trim('\n', '\r'))).Append("</pre>");
            }
            else o.Append(Tables(parts[i]));
        }
        return o.ToString();
    }

    /// <summary>Telegram has no tables: a Markdown table becomes aligned columns in a &lt;pre&gt; block.</summary>
    private static string Tables(string text)
    {
        var o = new List<string>();
        var rows = new List<List<string>>();
        void Flush()
        {
            if (rows.Count == 0) return;
            int count = rows.Max(r => r.Count);
            var widths = Enumerable.Range(0, count).Select(i => rows.Max(r => i < r.Count ? r[i].Length : 0)).ToArray();
            var lines = rows.Select(r => string.Join("  ", Enumerable.Range(0, count).Select(i => (i < r.Count ? r[i] : "").PadRight(widths[i]))).Trim());
            o.Add("<pre>" + Escape(string.Join("\n", lines)) + "</pre>");
            rows = [];
        }
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length > 1 && s[0] == '|' && s[^1] == '|')
            {
                var cells = s[1..^1].Split('|').Select(c => c.Trim().Replace("**", "")).ToList();
                // the line under the header: |---|:--:|
                if (cells.TrueForAll(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '))) continue;
                if (cells.TrueForAll(c => c.Length == 0)) continue;
                rows.Add(cells);
            }
            else
            {
                Flush();
                o.Add(Inline(line));
            }
        }
        Flush();
        return string.Join("\n", o);
    }

    private static string Inline(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('#')) return "<b>" + Spans(trimmed.TrimStart('#').Trim()) + "</b>";
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal)) return "• " + Spans(trimmed[2..]);
        return Spans(line);
    }

    /// <summary>Inline code first (its content stays as it is), then links, bold and italic on the escaped rest.</summary>
    private static string Spans(string text)
    {
        var pieces = text.Split('`');
        if (pieces.Length % 2 == 0) return Style(Escape(text));
        return string.Concat(pieces.Select((p, i) => i % 2 == 1 ? "<code>" + Escape(p) + "</code>" : Style(Escape(p))));
    }

    private static readonly Regex Link = new(@"\[([^\]]+)\]\((https?://[^\s)""]+)\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<![\w*])\*(?!\s)([^*\n]+?)\*(?![\w*])", RegexOptions.Compiled);

    private static string Style(string s)
    {
        s = Link.Replace(s, "<a href=\"$2\">$1</a>");
        s = Bold.Replace(s, "<b>$1</b>");
        return Italic.Replace(s, "<i>$1</i>");
    }
}
