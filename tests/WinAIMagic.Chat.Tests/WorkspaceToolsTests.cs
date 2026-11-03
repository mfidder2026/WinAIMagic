using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinAIMagic.Chat.Tests;

public class WorkspaceToolsTests : ChatTest
{
    private readonly string dir;

    public WorkspaceToolsTests()
    {
        dir = NewFolder("work");
        Settings.Shared.Workspace = dir;
    }

    public static Task<string> Call(string name, JsonObject args, CancellationToken ct = default) =>
        WorkspaceTools.RunAsync(new ToolCall("t", name, args.ToJsonString()), ct);

    [Fact]
    public async Task StaysInsideTheWorkspace()
    {
        Assert.Throws<ToolError>(() => WorkspaceTools.Resolve("../escape.txt"));
        Assert.Throws<ToolError>(() => WorkspaceTools.Resolve(@"C:\Windows\win.ini"));
        Assert.Throws<ToolError>(() => WorkspaceTools.Resolve("sub/../../escape.txt"));
        // a link inside the workspace that points outside must not work either
        Junction(Path.Combine(dir, "link"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers"));
        Assert.Throws<ToolError>(() => WorkspaceTools.Resolve("link/etc/hosts"));
        var o = await Call("read_file", new JsonObject { ["path"] = "link/etc/hosts" });
        Assert.StartsWith("Error:", o);
        WorkspaceTools.Resolve("new/deeper/file.txt");
    }

    [Fact]
    public async Task WriteReadEditList()
    {
        var w = await Call("write_file", new JsonObject { ["path"] = "src/hello.py", ["content"] = "print('hallo')\nprint('wereld')\n" });
        Assert.Contains("Wrote", w);
        var r = await Call("read_file", new JsonObject { ["path"] = "src/hello.py" });
        Assert.Contains("1\tprint('hallo')", r);
        var e = await Call("edit_file", new JsonObject { ["path"] = "src/hello.py", ["old_text"] = "wereld", ["new_text"] = "pc" });
        Assert.Contains("Edited", e);
        var l = await Call("list_dir", new JsonObject { ["path"] = "src" });
        Assert.Contains("hello.py", l);
        var bad = await Call("edit_file", new JsonObject { ["path"] = "src/hello.py", ["old_text"] = "print", ["new_text"] = "x" });
        Assert.Contains("2 times", bad);
        Assert.Contains("Include more surrounding text", bad);
    }

    [Fact]
    public async Task BinaryWriteForAPrg()
    {
        // a C64 BASIC program: load address $0801, then 10 PRINT"HI"
        byte[] bytes = [0x01, 0x08, 0x0B, 0x08, 0x0A, 0x00, 0x99, 0x22, 0x48, 0x49, 0x22, 0x00, 0x00, 0x00];
        var w = await Call("write_file", new JsonObject { ["path"] = "hi.prg", ["content"] = Convert.ToBase64String(bytes), ["encoding"] = "base64" });
        Assert.Contains("14 bytes", w);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dir, "hi.prg")));
        var r = await Call("read_file", new JsonObject { ["path"] = "hi.prg" });
        Assert.Contains("binary", r);
        Assert.Contains("01 08 0b 08", r);
        var nope = await Call("write_file", new JsonObject { ["path"] = "x", ["content"] = "abc%", ["encoding"] = "base64" });
        Assert.Contains("not valid base64", nope);
    }

    [Fact]
    public async Task RunCommandAndTailALog()
    {
        await Call("write_file", new JsonObject { ["path"] = "run.ps1", ["content"] = "1..50 | ForEach-Object { \"regel $_\" } > app.log\nWrite-Output klaar\nexit 3\n" });
        var o = await Call("run_command", new JsonObject { ["command"] = @"& .\run.ps1" });
        Assert.Contains("klaar", o);
        Assert.Contains("exit code 3", o);
        var tail = await Call("read_file", new JsonObject { ["path"] = "app.log", ["tail"] = 2 });
        Assert.Contains("regel 50", tail);
        Assert.DoesNotContain("regel 48", tail);
        var pwd = await Call("run_command", new JsonObject { ["command"] = "(Get-Location).Path" });
        Assert.Contains(WorkspaceTools.Workspace!, pwd);
        var slow = await Call("run_command", new JsonObject { ["command"] = "Start-Sleep 5", ["timeout"] = 1 });
        Assert.Contains("timeout", slow);
    }

    /// <summary>Each conversation has its own folder: what one chat writes, the other does not see.</summary>
    [Fact]
    public async Task EachChatHasItsOwnFolder()
    {
        var other = NewFolder("other");
        WorkspaceTools.Current = dir;
        await Call("write_file", new JsonObject { ["path"] = "a.txt", ["content"] = "chat A" });
        WorkspaceTools.Current = other;
        var list = await Call("list_dir", new JsonObject { ["path"] = "." });
        Assert.DoesNotContain("a.txt", list);
        Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
        // a chat without a folder has no file tools
        WorkspaceTools.Current = "";
        var none = await Call("list_dir", new JsonObject { ["path"] = "." });
        Assert.Contains("No workspace", none);
    }

    /// <summary>Each workspace tool falls under one right: read, write or run.</summary>
    [Fact]
    public void ToolsMapToRights()
    {
        Assert.Equal(WorkspaceRight.Read, WorkspaceTools.RightFor("list_dir"));
        Assert.Equal(WorkspaceRight.Read, WorkspaceTools.RightFor("read_file"));
        Assert.Equal(WorkspaceRight.Write, WorkspaceTools.RightFor("write_file"));
        Assert.Equal(WorkspaceRight.Write, WorkspaceTools.RightFor("edit_file"));
        Assert.Equal(WorkspaceRight.Run, WorkspaceTools.RightFor("run_command"));
        foreach (var language in new[] { "nl", "en", "de", "fr" })
        {
            Settings.Shared.Language = language;
            Assert.Equal(3, Enum.GetValues<WorkspaceRight>().Select(r => r.Letter()).Distinct().Count());
        }
        Settings.Shared.Language = "nl";
        Assert.Equal(new[] { "L", "S", "U" }, Enum.GetValues<WorkspaceRight>().Select(r => r.Letter()));
    }

    [Fact]
    public void DefaultRightsAreReadAllowedTheRestAsked()
    {
        Assert.Equal("allow", Settings.Shared.Right(WorkspaceRight.Read));
        Assert.Equal("ask", Settings.Shared.Right(WorkspaceRight.Write));
        Assert.Equal("ask", Settings.Shared.Right(WorkspaceRight.Run));
    }

    [Fact]
    public void ToolSchemasAreExact()
    {
        var tools = WorkspaceTools.Definitions;
        Assert.Equal(new[] { "list_dir", "read_file", "write_file", "edit_file", "run_command" }, tools.Select(t => t!["function"]!["name"]!.GetValue<string>()));
        var read = tools[1]!["function"]!["parameters"]!;
        Assert.Equal("object", read["type"]!.GetValue<string>());
        Assert.Equal("integer", read["properties"]!["tail"]!["type"]!.GetValue<string>());
        Assert.Equal("path", read["required"]![0]!.GetValue<string>());
        Assert.Equal("base64", tools[2]!["function"]!["parameters"]!["properties"]!["encoding"]!["enum"]![1]!.GetValue<string>());
        var run = tools[4]!["function"]!["description"]!.GetValue<string>();
        Assert.Contains("PowerShell", run);
        Assert.Contains("Start-Process", run);
        Assert.DoesNotContain("zsh", run);
        Assert.Contains("max 1800", tools[4]!["function"]!["parameters"]!["properties"]!["timeout"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Limits()
    {
        // list_dir shows 500
        for (int i = 0; i < 505; i++) File.WriteAllText(Path.Combine(dir, $"f{i:000}.txt"), "");
        var list = await Call("list_dir", new JsonObject { ["path"] = "." });
        Assert.Equal(501, list.Split('\n').Length);
        Assert.Contains("0 bytes  20", list);

        // read_file: 2 MB only with offset or tail
        var big = Path.Combine(dir, "big.log");
        File.WriteAllText(big, string.Concat(Enumerable.Range(1, 300_000).Select(i => $"regel {i}\n")));
        var whole = await Call("read_file", new JsonObject { ["path"] = "big.log" });
        Assert.Equal("Error: big.log is larger than 2 MB; read it with offset/limit or tail.", whole);
        var end = await Call("read_file", new JsonObject { ["path"] = "big.log", ["tail"] = 3 });
        Assert.Contains("regel 300000", end);
        var start = await Call("read_file", new JsonObject { ["path"] = "big.log", ["offset"] = 2, ["limit"] = 2 });
        Assert.Equal("2\tregel 2\n3\tregel 3", start);

        // 40 000 characters of output
        File.WriteAllText(Path.Combine(dir, "long.txt"), new string('x', 50_000));
        var longOne = await Call("read_file", new JsonObject { ["path"] = "long.txt" });
        Assert.EndsWith("\n… (truncated; use offset/limit)", longOne);
        Assert.Equal(40_000 + "\n… (truncated; use offset/limit)".Length, longOne.Length);

        // a default of 400 lines with only an offset
        File.WriteAllText(Path.Combine(dir, "lines.txt"), string.Concat(Enumerable.Range(1, 1000).Select(i => $"{i}\n")));
        var part = await Call("read_file", new JsonObject { ["path"] = "lines.txt", ["offset"] = 10 });
        Assert.Equal(400, part.Split('\n').Length);
        Assert.StartsWith("10\t10", part);

        // a missing file, exact
        Assert.Equal("Error: nope.txt does not exist.", await Call("read_file", new JsonObject { ["path"] = "nope.txt" }));
        Assert.Equal("Error: nope does not exist.", await Call("list_dir", new JsonObject { ["path"] = "nope" }));
        Assert.Equal("Unknown tool iets", await Call("iets", new JsonObject()));
        Assert.Contains("binary file (9 bytes)", await WriteAndRead("met-nul.bin", [1, 2, 0, 4, 5, 6, 7, 8, 9]));
    }

    private async Task<string> WriteAndRead(string name, byte[] data)
    {
        File.WriteAllBytes(Path.Combine(dir, name), data);
        return await Call("read_file", new JsonObject { ["path"] = name });
    }

    /// <summary>Windows PowerShell writes UTF-16: that is a text file, not a binary one.</summary>
    [Fact]
    public async Task ReadsTextWithAByteOrderMark()
    {
        Assert.Equal("1\thallo\n2\twereld", await WriteAndRead("u16.txt", [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("hallo\r\nwereld")]));
        Assert.Equal("1\théllo", await WriteAndRead("u8.txt", [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("héllo")]));
    }

    [Fact]
    public async Task EditKeepsWindowsLineEnds()
    {
        File.WriteAllText(Path.Combine(dir, "w.txt"), "een\r\ntwee\r\ndrie\r\n");
        var e = await Call("edit_file", new JsonObject { ["path"] = "w.txt", ["old_text"] = "een\ntwee", ["new_text"] = "1\n2" });
        Assert.Contains("Edited", e);
        Assert.Equal("1\r\n2\r\ndrie\r\n", File.ReadAllText(Path.Combine(dir, "w.txt")));
    }

    [Fact]
    public void ApprovalTexts()
    {
        var write = WorkspaceTools.ApprovalText(new ToolCall("t", "write_file", new JsonObject { ["path"] = "a", ["content"] = "AAEC", ["encoding"] = "base64" }.ToJsonString()));
        Assert.Equal("Bestand schrijven: a", write.Title);
        Assert.Equal("Binair bestand, 3 bytes", write.Detail);
        var edit = WorkspaceTools.ApprovalText(new ToolCall("t", "edit_file", new JsonObject { ["path"] = "a", ["old_text"] = "x", ["new_text"] = "y" }.ToJsonString()));
        Assert.Equal("− x\n+ y", edit.Detail);
        var run = WorkspaceTools.ApprovalText(new ToolCall("t", "run_command", new JsonObject { ["command"] = "dir" }.ToJsonString()));
        Assert.Equal("Commando uitvoeren in de werkmap", run.Title);
        Assert.Equal("dir", run.Detail);
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}

/// <summary>The sandbox on Windows: every way a path can lead out of the folder, or mean something else than it looks.</summary>
public class SandboxTests : ChatTest
{
    private readonly string dir;
    private readonly string outside;
    private readonly ITestOutputHelper log;

    public SandboxTests(ITestOutputHelper output)
    {
        log = output;
        dir = NewFolder("work");
        outside = NewFolder("buiten");
        File.WriteAllText(Path.Combine(outside, "geheim.txt"), "geheim");
        Settings.Shared.Workspace = dir;
    }

    private static void Refused(string path)
    {
        var e = Assert.Throws<ToolError>(() => WorkspaceTools.Resolve(path));
        Assert.DoesNotContain("exception", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"sub\..\..\escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData(@"sub/..\../escape.txt")]
    [InlineData(@".. \escape.txt")]
    [InlineData(@"..\buiten\geheim.txt")]
    [InlineData(@"nieuw\deel\..\..\..\escape.txt")]
    public void DotDot(string path) => Refused(path);

    [Theory]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"c:\windows\WIN.INI")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData("/Windows/win.ini")]
    [InlineData(@"C:foo.txt")]
    [InlineData("C:")]
    [InlineData(@"Q:\bestaat\niet.txt")]
    public void AbsolutePathsAndOtherDrives(string path) => Refused(path);

    [Fact]
    public void AnAbsolutePathInsideIsFine()
    {
        var root = WorkspaceTools.Workspace!;
        Assert.Equal(Path.Combine(root, "a.txt"), WorkspaceTools.Resolve(Path.Combine(root, "a.txt")));
        Refused(Path.Combine(outside, "geheim.txt"));
    }

    [Fact]
    public void AnOtherDriveThanTheFolder()
    {
        var other = char.ToUpperInvariant(WorkspaceTools.Workspace![0]) == 'C' ? @"D:\x.txt" : @"C:\x.txt";
        Refused(other);
    }

    /// <summary>The same folder in other capitals is the same folder; a folder that only starts the same is another one.</summary>
    [Fact]
    public void CapitalsDoNotMatter()
    {
        var root = WorkspaceTools.Workspace!;
        File.WriteAllText(Path.Combine(root, "Naam.txt"), "x");
        var resolved = WorkspaceTools.Resolve(Path.Combine(root.ToUpperInvariant(), "NAAM.TXT"));
        Assert.Equal(Path.Combine(root, "Naam.txt"), resolved);
        Assert.Equal(resolved, WorkspaceTools.Resolve("naam.TXT"));
        Directory.CreateDirectory(root + "-twee");
        Refused(Path.Combine(root + "-twee", "x.txt"));
    }

    [Theory]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData("//server/share/x.txt")]
    [InlineData(@"\\server")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData("//?/C:/Windows/win.ini")]
    [InlineData(@"\\.\C:\Windows\win.ini")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\UNC\server\share\x.txt")]
    [InlineData(@"\??\C:\Windows\win.ini")]
    [InlineData(@"\\localhost\C$\Windows\win.ini")]
    public void UncAndDevicePaths(string path) => Refused(path);

    /// <summary>Also the path of the folder itself, written as a device path, goes through nothing.</summary>
    [Fact]
    public void DeviceFormOfTheFolderItself() => Refused(@"\\?\" + Path.Combine(WorkspaceTools.Workspace!, "a.txt"));

    [Theory]
    [InlineData("bestand.txt:stroom")]
    [InlineData("bestand.txt::$DATA")]
    [InlineData("bestand.txt:stroom:$DATA")]
    [InlineData(@"sub\a:b\c.txt")]
    [InlineData("map:x/y.txt")]
    public async Task AlternateDataStreams(string path)
    {
        File.WriteAllText(Path.Combine(dir, "bestand.txt"), "x");
        var e = Assert.Throws<ToolError>(() => WorkspaceTools.Resolve(path));
        Assert.Contains("alternate data streams", e.Message);
        var o = await WorkspaceToolsTests.Call("write_file", new JsonObject { ["path"] = path, ["content"] = "verborgen" });
        Assert.StartsWith("Error:", o);
        Assert.Equal("x", File.ReadAllText(Path.Combine(dir, "bestand.txt")));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("Nul.tar.gz")]
    [InlineData("AUX")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("COM¹")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData(@"sub\NUL\x.txt")]
    [InlineData("sub/con/x.txt")]
    [InlineData("CON ")]
    [InlineData("NUL.")]
    public async Task ReservedNames(string path)
    {
        var e = Assert.Throws<ToolError>(() => WorkspaceTools.Resolve(path));
        Assert.Contains("device names", e.Message);
        var o = await WorkspaceToolsTests.Call("write_file", new JsonObject { ["path"] = path, ["content"] = "x" });
        Assert.StartsWith("Error:", o);
    }

    [Theory]
    [InlineData("console.txt")]
    [InlineData("nullable.cs")]
    [InlineData("auxiliary")]
    [InlineData("com10.txt")]
    [InlineData("lpt.txt")]
    public void NamesThatOnlyLookLikeADevice(string path) => Assert.EndsWith(path, WorkspaceTools.Resolve(path));

    [Fact]
    public void SlashesOfBothKinds()
    {
        var root = WorkspaceTools.Workspace!;
        Assert.Equal(Path.Combine(root, "a", "b", "c.txt"), WorkspaceTools.Resolve(@"a/b\c.txt"));
        Assert.Equal(root, WorkspaceTools.Resolve("."));
        Assert.Equal(root, WorkspaceTools.Resolve(""));
        Assert.Equal(root, WorkspaceTools.Resolve("./"));
        Assert.Equal(Path.Combine(root, "c.txt"), WorkspaceTools.Resolve(@"a\..\c.txt"));
    }

    [Theory]
    [InlineData("~")]
    [InlineData("~/geheim.txt")]
    [InlineData(@"~\Documents\geheim.txt")]
    public void Tilde(string path) => Refused(path);

    /// <summary>Only ~ alone is the home folder; Word's lock files start with a tilde too.</summary>
    [Fact]
    public void ATildeInAName() => Assert.EndsWith("~$rapport.docx", WorkspaceTools.Resolve("~$rapport.docx"));

    [Fact]
    public async Task JunctionsLeadingOut()
    {
        Junction(Path.Combine(dir, "uit"), outside);
        Refused(@"uit\geheim.txt");
        Refused("uit/nieuw.txt");
        Refused("uit");
        Refused(@"uit\nieuw\diep\x.txt");
        var o = await WorkspaceToolsTests.Call("read_file", new JsonObject { ["path"] = @"uit\geheim.txt" });
        Assert.StartsWith("Error:", o);
        var w = await WorkspaceToolsTests.Call("write_file", new JsonObject { ["path"] = @"uit\nieuw.txt", ["content"] = "x" });
        Assert.StartsWith("Error:", w);
        Assert.False(File.Exists(Path.Combine(outside, "nieuw.txt")));
        // junction in a folder that is itself reached through a junction
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        Junction(Path.Combine(dir, "sub", "weer"), Path.Combine(dir, "uit"));
        Refused(@"sub\weer\geheim.txt");
    }

    [Fact]
    public void AJunctionInsideIsFine()
    {
        var real = Path.Combine(dir, "echt");
        Directory.CreateDirectory(real);
        Junction(Path.Combine(dir, "kort"), real);
        Assert.Equal(Path.Combine(WorkspaceTools.Workspace!, "echt", "x.txt"), WorkspaceTools.Resolve(@"kort\x.txt"));
    }

    /// <summary>The folder itself may be reached through a link: inside is measured from its real place.</summary>
    [Fact]
    public async Task AFolderReachedThroughAJunction()
    {
        var alias = Path.Combine(Root, "alias");
        Junction(alias, dir);
        WorkspaceTools.Current = alias;
        Assert.Equal(Path.Combine(WorkspaceTools.Workspace!, "a.txt"), WorkspaceTools.Resolve("a.txt"));
        Assert.Equal(Path.Combine(WorkspaceTools.Workspace!, "a.txt"), WorkspaceTools.Resolve(Path.Combine(alias, "a.txt")));
        var w = await WorkspaceToolsTests.Call("write_file", new JsonObject { ["path"] = "a.txt", ["content"] = "x" });
        Assert.Equal("Wrote 1 bytes to a.txt", w);
        Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
    }

    [Fact]
    public async Task SymbolicLinks()
    {
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "bestand"), Path.Combine(outside, "geheim.txt"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("NOT TESTED: symbolic links need Developer Mode or elevation here: " + e.Message);
            return;
        }
        Directory.CreateSymbolicLink(Path.Combine(dir, "map"), outside);
        Refused("bestand");
        Refused(@"map\geheim.txt");
        Refused(@"map\nieuw.txt");
        // a link to something that does not exist yet would write outside
        File.CreateSymbolicLink(Path.Combine(dir, "los"), Path.Combine(outside, "komt-nog.txt"));
        Refused("los");
        var w = await WorkspaceToolsTests.Call("write_file", new JsonObject { ["path"] = "los", ["content"] = "x" });
        Assert.StartsWith("Error:", w);
        Assert.False(File.Exists(Path.Combine(outside, "komt-nog.txt")));
    }

    /// <summary>The short name leads to the same place: it is judged after it is resolved.</summary>
    [Fact]
    public void ShortNames()
    {
        var longOutside = NewFolder("een lange mapnaam buiten");
        var shortOutside = ShortName(longOutside);
        var longInside = Path.Combine(dir, "een lange mapnaam");
        Directory.CreateDirectory(longInside);
        File.WriteAllText(Path.Combine(longInside, "een lang bestand.txt"), "x");
        var shortInside = ShortName(longInside);
        if (shortOutside == longOutside || shortInside == longInside)
        {
            log.WriteLine("NOT TESTED: this volume does not generate 8.3 names");
            return;
        }
        log.WriteLine(shortInside);
        Refused(shortOutside);
        Refused(Path.Combine(shortOutside, "x.txt"));
        // a short name in the folder resolves to its long name
        var name = Path.GetFileName(shortInside);
        Assert.Contains('~', name);
        Assert.Equal(Path.Combine(WorkspaceTools.Workspace!, "een lange mapnaam"), WorkspaceTools.Resolve(name));
        Assert.Equal(Path.Combine(WorkspaceTools.Workspace!, "een lange mapnaam", "een lang bestand.txt"), WorkspaceTools.Resolve(name + @"\een lang bestand.txt"));
        // a short path to the folder itself is the folder
        Assert.Equal(Path.Combine(WorkspaceTools.Workspace!, "een lange mapnaam"), WorkspaceTools.Resolve(shortInside));
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static string ShortName(string path)
    {
        var buffer = new StringBuilder(1024);
        return GetShortPathName(path, buffer, buffer.Capacity) == 0 ? path : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int size);
}

/// <summary>run_command: the shell, its output and its end, on both PowerShells when both are here.</summary>
public class RunCommandTests : ChatTest
{
    private readonly string dir;

    public RunCommandTests()
    {
        dir = NewFolder("work");
        Settings.Shared.Workspace = dir;
    }

    /// <summary>The shell the app chooses, Windows PowerShell, and a pwsh from the Store when there is one: it can run
    /// commands, though the app does not choose it, so the tests of a whole tree stopping only use the first two.</summary>
    public static IEnumerable<object[]> Shells()
    {
        yield return [""];
        yield return [CommandRunner.WindowsPowerShell];
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe");
        if (File.Exists(store)) yield return [store];
    }

    public static IEnumerable<object[]> ChosenShells()
    {
        yield return [""];
        yield return [CommandRunner.WindowsPowerShell];
    }

    private void Use(string shell) => CommandRunner.ShellOverride = shell.Length == 0 ? null : shell;

    private static Task<string> Run(string command, int? timeout = null, CancellationToken ct = default)
    {
        var a = new JsonObject { ["command"] = command };
        if (timeout != null) a["timeout"] = timeout;
        return WorkspaceToolsTests.Call("run_command", a, ct);
    }

    [Fact]
    public void ChoosesPwshIfPresentElseFiveOne()
    {
        var shell = Path.GetFileName(CommandRunner.Shell).ToLowerInvariant();
        Assert.Contains(shell, new[] { "pwsh.exe", "powershell.exe" });
        Assert.DoesNotContain("WindowsApps", CommandRunner.Shell);
        Assert.True(File.Exists(CommandRunner.WindowsPowerShell));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task OutputAndExitCodes(string shell)
    {
        Use(shell);
        Assert.Contains("exit code 0", await Run("Write-Output 'hallo'"));
        Assert.Contains("exit code 3", await Run("exit 3"));
        Assert.Contains("exit code 7", await Run("cmd /c exit 7"));
        Assert.Contains("exit code 1", await Run("Write-Error 'boem'"));
        Assert.Equal("(no output)\n[exit code 0]", await Run("$null = 1"));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task Utf8InAndOut(string shell)
    {
        Use(shell);
        var o = await Run("'é€ 日本語 ✓'");
        Assert.Contains("é€ 日本語 ✓", o);
        var native = await Run("cmd /c echo plain");
        Assert.Contains("plain", native);
        // a redirect writes UTF-8, also on 5.1, and read_file reads it
        await Run("'é€' > uit.txt");
        Assert.Contains("é€", await WorkspaceToolsTests.Call("read_file", new JsonObject { ["path"] = "uit.txt" }));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task ErrorsArePlainText(string shell)
    {
        Use(shell);
        var o = await Run("Write-Error 'boem'; Get-Item nergens-te-vinden");
        Assert.DoesNotContain("CLIXML", o);
        Assert.Contains("boem", o);
    }

    [Fact]
    public async Task LongCommandsAndOutputs()
    {
        // far over the 32 767 characters of a command line
        var text = new string('a', 40_000);
        var o = await Run($"$x = '{text}'; $x.Length");
        Assert.Contains("40000", o);

        var big = await Run("'b' * 20000; 'c' * 20000");
        Assert.Contains("… (", big);
        Assert.Contains("characters cut) …", big);
        Assert.StartsWith(new string('b', 1000), big);
        Assert.Contains(new string('c', 1000), big);
        Assert.Contains("exit code 0", big);
        Assert.True(big.Length < 24_200, big.Length.ToString());
    }

    [Fact]
    public async Task TimeoutIsClampedToOneTo1800()
    {
        var sw = Stopwatch.StartNew();
        Assert.Contains("stopped after 1 s (timeout)", await Run("Start-Sleep 30", 0));
        Assert.True(sw.Elapsed.TotalSeconds < 15, sw.Elapsed.ToString());
        Assert.Contains("stopped after 1 s (timeout)", await Run("Start-Sleep 30", -5));
    }

    /// <summary>The time-out stops the whole tree, not only the shell.</summary>
    [Theory]
    [MemberData(nameof(ChosenShells))]
    public async Task TimeoutStopsTheWholeTree(string shell)
    {
        Use(shell);
        var o = await Run("$p = Start-Process ping.exe -ArgumentList '-n','60','127.0.0.1' -NoNewWindow -PassThru -RedirectStandardOutput kind.txt; $p.Id > pid.txt; Start-Sleep 60", 4);
        Assert.Contains("stopped after 4 s (timeout)", o);
        var pid = int.Parse(File.ReadAllLines(Path.Combine(dir, "pid.txt")).First(l => l.Trim().Length > 0).Trim());
        await Until(() => Gone(pid));
    }

    /// <summary>A program left running holds the pipe, and output reading and the result do not wait for it (the Mac did).</summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task AProgramHoldingThePipeBlocksNothing(string shell)
    {
        Use(shell);
        var sw = Stopwatch.StartNew();
        var o = await Run("$p = Start-Process ping.exe -ArgumentList '-n','12','127.0.0.1' -NoNewWindow -PassThru; $p.Id > pid.txt; Write-Output 'klaar'", 60);
        sw.Stop();
        Assert.Contains("klaar", o);
        Assert.Contains("exit code 0", o);
        Assert.True(sw.Elapsed.TotalSeconds < 9, $"waited {sw.Elapsed}: {o}");
        var pid = int.Parse(File.ReadAllLines(Path.Combine(dir, "pid.txt")).First(l => l.Trim().Length > 0).Trim());
        Assert.False(Gone(pid), "the program goes on after the command");
        Process.GetProcessById(pid).Kill();
    }

    /// <summary>The time-out also works with a child that holds the pipe while the shell waits.</summary>
    [Fact]
    public async Task TimeoutWorksWhileAChildHoldsThePipe()
    {
        var sw = Stopwatch.StartNew();
        var o = await Run("Start-Process ping.exe -ArgumentList '-n','90','127.0.0.1' -NoNewWindow -Wait", 2);
        Assert.Contains("stopped after 2 s (timeout)", o);
        Assert.True(sw.Elapsed.TotalSeconds < 12, sw.Elapsed.ToString());
    }

    /// <summary>A detached program survives the command that started it.</summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public async Task DetachedStartProcessSurvives(string shell)
    {
        Use(shell);
        var after = Path.Combine(dir, "daarna.txt");
        var child = $"Start-Sleep 8; Set-Content -LiteralPath '{after}' -Value ok";
        var sw = Stopwatch.StartNew();
        var o = await Run($"Start-Process '{CommandRunner.WindowsPowerShell}' -WindowStyle Hidden -ArgumentList '-NoProfile','-Command',\"{child}\"", 60);
        Assert.Contains("exit code 0", o);
        Assert.True(sw.Elapsed.TotalSeconds < 7.5, sw.Elapsed.ToString());
        Assert.False(File.Exists(after));
        await Until(() => File.Exists(after), 30);
    }

    /// <summary>Stop stops the running command and its tree.</summary>
    [Fact]
    public async Task StopStopsTheTree()
    {
        using var cts = new CancellationTokenSource();
        var running = Run("$p = Start-Process ping.exe -ArgumentList '-n','60','127.0.0.1' -NoNewWindow -PassThru -RedirectStandardOutput kind.txt; $p.Id > pid.txt; Start-Sleep 60", 120, cts.Token);
        var pidFile = Path.Combine(dir, "pid.txt");
        int pid = 0;
        await Until(() => TryReadPid(pidFile, out pid), 30);
        Assert.False(Gone(pid));
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        var o = await running;
        Assert.Contains("stopped by the user", o);
        Assert.True(sw.Elapsed.TotalSeconds < 6, sw.Elapsed.ToString());
        await Until(() => Gone(pid));
    }

    [Fact]
    public async Task AKillOnCloseJobStopsItsProcesses()
    {
        var job = new KillOnCloseJob();
        using var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        job.Assign(p.Handle);
        Assert.False(p.HasExited);
        job.Dispose();
        await Until(() => p.HasExited);
    }

    [Fact]
    public async Task AReleasedJobLeavesItsProcessesAlone()
    {
        var job = new KillOnCloseJob();
        using var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        job.Assign(p.Handle);
        job.Release();
        job.Dispose();
        await Task.Delay(500);
        Assert.False(p.HasExited);
        p.Kill();
    }

    /// <summary>The shell may still be writing it.</summary>
    private static bool TryReadPid(string file, out int pid)
    {
        pid = 0;
        try
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return int.TryParse(reader.ReadToEnd().Trim(), out pid);
        }
        catch (IOException) { return false; }
    }

    private static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
    }
}
