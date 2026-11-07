using System.Text.Json.Nodes;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>One step of the model's list: what, and how far (todo, doing, done).</summary>
public sealed record WorkStep(string Title, string Status)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDone => Status == "done";
}

/// <summary>task_list: the model sets up a larger piece of work in steps and ticks them off, so the user sees where it is.</summary>
public static class WorkSteps
{
    public const string ToolName = "task_list";
    private static readonly string[] Statuses = ["todo", "doing", "done"];

    public static JsonObject Definition => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = ToolName,
            ["description"] = "Show the user your plan for a task of three steps or more, and keep it up to date: call it first with all steps, and again whenever a step starts or is done (always the whole list). Not for a simple question.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["steps"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "The step, a few words" },
                                ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("todo", "doing", "done") },
                            },
                            ["required"] = new JsonArray("title", "status"),
                        },
                    },
                },
                ["required"] = new JsonArray("steps"),
            },
        },
    };

    /// <summary>The steps from the model's call; a status it made up counts as todo, and at most 30 steps.</summary>
    public static List<WorkStep>? Parse(JsonObject arguments)
    {
        if (arguments["steps"] is not JsonArray list) return null;
        var steps = new List<WorkStep>();
        foreach (var item in list)
        {
            if (item is not JsonObject o) continue;
            var title = (WorkspaceTools.Str(o, "title") ?? "").Trim();
            if (title.Length == 0) continue;
            var status = (WorkspaceTools.Str(o, "status") ?? "").ToLowerInvariant();
            steps.Add(new WorkStep(TextLimit.Prefix(title, 120), Statuses.Contains(status) ? status : "todo"));
        }
        return steps.Count == 0 ? null : steps.Take(30).ToList();
    }

    /// <summary>What the model hears back.</summary>
    public static string Summary(List<WorkStep> steps)
    {
        int done = steps.Count(s => s.IsDone);
        var doing = steps.Where(s => s.Status == "doing").Select(s => s.Title).ToList();
        return $"Task list updated: {done} of {steps.Count} done" + (doing.Count == 0 ? "." : "; now: " + string.Join(", ", doing) + ".");
    }

    public static string Label(List<WorkStep>? steps)
    {
        if (steps == null) return L("Stappenlijst");
        int done = steps.Count(s => s.IsDone), all = steps.Count;
        return L($"Stappenlijst: {done} van {all} klaar");
    }
}
