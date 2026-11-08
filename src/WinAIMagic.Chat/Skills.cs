using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>A skill: instructions for one subject (taskbar and appearance, energy, network…) with commands that were tested on
/// this platform. Only the name and description go in the system prompt; the model loads the rest with use_skill, so a local
/// model does not read every skill on every message.</summary>
public sealed record Skill(string Name, string Description, string Platform, string Body);

public static class Skills
{
    public const string ToolName = "use_skill";
    public const string ThisPlatform = "windows";

    /// <summary>SKILL.md: front matter between --- lines (name, description, platform), then the instructions.</summary>
    public static Skill? Parse(string text, string fallbackName)
    {
        var (fields, body) = FrontMatter.Parse(text);
        var name = (fields.GetValueOrDefault("name") ?? fallbackName).Trim();
        if (name.Length == 0 || body.Length == 0) return null;
        return new Skill(name, fields.GetValueOrDefault("description") ?? "", (fields.GetValueOrDefault("platform") ?? "all").ToLowerInvariant(), body);
    }

    /// <summary>The skills of this platform from these sources, in order; a later one replaces a skill of the same name.</summary>
    public static List<Skill> Load(IEnumerable<IEnumerable<(string Folder, string Text)>> sources, string platform = ThisPlatform)
    {
        var byName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            foreach (var (folder, text) in source)
                if (Parse(text, folder) is { } s && (s.Platform == "all" || s.Platform == platform))
                    byName[s.Name] = s;
        return byName.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every SKILL.md one level down in this folder: (the folder's name, the text).</summary>
    public static IEnumerable<(string Folder, string Text)> FromFolder(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(dir, "SKILL.md");
            string? text = null;
            try { if (File.Exists(file)) text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (text != null) yield return (Path.GetFileName(dir), text);
        }
    }

    /// <summary>The skills that come with the app ("uiterlijk/SKILL.md" → "uiterlijk").</summary>
    public static IEnumerable<(string Folder, string Text)> BuiltIn() =>
        BundledTexts.Under("skills").Select(x => (x.Path.Split('/')[0], x.Text));

    /// <summary>The built-in skills, then the user's own.</summary>
    public static List<Skill> All => Load([BuiltIn(), FromFolder(Paths.UserSkills)]);

    public static JsonObject Definition => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = ToolName,
            ["description"] = "Load the full instructions of a skill from the list in the system prompt. Do this before you work on that subject: the skill has commands that were tested on this computer.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The skill's name, exactly as listed" } },
                ["required"] = new JsonArray("name"),
            },
        },
    };

    /// <summary>For the system prompt: one line per skill. Sorted, so the prompt stays the same and the engine can reuse it.</summary>
    public static string PromptSection(IReadOnlyList<Skill> skills) =>
        skills.Count == 0 ? ""
        : L("Je hebt skills: instructies per onderwerp, met geteste commando's. Laad een skill met use_skill voordat je aan dat onderwerp werkt, en volg hem op:")
          + "\n" + string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));

    public static string Run(ToolCall call, IReadOnlyList<Skill>? skills = null)
    {
        skills ??= All;
        var name = (WorkspaceTools.Str(WorkspaceTools.Args(call), "name") ?? "").Trim();
        if (skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found) return $"# Skill: {found.Name}\n\n{found.Body}";
        return $"There is no skill named \"{name}\". Available: " + string.Join(", ", skills.Select(s => s.Name));
    }

    /// <summary>Words that say nothing about a subject.</summary>
    private static readonly HashSet<string> Common =
    [
        "voor", "met", "een", "het", "van", "naar", "zodra", "worden", "wordt", "moet", "laden", "plus", "geteste", "vaste",
        "code", "harde", "regels", "this", "that", "with", "when", "from", "load", "loaded", "into", "your", "what", "alle",
        "deze", "maken", "schrijven", "programmeren", "gebruik", "door", "over", "wilt", "skill", "instructies", "windows",
    ];

    internal static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>();
        var word = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant() + " ")
        {
            if (char.IsLetterOrDigit(ch)) { word.Append(ch); continue; }
            if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        return words;
    }

    /// <summary>The skills a question is clearly about: a word of the skill's name, and at least three words of its name and
    /// description together in the question. A small model often skips use_skill; then the app loads these itself.</summary>
    public static List<Skill> Matching(string question, IReadOnlyList<Skill>? skills = null)
    {
        var asked = Words(question);
        // "sprite" counts for "sprites": a shared start of at least four letters
        bool Found(string term) => asked.Any(a => a == term
            || (Math.Min(a.Length, term.Length) >= 4 && (a.StartsWith(term, StringComparison.Ordinal) || term.StartsWith(a, StringComparison.Ordinal))));
        var scored = new List<(Skill Skill, int Hits)>();
        foreach (var s in skills ?? All)
        {
            var name = Words(s.Name.Replace('-', ' '));
            name.ExceptWith(Common);
            var terms = new HashSet<string>(name);
            terms.UnionWith(Words(s.Description).Where(w => w.Length >= 4 || w.Any(char.IsDigit)));
            terms.ExceptWith(Common);
            int hits = terms.Count(Found);
            if (name.Any(Found) && hits >= 3) scored.Add((s, hits));
        }
        return scored.OrderByDescending(x => x.Hits).Take(2).Select(x => x.Skill).ToList();
    }

    /// <summary>The step of a skill the app loaded itself.</summary>
    public static string AutoLabel(string name) => L($"Skill geladen: {name} (vanzelf)");

    public static string Label(ToolCall call) => L($"Skill laden: {WorkspaceTools.Str(WorkspaceTools.Args(call), "name") ?? ""}");
}
