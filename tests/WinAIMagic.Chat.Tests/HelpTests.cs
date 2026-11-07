using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>The help: the same topics in both languages, one for every page, and a search that ranks.</summary>
public class HelpTests : ChatTest
{
    [Fact]
    public void BothLanguagesHaveTheSameTopics()
    {
        var nl = HelpCenter.Topics("nl");
        var en = HelpCenter.Topics("en");
        Assert.True(nl.Count >= 18, $"{nl.Count} topics");
        Assert.Equal(nl.Select(t => t.Id), en.Select(t => t.Id));
        Assert.All(nl.Concat(en), t => Assert.True(t.Title.Length > 0 && t.Body.Length > 200, t.Id));
        // the help is about this PC, not a Mac (only the cluster says a Mac can be a helper)
        Assert.All(nl.Where(t => t.Id != "cluster"), t => Assert.DoesNotContain(" Mac ", " " + t.Body + " "));
    }

    [Fact]
    public void EveryPageHasItsTopic()
    {
        var ids = HelpCenter.Topics("en").Select(t => t.Id).ToHashSet();
        foreach (var page in new[] { "Live", "Models", "Documents", "Images", "Compare", "Cluster", "System", "Help", "Chat", null })
            Assert.Contains(HelpCenter.TopicFor(page), ids);
        // the settings tab and F1 open these
        foreach (var id in new[] { "start", "troubleshooting", "shortcuts", "sysadmin", "skills" }) Assert.Contains(id, ids);
    }

    [Fact]
    public void Search()
    {
        var nl = HelpCenter.Topics("nl");
        Assert.Equal("sysadmin", HelpCenter.Search("wachtwoord beheerder", nl).FirstOrDefault()?.Id);
        Assert.Equal("cluster", HelpCenter.Search("Thunderbolt", HelpCenter.Topics("en")).FirstOrDefault()?.Id);
        Assert.Equal(nl.Count, HelpCenter.Search("", nl).Count);
        Assert.Empty(HelpCenter.Search("xyzzy", nl));
    }

    [Fact]
    public void SearchRanksTitleOverText()
    {
        HelpTopic[] topics = [new("a", "Over iets", "", "", "modellen modellen modellen"), new("b", "Modellen", "", "", "tekst"), new("c", "Ander", "", "modellen", "x")];
        Assert.Equal(["b", "c", "a"], HelpCenter.Search("modellen", topics).Select(t => t.Id));
    }

    [Fact]
    public void Blocks()
    {
        var b = HelpCenter.Blocks("Intro\n## Kop\n| a | b |\n|---|---|\n| 1 | 2 |\n> Tip\n```\n## not a heading\n```");
        Assert.Equal(
        [
            new HelpCenter.Block(HelpCenter.BlockKind.Text, "Intro"), new(HelpCenter.BlockKind.Heading, "Kop"),
            new(HelpCenter.BlockKind.Text, "• a — b\n• 1 — 2"), new(HelpCenter.BlockKind.Tip, "Tip"), new(HelpCenter.BlockKind.Text, "```\n## not a heading\n```"),
        ], b);
    }
}
