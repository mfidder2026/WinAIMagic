using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Studio;

/// <summary>Turns the user's description into one the image model follows well: in English, concrete and visual. The chat model writes
/// it, and may first look up on the web what something looks like (a building, a product, a style). What goes to the search engine is
/// cleaned as in the documents module. Taken over from the Mac (c954667), with its instruction word for word.</summary>
public static class ImagePrompt
{
    public const int MaxRounds = 4;

    public const string Instructions =
        "You write descriptions for an image model that draws what it reads. Rewrite the user's description as one English " +
        "paragraph of at most 80 words: the subject first, then what it looks like, the setting, the light and the style " +
        "(photo, painting, drawing). Keep everything the user asked for and add nothing they did not want. When the description " +
        "names something real whose look matters (a building, a place, a product, a person's public appearance, an art style) " +
        "and you are not sure what it looks like, use web_search and then fetch_url on the best result, at most a few times. " +
        "Describe it as the pages describe it: its material, colours, shape, towers or roofs. What you remember yourself may be " +
        "wrong; the pages go first, and leave out what they do not say. Answer with the description only: no title, no quotes, " +
        "no explanation.";

    public sealed record Result(string Prompt, IReadOnlyList<Source> Sources);

    /// <summary>`send` posts one request and gives the decoded answer; `run` runs a web tool (tests stand in for both); `onStep` names
    /// what happens.</summary>
    public static async Task<Result> Improve(string text, string model, Func<JsonObject, Task<JsonObject>> send, Action<string> onStep,
                                             Func<ToolCall, Task<WebTools.Result>>? run = null)
    {
        run ??= call => WebTools.RunAsync(call);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = Instructions },
            new JsonObject { ["role"] = "user", ["content"] = text },
        };
        var sources = new List<Source>();
        for (int round = 0; round <= MaxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"] = model, ["messages"] = messages.DeepClone(), ["stream"] = false, ["temperature"] = 0.3, ["max_tokens"] = 400,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }, ["thinking_budget_tokens"] = 0,
            };
            // the last round may only answer
            if (round < MaxRounds) body["tools"] = WebTools.Definitions;
            else body["tool_choice"] = "none";
            var answer = await send(body);
            if (answer["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["message"] is not JsonObject message) break;
            var calls = message["tool_calls"] as JsonArray;
            if (calls == null || calls.Count == 0)
            {
                var prompt = Cleaned(message["content"]?.GetValue<string>() ?? "");
                return new Result(prompt.Length == 0 ? text : prompt, sources);
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.GetValue<string>() ?? "", ["tool_calls"] = calls.DeepClone() });
            foreach (var node in calls)
            {
                var function = node?["function"];
                string id = node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                string name = function?["name"]?.GetValue<string>() ?? "";
                string arguments = function?["arguments"]?.GetValue<string>() ?? "{}";
                if (name == "web_search")
                {
                    try
                    {
                        if (JsonNode.Parse(arguments) is JsonObject args && args["query"]?.GetValue<string>() is { } query)
                        {
                            args["query"] = SearchQuery.Clean(query);
                            arguments = args.ToJsonString();
                        }
                    }
                    catch (JsonException) { }
                }
                var call = new ToolCall(id, name, arguments);
                onStep(Label(call));
                var output = await run(call);
                foreach (var s in output.Sources) if (!sources.Any(x => x.Url == s.Url)) sources.Add(s);
                // the fast model has a context of 16K: a page is cut to what fits a few times
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output.Text.Length > 3000 ? output.Text[..3000] : output.Text });
            }
        }
        return new Result(text, sources);
    }

    /// <summary>What the model does, after its name: "zoekt: Rijksmuseum", "leest: rijksmuseum.nl".</summary>
    public static string Label(ToolCall call)
    {
        var args = WorkspaceTools.Args(call);
        return call.Name switch
        {
            "web_search" => L($"zoekt: {WorkspaceTools.Str(args, "query") ?? ""}"),
            "fetch_url" => L($"leest: {(Uri.TryCreate(WorkspaceTools.Str(args, "url") ?? "", UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "…")}"),
            _ => call.Name,
        };
    }

    /// <summary>The description alone: no quotes, no label in front, no thinking left behind.</summary>
    public static string Cleaned(string answer)
    {
        var s = answer;
        int end = s.IndexOf("</think>", StringComparison.Ordinal);
        if (end >= 0) s = s[(end + "</think>".Length)..];
        s = s.Trim();
        foreach (var label in new[] { "Description:", "Prompt:", "Image description:" })
            if (s.StartsWith(label, StringComparison.OrdinalIgnoreCase)) s = s[label.Length..].Trim();
        const string quotes = "\"'“”";
        if (s.Length > 1 && quotes.Contains(s[0]) && quotes.Contains(s[^1])) s = s[1..^1];
        return s.Trim();
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

    /// <summary>One request to the chat engine at `baseUrl`, with the app's key when it has one.</summary>
    public static Func<JsonObject, Task<JsonObject>> Sender(Uri baseUrl) => async body =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.ToString().TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var key = Settings.Shared.ApiKey;
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(req);
        var data = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(L($"Engine gaf {(int)response.StatusCode}: {(data.Length > 300 ? data[..300] : data)}"));
        return JsonNode.Parse(data) as JsonObject ?? new JsonObject();
    };
}
