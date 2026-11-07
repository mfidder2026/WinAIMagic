using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>What the app does for the model: its state in plain lines, and the actions of app_action. The app implements it on its
/// own thread; tests give their own.</summary>
public interface IAppControl
{
    string Status();
    /// <summary>The settings set_setting may change, with their values where those are fixed ("Thinking (true/false)").</summary>
    IReadOnlyList<string> SettingNames { get; }
    /// <summary>What the action will do, for the approval card, or why it cannot. AfterAnswer: it would stop the engine this
    /// conversation runs on, so it waits until the answer is done. Ask: it always asks (deleting a model), whatever the rights.</summary>
    AppActionPlan Plan(string action, JsonObject args);
    Task<string> Run(string action, JsonObject args);
}

public sealed record AppActionPlan(string? Title = null, string? Detail = null, string? Error = null, bool AfterAnswer = false, bool Ask = false, bool ReadOnly = false);

/// <summary>The app knows itself: the model reads WinAIMagic's own help (app_help), sees how the app and this PC stand now
/// (app_status), does what the user asks of the app (app_action: models, engine, settings, screens), and makes or changes the
/// user's own skills (create_skill), instead of guessing how the app works or only explaining it.</summary>
public static class AppTools
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string> { "app_help", "app_status", "app_action", "create_skill" };

    /// <summary>The app behind the tools; null in a test without one.</summary>
    public static IAppControl? Control { get; set; }

    /// <summary>The actions of app_action, for its description and its check.</summary>
    public static readonly string[] Actions =
    [
        "open_page", "open_help", "download_model", "pause_download", "check_update", "delete_model",
        "choose_model", "start_engine", "stop_engine", "set_setting", "make_image", "new_conversation",
        "find_helpers", "add_helper", "use_helper", "cluster_role", "allow_lan", "start_helper", "stop_helper",
    ];

    /// <summary>The settings app_action may change, named in its description, so a small model need not look them up first.</summary>
    private static string SettingList => Control?.SettingNames is { Count: > 0 } names ? string.Join(", ", names) : "see app_status";

    public static JsonArray Definitions(bool skills)
    {
        var tools = new JsonArray(
            Function("app_help", "Read WinAIMagic's own help: what the app can do and how. Without arguments it lists the topics; give a topic id, or a few words to search for. Use it whenever the user asks about WinAIMagic itself (its screens, models, settings, skills, system management, the API, problems), instead of guessing.",
                new JsonObject
                {
                    ["topic"] = Prop("string", "A topic id from the list, e.g. skills or models"),
                    ["query"] = Prop("string", "Words to search the help for"),
                }),
            Function("app_status", "How WinAIMagic and this PC stand right now: the app's version, the PC's graphics card, VRAM and RAM, every model in the catalog with its id (downloaded or not, whether it fits in VRAM + RAM or streams from the SSD, which is recommended, downloads running), which models run (heavy, fast, images), the work folder and the settings with their names and values. Read-only. Read it before app_action.",
                new JsonObject()),
            Function("app_action", "Do something in WinAIMagic itself for the user, instead of explaining how: "
                + "open_page (page: chat, live, models, documents, images, compare, cluster, system, help), open_help (topic), "
                + "download_model, pause_download, check_update, delete_model (model: an id from app_status), "
                + "choose_model (role: heavy, fast or images; model: id), start_engine, stop_engine, "
                + $"set_setting (setting and value; settings: {SettingList}), make_image (prompt, in English), new_conversation. "
                + "Cluster (several PCs share one model; only models on the second engine): find_helpers (asks the helpers again and lists them), add_helper (address, host or host:port), use_helper (helper: its id host:port; value: true or false), cluster_role (value: main or helper), allow_lan (value: true or false; needed without a direct cable), start_helper, stop_helper. To build a cluster: on each other PC that PC's own WinAIMagic becomes helper (cluster_role helper; it starts the helper); on the main PC find_helpers, use_helper for each, then start_engine; app_status shows how far the model parts are and what went wrong. "
                + "Only do what the user asked for: never download, delete or switch a model on your own, also not one you recommend. The user approves a change on a card first. An action that stops the engine this conversation runs on (stop_engine, a new heavy model, make_image) happens when your answer is done: finish your answer then, and say what will happen.",
                new JsonObject
                {
                    ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Actions.Select(x => JsonValue.Create(x))]) },
                    ["model"] = Prop("string", "A model id from app_status"),
                    ["role"] = Prop("string", "heavy, fast or images"),
                    ["page"] = Prop("string", "The screen to open"),
                    ["topic"] = Prop("string", "A help topic id"),
                    ["setting"] = Prop("string", "A setting name from app_status"),
                    ["value"] = Prop("string", "The new value of the setting"),
                    ["prompt"] = Prop("string", "What the image should show, in English"),
                    ["address"] = Prop("string", "A helper's address, host or host:port"),
                    ["helper"] = Prop("string", "A helper's id (host:port) from app_status"),
                }, "action"));
        if (skills)
            tools.Add(Function("create_skill", "Make a new skill of the user's own, or replace one of their own skills, so it shows in the skill list and can be loaded with use_skill. A skill is instructions for one subject with commands that work on this PC, in ONE Markdown text that is complete on its own and quick to read: put everything that was learned or gathered in it (the rules, the steps, the commands, the common mistakes, short examples), condensed. A later conversation works in another folder and does not have this one, so never refer to files in the work folder or tell the model to read them; if a small script is needed, put it in the skill as a code block, to be written to the work folder when it is used. The user approves it first. Do not put a skill in a work folder or in WINAIMAGIC.md: this tool is the only place skills go.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Short name, lower case with hyphens, e.g. c64-assembler"),
                    ["description"] = Prop("string", "One line: what the skill is about and when to load it"),
                    ["instructions"] = Prop("string", "The whole skill in Markdown, complete on its own: rules, steps, commands, common mistakes, short examples, small scripts as code blocks. No references to files in the work folder"),
                }, "name", "description", "instructions"));
        return tools;
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]) },
        },
    };

    /// <summary>For the system prompt: what this app is, so the model knows where it runs and asks the help instead of guessing.</summary>
    public static string PromptSection(bool skills) =>
        L("Je draait in WinAIMagic, een app die grote AI-modellen helemaal op deze Windows-pc draait. De app heeft schermen voor chatten, modellen, documenten, afbeeldingen (Beeld), vergelijken, een cluster, het systeem en de hulp. Je kent de app: lees met app_help hoe iets werkt en met app_status hoe de app en deze pc er nu voor staan, en gok niet. Vraagt de gebruiker iets te doen in de app (een model downloaden, kiezen of starten, een instelling wijzigen, een scherm openen, een afbeelding maken), doe het dan zelf met app_action in plaats van uit te leggen hoe het moet. Doe alleen wat de gebruiker vraagt: download, verwijder of wissel een model nooit uit jezelf, ook niet als je het aanraadt.")
        + (skills ? " " + L("Vraagt de gebruiker om een skill te maken of aan te passen, gebruik dan create_skill; een skill hoort niet in de werkmap of in WINAIMAGIC.md. Een skill is één compact, op zichzelf staand md-bestand: zet alles wat je leerde of verzamelde erin, en verwijs niet naar bestanden in de werkmap, want een later gesprek werkt in een andere map.") : "");

    // MARK: - running

    public static string Help(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        var topics = HelpCenter.Topics();
        var id = (WorkspaceTools.Str(a, "topic") ?? "").Trim();
        var query = (WorkspaceTools.Str(a, "query") ?? "").Trim();
        if (topics.Count == 0) return "The help texts are missing in this version of the app.";
        HelpTopic? found = id.Length > 0 ? topics.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || t.Title.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;
        if (found == null && (query.Length > 0 || id.Length > 0)) found = HelpCenter.Search(query.Length > 0 ? query : id, topics).FirstOrDefault();
        if (found != null) return $"# {found.Title} (topic {found.Id})\n\n{found.Body}";
        var list = string.Join("\n", topics.Select(t => $"- {t.Id}: {t.Title}"));
        return (id.Length > 0 || query.Length > 0 ? "Nothing found for that. " : "") + "The help topics of WinAIMagic (read one with app_help topic):\n" + list;
    }

    public static string RunStatus() => Control?.Status() ?? $"WinAIMagic {Paths.AppVersion}. No more status is available here.";

    /// <summary>A skill name as a folder: lower case letters, digits and hyphens.</summary>
    public static string? SkillName(string raw)
    {
        var name = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return name.Length is > 0 and <= 60 ? name : null;
    }

    public static (string Name, string Description, string Text)? Skill(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        if (SkillName(WorkspaceTools.Str(a, "name") ?? "") is not { } name) return null;
        var description = (WorkspaceTools.Str(a, "description") ?? "").Replace('\n', ' ').Trim();
        var body = (WorkspaceTools.Str(a, "instructions") ?? "").Trim();
        if (body.Length == 0) return null;
        var text = new StringBuilder()
            .Append("---\nname: ").Append(name)
            .Append("\ndescription: ").Append(description)
            .Append("\nplatform: ").Append(Skills.ThisPlatform)
            .Append("\n---\n").Append(body).Append('\n').ToString();
        return (name, description, text);
    }

    /// <summary>Whether this replaces a skill: one of the user's own (the file is overwritten) or a built-in one (the user's copy wins).</summary>
    public static string? Replaces(string name)
    {
        if (File.Exists(Path.Combine(Paths.UserSkills, name, "SKILL.md"))) return "own";
        return Skills.Load([Skills.BuiltIn()]).Exists(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "builtin" : null;
    }

    public static string WriteSkill(string name, string text)
    {
        var dir = Path.Combine(Paths.UserSkills, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(file + ".tmp", text, new UTF8Encoding(false));
        File.Move(file + ".tmp", file, true);
        return file;
    }

    public static string Label(ToolCall call)
    {
        var a = WorkspaceTools.Args(call);
        return call.Name switch
        {
            "app_help" => L($"Hulp lezen: {WorkspaceTools.Str(a, "topic") ?? WorkspaceTools.Str(a, "query") ?? ""}").TrimEnd(' ', ':'),
            "app_status" => L("Stand van de app bekijken"),
            "app_action" => L($"In de app: {WorkspaceTools.Str(a, "action") ?? ""}"),
            "create_skill" => L($"Skill maken: {WorkspaceTools.Str(a, "name") ?? ""}"),
            _ => call.Name,
        };
    }
}
