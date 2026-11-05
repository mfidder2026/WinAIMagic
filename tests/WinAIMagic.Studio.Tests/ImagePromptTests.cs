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
        }, _ => { }, _ => Task.FromResult(new WebTools.Result("text", new List<Source> { new("Rijksmuseum", "https://www.rijksmuseum.nl/") })), _ => Task.FromResult<string?>(null));
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
        Assert.Contains(steps, s => s.StartsWith("zoekt:", StringComparison.Ordinal));
    }

    [Fact]
    public void EnglishIsRecognised()
    {
        Assert.True(ImagePrompt.IsEnglish("A photograph of the Eiffel Tower in Paris on a clear afternoon"));
        Assert.False(ImagePrompt.IsEnglish("De Eiffeltoren, een smeedijzeren monument in Parijs met vier poten"));
        Assert.False(ImagePrompt.IsEnglish("Der Eiffelturm in Paris mit blauem Himmel und einer Wiese"));
    }

    /// <summary>A Dutch answer is asked for once more, in English, without tools.</summary>
    [Fact]
    public async Task ADutchAnswerIsAskedAgainInEnglish()
    {
        var bodies = new List<JsonObject>();
        var r = await ImagePrompt.Improve("maak een foto van de eifeltoren", "m", b =>
        {
            bodies.Add(b);
            return Task.FromResult(bodies.Count == 1 ? Answer("De Eiffeltoren in Parijs met een blauwe lucht") : Answer("A photograph of the Eiffel Tower in Paris"));
        }, _ => { });
        Assert.Equal("A photograph of the Eiffel Tower in Paris", r.Prompt);
        Assert.Equal(2, bodies.Count);
        Assert.Null(bodies[1]["tools"]);
        Assert.Equal("The user's request:\nmaak een foto van de eifeltoren\n\nWrite the English description now.",
                     ((JsonArray)bodies[0]["messages"]!)[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void PagesWithTextAreReadNotStockPhotoSites()
    {
        var hits = new List<Source> { new("a", "https://www.alamy.com/x"), new("b", "https://www.rijksmuseum.nl/gebouw"), new("c", "https://nl.wikipedia.org/wiki/Rijksmuseum") };
        Assert.Equal(new[] { "https://www.rijksmuseum.nl/gebouw", "https://nl.wikipedia.org/wiki/Rijksmuseum" }, ImagePrompt.PagesToRead(hits).Select(s => s.Url));
        Assert.Empty(ImagePrompt.PagesToRead(new[] { hits[0] }));
    }

    /// <summary>The model searched but read nothing: its description came from memory, so the app reads the pages and asks again.</summary>
    [Fact]
    public async Task ASearchWithoutReadingGetsPagesRead()
    {
        var bodies = new List<JsonObject>();
        var fetched = new List<string>();
        var r = await ImagePrompt.Improve("het Rijksmuseum", "m", b =>
        {
            bodies.Add(b);
            return Task.FromResult(bodies.Count switch
            {
                1 => Answer("", Search("x")),
                2 => Answer("A photograph of the Rijksmuseum with a dome"),
                _ => Answer("A photograph of the Rijksmuseum, a red-brick building with two towers"),
            });
        }, _ => { },
        _ => Task.FromResult(new WebTools.Result("hits", new List<Source> { new("Rijksmuseum", "https://www.rijksmuseum.nl/gebouw"), new("Wiki", "https://nl.wikipedia.org/wiki/Rijksmuseum") })),
        url => { fetched.Add(url); return Task.FromResult<string?>("A red-brick building with two towers."); });
        Assert.Equal("A photograph of the Rijksmuseum, a red-brick building with two towers", r.Prompt);
        Assert.Null(bodies[^1]["tools"]);      // the last round may only answer
        Assert.Equal(2, fetched.Count);
        Assert.Contains(r.Sources, s => s.Url.Contains("wikipedia"));
    }
}
