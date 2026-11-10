using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

/// <summary>An answer of many rounds is one card with its parts in the order they came (on the Mac the owner saw text jump away).</summary>
public class AnswerTimelineTests
{
    private static ChatMessage Round(string text, string reasoning = "", params string[] steps) => new()
    {
        Role = ChatRole.Assistant, Text = text, Reasoning = reasoning,
        Activities = [.. steps.Select(s => new ToolActivity { Label = s, Done = true })],
    };

    [Fact]
    public void QuestionsSplitAnswersAndAutomaticMessagesStayInside()
    {
        var q1 = new ChatMessage { Role = ChatRole.User, Text = "maak een spel" };
        var go = new ChatMessage { Role = ChatRole.User, Text = "ga door", Automatic = true };
        var q2 = new ChatMessage { Role = ChatRole.User, Text = "nog een" };
        var items = ChatItem.Group([q1, Round("", "plan", "Uitvoeren: cd"), go, Round("klaar"), q2, Round("ja")]);
        Assert.Equal(4, items.Count);
        Assert.True(items[1].IsAnswer);
        Assert.Equal(3, items[1].Rounds.Count); // the automatic go-on belongs to the answer
        Assert.True(items[3].Contains(items[3].Id));
        Assert.Equal(q2.Id, items[2].Id);
    }

    [Fact]
    public void PartsAreChronological()
    {
        var rounds = new List<ChatMessage>
        {
            Round("", "eerst denken", "Uitvoeren: cd C:\\werk"),
            Round("Het werkt, nu de sprite.", "tweede gedachte", "Schrijven: spook.txt"),
            Round("KLAAR"),
        };
        var parts = AnswerPart.Of(rounds, false);
        var kinds = parts.Select(p => p.Kind switch
        {
            PartKind.Thought => "denk:" + p.Text,
            PartKind.Step => "stap:" + p.Text,
            PartKind.Text => "tekst:" + p.Text,
            _ => "?",
        });
        Assert.Equal(["denk:eerst denken", "stap:Uitvoeren: cd C:\\werk", "denk:tweede gedachte", "tekst:Het werkt, nu de sprite.", "stap:Schrijven: spook.txt", "tekst:KLAAR"], kinds);
        Assert.Equal(parts.Count, parts.Select(p => p.Id).Distinct().Count()); // ids are unique
        // a finished answer keeps its last text in view; the rest folds into one line
        Assert.Equal(5, AnswerPart.TailStart(parts));
    }

    /// <summary>On the Mac in 1.14.10 the learning step after the answer ("MACAIMAGIC.md bijgewerkt") folded the answer away: only
    /// "Gedachtegang en 28 stappen" was left in the chat.</summary>
    [Fact]
    public void TheAnswerStaysInViewWithAStepAfterIt()
    {
        static List<string> Kinds(IEnumerable<AnswerPart> parts) => parts.Select(p => p.Kind == PartKind.Thought ? "denk" : p.Text).ToList();
        static (List<AnswerPart> Work, List<AnswerPart> Tail) Split(List<AnswerPart> parts)
        {
            int t = AnswerPart.TailStart(parts);
            return (parts.GetRange(0, t), parts.GetRange(t, parts.Count - t));
        }
        var (work, tail) = Split(AnswerPart.Of([Round("", "plan", "Uitvoeren: c64run"), Round("De tekst breekt nu op spaties af.", "klaar", "WINAIMAGIC.md bijgewerkt")], false));
        Assert.Equal(["denk", "Uitvoeren: c64run", "denk"], Kinds(work));
        Assert.Equal(["De tekst breekt nu op spaties af.", "WINAIMAGIC.md bijgewerkt"], Kinds(tail));
        // a text before much more work folds with that work; the last parts after it stay in view
        Assert.Empty(Split(AnswerPart.Of([Round("Ik begin.", "", "a", "b", "c"), Round("", "")], false)).Tail);
        // a text and then thinking: the old rule
        Assert.Empty(Split(AnswerPart.Of([Round("Tussendoor."), Round("", "denk verder", "x")], false)).Tail);
    }

