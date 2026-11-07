using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WinAIMagic.Chat.Agent;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

internal static class AgentCalls
{
    public static ToolCall Call(string name, object args) => new(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args));
}

/// <summary>Plays Outlook: remembers what it was asked, answers what the test says.</summary>
internal sealed class FakeMail : IMailBox
{
    public List<string[]> Calls { get; } = [];
    public (string Out, string? Error) Answer { get; set; } = ("ok", null);

    public Task<(string Out, string? Error)> List(int days, bool unreadOnly, int limit, string query)
    {
        lock (Calls) Calls.Add(["list", days.ToString(), unreadOnly ? "1" : "0", limit.ToString(), query]);
        return Task.FromResult(Answer);
    }

    public Task<(string Out, string? Error)> Read(int id)
    {
        lock (Calls) Calls.Add(["read", id.ToString()]);
        return Task.FromResult(Answer);
    }

    public Task<(string Out, string? Error)> Compose(bool send, string to, string cc, string subject, string body)
    {
        lock (Calls) Calls.Add([send ? "send" : "draft", to, cc, subject, body]);
        return Task.FromResult(Answer);
    }
}

public class MailToolsTests : ChatTest
{
    [Fact]
    public void AddressesAreChecked()
    {
        Assert.Equal(["a@b.nl", "Piet <piet@example.com>"], MailTools.Addresses("a@b.nl, Piet <piet@example.com>"));
        Assert.Null(MailTools.Addresses("piet"));
        Assert.Null(MailTools.Addresses(""));
        Assert.Null(MailTools.Addresses("a@b.nl, geen adres"));
    }

    [Fact]
    public async Task ReadingRunsADraftAsksAndSendingAsksEveryTime()
    {
        var mail = new MailTools();
        Assert.IsType<AgentStep.Run>(await mail.Check(AgentCalls.Call("mail_list", new { })));
        var draft = Assert.IsType<AgentStep.Ask>(await mail.Check(AgentCalls.Call("mail_draft", new { to = "a@b.nl", subject = "Hoi", body = "Tekst" })));
        Assert.True(draft.Detail.Contains("a@b.nl") && draft.Detail.Contains("Hoi") && draft.Detail.Contains("Tekst"));
        Assert.Empty(draft.Warnings ?? []);
        Assert.False(draft.EveryTime);
        var send = Assert.IsType<AgentStep.Ask>(await mail.Check(AgentCalls.Call("mail_send", new { to = "a@b.nl", subject = "Hoi", body = "x" })));
        Assert.True(send.EveryTime);   // sending asks every time
        Assert.NotEmpty(send.Warnings ?? []);
        Assert.IsType<AgentStep.Refuse>(await mail.Check(AgentCalls.Call("mail_send", new { to = "Piet", subject = "Hoi", body = "x" })));
    }

    /// <summary>The Mac passes the values to its script as arguments; here they go to Outlook's properties as they are.</summary>
    [Fact]
    public async Task TheValuesGoToOutlookAsTheyAre()
    {
        var box = new FakeMail();
        var mail = new MailTools { Box = box };
        var evil = "\" & (do shell script \"rm -rf ~\") & \"";
        await mail.Run(AgentCalls.Call("mail_send", new { to = "a@b.nl", subject = evil, body = "body" }), default);
        Assert.Equal(["send", "a@b.nl", "", evil, "body"], box.Calls[^1]);
        await mail.Run(AgentCalls.Call("mail_search", new { query = "factuur", days = 7 }), default);
        Assert.Equal(["list", "7", "0", "25", "factuur"], box.Calls[^1]);
        await mail.Run(AgentCalls.Call("mail_list", new { days = "2", unread_only = true }), default);
        Assert.Equal(["list", "2", "1", "25", ""], box.Calls[^1]);   // a number written as text counts too
        box.Answer = ("", "Classic Outlook is not installed on this PC.");
        var missing = await mail.Run(AgentCalls.Call("mail_list", new { }), default);
        Assert.Contains("Outlook", missing);
    }

