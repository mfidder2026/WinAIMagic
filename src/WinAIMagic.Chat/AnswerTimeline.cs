namespace WinAIMagic.Chat;

/// <summary>The chat as rows: a question of the user, or one answer with all its rounds. An answer of a model that works is many
/// messages (a round of thinking, text and tool calls each, and the app's own "go on" and pictures in between); shown as one, in the
/// order it came, the user can read along instead of seeing parts jump away.</summary>
public sealed record ChatItem(ChatMessage? Question, List<ChatMessage> Rounds)
{
    public Guid Id => Question?.Id ?? Rounds[0].Id;

    public bool IsAnswer => Question == null;

    public bool Contains(Guid id) => Question?.Id == id || Rounds.Exists(m => m.Id == id);

    /// <summary>Visible messages in order: each question of the user starts a new row, everything after it is the answer.</summary>
    public static List<ChatItem> Group(IEnumerable<ChatMessage> messages)
    {
        var items = new List<ChatItem>();
        var rounds = new List<ChatMessage>();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User && m.Automatic != true)
            {
                if (rounds.Count > 0) { items.Add(new ChatItem(null, rounds)); rounds = []; }
                items.Add(new ChatItem(m, []));
            }
            else rounds.Add(m);
        }
        if (rounds.Count > 0) items.Add(new ChatItem(null, rounds));
        return items;
    }
}

public enum PartKind { Thought, Step, Text, Looked, Resumed, Error }

/// <summary>One piece of an answer, in the order it came.</summary>
public sealed record AnswerPart(PartKind Kind, string Id, string Text = "", bool Live = false, ToolActivity? Step = null, List<Attachment>? Attachments = null)
{
    /// <summary>The parts of the rounds; the last round's thinking is live while the model has not started its text.</summary>
    public static List<AnswerPart> Of(IReadOnlyList<ChatMessage> rounds, bool streaming)
    {
        var parts = new List<AnswerPart>();
        for (int n = 0; n < rounds.Count; n++)
        {
            var m = rounds[n];
            var key = m.Id.ToString();
            if (m.Role == ChatRole.User)
            {
                parts.Add(m.Attachments.Count == 0 ? new AnswerPart(PartKind.Resumed, key) : new AnswerPart(PartKind.Looked, key, Attachments: m.Attachments));
                continue;
            }
            if (m.Reasoning.Length > 0)
                parts.Add(new AnswerPart(PartKind.Thought, key + "r", m.Reasoning, streaming && n == rounds.Count - 1 && m.Text.Length == 0));
            // the text of a round comes before its tool calls: the model writes it, then asks for the tools
            if (m.Text.Length > 0) parts.Add(new AnswerPart(PartKind.Text, key + "t", m.Text));
            foreach (var a in m.Activities) parts.Add(new AnswerPart(PartKind.Step, a.Id.ToString(), a.Label, Step: a));
            if (m.Error is { } e) parts.Add(new AnswerPart(PartKind.Error, key + "e", e));
        }
        return parts;
    }

    /// <summary>Where the tail of a finished answer starts: its last text and errors stay in view, all before folds away.</summary>
    public static int TailStart(IReadOnlyList<AnswerPart> parts)
    {
        for (int i = parts.Count - 1; i >= 0; i--)
            if (parts[i].Kind is not (PartKind.Text or PartKind.Error)) return i + 1;
        return 0;
    }

    /// <summary>Lines of thinking shown while the model thinks, and how long each may be.</summary>
    public const int GistLines = 4, GistWidth = 120;

    /// <summary>The thinking without opening it. While the model thinks: its last lines, one below the other (the owner saw the
    /// lines run together into one fast-moving block); the line being written keeps its end, an earlier one its start. Afterwards:
    /// its start as one line.</summary>
    public static string Gist(string text, bool live)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (!live)
        {
            var flat = string.Join(" ", lines);
            return flat[..Math.Min(200, flat.Length)];
        }
        var last = lines.Skip(Math.Max(0, lines.Count - GistLines)).ToList();
        for (int i = 0; i < last.Count; i++)
            if (last[i].Length > GistWidth)
                last[i] = i == last.Count - 1 ? "…" + last[i][^(GistWidth - 1)..] : last[i][..(GistWidth - 1)] + "…";
        return string.Join("\n", last);
    }
}
