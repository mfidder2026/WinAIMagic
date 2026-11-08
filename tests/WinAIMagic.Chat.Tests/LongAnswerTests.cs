using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>A model that plans aloud must not fill its context and start over (GLM on the Mac stayed at step 1 of 7 for six hours).</summary>
public class LongAnswerTests : ChatTest
{
    [Fact]
    public void ACutOffAnswerComesBackAsItsStartAndEnd()
    {
        var text = new string('a', 3000) + new string('b', 100_000) + new string('c', 3000);
        var shortText = ChatClient.Shortened(text);
        Assert.True(shortText.Length < 6500);
        Assert.StartsWith(new string('a', 3000), shortText);
        Assert.EndsWith(new string('c', 3000), shortText);
        Assert.DoesNotContain("b", shortText);
    }

    [Fact]
    public void AShortAnswerStaysWhole() => Assert.Equal("kort antwoord", ChatClient.Shortened("kort antwoord"));

    [Fact]
    public void OneAnswerTakesAQuarterOfTheContextAtMost()
    {
        var chat = new ChatClient { ContextLimit = 65_536 };
        Assert.Equal(16_384, chat.MaxAnswerTokens);
        chat.ContextLimit = 8192;
        Assert.Equal(4096, chat.MaxAnswerTokens);
    }

    /// <summary>The engine reads the whole conversation again when the request starts differently. JsonObject keeps the order in which
    /// the keys were added, so this holds here without sorting (the Mac had to sort its dictionaries).</summary>
    [Fact]
    public void ToolDefinitionsAlwaysSerializeTheSame()
    {
        Settings.Shared.WebSearch = true;
        var seen = new HashSet<string>();
        for (int i = 0; i < 50; i++) seen.Add(ChatClient.CurrentTools().ToJsonString());
        Assert.Single(seen);
    }

    /// <summary>A cut-off answer is marked, and goes back to the model as its start and end only.</summary>
    [Fact]
    public void ACutOffAnswerIsSentBackShortened()
    {
        var chat = new ChatClient();
        var long1 = new string('x', 20_000);
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Text = "Plan" },
            new() { Role = ChatRole.Assistant, Text = long1, CutOff = true },
            new() { Role = ChatRole.Assistant, Text = long1 },
        };
        var messages = chat.RequestMessages(history);
        Assert.True(messages[2]!["content"]!.GetValue<string>().Length < 6500);
        Assert.Equal(20_000, messages[3]!["content"]!.GetValue<string>().Length);
    }
}

public class LongMessageViewTests
{
    [Fact]
    public void AVeryLongMessageShowsItsStartAndEnd()
    {
        var lines = Enumerable.Range(1, 5000).Select(i => $"regel {i} van het plan");
        var f = LongText.Folded(string.Join("\n", lines))!.Value;
        Assert.StartsWith("regel 1 van", f.Head);
        Assert.EndsWith("regel 5000 van het plan", f.Tail);
        Assert.False(f.Head.EndsWith('\n'));
        Assert.True(f.Hidden > 100_000);
        Assert.True(f.Head.Length + f.Tail.Length <= 8000);
    }

    [Fact]
    public void AnOrdinaryAnswerIsNotFolded() => Assert.Null(LongText.Folded(new string('x', 5000)));
}