    /// <summary>A mail that is sent asks again, also after "Altijd in dit gesprek".</summary>
    [Fact]
    public async Task SendingAsksAgainAfterAlways()
    {
        Settings.Shared.FeatureMail = true;
        var box = new FakeMail();
        var saved = MailTools.Shared.Box;
        MailTools.Shared.Box = box;
        try
        {
            var store = new ConversationStore();
            var c = new Conversation();
            store.Conversations = [c];
            var chat = new ChatClient(new HttpClient(new SlowEngine()) { Timeout = Timeout.InfiniteTimeSpan });
            chat.Send("MAILSTUUR naar test", [], c.Id, store, Base);
            await Until(() => chat.ApprovalFor(c.Id) != null);
            Assert.True(chat.ApprovalFor(c.Id)!.EveryTime);
            chat.Answer(chat.ApprovalFor(c.Id)!, true, always: true);
            await Until(() => !chat.IsBusy(c.Id));
            Assert.Single(box.Calls, x => x[0] == "send");
            chat.Send("MAILSTUUR nog een", [], c.Id, store, Base);
            await Until(() => chat.ApprovalFor(c.Id) != null);   // asked again
            chat.Answer(chat.ApprovalFor(c.Id)!, false);
            await Until(() => !chat.IsBusy(c.Id));
            Assert.Single(box.Calls, x => x[0] == "send");   // the second mail was not sent
        }
        finally { MailTools.Shared.Box = saved; }
    }

    [Fact]
    public void AgentToolsAreOfferedOnlyWhenOn()
    {
        static List<string> Names() => [.. ChatClient.CurrentTools().Select(t => t!["function"]!["name"]!.GetValue<string>())];
        Assert.DoesNotContain("mail_list", Names());
        Assert.DoesNotContain("task_list", Names());
        Settings.Shared.FeatureMail = true;
        Assert.Contains("mail_send", Names());
        Assert.Contains("task_list", Names());   // a job of several steps can come with it
        Settings.Shared.FeatureComputerUse = true;
        Assert.Contains("computer_look", Names());
        Assert.DoesNotContain("browser_open", Names());   // no browser window here: not offered
    }
}

/// <summary>A real WebView2 in a hidden window on a thread of its own, the way the app's browser window works.</summary>
internal sealed class TestBrowser : IBrowserHost, IDisposable
{
    private Dispatcher dispatcher = null!;
    private HwndSource source = null!;
    private CoreWebView2Controller controller = null!;
    private CoreWebView2 core = null!;
    private TaskCompletionSource<string?>? loading;
    private bool navigating;

