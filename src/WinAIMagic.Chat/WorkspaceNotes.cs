using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>WINAIMAGIC.md in the work folder: the user's notes for the model (like CLAUDE.md for Claude Code) and what the model
/// learned in earlier conversations there, so it does not make the same mistake twice. Read again for every message.</summary>
public static class WorkspaceNotes
{
    /// <summary>The line that starts what was learned: the user's own text stays above it, whatever its language.</summary>
    private const string Marker = "<!-- winaimagic:lessons -->";
    private const string MacMarker = "<!-- macaimagic:lessons -->";
    public const string FileName = "WINAIMAGIC.md";
    private const string MacFileName = "MACAIMAGIC.md";
    /// <summary>The newest lessons stay; the oldest go, so the file keeps fitting in the system prompt.</summary>
    public const int MaxLessons = 60;
    private const int MaxPromptCharacters = 8_000;

    /// <summary>A folder that came from a Mac has MACAIMAGIC.md; that file stays the notes while it is the only one.</summary>
    public static string PathIn(string folder)
    {
        var current = Path.Combine(folder, FileName);
        var mac = Path.Combine(folder, MacFileName);
        return !File.Exists(current) && File.Exists(mac) ? mac : current;
    }

    /// <summary>Is this file the notes of a folder, not a draft next to them?</summary>
    public static bool IsNotes(string file) =>
        Path.GetFileName(file) is { } name && (name.Equals(FileName, StringComparison.OrdinalIgnoreCase) || name.Equals(MacFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The notes for the system prompt: when they are long, the start (the user's instructions) and the end (the newest lessons).</summary>
    public static string? ForPrompt(string folder)
    {
        var text = AtomicFile.ReadUtf8(PathIn(folder));
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length <= MaxPromptCharacters) return text;
        return TextLimit.Prefix(text, MaxPromptCharacters / 2) + "\n…\n" + TextLimit.Suffix(text, MaxPromptCharacters / 2);
    }

    public static DateTime? Modified(string folder)
    {
        var file = PathIn(folder);
        return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
    }

    /// <summary>A new work folder gets the file, with an explanation for the user.</summary>
    public static void CreateIfNeeded(string folder)
    {
        var file = PathIn(folder);
        if (File.Exists(file)) return;
        var text = "# " + ChatTexts.NotesTitle + "\n\n"
            + L("Het model leest dit bestand bij elk bericht in deze werkmap. Schrijf hier zelf instructies voor het model, bijvoorbeeld hoe het een emulator start. Onder Geleerd zet de app wat het model tijdens gesprekken leert, zodat het fouten niet twee keer maakt. Je mag daar alles aanpassen of weghalen.")
            + "\n\n" + Section;
        try { AtomicFile.Write(file, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Section => "## " + L("Geleerd") + "\n" + Marker + "\n";

    /// <summary>Adds what is new under Geleerd and gives back what was added.</summary>
    public static List<string> Add(IReadOnlyList<string> lessons, string folder)
    {
        CreateIfNeeded(folder);
        var file = PathIn(folder);
        var text = AtomicFile.ReadUtf8(file);
        if (text == null) return [];
        // a file from a Mac keeps its own marker
        var marker = text.Contains(Marker, StringComparison.Ordinal) ? Marker : text.Contains(MacMarker, StringComparison.Ordinal) ? MacMarker : null;
        if (marker == null)
        {
            // the user's own file: the section goes below their text
            text += (text.EndsWith('\n') ? "\n" : "\n\n") + Section;
            marker = Marker;
        }
        int end = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var head = text[..end];
        var known = text[end..].Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var seen = known.Select(l => l.ToLowerInvariant()).ToHashSet();
        var added = lessons.Where(l => !seen.Contains(l.ToLowerInvariant())).ToList();
        if (added.Count == 0) return [];
        known.AddRange(added);
        // the user's own lines below the marker count too: only lessons are dropped, oldest first
        while (known.Count(l => l.StartsWith("- ", StringComparison.Ordinal)) > MaxLessons)
            known.RemoveAt(known.FindIndex(l => l.StartsWith("- ", StringComparison.Ordinal)));
        try { AtomicFile.Write(file, head + "\n" + string.Join("\n", known) + "\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return added;
    }

    /// <summary>The lessons in a model's answer: at most five lines of the form "- …", not too long, and nothing else.</summary>
    public static List<string> Lessons(string answer)
    {
        var seen = new HashSet<string>();
        return answer.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Length > 8 && l.Length <= 300 && seen.Add(l.ToLowerInvariant()))
            .Take(5).ToList();
    }
}