    [Fact]
    public void OnlyTheLastRoundThinksLive()
    {
        var parts = AnswerPart.Of([Round("", "a", "x"), Round("", "b")], true);
        Assert.Equal([false, true], parts.Where(p => p.Kind == PartKind.Thought).Select(p => p.Live));
    }

    [Fact]
    public void ARoundWithOnlyThinkingAndStepsStaysVisible()
    {
        var m = Round("", "denk", "Uitvoeren: dir");
        m.ToolCalls = [new ToolCall("1", "run_command", "{}")];
        Assert.False(m.IsHidden); // its thinking must not vanish once the tools ran
    }

    [Fact]
    public void Gist()
    {
        Assert.Equal("regel een regel twee", AnswerPart.Gist("regel een\nregel twee", false));
        // while thinking: the last lines one below the other, not one block that runs together
        Assert.Equal("twee\ndrie\nvier\nvijf", AnswerPart.Gist("een\ntwee\n\n  drie\nvier\nvijf", true));
        // the line being written keeps its end, an earlier long one its start
        var live = AnswerPart.Gist(new string('b', 300) + "\n" + new string('a', 500) + "einde", true).Split('\n');
        Assert.Equal(AnswerPart.GistWidth, live[0].Length);
        Assert.EndsWith("…", live[0]);
        Assert.EndsWith("einde", live[1]);
        Assert.Equal(AnswerPart.GistWidth, live[1].Length);
    }
}

/// <summary>A conversation with a work folder and a scripted model, for the tests below.</summary>
public abstract class LoopTest : ChatTest
{
    protected readonly ConversationStore Store;
    protected Conversation C => Store.Conversations[0];

    // the store reads the test's own data folder, which the base sets up first
    protected LoopTest()
    {
        Store = new ConversationStore();
        C.Workspace = NewFolder("work");
        Settings.Shared.RightRead = "allow";
    }

    protected async Task<FakeEngine> Ask(string text, FakeEngine engine)
    {
        var chat = new ChatClient(engine.Client);
        chat.Send(text, [], C.Id, Store, Base);
        await chat.Completion;
        return engine;
    }
}

/// <summary>Each round of the agent loop keeps its own steps; the next round no longer takes the whole list over.</summary>
public class StepsPerRoundTests : LoopTest
{
    [Fact]
    public async Task EachRoundKeepsItsSteps()
    {
        var engine = new FakeEngine(
            () => Call("list_dir", new JsonObject { ["path"] = "." }, "1"),
            () => Call("list_dir", new JsonObject { ["path"] = "." }, "2"),
            () => Say("KLAAR"));
        await Ask("Werk", engine);
        var answer = ChatItem.Group(C.Messages.Where(m => !m.IsHidden)).Single(i => i.IsAnswer);
        var parts = AnswerPart.Of(answer.Rounds, false);
        Assert.Equal([PartKind.Step, PartKind.Step, PartKind.Text], parts.Select(p => p.Kind));
    }
}

/// <summary>On the Mac DeepSeek thought 16,000 tokens before every step; only the first round of an answer gets the full budget.</summary>
public class FollowUpThinkingTests : LoopTest
{
    [Fact]
    public void LaterRoundsThinkLess()
    {
        Assert.Equal(16_000, ChatClient.ThinkingBudget(16_000, 0));
        Assert.Equal(ChatClient.FollowUpThinking, ChatClient.ThinkingBudget(16_000, 1));
        Assert.Equal(2_000, ChatClient.ThinkingBudget(2_000, 5)); // a smaller setting stays
        Assert.Equal(0, ChatClient.ThinkingBudget(0, 0)); // no limit in the first round
        Assert.Equal(ChatClient.FollowUpThinking, ChatClient.ThinkingBudget(0, 3));
    }