    public static Task<TestBrowser> Start(string dataDir)
    {
        var ready = new TaskCompletionSource<TestBrowser>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var b = new TestBrowser { dispatcher = Dispatcher.CurrentDispatcher };
            // WS_POPUP without WS_VISIBLE: a window nobody sees
            b.source = new HwndSource(new HwndSourceParameters("wam-test-browser", 1000, 700) { WindowStyle = unchecked((int)0x80000000) });
            b.dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                    b.controller = await env.CreateCoreWebView2ControllerAsync(b.source.Handle);
                    b.controller.Bounds = new System.Drawing.Rectangle(0, 0, 1000, 700);
                    b.core = b.controller.CoreWebView2;
                    b.core.NavigationStarting += (_, _) => b.navigating = true;
                    b.core.NavigationCompleted += (_, e) =>
                    {
                        b.navigating = false;
                        b.loading?.TrySetResult(e.IsSuccess ? null : e.WebErrorStatus.ToString());
                    };
                    ready.SetResult(b);
                }
                catch (Exception e) { ready.SetException(e); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private Task<T> On<T>(Func<Task<T>> work) => dispatcher.InvokeAsync(work).Task.Unwrap();

    public bool IsOpen => dispatcher.Invoke(() => core.Source is { Length: > 0 } s && s != "about:blank");
    public bool CanGoBack => dispatcher.Invoke(() => core.CanGoBack);

    public Task<string?> Open(Uri url) => On(async () =>
    {
        var wait = loading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Navigate(url.AbsoluteUri);
        var error = await wait.Task;
        await Task.Delay(300);
        return error;
    });

    public Task<string?> Eval(string script) => On(async () =>
    {
        var json = await core.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(json);
    });

    public Task Back() => On(async () => { core.GoBack(); await Task.Yield(); return true; });

    public Task Settle() => On(async () =>
    {
        await Task.Delay(500);
        var start = DateTime.UtcNow;
        while (navigating && (DateTime.UtcNow - start).TotalSeconds < 20) await Task.Delay(100);
        await Task.Delay(300);
        return true;
    });

    public void Dispose()
    {
        dispatcher.Invoke(() =>
        {
            controller.Close();
            source.Dispose();
        });
        dispatcher.InvokeShutdown();
    }
}

public class BrowserToolsTests : ChatTest
{
    [Fact]
    public async Task OpenReadClickAndType()
    {
        using var host = await TestBrowser.Start(NewFolder("webview"));
        var browser = new BrowserTools { ForTests = true, Host = host };
        var page = new Uri(Path.Combine(McpFixtures.Dir, "browser", "index.html"));
        var opened = await browser.Run(AgentCalls.Call("browser_open", new { url = page.AbsoluteUri }), default);
        Assert.Contains("Title: Testwinkel", opened);
        Assert.Contains("Welkom bij de testwinkel", opened);
        int Number(string text, string label)
        {
            var line = text.Split('\n').First(l => l.Contains($"\"{label}\""));
            return int.Parse(line[1..line.IndexOf(']')]);
        }
        int password = Number(opened, "Wachtwoord"), card = Number(opened, "Kaartnummer");
        Assert.Contains("password or payment field", opened);

        // never a password or card field
        foreach (var n in new[] { password, card })
            Assert.IsType<AgentStep.Refuse>(await browser.Check(AgentCalls.Call("browser_type", new { element = n, text = "geheim" })));
        // a link runs, a button asks, a buying button asks every time
        Assert.IsType<AgentStep.Run>(await browser.Check(AgentCalls.Call("browser_click", new { element = Number(opened, "Over ons") })));
        var more = Assert.IsType<AgentStep.Ask>(await browser.Check(AgentCalls.Call("browser_click", new { element = Number(opened, "Meer tonen") })));
        Assert.True((more.Warnings ?? []).Count == 0 && !more.EveryTime);
        var buy = Assert.IsType<AgentStep.Ask>(await browser.Check(AgentCalls.Call("browser_click", new { element = Number(opened, "Nu kopen") })));
        Assert.NotEmpty(buy.Warnings ?? []);
        Assert.True(buy.EveryTime);

        var shown = await browser.Run(AgentCalls.Call("browser_click", new { element = Number(opened, "Meer tonen") }), default);
        Assert.Contains("Getoond!", shown);

        // typing runs, sending the form asks
        int search = Number(shown, "Zoeken");
        Assert.IsType<AgentStep.Run>(await browser.Check(AgentCalls.Call("browser_type", new { element = search, text = "fiets" })));
        Assert.IsType<AgentStep.Ask>(await browser.Check(AgentCalls.Call("browser_type", new { element = search, text = "fiets", submit = true })));
        await browser.Run(AgentCalls.Call("browser_type", new { element = search, text = "fiets" }), default);
        var after = await browser.Run(AgentCalls.Call("browser_read", new { }), default);
        Assert.Contains("= \"fiets\"", after);

        // what the model types goes in as a value, never as script
        await browser.Run(AgentCalls.Call("browser_type", new { element = search, text = "'); document.title = 'gehackt'; ('" }), default);
        Assert.Contains("Title: Testwinkel", await browser.Run(AgentCalls.Call("browser_read", new { }), default));

        var next = await browser.Run(AgentCalls.Call("browser_click", new { element = Number(after, "Over ons") }), default);
        Assert.Contains("Title: Over ons", next);
        var back = await browser.Run(AgentCalls.Call("browser_back", new { }), default);
        Assert.Contains("Title: Testwinkel", back);
        Assert.IsType<AgentStep.Refuse>(await browser.Check(AgentCalls.Call("browser_open", new { url = "javascript:alert(1)" })));
        Assert.IsType<AgentStep.Refuse>(await new BrowserTools { Host = host }.Check(AgentCalls.Call("browser_open", new { url = page.AbsoluteUri })));   // file pages only in tests
    }
}

public class ComputerToolsTests : ChatTest
{
    [Fact]
    public void Keys()
    {
        Assert.Equal((ushort)0x0D, ComputerTools.KeyCode("enter")?.Vk);
        var save = ComputerTools.KeyCode("ctrl+s");
        Assert.Equal((ushort)'S', save?.Vk);
        Assert.Equal(ComputerTools.Mods.Ctrl, save?.Modifiers);
        Assert.Equal(ComputerTools.Mods.Ctrl | ComputerTools.Mods.Shift, ComputerTools.KeyCode("ctrl+shift+n")?.Modifiers);
        Assert.Null(ComputerTools.KeyCode("alt+f4"));   // never close a program
        Assert.Null(ComputerTools.KeyCode("win+r"));    // the Windows key leaves the allowed program
        Assert.Null(ComputerTools.KeyCode("hyper+x"));
        Assert.Null(ComputerTools.KeyCode("f13"));
    }

