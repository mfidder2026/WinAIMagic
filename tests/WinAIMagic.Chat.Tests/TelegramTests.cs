using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using WinAIMagic.Chat.Telegram;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

public class TelegramFormatTests
{
    [Fact]
    public void LongTextIsSplitUnderTheLimit()
    {
        var paragraph = string.Concat(Enumerable.Repeat("woord ", 120));   // 720 characters
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 12));
        var parts = TelegramFormat.Chunks(text, 2000);
        Assert.True(parts.Count > 3);
        Assert.All(parts, p => Assert.True(p.Length <= 2000 + 4));
        int Words(string s) => s.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(Words(text), Words(string.Join(" ", parts)));
        Assert.Equal(["kort"], TelegramFormat.Chunks("kort"));
    }

    [Fact]
    public void ACodeBlockThatIsSplitStaysACodeBlock()
    {
        var code = "```\n" + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"regel {i}")) + "\n```";
        var parts = TelegramFormat.Chunks(code, 1000);
        Assert.True(parts.Count > 1);
        foreach (var p in parts) Assert.True(p.Split("```").Length % 2 == 1, "every part has whole code blocks: " + p[..40]);
    }

    [Fact]
    public void HtmlIsSafeAndKeepsTheMarkdown()
    {
        Assert.Equal("a &lt; b &amp; c &gt; d", TelegramFormat.Html("a < b & c > d"));
        Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt;", TelegramFormat.Html("<script>alert(1)</script>"));
        Assert.Equal("<b>vet</b> en <i>schuin</i>", TelegramFormat.Html("**vet** en *schuin*"));
        Assert.Equal("doe <code>ls -la &lt;map&gt;</code>", TelegramFormat.Html("doe `ls -la <map>`"));
        Assert.Equal("<pre>let a = 1 &lt; 2</pre>", TelegramFormat.Html("```swift\nlet a = 1 < 2\n```"));
        Assert.Equal("<a href=\"https://example.com/a?b=1&amp;c=2\">site</a>", TelegramFormat.Html("[site](https://example.com/a?b=1&c=2)"));
        Assert.Equal("[x](javascript:alert(1))", TelegramFormat.Html("[x](javascript:alert(1))"));   // only http(s) links
        Assert.Equal("<b>Titel</b>\n• punt", TelegramFormat.Html("## Titel\n- punt"));
        Assert.Equal("2 * 3 * 4", TelegramFormat.Html("2 * 3 * 4"));
        Assert.Equal("Vrij:\n<pre>Totaal  1,8 TB\nVrij    150 GB</pre>\nKlaar",
            TelegramFormat.Html("Vrij:\n| | |\n|---|---|\n| Totaal | 1,8 TB |\n| **Vrij** | 150 GB |\nKlaar"));
    }
}

public class TelegramAccessTests
{
    private static TelegramBot.Message Message(string text, long from = 42, long? chat = null, string type = "private") =>
        new(1, new TelegramBot.User(from, false, "Mila"), new TelegramBot.Chat(chat ?? from, type), text);

    [Fact]
    public void OnlyThePairingCodeLetsSomeoneIn()
    {
        var access = new TelegramAccess();
        Assert.Equal(TelegramAccess.Kind.Ignored, access.Decide(Message("hallo")).Kind);   // nobody without a code
        var now = DateTimeOffset.Now;
        var code = access.NewCode(now);
        Assert.Equal(6, code.Length);
        Assert.Equal(TelegramAccess.Kind.Ignored, access.Decide(Message(code == "000000" ? "111111" : "000000"), now).Kind);   // a wrong code
        Assert.Equal(TelegramAccess.Kind.Ignored, access.Decide(Message(code, chat: -5, type: "group"), now).Kind);   // not in a group
        Assert.Equal(TelegramAccess.Kind.Ignored, access.Decide(Message("ik weet het: " + code), now).Kind);   // the code alone
        var paired = access.Decide(Message("/start " + code), now);
        Assert.Equal(TelegramAccess.Kind.Paired, paired.Kind);
        Assert.Equal(42, paired.Account!.Id);
        Assert.Equal("Mila", paired.Account.Name);
        Assert.Equal(new TelegramAccess.Decision(TelegramAccess.Kind.Allowed, paired.Account), access.Decide(Message("hallo")));
        Assert.Null(access.Code);   // a code works once
        Assert.Equal(TelegramAccess.Kind.Ignored, access.Decide(Message(code, from: 7), now).Kind);   // not for a second account
    }

