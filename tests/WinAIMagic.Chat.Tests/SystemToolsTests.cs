using System.Text.Json.Nodes;
using Microsoft.Win32;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

public class SkillsTests : ChatTest
{
    [Fact]
    public void ParseFrontMatter()
    {
        var s = Skills.Parse("---\r\nname: taakbalk\r\ndescription: \"Taakbalk\"\r\nplatform: windows\r\n---\r\n\r\nGebruik het register.\r\n", "x");
        Assert.Equal(new Skill("taakbalk", "Taakbalk", "windows", "Gebruik het register."), s);
        // no front matter: the folder's name, for every platform
        var plain = Skills.Parse("Just text", "folder")!;
        Assert.Equal("folder", plain.Name);
        Assert.Equal("all", plain.Platform);
        Assert.Null(Skills.Parse("---\nname: empty\n---\n", "x"));
    }

    [Fact]
    public void UserSkillReplacesBuiltInAndPlatformFilters()
    {
        var builtIn = new[] { ("dock", "---\nname: dock\ndescription: built in\nplatform: windows\n---\nA"), ("mac", "---\nname: mac\nplatform: macos\n---\nB") };
        var user = NewFolder("skills");
        Directory.CreateDirectory(Path.Combine(user, "mine"));
        File.WriteAllText(Path.Combine(user, "mine", "SKILL.md"), "---\nname: Dock\ndescription: mine\n---\nC");
        var skills = Skills.Load([builtIn, Skills.FromFolder(user)]);
        Assert.Equal(["mine"], skills.Select(s => s.Description));
        Assert.Contains("C", Skills.Run(new ToolCall("1", "use_skill", """{"name":"dock"}"""), skills));
        Assert.Contains("no skill", Skills.Run(new ToolCall("1", "use_skill", """{"name":"nope"}"""), skills));
    }

    /// <summary>The eight built-in skills, for Windows, and the read commands they give pass system_read.</summary>
    [Fact]
    public void BuiltInSkillsLoad()
    {
        var skills = Skills.Load([Skills.BuiltIn()]);
        Assert.True(skills.Count >= 8, string.Join(", ", skills.Select(s => s.Name)));
        Assert.All(skills, s => Assert.NotEmpty(s.Description));
        foreach (var name in new[] { "uiterlijk", "energie", "netwerk", "software", "opstartitems", "opslag", "privacy-beveiliging", "diagnose" })
            Assert.Contains(skills, s => s.Name == name);
        Assert.DoesNotContain(skills, s => s.Platform == "macos");
        foreach (var c in new[] { @"Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'", "powercfg /query",
                     "Get-DnsClientServerAddress", "Get-NetFirewallProfile", "Get-Process", "Get-PSDrive -PSProvider FileSystem",
                     "Get-CimInstance Win32_StartupCommand", "Get-WinEvent -LogName System -MaxEvents 50", "winget search vlc", "winget upgrade" })
            Assert.True(SystemTools.IsReadOnly(c), c);
        // every command a skill lists for reading (a list item "- `…`" or a table's check column) passes system_read,
        // with its placeholders filled in; one the skill itself says does not work there is left out
        foreach (var s in skills)
            foreach (var line in s.Body.Split('\n'))
            {
                var t = line.Trim();
                string? command = null;
                if (t.StartsWith("- `", StringComparison.Ordinal)) command = t[3..t.IndexOf('`', 3)];
                else if (t.StartsWith('|') && t.Split('|') is { Length: > 3 } cells && cells[2].Trim() is { } check && check.StartsWith('`'))
                    command = check.Trim('`').Split('`')[0];
                if (command == null || t.Contains("werkt niet")) continue;
                command = System.Text.RegularExpressions.Regex.Replace(command, "<[^>]+>", "x");
                Assert.True(SystemTools.IsReadOnly(command), $"{s.Name}: {command}");
            }
    }

    /// <summary>Only the names go in the prompt, sorted; off means the same prompt and tools as before.</summary>
    [Fact]
    public void SkillsGoInThePromptOnlyWhenOn()
    {
        var chat = new ChatClient();
        var off = chat.RequestMessages([])[0]!["content"]!.GetValue<string>();
        Assert.DoesNotContain("use_skill", off);
        Assert.Empty(ChatClient.CurrentTools());
        Settings.Shared.Skills = true;
        var on = chat.RequestMessages([])[0]!["content"]!.GetValue<string>();
        Assert.Contains("- diagnose:", on);
        Assert.Contains(ChatClient.CurrentTools(), t => t!["function"]!["name"]!.GetValue<string>() == "use_skill");
        Settings.Shared.SystemManagement = true;
        var names = ChatClient.CurrentTools().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["use_skill", "system_read", "system_change", "system_undo"], names);
    }
}

