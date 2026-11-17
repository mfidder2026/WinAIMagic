using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>A skill too long for the context is shortened (the Mac's b10dd36): the owner's 71 KB Kick Assembler skill, about 20,000
/// tokens, made a request of 30,291 tokens for a context of 24,576 on a 32 GB Mac, and the engine refused it.</summary>
public class SkillSizeTests
{
    private static Skill Long(string folder)
    {
        var body = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"## Deel {i}\n" + new string('x', 590)));
        return new Skill("Commodore 64 Kick Assembler Developer", "C64", "windows", body, folder);
    }

    [Fact]
    public void ALongSkillIsShortenedWithItsHeadingsAndPath()
    {
        var folder = @"C:\Users\x\AppData\Local\WinAIMagic\Skills\c64_kickassembler";
        var text = Skills.Text(Long(folder), 26_000);
        Assert.InRange(text.Length, 20_000, 26_000 + 6_000);
        Assert.Contains("## Deel 1\n", text);
        Assert.Contains("longer than this conversation can hold", text);
        Assert.Contains(Path.Combine(folder, "SKILL.md"), text);
        Assert.Contains("Select-String -Path", text);
        Assert.Contains("  ## Deel 50", text);           // a heading of the part left out
        Assert.Contains("more headings", text);          // at most 80 of them
    }

    [Fact]
    public void AShortSkillOrNoLimitIsWhole()
    {
        var skill = Long(@"C:\x");
        Assert.DoesNotContain("longer than", Skills.Text(skill));
        Assert.DoesNotContain("longer than", Skills.Text(skill, 200_000));
        Assert.Equal(26_000, new ChatClient { ContextLimit = 32_500 }.SkillLimit);
    }
}
