using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>Qwen's template keeps the model's earlier thinking (Qwen3.8 for the whole conversation, Qwen3.6 since the last question)
/// and knows low, medium and high/xhigh only (the Mac's d9c31a4).</summary>
public class QwenRequestTests : ChatTest
{
    private static List<ChatMessage> History() =>
    [
        new() { Role = ChatRole.User, Text = "Wat staat er?" },
        new() { Role = ChatRole.Assistant, ToolCalls = [new ToolCall("a", "list_dir", "{}")], Reasoning = "Eerst kijken wat er in de map staat." },
        new() { Role = ChatRole.Tool, Text = "(leeg)", ToolCallId = "a" },
        new() { Role = ChatRole.Assistant, Text = "Klaar.", Reasoning = "De map is leeg." },
        new() { Role = ChatRole.Assistant, Text = new string('x', 10), Reasoning = "Lang nagedacht", CutOff = true },
    ];

    private static List<JsonNode> Assistant(JsonArray o) => [.. o.Where(m => m!["role"]!.GetValue<string>() == "assistant").Select(m => m!)];

    [Fact]
    public void EarlierThinkingGoesBackWhenKept()
    {
        var chat = new ChatClient();
        var sent = Assistant(chat.RequestMessages(History(), keepThinking: true));
        // a cut-off answer goes back shortened, without its thinking
        Assert.Equal(["Eerst kijken wat er in de map staat.", "De map is leeg.", null], sent.Select(m => m["reasoning_content"]?.GetValue<string>()));
        Assert.All(Assistant(chat.RequestMessages(History())), m => Assert.Null(m["reasoning_content"]));
    }

    [Fact]
    public void AnOlderConversationKeepsSendingEmptyThinking()
    {
        var now = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(new Conversation(), ChatJson.Options), ChatJson.Options)!;
        Assert.True(now.ThinkingKept);
        // a conversation saved by 1.14.13 has no thinkingKept: it stays as the engine read it
        var json = JsonNode.Parse(JsonSerializer.Serialize(new Conversation(), ChatJson.Options))!.AsObject();
        json.Remove("thinkingKept");
        Assert.Null(JsonSerializer.Deserialize<Conversation>(json.ToJsonString(), ChatJson.Options)!.ThinkingKept);
    }

    [Fact]
    public void MaximaalBecomesHighForQwen()
    {
        Settings.Shared.ReasoningEffort = "max";
        Settings.Shared.Thinking = true;
        Assert.Equal("high", ChatClient.TemplateKwargs(qwen: true)["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("max", ChatClient.TemplateKwargs()["reasoning_effort"]!.GetValue<string>());
        // the thinking of earlier questions stays out (Qwen3.8 keeps it all by default)
        Assert.False(ChatClient.TemplateKwargs(qwen: true)["preserve_thinking"]!.GetValue<bool>());
        Assert.Null(ChatClient.TemplateKwargs()["preserve_thinking"]);
    }

    /// <summary>Qwen's card: with thinking top_p 0.95, without 0.8; top_k 20 and min_p 0 either way. Other models keep top_p 1.</summary>
    [Fact]
    public void QwenGetsItsOwnSampling()
    {
        var chat = new ChatClient { ModelFamily = () => "qwen" };
        Settings.Shared.Thinking = true;
        var body = chat.StreamBody(History(), null);
        Assert.Equal(0.95, body["top_p"]!.GetValue<double>());
        Assert.Equal(20, body["top_k"]!.GetValue<int>());
        Assert.Equal(0.0, body["min_p"]!.GetValue<double>());
        Settings.Shared.Thinking = false;
        Assert.Equal(0.8, chat.StreamBody(History(), null)["top_p"]!.GetValue<double>());
        var other = new ChatClient { ModelFamily = () => "glm" }.StreamBody(History(), null);
        Assert.Equal(1.0, other["top_p"]!.GetValue<double>());
        Assert.Null(other["top_k"]);
    }
}
