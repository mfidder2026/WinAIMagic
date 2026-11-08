using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>A small model (Ministral-3-8B, on the Mac) did not load the owner's C64 skill for a C64 question: the app loads a skill
/// itself when the question is clearly about it.</summary>
public class SkillMatchTests : ChatTest
{
    private static readonly Skill C64 = new("c64-basic-v2",
        "Commodore 64 BASIC V2 programmeren: harde regels, tokentabel, verboden statements, vaste patronen voor scherm/sprites/SID/invoer, plus de geteste toolchain (syntax checker en .bas→.prg compiler) en het VICE-pad. Laden zodra er C64 BASIC-code geschreven, gecontroleerd of gecompileerd moet worden.",
        "windows", "# C64");
    private static readonly Skill Look = new("uiterlijk", "Donkere modus, accentkleur, de taakbalk: plaats, grootte, verbergen, apps erin.", "windows", "# Uiterlijk");

    [Fact]
    public void TheOwnersQuestionLoadsTheC64Skill()
    {
        var q = "Je bent een commodore 64 basic programmeur en je gaat een gave tekstadventure maken voor de commodore 64 in basic v2. Als intro vliegt er een spookje door het scherm met enge muziek. Zoek op internet naar gave teksten, sprites en gameplay.";
        Assert.Equal(["c64-basic-v2"], Skills.Matching(q, [Look, C64]).Select(s => s.Name));
        Assert.Equal(["c64-basic-v2"], Skills.Matching("Schrijf een C64 BASIC programma met een sprite", [C64]).Select(s => s.Name));
    }

    [Fact]
    public void OtherQuestionsDoNot()
    {
        Assert.Empty(Skills.Matching("Wat is het weer morgen in Amsterdam?", [C64, Look]));
        Assert.Empty(Skills.Matching("Leg uit hoe BASIC werkt", [C64]));   // one word is not enough
        Assert.Empty(Skills.Matching("Zet het scherm op donker", [C64, Look]));
    }

    /// <summary>The skill goes into the conversation as a use_skill call and its result, once per conversation, and shows as a step.</summary>
    [Fact]
    public void TheSkillIsLoadedOnceAsAStep()
    {
        Settings.Shared.Skills = true;
        var store = new ConversationStore();
        var c = new Conversation();
        c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "Schrijf een C64 BASIC programma met een sprite" });
        store.Conversations = [c];
        ChatClient.LoadMatchingSkills(c.Id, store, [C64]);
        ChatClient.LoadMatchingSkills(c.Id, store, [C64]);
        Assert.Equal(3, c.Messages.Count);
        var step = c.Messages[1];
        Assert.Equal(ChatRole.Assistant, step.Role);
        Assert.True(step.Automatic);
        Assert.Equal(Skills.ToolName, step.ToolCalls.Single().Name);
        Assert.Equal("Skill geladen: c64-basic-v2 (vanzelf)", step.Activities.Single().Label);
        Assert.False(step.IsHidden);
        Assert.Equal(step.ToolCalls[0].Id, c.Messages[2].ToolCallId);
        Assert.StartsWith("# Skill: c64-basic-v2", c.Messages[2].Text);
        // with skills off nothing is loaded
        Settings.Shared.Skills = false;
        var other = new Conversation();
        other.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "Schrijf een C64 BASIC programma met een sprite" });
        store.Conversations = [other];
        ChatClient.LoadMatchingSkills(other.Id, store, [C64]);
        Assert.Single(other.Messages);
    }
}