public class SystemToolsTests : ChatTest
{
    private const string TestKey = @"HKCU\Software\WinAIMagicTest";

    public override void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\WinAIMagicTest", false);
        base.Dispose();
    }

    [Fact]
    public void ReadOnlyAllowlist()
    {
        foreach (var c in new[] { "systeminfo", "Get-PSDrive C", @"Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Desktop' -Name WallPaper", "Get-NetIPConfiguration",
                     "Get-MpComputerStatus", "winget list", "winget upgrade --include-unknown", "powercfg /getactivescheme", "ipconfig /all",
                     @"reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "netsh advfirewall show allprofiles", "ping -n 3 1.1.1.1",
                     "manage-bde -status", "C:\\Windows\\System32\\tasklist.exe", "sc query wuauserv", "Test-NetConnection 1.1.1.1 -Port 443" })
            Assert.True(SystemTools.IsReadOnly(c), c);
        foreach (var c in new[] { @"Set-ItemProperty -Path 'HKCU:\x' -Name a -Value 1", "powercfg /change monitor-timeout-ac 0", "systeminfo; Remove-Item ~ -Recurse",
                     "Get-Process | Stop-Process", "Get-Content $(whoami)", "Get-Content `whoami`", "systeminfo > out.txt", "Set-DnsClientServerAddress -InterfaceAlias Wi-Fi -ServerAddresses 1.1.1.1",
                     "ipconfig /release", "winget install vlc", "winget upgrade --all", "winget upgrade --id VLC.VLC", "Remove-Item file", "reg add HKCU\\x /v a /d 1 /f",
                     "sc stop wuauserv", "Get-Process && Remove-Item x", "Get-Content 'unclosed", "netsh advfirewall set allprofiles state off", "Get-Credential",
                     "Invoke-Expression 'x'", "iex x", "powercfg /batteryreport /output C:\\Windows\\x.html", "Get-ChildItem @args", "Get-Item {x}", "[IO.File]::Delete('x')",
                     "bcdedit /set testsigning on", "Get-Process -Name \"$env:USERNAME\"" })
            Assert.False(SystemTools.IsReadOnly(c), c);
    }

    [Fact]
    public void ChecksAsModelsWriteThem()
    {
        Assert.Equal(@"Get-ItemPropertyValue -Path 'HKCU:\x' -Name a", SystemTools.ReadableCheck(@"Get-ItemPropertyValue -Path 'HKCU:\x' -Name a 2>$null"));
        Assert.Equal(@"Get-ItemPropertyValue -Path 'HKCU:\x' -Name a", SystemTools.ReadableCheck(@"Get-ItemPropertyValue -Path 'HKCU:\x' -Name a -ErrorAction SilentlyContinue"));
        Assert.Equal(@"reg query HKCU\x /v a", SystemTools.ReadableCheck(@"reg query HKCU\x /v a 2>nul"));
        Assert.Equal("powercfg /query", SystemTools.ReadableCheck("powercfg /query 2>&1"));
        Assert.Equal(@"reg query HKCU\x /v a", SystemTools.ReadableCheck(@"reg query HKCU\x /v a || echo ""(bestaat nog niet)"""));
        Assert.Null(SystemTools.ReadableCheck(@"reg query HKCU\x || Remove-Item ~ -Recurse"));
        Assert.Null(SystemTools.ReadableCheck(@"reg query HKCU\x || echo ""$(whoami)"""));
        Assert.Null(SystemTools.ReadableCheck(@"Set-ItemProperty -Path HKCU:\x -Name a -Value 1 2>$null"));
    }

    private static SystemTools.Step Step(string apply, string undo = "x", bool admin = true) => new("t", "", apply, undo, admin);

    [Fact]
    public void Warnings()
    {
        Assert.Empty(SystemTools.Warnings([Step(@"Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl -Value 0")], false));
        Assert.Single(SystemTools.Warnings([Step("Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled False")], false));
        Assert.Single(SystemTools.Warnings([Step("Set-MpPreference -DisableRealtimeMonitoring $true")], false));
        Assert.Single(SystemTools.Warnings([Step(@"Set-ItemProperty -Path HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System -Name EnableLUA -Value 0")], false));
        Assert.Equal(2, SystemTools.Warnings([Step("Start-Process powershell -Verb RunAs -ArgumentList 'Restart-Computer'")], false).Count);
        Assert.Single(SystemTools.Warnings([Step(@"Remove-Item C:\Temp\x -Recurse -Force")], false));
        Assert.Empty(SystemTools.Warnings([Step("bcdedit /enum")], false));
        // a plan after reading the web, and a step without undo, are said too
        Assert.Equal(2, SystemTools.Warnings([Step("Stop-Process -Name explorer -Force", undo: "")], true).Count);
    }

    [Fact]
    public void PlanParsing()
    {
        var call = new ToolCall("1", "system_change", """
            {"summary":"Taakbalk links","steps":[{"title":"Taakbalk","check":"Get-ItemPropertyValue -Path 'HKCU:\\x' -Name TaskbarAl","apply":"Set-ItemProperty -Path 'HKCU:\\x' -Name TaskbarAl -Value 0","undo":"Set-ItemProperty -Path 'HKCU:\\x' -Name TaskbarAl -Value 1","admin":"false"},{"apply":"powercfg /change monitor-timeout-ac 30","undo":"powercfg /change monitor-timeout-ac 10","admin":true}]}
            """);
        var plan = SystemTools.Plan(call);
        Assert.Equal("Taakbalk links", plan.Summary);
        Assert.Equal(2, plan.Steps.Count);
        Assert.False(plan.Steps[0].Admin);
        Assert.True(plan.Steps[1].Admin);
        Assert.Equal("powercfg /change monitor-timeout-ac 30", plan.Steps[1].Title);
        Assert.Throws<SystemTools.PlanException>(() => SystemTools.Plan(new ToolCall("2", "system_change", """{"summary":"x","steps":[]}""")));
        // the steps as a JSON string
        var asString = new ToolCall("3", "system_change", """{"summary":"s","steps":"[{\"apply\":\"$true\",\"undo\":\"$true\"}]"}""");
        Assert.Single(SystemTools.Plan(asString).Steps);
    }

    /// <summary>The Mac's preference domains and killall are registry keys and Stop-Process here.</summary>
    [Fact]
    public void DomainsAndKillalls()
    {
        Assert.Equal([@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"HKCU\Control Panel\Desktop"],
            SystemTools.Keys(@"Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name HideFileExt -Value 0; reg add ""HKEY_CURRENT_USER\Control Panel\Desktop"" /v X /d 1 /f"));
        Assert.Equal([@"HKLM\SOFTWARE\X"], SystemTools.Keys(@"New-ItemProperty -LiteralPath Registry::HKEY_LOCAL_MACHINE\SOFTWARE\X -Name a -Value 1"));
        Assert.Empty(SystemTools.Keys("powercfg /change monitor-timeout-ac 30"));
        Assert.Equal(["explorer", "searchhost"], SystemTools.Restarts("Stop-Process -Name explorer -Force\ntaskkill /f /im SearchHost.exe"));
    }

    /// <summary>A real change on a test key: the value before, the change, the record, and undo back to the value before.</summary>
    [Fact]
    public async Task ApplyAndUndoRoundTrip()
    {
        var folder = NewFolder("changes");
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\WinAIMagicTest")) k.SetValue("color", "blue");
        var step = new SystemTools.Step("Colour", $"Get-ItemPropertyValue -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color",
            $"Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color -Value red; New-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name added -Value 1 | Out-Null",
            $"Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color -Value wrong", false);   // a wrong undo
        var change = await SystemTools.Apply("Make it red", [step], false, folder);
        Assert.True(change.Succeeded, change.Results[0].Output);
        Assert.Equal("blue", change.Results[0].Before);
        Assert.Equal("red", change.Results[0].After);
        Assert.True(change.Exports.ContainsKey(TestKey));
        Assert.Equal([change.Id], SystemTools.Load(folder).Select(c => c.Id));

        // the model's undo was wrong; the export made before the change puts the exact value back, and the added value goes
        var undone = await SystemTools.Undo(change, folder);
        Assert.NotNull(undone.Undone);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\WinAIMagicTest")!)
        {
            Assert.Equal("blue", k.GetValue("color"));
            Assert.Null(k.GetValue("added"));
        }
        Assert.Contains("back to", undone.UndoReport);
        Assert.NotNull(SystemTools.Load(folder)[0].Undone);
        // the record is the Mac's JSON
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, change.Id, "plan.json")))!.AsObject();
        foreach (var key in new[] { "id", "date", "summary", "steps", "results", "exports", "snapshot", "undone", "undoReport" }) Assert.True(json.ContainsKey(key), key);
        Assert.Equal("blue", json["results"]![0]!["before"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedStepStopsThePlan()
    {
        var folder = NewFolder("changes");
        SystemTools.Step[] steps = [new("fails", "", "exit 3", "$true", false), new("never", "", "$true", "$true", false)];
        var change = await SystemTools.Apply("x", steps, false, folder);
        Assert.False(change.Succeeded);
        Assert.True(change.Results[0].Ran);
        Assert.False(change.Results[1].Ran);
        Assert.Contains("not run", SystemTools.Report(change));
    }

    /// <summary>The script that runs after the UAC prompt, run here without it: each command gets its own output and status, and
    /// a failing one stops the rest.</summary>
    [Fact]
    public async Task AdminStepsShareOnePrompt()
    {
        SystemTools.ElevateForTests = false;
        try
        {
            var r = await SystemTools.RunAdmin(["Write-Output een", "Write-Error 'mis'", "Write-Output drie"]);
            Assert.Equal(("een", true), r[0]);
            Assert.False(r[1].Ok);
            Assert.Contains("mis", r[1].Output);
            Assert.False(r[2].Ok);
            Assert.StartsWith("Not run", r[2].Output);
            // a program's exit code counts
            Assert.False((await SystemTools.RunAdmin(["cmd /c exit 4"]))[0].Ok);
            Assert.True((await SystemTools.RunAdmin(["cmd /c exit 0", "Write-Output x"])).TrueForAll(x => x.Ok));

            var folder = NewFolder("changes");
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\WinAIMagicTest")) k.SetValue("n", 1);
            SystemTools.Step[] steps =
            [
                new("one", "", "Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name n -Value 2", "$true", true),
                new("two", "", "throw 'kapot'", "$true", true),
                new("three", "", "Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name n -Value 3", "$true", true),
            ];
            var change = await SystemTools.Apply("admin", steps, false, folder);
            Assert.True(change.Results[0].Ok);
            Assert.False(change.Results[1].Ok);
            Assert.Contains("kapot", change.Results[1].Output);
            Assert.False(change.Results[2].Ok);
            Assert.Equal(2, (int)Registry.CurrentUser.OpenSubKey(@"Software\WinAIMagicTest")!.GetValue("n")!);
        }
        finally { SystemTools.ElevateForTests = true; }
    }

    [Fact]
    public void AdminLogIsRead()
    {
        var r = SystemTools.ParseAdminLog("@@STEP 0\r\nok\r\n@@EXIT 0\r\n@@STEP 1\r\nfout\r\n@@EXIT 1\r\n@@END\r\n", 3, "niet");
        Assert.Equal([("ok", true), ("fout", false), ("niet", false)], r);
    }

    [Fact]
    public void ValueNamesOfAnExport()
    {
        var text = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\T]\r\n\"color\"=\"blue\"\r\n@=\"d\"\r\n\r\n[HKEY_CURRENT_USER\\Software\\T\\Sub]\r\n\"other\"=dword:00000001\r\n";
        Assert.Equal(new HashSet<string> { "color", "" }, SystemTools.ValueNames(text, @"HKCU\Software\T"));
    }
}

