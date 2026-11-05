using System.Text.Json.Nodes;
using WinAIMagic.Chat;
using Xunit;

namespace WinAIMagic.Studio.Tests;

/// <summary>The chat model rewriting the description for the image model, with stand-ins for the engine and the web. Names as on the Mac.</summary>
public class ImagePromptTests
{
    private static JsonObject Answer(string? content, JsonArray? calls = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (calls != null) message["tool_calls"] = calls;
        return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
    }

    private static JsonArray Search(string query) => new(new JsonObject
    {
        ["id"] = "c1", ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = "web_search", ["arguments"] = new JsonObject { ["query"] = query }.ToJsonString() },
    });

    [Fact]
    public void TheAnswerIsTheDescriptionAlone()
    {
        Assert.Equal("a red fox on a bench", ImagePrompt.Cleaned("Prompt: \"a red fox on a bench\""));
        Assert.Equal("a fox", ImagePrompt.Cleaned("<think>hmm</think>\n a fox "));
    }

    [Fact]
    public async Task WithoutSearchingOneRequestIsEnough()
    {
        var bodies = new List<JsonObject>();
        var r = await ImagePrompt.Improve("een rode vos", "m", b => { bodies.Add(b); return Task.FromResult(Answer("a red fox, photo")); }, _ => { });
        Assert.Equal("a red fox, photo", r.Prompt);
        Assert.Single(bodies);
        Assert.NotNull(bodies[0]["tools"]);
        Assert.False(bodies[0]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnEmptyAnswerKeepsTheUsersDescription()
    {
        var r = await ImagePrompt.Improve("een rode vos", "m", _ => Task.FromResult(Answer("")), _ => { });
        Assert.Equal("een rode vos", r.Prompt);
    }

    /// <summary>A model that keeps searching is asked for an answer in the last round, without tools.</summary>
    [Fact]
    public async Task TheLastRoundMayOnlyAnswer()
    {
        var bodies = new List<JsonObject>();
        var r = await ImagePrompt.Improve("het Rijksmuseum", "m", b =>
        {
            bodies.Add(b);
            return Task.FromResult(b["tools"] == null ? Answer("the Rijksmuseum, red brick, towers") : Answer("", Search("Rijksmuseum")));
        }, _ => { }, _ => Task.FromResult(new WebTools.Result("text", new List<Source> { new("Rijksmuseum", "https://www.rijksmuseum.nl/") })));
        Assert.Equal(ImagePrompt.MaxRounds + 1, bodies.Count);
        Assert.Null(bodies[^1]["tools"]);
        Assert.Equal("none", bodies[^1]["tool_choice"]!.GetValue<string>());
        Assert.Equal("the Rijksmuseum, red brick, towers", r.Prompt);
        Assert.Single(r.Sources);     // the same page once
    }

    /// <summary>What goes to the search engine is cleaned as in the documents module; the steps are named.</summary>
    [Fact]
    public async Task TheSearchIsCleanedAndTheStepsAreNamed()
    {
        string? searched = null;
        var steps = new List<string>();
        int round = 0;
        await ImagePrompt.Improve("x", "m", _ => Task.FromResult(round++ == 0 ? Answer("", Search("museum jan@example.com 0612345678")) : Answer("a museum")),
            steps.Add, call =>
            {
                searched = call.Arguments;
                return Task.FromResult(new WebTools.Result("text", new List<Source>()));
            });
        Assert.DoesNotContain("example.com", searched);
        Assert.DoesNotContain("0612345678", searched);
        Assert.Contains(steps, s => s.StartsWith("Zoeken:", StringComparison.Ordinal));
        Assert.Contains("Beschrijving schrijven…", steps);
    }
}
