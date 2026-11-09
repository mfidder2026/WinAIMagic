using System.Text;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>The file panel beside the chat: what it lists of the working folder and what it shows of a file.</summary>
public class WorkspaceFilesTests : ChatTest
{
    [Fact]
    public void FoldersComeFirstInExplorersOrderAndHiddenOnesStayOut()
    {
        var dir = NewFolder("panel");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        foreach (var n in new[] { "file10.txt", "file2.txt", "Alpha.cs" }) File.WriteAllText(Path.Combine(dir, n), "x");
        var hidden = Path.Combine(dir, "geheim.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var (entries, more) = WorkspaceFiles.List(dir);
        Assert.Equal(["src", "Alpha.cs", "file2.txt", "file10.txt"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsFolder);
        Assert.Equal(0, more);
        Assert.Empty(WorkspaceFiles.List(Path.Combine(dir, "bestaat-niet")).Entries);
    }

    [Fact]
    public void AVeryFullFolderShowsItsFirstPartAndCountsTheRest()
    {
        var dir = NewFolder("vol");
        for (int i = 0; i < WorkspaceFiles.MaxEntries + 7; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "");
        var (entries, more) = WorkspaceFiles.List(dir);
        Assert.Equal(WorkspaceFiles.MaxEntries, entries.Count);
        Assert.Equal(7, more);
    }

    [Fact]
    public void TextIsShownAndABinaryFileIsNamedAsSuch()
    {
        var dir = NewFolder("lees");
        var code = Path.Combine(dir, "spel.bas");
        File.WriteAllText(code, "10 print \"héllo\"\n20 goto 10\n", new UTF8Encoding(true));
        Assert.Equal("10 print \"héllo\"\n20 goto 10\n", WorkspaceFiles.Read(code).Text);
        Assert.Null(WorkspaceFiles.Read(code).Note);

        var old = Path.Combine(dir, "oud.txt");
        File.WriteAllBytes(old, [(byte)'c', 0xE9, (byte)'!']);        // Windows-1252, not UTF-8
        Assert.Equal("cé!", WorkspaceFiles.Read(old).Text);

        var prg = Path.Combine(dir, "spel.prg");
        File.WriteAllBytes(prg, [0x01, 0x08, 0x0E, 0x08, 0x0A, 0x00, 0x99, 0x00, 0x00, 0x00]);
        var shown = WorkspaceFiles.Read(prg);
        Assert.Null(shown.Text);
        Assert.Contains("10 B", shown.Note);

        Assert.Null(WorkspaceFiles.Read(Path.Combine(dir, "weg.txt")).Text);
    }

    [Fact]
    public void ALongFileShowsItsFirstPartUpToAWholeLine()
    {
        var dir = NewFolder("lang");
        var log = Path.Combine(dir, "lang.log");
        var line = new string('é', 99) + "\n";                       // 199 bytes in UTF-8: the cut falls inside a character
        File.WriteAllText(log, string.Concat(Enumerable.Repeat(line, 6000)));
        var shown = WorkspaceFiles.Read(log);
        Assert.NotNull(shown.Text);
        Assert.EndsWith("é", shown.Text);
        Assert.DoesNotContain('�', shown.Text);
        Assert.True(shown.Text!.Length < 6000 * line.Length);
        Assert.NotNull(shown.Note);
    }
}
