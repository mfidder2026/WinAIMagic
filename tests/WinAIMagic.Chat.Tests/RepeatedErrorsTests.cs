using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

/// <summary>The same error three times: the app tells the model its idea of the cause is probably wrong. In the Terminator intro on
/// the Mac (Qwen3.6, 11 Nov 2026) Kick Assembler said "Syntax error" on `beq !t1-done` in 10 of 13 builds while the model changed other
/// lines and rewrote the whole file 14 times.</summary>
public class RepeatedErrorsTests
{
    /// <summary>Kick Assembler's output as c64kbuild.py shows it on this PC, the error on line `line`.</summary>
    private static string KickAss(int line, string source = "        beq !t1-done") => $"""
        [1] syntax checker: terminator.asm

        Summary: 0 error(s), 0 warning(s)
        [2] assembleren: java -jar KickAss.jar terminator.asm -vicesymbols -showmem -o build\terminator.prg
        parsing
        Got 19 errors while executing:
          (C:\Users\fremen\Development\Kick Assembler\QwenFlashKick\terminator\terminator.asm {line}:17) Syntax error
          (C:\Users\fremen\Development\Kick Assembler\QwenFlashKick\terminator\terminator.asm {line + 4}:1) Invalid token
          ...

                lda #>termData
                sta $fe
        !t1:    lda ($fd),y
        {source}
                        ^

        Error: Syntax error
        at line {line}, column 17 in terminator.asm

        FOUT: Kick Assembler gaf een fout (zie hierboven): eerst repareren

        [exit code 1]
        """;

    [Fact]
    public void TheTerminatorErrorIsRecognizedWhereverItMoves()
    {
        var repeated = new RepeatedErrors();
        Assert.Null(repeated.Note(KickAss(67)));
        Assert.Null(repeated.Note(KickAss(49)));
        var warning = repeated.Note(KickAss(54));
        Assert.NotNull(warning);     // the third time
        Assert.Contains("beq !t#-done", warning);
        Assert.Contains("3 times", warning);
        Assert.DoesNotContain("fremen", warning);    // folders are left out
        Assert.Null(repeated.Note(KickAss(57)));
        Assert.Null(repeated.Note(KickAss(60)));
        Assert.NotNull(repeated.Note(KickAss(63)));  // and again every third time
    }

    [Fact]
    public void OtherErrorsAndGoodBuildsDoNotCount()
    {
        var repeated = new RepeatedErrors();
        Assert.Null(repeated.Note(KickAss(10, "        beq !t1-done")));
        Assert.Null(repeated.Note(KickAss(20, "        lda #$100")));
        Assert.Null(repeated.Note(KickAss(30, "        sta $0400+40*10+x")));
        var good = "[2] assembleren: java -jar KickAss.jar t.asm\nparsing\nflex pass 1\nOutput pass\nWriting prg file: build\\t.prg\n[3] build\\t.prg: laadadres $0801, 120 bytes\nOK: build\\t.prg\n\n[exit code 0]";
        Assert.Null(RepeatedErrors.Signature(good));     // a good build reports no error
        for (int i = 0; i < 5; i++) Assert.Null(repeated.Note(good));
        Assert.Null(RepeatedErrors.Signature("Wrote 4647 bytes to terminator\\terminator.asm"));
    }

    /// <summary>The checker names the line before Kick Assembler, a Python script ends with its exception, and PowerShell reports a
    /// command it does not know: each is the same error when only its numbers or folders change.</summary>
    [Fact]
    public void TheNewBuildOutputTheCheckerAndPython()
    {
        static string Checker(int n) => $"[1] syntax checker: t.asm\nERROR: Line {n}: '!t1-done' - een !-label mag geen - of + in de naam hebben\n\nSummary: 1 error(s), 0 warning(s)\nFOUT: de syntax checker vond ERRORs: eerst repareren\n\n[exit code 1]";
        Assert.Equal(RepeatedErrors.Signature(Checker(54)), RepeatedErrors.Signature(Checker(98)));
        Assert.Contains("!t#-done", RepeatedErrors.Signature(Checker(54)));
        var python = "Traceback (most recent call last):\n  File \"<stdin>\", line 18, in <module>\nTypeError: '>=' not supported between instances of 'str' and 'int'\n\n[exit code 1]";
        var key = RepeatedErrors.Signature(python) ?? "";
        Assert.Contains("typeerror", key);
        Assert.DoesNotContain("traceback", key);     // the generic first line says nothing about the error
        static string Shell(string folder) => $"c64kbuild : The term 'c64kbuild' is not recognized as a name of a cmdlet, function, script file, or executable program.\nAt line:1 char:1\n+ c64kbuild {folder}\\intro.asm\n+ CategoryInfo          : ObjectNotFound: (c64kbuild:String) [], CommandNotFoundException\n+ FullyQualifiedErrorId : CommandNotFoundException\n\n[exit code 1]";
        Assert.Equal(RepeatedErrors.Signature(Shell(@"C:\a")), RepeatedErrors.Signature(Shell(@"D:\werk\b")));
        Assert.StartsWith("c#kbuild : the term", RepeatedErrors.Signature(Shell(@"C:\a")));     // digits count as #
    }
}

