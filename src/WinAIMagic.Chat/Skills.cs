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

    public static string Label(ToolCall call) => L($"Skill laden: {WorkspaceTools.Str(WorkspaceTools.Args(call), "name") ?? ""}");
}