    [Fact]
    public async Task OnlyAllowedAppsAndNeverThePasswordApps()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
        Settings.Shared.ComputerApps = notepad + "\n" + cmd;
        var tools = new ComputerTools();
        Assert.Equal([notepad], tools.Allowed);   // the command line never, whatever the list says
        Assert.False(tools.Allow(@"C:\Program Files\KeePassXC\KeePassXC.exe"));
        Assert.DoesNotContain(tools.Allowed, p => p.Contains("KeePass"));
        Assert.Equal(notepad, tools.Resolve("NOTEPAD"));
        Assert.Equal(notepad, tools.Resolve("notepad.exe"));
        Assert.IsType<AgentStep.Refuse>(await tools.Check(AgentCalls.Call("computer_look", new { app = "calc" })));   // not allowed
        Assert.IsType<AgentStep.Run>(await tools.Check(AgentCalls.Call("computer_look", new { app = "notepad" })));
        Assert.IsType<AgentStep.Refuse>(await tools.Check(AgentCalls.Call("computer_click", new { app = "notepad", element = 3 })));   // look first
        var key = Assert.IsType<AgentStep.Ask>(await tools.Check(AgentCalls.Call("computer_key", new { app = "notepad", keys = "enter" })));
        Assert.True(key.EveryTime && (key.Warnings ?? []).Count > 0);   // enter can send something
        Assert.IsType<AgentStep.Refuse>(await tools.Check(AgentCalls.Call("computer_key", new { app = "notepad", keys = "alt+f4" })));
        Assert.Equal(cmd, Path.Combine(Path.GetDirectoryName(cmd)!, "cmd.exe"));
        Assert.True(ComputerTools.IsNever(cmd) && ComputerTools.IsNever(@"C:\Tools\WinAIMagic.exe"));
    }
}

public class VoiceTests
{
    [Fact]
    public void WhatIsReadAloud()
    {
        var text = SpeechText.Speakable("## Kop\nDit is **vet** en [een link](https://x.nl).\n```\ncode\n```\n| a | b |\n|---|---|\nZie https://example.com");
        Assert.False(text.Contains('*') || text.Contains('#') || text.Contains("code") || text.Contains("https") || text.Contains("---"), text);
        Assert.Contains("Dit is vet en een link.", text);
    }
}