/// <summary>The same file written in full again and again, never run: the app tells the model the third time. In the Terminator intro
/// on the Mac (Qwen3.8, 11 Nov 2026) terminator.asm was written in full 9 times in 40 minutes and never built.</summary>
public class UntestedRewritesTests
{
    /// <summary>The pattern of that run, made up: analysis scripts and searches in a folder named like the file, a data script that is
    /// run, and the program itself that is written again and again.</summary>
    [Fact]
    public void ThePatternOfTheTerminatorRun()
    {
        var r = new UntestedRewrites();
        var notes = new List<string>();
        void Write(string path)
        {
            if (r.Wrote(path) is { } n) notes.Add($"{path} {(n.Contains("3 times") ? 3 : n.Contains("6 times") ? 6 : 0)}");
        }
        r.Ran("cd C:\\Users\\x\\Development\\Terminator; python -c \"raw = open('s1.kla','rb').read()\"");
        Write("gen.py");
        r.Ran("cd C:\\Users\\x\\Development\\Terminator; python gen.py; Get-ChildItem *.bin");
        Write("gen.py");
        Write("gen.py");
        r.Ran("cd C:\\Users\\x\\Development\\Terminator; python gen.py");
        Write("gen.py");
        for (int k = 1; k <= 6; k++)
        {
            Write("terminator.asm");
            if (k == 4) r.Ran("Select-String -Pattern SID -Path \"$env:LOCALAPPDATA\\WinAIMagic\\Skills\\c64_kickassembler\\SKILL.md\"");
            if (k == 5) r.Ran("cd C:\\Users\\x\\Development\\Terminator; Get-ChildItem; Get-Content WINAIMAGIC.md");
        }
        Assert.Equal(["terminator.asm 3", "terminator.asm 6"], notes);   // the folder named Terminator is not the file
    }

    [Fact]
    public void AFileCountsAsRunWhenACommandNamesIt()
    {
        foreach (var command in new[] { "python c64kbuild.py terminator.asm", "python c64run.py build\\terminator.prg --shot t.png",
                     "cd C:\\x\\Terminator; java -jar KickAss.jar Terminator.asm" })
        {
            var r = new UntestedRewrites();
            Assert.Null(r.Wrote("terminator.asm"));
            Assert.Null(r.Wrote("terminator.asm"));
            r.Ran(command);
            Assert.Null(r.Wrote("terminator.asm"));
            Assert.Null(r.Wrote("terminator.asm"));
            Assert.NotNull(r.Wrote("terminator.asm"));   // three writes after the run
        }
        var other = new UntestedRewrites();
        other.Wrote("src\\a.asm");
        other.Wrote("src/a.asm");
        other.Ran("python c64kbuild.py b.asm; cd area; Get-ChildItem");
        Assert.NotNull(other.Wrote("src\\a.asm"));      // another file, and a word that only starts like it, do not count
        Assert.Null(other.Wrote("other.asm"));          // each file counts on its own
    }
}

/// <summary>Both notes ride on the tool result itself, through the real chat loop, against the stand-in engine.</summary>
public class RepeatedErrorsChatTests : ChatTest
{
    private readonly ConversationStore store;
    private readonly string work;

    public RepeatedErrorsChatTests()
    {
        Settings.Shared.WorkspaceTools = true;
        Settings.Shared.LearnFromChats = false;
        Settings.Shared.RightRun = "allow";
        Settings.Shared.RightWrite = "allow";
        work = NewFolder("work");
        Settings.Shared.Workspace = work;
        store = new ConversationStore();
        store.Update(store.Conversations[0].Id, c => c.Workspace = work);
    }

    private Conversation C => store.Conversations[0];

    private async Task Ask(string text, FakeEngine engine)
    {
        var chat = new ChatClient(engine.Client);
        chat.Send(text, [], C.Id, store, new Uri("http://127.0.0.1:8080"));
        await chat.Completion;
    }

    [Fact]
    public async Task TheThirdSameErrorTellsTheModel()
    {
        Func<HttpResponseMessage> Build(int line, string id) => () => Call("run_command",
            new JsonObject { ["command"] = $"Write-Output 'parsing'; Write-Output '        beq !t1-done'; Write-Output '                ^'; Write-Output 'Error: Syntax error'; Write-Output 'at line {line}, column 17 in intro.asm'; exit 1" }, id);
        await Ask("BOUWFOUT", new FakeEngine(Build(67, "a"), Build(49, "b"), Build(54, "c"), Build(57, "d"), () => Say("Ik geef het op.")));

        var results = C.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Text).ToList();
        Assert.Equal(4, results.Count);
        Assert.Equal([false, false, true, false], results.Select(r => r.Contains("Note from the app")));
        Assert.Contains("Syntax error", results[2]);    // the warning comes with the error itself
        var steps = C.Messages.SelectMany(m => m.Activities).ToList();
        Assert.Contains(steps, s => s.Output?.Contains("Note from the app") == true);    // the user sees it in the step
        // on the Mac the last step of a round was never stored as done, without its output (33 of 37 in the Terminator run)
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.True(s.Done && s.Output != null));
    }

    [Fact]
    public async Task TheThirdWholeWriteWithoutARunTellsTheModel()
    {
        Func<HttpResponseMessage> Write(string id) => () => Call("write_file", new JsonObject { ["path"] = "intro.asm", ["content"] = "* = $0801\n" + id }, id);
        await Ask("SCHRIJF DRIE KEER", new FakeEngine(Write("a"), Write("b"), Write("c"), () => Say("Klaar.")));

        var results = C.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Text).ToList();
        Assert.Equal([false, false, true], results.Select(r => r.Contains("Note from the app")));
        Assert.Contains("intro.asm in full 3 times", results[2]);
        Assert.Equal("* = $0801\nc", File.ReadAllText(Path.Combine(work, "intro.asm")).Replace("\r\n", "\n"));   // the write itself still happens
    }
}
