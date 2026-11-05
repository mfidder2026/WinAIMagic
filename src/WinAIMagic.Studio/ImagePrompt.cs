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

    /// <summary>Measured on the Mac with the Eiffel Tower: a long Dutch description full of facts (324 metres, rivets, "a roof") drew a tower
    /// that does not exist; "A photograph of the Eiffel Tower in Paris" drew the real one. The image model knows famous things by their
    /// English name, reads English best, and draws every word it gets. Word for word as on the Mac (008ab2f).</summary>
    public const string Instructions =
        "You write the description an image model draws from. The image model reads English only and draws every word, so: " +
        "write in English, whatever language the user wrote in. Start with what kind of picture it is and the subject by its " +
        "common English name (\"A photograph of the Eiffel Tower in Paris\"). Then add only what can be seen: the view, the " +
        "setting, the light, the colours, the style. No measurements, dates, history, materials or how it was built, and no " +
        "details you are not sure of: a wrong detail ends up in the picture. Keep everything the user asked for. At most 60 " +
        "words, one paragraph. Never describe the shape or parts of a real, named thing from memory: such details are often " +
        "wrong, and a wrong detail ends up in the picture. When the request names a real place, building, landmark, product or " +
        "artwork, first use web_search and read one good page with fetch_url, then describe in a few words what the pages say " +
        "it looks like (its shape, colours and surroundings). If they say nothing about its look, give only its name and the " +
        "scene. Answer with the description only: no title, no quotes, no explanation.";

    private const string FromPages =
        "Here are pages about it. Rewrite the description so that what it says about the look of the subject (its shape, colours, " +
        "towers, roofs, material as it looks) comes from these pages, not from memory. Leave out what they do not say. Keep the " +
        "subject's name in the first words. English, at most 60 words, the description only.\n\n";

    public sealed record Result(string Prompt, IReadOnlyList<Source> Sources);

    /// <summary>Pages of photos without text: they say nothing about what a thing looks like.</summary>
    public static readonly string[] PicturesOnly = ["alamy.", "dreamstime.", "shutterstock.", "gettyimages.", "istockphoto.", "pinterest.",
                                                    "depositphotos.", "123rf.", "adobe.com", "unsplash.", "pexels.", "flickr."];

    /// <summary>The search hits to read when the model searched but read nothing: the first page with text (often the place's own, which says
    /// most about its look) and an encyclopedia.</summary>
    public static IReadOnlyList<Source> PagesToRead(IReadOnlyList<Source> hits)
    {
        var readable = hits.Where(h => !PicturesOnly.Any(p => h.Url.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        var own = readable.FirstOrDefault(h => !h.Url.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase));
        var encyclopedia = readable.FirstOrDefault(h => h.Url.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase));
        return new[] { own, encyclopedia }.OfType<Source>().ToList();
    }

    /// <summary>More English little words than Dutch, German or French ones.</summary>
    public static bool IsEnglish(string text)
    {
        var words = new string(text.ToLowerInvariant().Select(c => char.IsLetter(c) || c == '\'' ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] english = ["the", "a", "an", "of", "with", "and", "in", "on", "at", "its", "is", "from", "by"];
        string[] other = ["de", "het", "een", "van", "met", "en", "op", "bij", "die", "dat", "zijn", "der", "das", "und", "mit",
                          "ein", "eine", "auf", "le", "la", "les", "une", "des", "avec", "et", "du", "sur"];
        return words.Count(english.Contains) >= words.Count(other.Contains);
    }

    private static string? Content(JsonObject answer) =>
        answer["choices"] is JsonArray { Count: > 0 } choices ? choices[0]?["message"]?["content"]?.GetValue<string>() : null;

    /// <summary>`send` posts one request and gives the decoded answer; `run` runs a web tool; `fetch` reads a page (tests stand in for all
    /// three); `onStep` names what happens.</summary>
    public static async Task<Result> Improve(string text, string model, Func<JsonObject, Task<JsonObject>> send, Action<string> onStep,
                                             Func<ToolCall, Task<WebTools.Result>>? run = null, Func<string, Task<string?>>? fetch = null)
    {
        run ??= call => WebTools.RunAsync(call);
        fetch ??= async url =>
        {
            try { return (await WebTools.FetchAsync(url)).Body; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException) { return null; }
        };
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = Instructions },
            new JsonObject { ["role"] = "user", ["content"] = $"The user's request:\n{text}\n\nWrite the English description now." },
        };
        var sources = new List<Source>();
        var searchHits = new List<Source>();
        bool readPage = false, forcedRead = false;
        for (int round = 0; round <= MaxRounds + 1; round++)
        {
            var body = new JsonObject
            {
                ["model"] = model, ["messages"] = messages.DeepClone(), ["stream"] = false, ["temperature"] = 0.3, ["max_tokens"] = 400,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }, ["thinking_budget_tokens"] = 0,
            };
            // the last round, and the one after the app read the pages, may only answer
            if (round < MaxRounds && !forcedRead) body["tools"] = WebTools.Definitions;
            else body["tool_choice"] = "none";
            var answer = await send(body);
            if (answer["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["message"] is not JsonObject message) break;
            var calls = message["tool_calls"] as JsonArray;
            if (calls == null || calls.Count == 0)
            {
                var prompt = Cleaned(message["content"]?.GetValue<string>() ?? "");
                // it searched but read no page, so its description came from memory: the app reads the best hits and asks again
                if (!readPage && !forcedRead)
                {
                    var pages = new List<string>();
                    foreach (var page in PagesToRead(searchHits))
                    {
                        onStep(Label(new ToolCall("", "fetch_url", new JsonObject { ["url"] = page.Url }.ToJsonString())));
                        var read = await fetch(page.Url);
                        if (string.IsNullOrEmpty(read)) continue;
                        pages.Add("<<<\n" + (read.Length > 2500 ? read[..2500] : read) + "\n>>>");
                        if (!sources.Any(s => s.Url == page.Url)) sources.Add(page);
                    }
                    if (pages.Count > 0)
                    {
                        forcedRead = readPage = true;
                        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = prompt });
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = FromPages + string.Join("\n\n", pages) });
                        continue;
                    }
                }
                // a small model answers in the user's language now and then: once more, in English
                if (prompt.Length > 0 && !IsEnglish(prompt))
                {
                    onStep(L("schrijft het opnieuw, in het Engels"));
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = prompt });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = "That is not English. Write the same description in English, at most 60 words." });
                    var again = (JsonObject)body.DeepClone();
                    again["messages"] = messages.DeepClone();
                    again.Remove("tools");
                    again["tool_choice"] = "none";
                    var second = Cleaned(Content(await send(again)) ?? "");
                    if (second.Length > 0) prompt = second;
                }
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
                if (name == "web_search") searchHits = output.Sources.ToList();
                else if (name == "fetch_url") readPage = true;
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