/// <summary>The tools in the conversation: rights, the approval card, and admin plans that always ask.</summary>
public class SystemLoopTests : ChatTest
{
    private readonly ConversationStore store = new();
    private Conversation C => store.Conversations[0];

    public SystemLoopTests()
    {
        Settings.Shared.SystemManagement = true;
        Settings.Shared.Skills = true;
    }

    public override void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\WinAIMagicTest", false);
        base.Dispose();
    }

    private static Func<HttpResponseMessage> Change(bool admin) => () => Call("system_change", new JsonObject
    {
        ["summary"] = "Kleur op groen",
        ["steps"] = new JsonArray(new JsonObject
        {
            ["title"] = "kleur op groen zetten",
            ["check"] = "Get-ItemPropertyValue -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color 2>$null",
            ["apply"] = "Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color -Value groen",
            ["undo"] = "Set-ItemProperty -Path 'HKCU:\\Software\\WinAIMagicTest' -Name color -Value blauw",
            ["admin"] = admin,
        }),
    }, "s");

    [Fact]
    public async Task ReadSkillAndChangeThroughTheCard()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\WinAIMagicTest")) k.SetValue("color", "blauw");
        var engine = new FakeEngine(
            () => Call("use_skill", new JsonObject { ["name"] = "uiterlijk" }, "k"),
            () => Call("system_read", new JsonObject { ["command"] = "hostname" }, "r"),
            Change(false),
            () => Say("De kleur is groen. Ik kan het terugzetten."));
        var chat = new ChatClient(engine.Client);
        var asked = new List<Approval>();
        chat.AskUser = (a, _) => { asked.Add(a); return Task.FromResult(new ApprovalAnswer(true)); };
        chat.Send("Maak de kleur groen", [], C.Id, store, Base);
        await chat.Completion;
        var card = Assert.Single(asked);
        Assert.Equal("Kleur op groen", card.Title);
        Assert.Equal("blauw", card.Steps.Single().Current);
        Assert.False(card.Admin);
        Assert.Equal("groen", Registry.CurrentUser.OpenSubKey(@"Software\WinAIMagicTest")!.GetValue("color"));
        var tools = C.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Text).ToList();
        Assert.StartsWith("# Skill: uiterlijk", tools[0]);
        Assert.Equal(Environment.MachineName, tools[1].Trim(), ignoreCase: true);
        Assert.Contains("system_undo id", tools[2]);
        Assert.Null(C.Messages[^1].Error);
        Assert.Equal(["Skill laden: uiterlijk", "Systeem lezen: hostname", "Systeemwijziging: Kleur op groen"], C.Messages[^1].Activities.Select(a => a.Label));

        // undo through the model, with the card that says each step is put back
        var id = SystemTools.Load().Single().Id;
        engine.Then(_ => Call("system_undo", new JsonObject { ["id"] = id }, "u"));
        engine.Then(_ => Say("Teruggezet."));
        chat.Send("Zet het terug", [], C.Id, store, Base);
        await chat.Completion;
        Assert.Equal("Terugzetten: kleur op groen zetten", asked[^1].Steps.Single().Title);
        Assert.Equal("blauw", Registry.CurrentUser.OpenSubKey(@"Software\WinAIMagicTest")!.GetValue("color"));
    }

    /// <summary>"Always in this conversation" covers system changes too, but an admin plan asks every time.</summary>
    [Fact]
    public async Task AdminAlwaysAsks()
    {
        var engine = new FakeEngine(Change(false), () => Say("Klaar."), Change(true), () => Say("Niet gedaan."));
        var chat = new ChatClient(engine.Client);
        var asked = new List<Approval>();
        chat.AskUser = (a, _) => { asked.Add(a); return Task.FromResult(a.Admin ? new ApprovalAnswer(false) : new ApprovalAnswer(true, true)); };
        chat.Send("een", [], C.Id, store, Base);
        await chat.Completion;
        chat.Send("twee", [], C.Id, store, Base);
        await chat.Completion;
        Assert.Equal(2, asked.Count);
        Assert.True(asked[1].Admin);
        Assert.Contains("did not approve", C.Messages.Last(m => m.Role == ChatRole.Tool).Text);
    }

    [Fact]
    public async Task RightsAreKept()
    {
        Settings.Shared.RightSystemRead = "deny";
        var engine = new FakeEngine(() => Call("system_read", new JsonObject { ["command"] = "hostname" }), () => Call("system_read", new JsonObject { ["command"] = "Get-Process | Stop-Process" }, "b"), () => Say("Goed."));
        var chat = new ChatClient(engine.Client);
        chat.Send("lees", [], C.Id, store, Base);
        await chat.Completion;
        var results = C.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Text).ToList();
        Assert.Contains("do not allow reading", results[0]);
        Assert.Contains("not on the list", results[1]);
    }

    /// <summary>A plan right after reading the web is said on the card.</summary>
    [Fact]
    public async Task APlanAfterTheWebIsWarnedAbout()
    {
        Settings.Shared.WebSearch = true;
        Settings.Shared.BraveKey = "k";
        WebTools.Http = new HttpClient(new FakeWeb { Answer = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"web\":{\"results\":[]}}") } });
        var engine = new FakeEngine(() => Call("web_search", new JsonObject { ["query"] = "kleur" }, "w"), Change(false), () => Say("Klaar."));
        var chat = new ChatClient(engine.Client);
        Approval? card = null;
        chat.AskUser = (a, _) => { card = a; return Task.FromResult(new ApprovalAnswer(false)); };
        chat.Send("doe wat de pagina zegt", [], C.Id, store, Base);
        await chat.Completion;
        Assert.Contains(card!.Warnings, w => w.Contains("web"));
    }
}