    [Fact]
    public void TheCodeExpires()
    {
        var access = new TelegramAccess();
        var now = DateTimeOffset.Now;
        var code = access.NewCode(now);
        Assert.Equal(TelegramAccess.Kind.Ignored, access.Decide(Message(code), now.AddMinutes(11)).Kind);
        Assert.Empty(access.Accounts);
    }
}

/// <summary>The bot, the service and the bridge against the Mac's stand-in for Telegram and a stand-in engine.</summary>
public class TelegramBridgeTests : ChatTest
{
    private readonly Process server;
    private readonly Uri telegram;
    private static readonly HttpClient Control = new();

    public TelegramBridgeTests()
    {
        int port = McpFixtures.FreePort();
        var psi = new ProcessStartInfo(McpFixtures.Python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-I", McpFixtures.Script("fake_telegram.py"), port.ToString() }) psi.ArgumentList.Add(a);
        server = Process.Start(psi)!;
        Assert.Contains("READY", server.StandardOutput.ReadLine());
        telegram = new Uri($"http://127.0.0.1:{port}/");
        Settings.Shared.WorkspaceTools = true;
        Settings.Shared.RightWrite = "ask";
    }

    public override void Dispose()
    {
        try { server.Kill(); } catch (InvalidOperationException) { }
        server.Dispose();
        base.Dispose();
    }

    private async Task Push(JsonObject update) =>
        (await Control.PostAsync(new Uri(telegram, "control/push"), new StringContent(update.ToJsonString(), Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();

    private Task PushText(string text, long from = 42) => Push(new JsonObject
    {
        ["message"] = new JsonObject
        {
            ["message_id"] = Random.Shared.Next(1, 9999),
            ["from"] = new JsonObject { ["id"] = from, ["is_bot"] = false, ["first_name"] = "Mila" },
            ["chat"] = new JsonObject { ["id"] = from, ["type"] = "private" },
            ["date"] = 0,
            ["text"] = text,
        },
    });

    private async Task<List<JsonObject>> Sent() =>
        [.. JsonNode.Parse(await Control.GetStringAsync(new Uri(telegram, "control/sent")))!.AsArray().Select(n => n!.AsObject())];

    private static string Text(JsonObject call) => call["text"]?.GetValue<string>() ?? "";
    private static string? Str(JsonObject call, string key) => call[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Waits for a call to Telegram that matches.</summary>
    private async Task<JsonObject> WaitForSent(Func<JsonObject, bool> match, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if ((await Sent()).LastOrDefault(match) is { } hit) return hit;
            await Task.Delay(100);
        }
        var all = string.Join(" | ", (await Sent()).Select(c => $"{Str(c, "method")} {Str(c, "parse_mode") ?? "-"} {TextLimit.Prefix(Text(c), 60)}"));
        throw new Xunit.Sdk.XunitException("never sent: " + all);
    }

    private (TelegramService Service, TelegramBridge Bridge, ChatClient Chat, ConversationStore Store) Make()
    {
        var service = new TelegramService(Path.Combine(Root, "telegram.json"));
        service.UseForTests(telegram, "TEST:OK");
        var chat = new ChatClient(new HttpClient(new SlowEngine()) { Timeout = Timeout.InfiniteTimeSpan });
        var store = new ConversationStore();
        var bridge = new TelegramBridge(service, chat, store, () => Base, () => true, () => Task.FromResult(true), () => "Model: test")
        {
            EditInterval = TimeSpan.FromMilliseconds(200),
        };
        chat.ApprovalAsked += a => bridge.Approval(a, false);
        return (service, bridge, chat, store);
    }

    private async Task Pair(TelegramService service)
    {
        await Until(() => service.IsOnline);
        var code = service.NewCode();
        await PushText("/start " + code);
        await Until(() => service.Accounts.Count > 0);
        await WaitForSent(c => Text(c).StartsWith("Gekoppeld", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBotRefusesAWrongTokenAndFallsBackToPlainText()
    {
        var wrong = new TelegramBot("WRONG", telegram);
        var e = await Assert.ThrowsAsync<TelegramException>(() => wrong.Me());
        Assert.Equal(TelegramException.Kinds.Unauthorized, e.Kind);
        var bot = new TelegramBot("TEST:OK", telegram);
        Assert.Equal("testbot", (await bot.Me()).Username);
        await bot.Send(42, "platte tekst", "<bad>");
        var calls = (await Sent()).Where(c => Str(c, "method") == "sendMessage").ToList();
        Assert.Equal(2, calls.Count);   // HTML refused, then sent as plain text
        Assert.Equal("platte tekst", Text(calls[1]));
        Assert.Null(calls[1]["parse_mode"]);
    }

    [Fact]
    public async Task AStrangerIsIgnoredAndAPairedAccountGetsItsAnswer()
    {
        var (service, _, _, store) = Make();
        service.Start();
        try
        {
            await Until(() => service.IsOnline);
            await PushText("hallo, wie ben jij?", from: 666);
            await Task.Delay(1500);
            Assert.DoesNotContain(store.Conversations, c => c.Telegram != null);   // no conversation for a stranger
            Assert.Empty(await Sent());   // and no answer: the bot gives nothing away
            Assert.Contains(service.Log, l => l.Contains("666") && !l.Contains("wie ben jij"));   // the log has the id, not the text

            await Pair(service);
            await PushText("hallo");
            var answer = await WaitForSent(c => Text(c).Contains("Antwoord op: hallo") && Str(c, "parse_mode") == "HTML");
            Assert.Equal(42, answer["chat_id"]!.GetValue<long>());
            var c = store.Conversations.Single(x => x.Telegram != null);
            Assert.Equal(42, c.Telegram);
            Assert.Contains("Telegram", c.Title);
            Assert.Equal("hallo", c.Messages[0].Text);

            // the next message goes on in the same conversation; /nieuw starts another
            await PushText("en nu?");
            await WaitForSent(x => Text(x).Contains("Antwoord op: en nu?"));
            Assert.Single(store.Conversations, x => x.Telegram != null);
            await PushText("/nieuw");
            await WaitForSent(x => Text(x).StartsWith("Nieuw gesprek", StringComparison.Ordinal));
            Assert.Equal(2, store.Conversations.Count(x => x.Telegram != null));
            Assert.True(service.Offset > 4);   // the offset is kept, so a restart does nothing twice
            Assert.True(new TelegramService(Path.Combine(Root, "telegram.json")).Offset > 4);
        }
        finally { service.Stop(); }
    }

    [Fact]
    public async Task AButtonInTelegramAnswersTheApproval()
    {
        var (service, bridge, chat, store) = Make();
        service.Start();
        try
        {
            await Pair(service);
            var dir = NewFolder("tg");
            var id = bridge.ConversationFor(service.Accounts[0]);
            store.Update(id, c => c.Workspace = dir);

            await PushText("SCHRIJF een bestand");
            var card = await WaitForSent(c => c["reply_markup"] != null && Str(c, "method") == "sendMessage");
            var buttons = card["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!["callback_data"]!.GetValue<string>()).ToList();
            var allow = buttons.First(b => b.StartsWith("a:", StringComparison.Ordinal));
            Assert.Contains(buttons, b => b.StartsWith("t:", StringComparison.Ordinal));
            Assert.True(chat.IsBusy(id));   // waits for the answer

            // a stranger's press does nothing
            await Push(new JsonObject { ["callback_query"] = new JsonObject { ["id"] = "q0", ["from"] = new JsonObject { ["id"] = 666, ["is_bot"] = false, ["first_name"] = "X" }, ["data"] = allow } });
            await Task.Delay(1000);
            Assert.NotNull(chat.ApprovalFor(id));

            await Push(new JsonObject
            {
                ["callback_query"] = new JsonObject
                {
                    ["id"] = "q1", ["from"] = new JsonObject { ["id"] = 42, ["is_bot"] = false, ["first_name"] = "Mila" }, ["data"] = allow,
                    ["message"] = new JsonObject { ["message_id"] = card["message_id"]?.GetValue<int>() ?? 100, ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" }, ["date"] = 0, ["text"] = "kaart" },
                },
            });
            await Until(() => !chat.IsBusy(id));
            Assert.True(File.Exists(Path.Combine(dir, "x.txt")));   // the yes in Telegram let it write
            await WaitForSent(c => Text(c).Contains('✅'));
            await WaitForSent(c => Text(c).Contains("Geschreven"));

            // the same button again: already answered
            await Push(new JsonObject { ["callback_query"] = new JsonObject { ["id"] = "q2", ["from"] = new JsonObject { ["id"] = 42, ["is_bot"] = false, ["first_name"] = "Mila" }, ["data"] = allow } });
            var late = await WaitForSent(c => Str(c, "method") == "answerCallbackQuery" && Str(c, "callback_query_id") == "q2");
            Assert.NotNull(late["text"]);
        }
        finally { service.Stop(); }
    }

    [Fact]
    public async Task AdministratorRightsHaveNoButtons()
    {
        var (service, bridge, _, _) = Make();
        var notices = new List<string>();
        bridge.AdminNotice = (title, body, _) => { lock (notices) notices.Add(body); };
        service.Start();
        try
        {
            await Pair(service);
            var id = bridge.ConversationFor(service.Accounts[0]);
            bridge.Approval(new Approval(id, new ToolCall("c", "system_change", "{}"), "Firewall aan", "") { Admin = true }, false);
            var card = await WaitForSent(c => Text(c).Contains("Firewall aan"));
            Assert.Null(card["reply_markup"]);   // no buttons for administrator rights
            Assert.Contains("pc", Text(card));
            await Until(() => { lock (notices) return notices.Contains("Firewall aan"); });
        }
        finally { service.Stop(); }
    }

    [Fact]
    public async Task AVoiceMessageBecomesTheQuestion()
    {
        var (service, bridge, _, store) = Make();
        Settings.Shared.FeatureVoice = true;
        string? heardFile = null;
        bridge.Transcribe = file => { heardFile = file; return Task.FromResult<(string?, string?)>(("hallo stem", null)); };
        service.Start();
        try
        {
            await Pair(service);
            await Push(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["message_id"] = 7, ["from"] = new JsonObject { ["id"] = 42, ["is_bot"] = false, ["first_name"] = "Mila" },
                    ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" }, ["date"] = 0, ["voice"] = new JsonObject { ["file_id"] = "voice1", ["duration"] = 2 },
                },
            });
            await WaitForSent(c => Text(c) == "🎙 hallo stem");
            await WaitForSent(c => Text(c).Contains("Antwoord op: hallo stem"));
            Assert.Equal(".ogg", Path.GetExtension(heardFile));
            Assert.False(File.Exists(heardFile));   // the recording is removed again
            Assert.Equal("hallo stem", store.Conversations.First(c => c.Telegram != null).Messages[0].Text);
        }
        finally { service.Stop(); }
    }

    [Fact]
    public async Task VoiceIsOffSaysSo()
    {
        var (service, bridge, _, _) = Make();
        bridge.Transcribe = _ => Task.FromResult<(string?, string?)>(("niet gebruikt", null));
        service.Start();
        try
        {
            await Pair(service);
            await Push(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["message_id"] = 8, ["from"] = new JsonObject { ["id"] = 42, ["is_bot"] = false, ["first_name"] = "Mila" },
                    ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" }, ["date"] = 0, ["voice"] = new JsonObject { ["file_id"] = "voice1", ["duration"] = 2 },
                },
            });
            await WaitForSent(c => Text(c).StartsWith("Spraakberichten staan uit", StringComparison.Ordinal));
            // without a recognizer for recordings it says that instead
            bridge.Transcribe = null;
            Settings.Shared.FeatureVoice = true;
            await Push(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["message_id"] = 9, ["from"] = new JsonObject { ["id"] = 42, ["is_bot"] = false, ["first_name"] = "Mila" },
                    ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" }, ["date"] = 0, ["voice"] = new JsonObject { ["file_id"] = "voice2", ["duration"] = 2 },
                },
            });
            await WaitForSent(c => Text(c).StartsWith("Spraakberichten kan WinAIMagic", StringComparison.Ordinal));
        }
        finally { service.Stop(); }
    }

    [Fact]
    public async Task TasksReportToTheFirstAccount()
    {
        var (service, bridge, _, _) = Make();
        Settings.Shared.TelegramNotify = true;
        service.Start();
        try
        {
            await Pair(service);
            bridge.Notify("Taak klaar: Agenda", "Om 10:00 tandarts.");
            var m = await WaitForSent(c => Text(c).Contains("Taak klaar: Agenda"));
            Assert.Equal(42, m["chat_id"]!.GetValue<long>());
            Settings.Shared.TelegramNotify = false;
            bridge.Notify("Taak klaar: Stil", "");
            await Task.Delay(500);
            Assert.DoesNotContain(await Sent(), c => Text(c).Contains("Stil"));
        }
        finally { service.Stop(); }
    }
}
