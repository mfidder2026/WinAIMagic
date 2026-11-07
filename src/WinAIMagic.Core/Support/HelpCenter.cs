namespace WinAIMagic.Core.Support;

/// <summary>One page of the help: help/&lt;language&gt;/&lt;NN-id&gt;.md, with front matter (title, icon, keywords).</summary>
public sealed record HelpTopic(string Id, string Title, string Icon, string Keywords, string Body);

/// <summary>The help in the app: Dutch, or English for every other language. Opened from the sidebar, with the ? at the top right
/// (the help for the page on screen), and with F1.</summary>
public static class HelpCenter
{
    /// <summary>The help's language: Dutch for Dutch, English for the others.</summary>
    public static string Language => AppLanguages.Current == AppLanguage.Nl ? "nl" : "en";

    public static IReadOnlyList<HelpTopic> Topics(string? language = null)
    {
        language ??= Language;
        var list = new List<HelpTopic>();
        foreach (var (path, text) in BundledTexts.Under("help/" + language))
        {
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.Contains('/')) continue;
            // 03-workspace.md → workspace
            var name = Path.GetFileNameWithoutExtension(path);
            int dash = name.IndexOf('-');
            if (Parse(text, dash >= 0 ? name[(dash + 1)..] : name) is { } t) list.Add(t);
        }
        return list;
    }

    public static HelpTopic? Parse(string text, string id)
    {
        var (fields, body) = FrontMatter.Parse(text);
        if (body.Length == 0) return null;
        return new HelpTopic(id, fields.GetValueOrDefault("title") ?? fields.GetValueOrDefault("name") ?? id,
            fields.GetValueOrDefault("icon") ?? "Help", fields.GetValueOrDefault("keywords") ?? "", body);
    }

    /// <summary>Topics whose title, keywords or text contain every word of the query, best first: a word in the title counts most,
    /// then in the keywords, then how often it is in the text.</summary>
    public static List<HelpTopic> Search(string query, IReadOnlyList<HelpTopic> topics)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [.. topics];
        int? Score(HelpTopic t)
        {
            string title = t.Title.ToLowerInvariant(), keys = t.Keywords.ToLowerInvariant(), body = t.Body.ToLowerInvariant();
            int total = 0;
            foreach (var w in words)
            {
                int inBody = Count(body, w);
                bool inTitle = title.Contains(w), inKeys = keys.Contains(w);
                if (!inTitle && !inKeys && inBody == 0) return null;
                total += (inTitle ? 20 : 0) + (inKeys ? 8 : 0) + Math.Min(inBody, 10);
            }
            return total;
        }
        return topics.Select((t, i) => (t, i, s: Score(t))).Where(x => x.s != null)
            .OrderByDescending(x => x.s).ThenBy(x => x.i).Select(x => x.t).ToList();
    }

    private static int Count(string text, string word)
    {
        int n = 0;
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    public enum BlockKind { Heading, Text, Tip }

    public sealed record Block(BlockKind Kind, string Text);

    /// <summary>Help markdown: ## headings, &gt; tips, tables as lines ("a — b"), lists with •, and the rest as chat markdown.</summary>
    public static List<Block> Blocks(string text)
    {
        var output = new List<Block>();
        var buffer = new List<string>();
        bool inCode = false;
        void Flush()
        {
            var t = string.Join("\n", buffer).Trim();
            if (t.Length > 0) output.Add(new Block(BlockKind.Text, t));
            buffer.Clear();
        }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { inCode = !inCode; buffer.Add(line); continue; }
            if (!inCode && (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal)))
            {
                Flush();
                output.Add(new Block(BlockKind.Heading, line.TrimStart('#').Trim()));
            }
            else if (!inCode && line.StartsWith("> ", StringComparison.Ordinal))
            {
                Flush();
                output.Add(new Block(BlockKind.Tip, line[2..]));
            }
            else if (!inCode && line.StartsWith('|'))
            {
                // a table becomes one line per row; the line under the header goes
                var cells = line.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                if (!cells.TrueForAll(c => c.All(ch => "-: ".Contains(ch)))) buffer.Add("• " + string.Join(" — ", cells));
            }
            else if (!inCode && line.StartsWith("- ", StringComparison.Ordinal)) buffer.Add("• " + line[2..]);
            else buffer.Add(line);
        }
        Flush();
        return output;
    }

    /// <summary>The help topic for a page of the app (its name in PaneKind).</summary>
    public static string TopicFor(string? page) => page switch
    {
        "Live" => "live",
        "Documents" => "documents",
        "Images" => "images",
        "Compare" => "compare",
        "Cluster" => "cluster",
        "Tasks" => "tasks",
        "System" => "system",
        "Help" => "start",
        "Chat" => "chat",
        _ => "models",
    };
}

/// <summary>The fields between --- lines at the top of a skill or help text (keys in lower case, quotes taken off), and the text after them.</summary>
public static class FrontMatter
{
    public static (Dictionary<string, string> Fields, string Body) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var fields = new Dictionary<string, string>();
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            int end = lines.FindIndex(1, l => l.Trim() == "---");
            if (end > 0)
            {
                foreach (var line in lines.GetRange(1, end - 1))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var value = line[(colon + 1)..].Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                    fields[line[..colon].Trim().ToLowerInvariant()] = value;
                }
                lines = lines.GetRange(end + 1, lines.Count - end - 1);
            }
        }
        return (fields, string.Join("\n", lines).Trim());
    }
}
