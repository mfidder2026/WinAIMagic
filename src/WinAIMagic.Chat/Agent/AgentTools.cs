using System.Text.Json.Nodes;

namespace WinAIMagic.Chat.Agent;

/// <summary>What a tool call of the agent sets needs before it runs.</summary>
public abstract record AgentStep
{
    /// <summary>Reading: it runs.</summary>
    public sealed record Run : AgentStep;
    /// <summary>A change: a card first. EveryTime: also after "Altijd in dit gesprek" (sending a mail, a click that sends).</summary>
    public sealed record Ask(string Title, string Detail, IReadOnlyList<string>? Warnings = null, bool EveryTime = false) : AgentStep;
    /// <summary>Never, with the reason for the model.</summary>
    public sealed record Refuse(string Reason) : AgentStep;
}

/// <summary>A set of tools of their own (mail, the browser, controlling other programs), switched on in Instellingen → Agent.
/// The chat client asks each call what it needs (Check), shows a card when it asks, and then runs it.</summary>
public interface IAgentToolSet
{
    IReadOnlySet<string> Names { get; }
    /// <summary>Switched on in the settings (and usable on this PC).</summary>
    bool IsOn { get; }
    JsonArray Definitions { get; }
    string PromptSection { get; }
    /// <summary>Which icon the approval card shows: "mail", "browser" or "computer".</summary>
    string Icon { get; }
    string Label(ToolCall call);
    Task<AgentStep> Check(ToolCall call);
    Task<string> Run(ToolCall call, CancellationToken ct);
}

public static class AgentTools
{
    /// <summary>The sets the chat offers when they are on. Tests put their own here.</summary>
    public static List<IAgentToolSet> All { get; set; } = [MailTools.Shared, BrowserTools.Shared, ComputerTools.Shared];

    public static IAgentToolSet? For(string name) => All.Find(s => s.IsOn && s.Names.Contains(name));

    /// <summary>The set a tool belongs to, on or off (for the card's icon).</summary>
    public static IAgentToolSet? Owner(string name) => All.Find(s => s.Names.Contains(name));

    public static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static JsonObject Function(string name, string description, JsonObject? properties = null, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? [],
                ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            },
        },
    };

    public static string Str(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a[key] is JsonValue n ? n.ToJsonString() : "";

    /// <summary>A number, also when the model wrote it as text ("3").</summary>
    public static int Int(JsonObject a, string key, int fallback = 0)
    {
        if (a[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out var i)) return i;
        return fallback;
    }

    public static bool Bool(JsonObject a, string key) =>
        a[key] is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Words on a button that send, buy, post or delete something: those ask every time.</summary>
    public const string Risky = @"(?i)\b(koop|kopen|buy|purchase|betaal|betalen|pay|bestel|bestellen|order|checkout|afrekenen|verstuur|versturen|verzend|send|submit|plaats|plaatsen|post|publish|publiceer|delete|verwijder|remove|bevestig|confirm|aanmelden|sign up|subscribe|abonneer|donate|doneer|transfer|overmaken)\b";
}
