using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

/// <summary>The app knows itself: its help, its state, its actions, and skills of the user's own.</summary>
public class AppToolsTests : ChatTest
{
    private readonly ConversationStore store;
    private Conversation C => store.Conversations[0];
    private readonly FakeApp fake = new();

    private sealed class FakeApp : IAppControl
    {
        public readonly List<string> Done = [];
        public string Status() => "WinAIMagic test. Models: ministral-3-8b-q4 (downloaded).";
        public IReadOnlyList<string> SettingNames => ["Thinking (true/false)"];
        public AppActionPlan Plan(string action, JsonObject args) => action switch
        {
            "open_page" => new(ReadOnly: true),
            "delete_model" => new("Ministral verwijderen", "de map", Ask: true),
            "stop_engine" => new("Engine stoppen", AfterAnswer: true),
            "download_model" when args["model"]?.ToString() == "nope" => new(Error: "There is no model nope."),
            _ => new($"{action} doen"),
        };
        public Task<string> Run(string action, JsonObject args) { Done.Add(action); return Task.FromResult($"{action} done"); }
    }

    public AppToolsTests()
    {
        // here, not as a field: a field is made before ChatTest points the app data at this test's own folder
        store = new ConversationStore();
        Settings.Shared.AppTools = true;
        Settings.Shared.Skills = true;
        AppTools.Control = fake;
    }

    public override void Dispose()
    {
        AppTools.Control = null;
        base.Dispose();
    }

    private async Task<(ChatClient Chat, List<Approval> Asked)> Run(bool allow, params Func<HttpResponseMessage>[] script)
    {
        var engine = new FakeEngine(script);
        var chat = new ChatClient(engine.Client);
        var asked = new List<Approval>();
        chat.AskUser = (a, _) => { asked.Add(a); return Task.FromResult(new ApprovalAnswer(allow)); };
        chat.Send("doe het", [], C.Id, store, Base);
        await chat.Completion;
        return (chat, asked);
    }

    private List<string> Results => C.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Text).ToList();

    [Fact]
    public void ThePromptSaysWhereItRunsAndTheToolsAreThere()
    {
        var system = new ChatClient().RequestMessages([])[0]!["content"]!.GetValue<string>();
        Assert.Contains("WinAIMagic", system);
        Assert.Contains("app_action", system);
        Assert.Contains("create_skill", system);
        var names = ChatClient.CurrentTools().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["app_help", "app_status", "app_action", "create_skill", "use_skill"], names);
        // without skills, no create_skill; with the switch off, nothing of this
        Settings.Shared.Skills = false;
        Assert.DoesNotContain("create_skill", ChatClient.CurrentTools().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        Settings.Shared.AppTools = false;
        Assert.Empty(ChatClient.CurrentTools());
    }

    [Fact]
    public void HelpByTopicByWordsAndAsAList()
    {
        Assert.StartsWith("# Skills (topic skills)", AppTools.Help(new ToolCall("1", "app_help", """{"topic":"skills"}""")));
        Assert.Contains("(topic sysadmin)", AppTools.Help(new ToolCall("1", "app_help", """{"query":"wachtwoord beheerder"}""")));
        var list = AppTools.Help(new ToolCall("1", "app_help", "{}"));
        Assert.Contains("- models:", list);
        Assert.Contains("- troubleshooting:", list);
    }

    /// <summary>What the owner asked: "make a skill for C64 assembler" ends as a skill in the skill list, not in WINAIMAGIC.md.</summary>
    [Fact]
    public async Task ASkillGoesToTheSkillsFolder()
    {
        var (_, asked) = await Run(true,
            () => Call("create_skill", new JsonObject { ["name"] = "C64 Assembler", ["description"] = "Programmeren in 6502-assembler voor de C64", ["instructions"] = "Bouw met cl65 -t c64 main.s." }, "s"),
            () => Say("De skill staat klaar."));
        Assert.Equal("Skill maken: c64-assembler", Assert.Single(asked).Title);
        var file = Path.Combine(Paths.UserSkills, "c64-assembler", "SKILL.md");
        Assert.True(File.Exists(file));
        var skill = Assert.Single(Skills.All, s => s.Name == "c64-assembler");
        Assert.Equal("windows", skill.Platform);
        Assert.Equal("Bouw met cl65 -t c64 main.s.", skill.Body);
        Assert.Contains("saved", Results[0]);

        // again: replacing their own skill says so on the card; a no leaves it alone
        await Run(false,
            () => Call("create_skill", new JsonObject { ["name"] = "c64-assembler", ["description"] = "x", ["instructions"] = "anders" }, "t"),
            () => Say("Goed."));
        Assert.Contains("Bouw met cl65", File.ReadAllText(file));
    }

    [Fact]
    public async Task ActionsAskAndRun()
    {
        var (_, asked) = await Run(true,
            () => Call("app_action", new JsonObject { ["action"] = "open_page", ["page"] = "models" }, "a"),
            () => Call("app_action", new JsonObject { ["action"] = "download_model", ["model"] = "ministral-3-8b-q4" }, "b"),
            () => Call("app_action", new JsonObject { ["action"] = "download_model", ["model"] = "nope" }, "c"),
            () => Call("app_action", new JsonObject { ["action"] = "fly" }, "d"),
            () => Call("app_action", new JsonObject { ["action"] = "stop_engine" }, "e"),
            () => Say("Gedaan."));
        // a screen opens without a card; a change asks; a stop after the answer says so on its card
        Assert.Equal(["download_model doen", "Engine stoppen"], asked.Select(a => a.Title));
        Assert.Contains(asked[1].Warnings, w => w.Contains("antwoord klaar"));
        Assert.Equal(["open_page", "download_model", "stop_engine"], fake.Done);
        Assert.Contains("There is no model nope", Results[2]);
        Assert.Contains("Unknown action", Results[3]);
        Assert.Equal(["Stand van de app bekijken"], new[] { AppTools.Label(new ToolCall("1", "app_status", "{}")) });
    }

    [Fact]
    public async Task RightsHoldForTheAppToo()
    {
        Settings.Shared.RightSystemChange = "allow";
        var (_, asked) = await Run(true,
            () => Call("app_action", new JsonObject { ["action"] = "set_setting", ["setting"] = "Thinking", ["value"] = "false" }, "a"),
            () => Call("app_action", new JsonObject { ["action"] = "delete_model", ["model"] = "ministral-3-8b-q4" }, "b"),
            () => Say("Klaar."));
        // allow: no card, except deleting a model, which always asks
        Assert.Equal(["Ministral verwijderen"], asked.Select(a => a.Title));

        Settings.Shared.RightSystemChange = "deny";
        fake.Done.Clear();
        await Run(true,
            () => Call("app_action", new JsonObject { ["action"] = "set_setting", ["setting"] = "Thinking", ["value"] = "true" }, "c"),
            () => Say("Niet gedaan."));
        Assert.Empty(fake.Done);
        Assert.Contains("do not allow", Results[^1]);
    }

    [Fact]
    public void SkillNames()
    {
        Assert.Equal("c64-assembler", AppTools.SkillName("  C64 Assembler!! "));
        Assert.Null(AppTools.SkillName("!!!"));
        Assert.Null(AppTools.Skill(new ToolCall("1", "create_skill", """{"name":"x","description":"d"}""")));
    }
}