    [Fact]
    public async Task TheSecondRoundAsksForLessThinking()
    {
        Settings.Shared.ThinkingBudget = 16_000;
        var engine = new FakeEngine(() => Call("list_dir", new JsonObject { ["path"] = "." }), () => Say("KLAAR"));
        await Ask("Werk", engine);
        Assert.Equal(16_000, engine.Requests[0]["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal(4_000, engine.Requests[1]["thinking_budget_tokens"]!.GetValue<int>());
    }
}

/// <summary>One thinking choice (off, low, normal, high) sets the switch, the effort and the budget, so it works for every model.</summary>
public class ThinkingLevelTests : LoopTest
{
    [Fact]
    public void Levels()
    {
        var s = Settings.Shared;
        s.ThinkingBudget = 16_000;
        s.ThinkingLevel = ThinkingLevel.Off;
        Assert.False(s.Thinking);
        Assert.Equal(ThinkingLevel.Off, s.ThinkingLevel);
        Assert.Equal(0, s.ThinkingBudgetNow);
        s.ThinkingLevel = ThinkingLevel.Low;
        Assert.True(s.Thinking);
        Assert.Equal("low", s.ReasoningEffort);
        Assert.Equal(2_000, s.ThinkingBudgetNow);
        s.ThinkingLevel = ThinkingLevel.Normal;
        Assert.Equal("medium", s.ReasoningEffort);
        Assert.Equal(8_000, s.ThinkingBudgetNow);
        s.ThinkingLevel = ThinkingLevel.High;
        Assert.Equal("high", s.ReasoningEffort);
        Assert.Equal(16_000, s.ThinkingBudgetNow);
        s.ReasoningEffort = "max";
        Assert.Equal(ThinkingLevel.High, s.ThinkingLevel); // max from Settings counts as high
        s.ThinkingLevel = ThinkingLevel.High;
        Assert.Equal("max", s.ReasoningEffort); // choosing high again keeps max
        Assert.Equal(2_000, ChatClient.ThinkingBudget(2_000, 3));
    }

    [Fact]
    public async Task LowThinkingTravelsAsTwoThousandTokens()
    {
        Settings.Shared.ThinkingLevel = ThinkingLevel.Low;
        var body = (await Ask("Hallo", new FakeEngine(() => Say("Hoi")))).Requests[0];
        Assert.Equal(2_000, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal("low", body["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
    }
}

/// <summary>"ga door" on work under way thinks like a later round; a real new question keeps the full budget.</summary>
public class GoOnThinkingTests : LoopTest
{
    [Fact]
    public void GoOnText()
    {
        foreach (var t in new[] { "ga door", "Ga door!", "verder", "continue", "Go on.", "ga door graag", "mach weiter" })
            Assert.True(ChatClient.IsGoOnText(t), t);
        foreach (var t in new[] { "ga door met het spel maar maak de spookjes groter en sneller", "door welke deur?", "maak een spel", "" })
            Assert.False(ChatClient.IsGoOnText(t), t);
    }

    [Fact]
    public void OnlyAfterAnswerWithSteps()
    {
        var c = new Conversation();
        c.Messages.AddRange([new ChatMessage { Role = ChatRole.User, Text = "maak een spel" },
            new ChatMessage { Role = ChatRole.Assistant, ToolCalls = [new ToolCall("1", "run_command", "{}")] },
            new ChatMessage { Role = ChatRole.Assistant, Text = "pauze" }, new ChatMessage { Role = ChatRole.User, Text = "ga door" }]);
        Assert.True(ChatClient.IsGoOn(c));
        c.Messages.Clear();
        c.Messages.AddRange([new ChatMessage { Role = ChatRole.User, Text = "hoi" }, new ChatMessage { Role = ChatRole.Assistant, Text = "hallo" },
            new ChatMessage { Role = ChatRole.User, Text = "ga door" }]);
        Assert.False(ChatClient.IsGoOn(c)); // no work under way: a full first round
    }

    [Fact]
    public async Task GoOnAfterWorkStartsWithLessThinking()
    {
        Settings.Shared.ThinkingBudget = 16_000;
        await Ask("Werk", new FakeEngine(() => Call("list_dir", new JsonObject { ["path"] = "." }), () => Say("Pauze.")));
        var again = await Ask("ga door", new FakeEngine(() => Say("KLAAR")));
        Assert.Equal(4_000, again.Requests[0]["thinking_budget_tokens"]!.GetValue<int>());
    }
}
