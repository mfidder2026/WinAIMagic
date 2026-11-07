using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

internal static class McpFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static string Script(string name) => Path.Combine(Dir, name);

    /// <summary>The fake servers are the Mac's Python scripts; python must be on the PATH.</summary>
    public static string Python => StdioTransport.FindExe("python") ?? throw new InvalidOperationException("python is not on the PATH");

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public class McpStdioTests : ChatTest
{
    private static McpClient Client(string? command = null, params string[] args) =>
        new(new StdioTransport(command ?? McpFixtures.Python, args.Length > 0 ? args : ["-I", McpFixtures.Script("fake_mcp_stdio.py")], new Dictionary<string, string>(), _ => { }));

    [Fact]
    public async Task HandshakeToolsAndCalls()
    {
        using var c = Client();
        await c.ConnectAsync();
        Assert.Equal("fake", c.ServerName);
        Assert.Equal("be nice", c.Instructions);
        // two pages, and a line from the shell and a ping from the server in between
        var tools = await c.ListToolsAsync();
        Assert.Equal(["echo", "delete_all", "fail"], tools.Select(t => t.Name));
        Assert.True(tools[0].ReadOnly);
        Assert.True(tools[1].Destructive);
        Assert.Null(tools[2].ReadOnly);

        var echo = await c.CallToolAsync("echo", new JsonObject { ["text"] = "hoi" });
        Assert.Equal("echo: hoi\n[image, image/png]", echo.Text);
        Assert.False(echo.IsError);
        Assert.True((await c.CallToolAsync("fail", [])).IsError);
        var e = await Assert.ThrowsAsync<McpException>(() => c.CallToolAsync("nope", []));
        Assert.Equal(-32602, e.Code);
    }

    [Fact]
    public async Task StoppedProgramFailsInsteadOfHanging()
    {
        using var c = Client("cmd", "/c", "exit", "1");
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<McpException>(() => c.ConnectAsync());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
    }

    /// <summary>npx and uvx are .cmd files and shims on Windows: anything that is not an .exe starts through cmd /c.</summary>
    [Fact]
    public async Task AProgramThatIsNoExeStartsThroughCmd()
    {
        var bat = Path.Combine(Root, "fake server.cmd");
        File.WriteAllText(bat, $"@\"{McpFixtures.Python}\" -I \"{McpFixtures.Script("fake_mcp_stdio.py")}\"\r\n");
        Assert.Null(StdioTransport.FindExe(bat));
        using var c = Client(bat, "--a b");
        await c.ConnectAsync();
        Assert.Equal(3, (await c.ListToolsAsync()).Count);
    }

    [Fact]
    public void ArgumentsAreQuotedAsTheyAreReadBack()
    {
        Assert.Equal("plain", StdioTransport.Quote("plain"));
        Assert.Equal("\"C:\\Program Files\\x\"", StdioTransport.Quote("C:\\Program Files\\x"));
        Assert.Equal("\"a \\\"b\\\"\"", StdioTransport.Quote("a \"b\""));
        Assert.Equal("\"\"", StdioTransport.Quote(""));
        Assert.Equal("\"C:\\dir with space\\\\\"", StdioTransport.Quote("C:\\dir with space\\"));
    }
}

public sealed class McpHttpTests : ChatTest
{
    private Process? server;
    private int port;

    private void StartServer(bool oauth)
    {
        port = McpFixtures.FreePort();
        var psi = new ProcessStartInfo(McpFixtures.Python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-I", McpFixtures.Script("fake_mcp_http.py"), port.ToString() }.Concat(oauth ? ["oauth"] : Array.Empty<string>())) psi.ArgumentList.Add(a);
        server = Process.Start(psi)!;
        Assert.Contains("READY", server.StandardOutput.ReadLine());
    }

    public override void Dispose()
    {
        try { server?.Kill(true); } catch (InvalidOperationException) { }
        server?.Dispose();
        McpSecrets.Store = new McpSecrets.Memory();
        base.Dispose();
    }

    private Uri Url => new($"http://127.0.0.1:{port}/mcp");

    [Fact]
    public async Task JsonAndServerSentEvents()
    {
        StartServer(false);
        using var c = new McpClient(new HttpTransport(Url, new Dictionary<string, string>(), "http", false, _ => { }));
        await c.ConnectAsync();
        Assert.Equal("http-fake", c.ServerName);
        Assert.Equal(["hello"], (await c.ListToolsAsync()).Select(t => t.Name));   // answered as server-sent events
        Assert.Equal("hello over http", (await c.CallToolAsync("hello", [])).Text);
    }

    [Fact]
    public async Task UnauthorizedWithoutOAuthSaysSo()
    {
        StartServer(true);
        using var c = new McpClient(new HttpTransport(Url, new Dictionary<string, string>(), "http", false, _ => { }));
        var e = await Assert.ThrowsAsync<McpException>(() => c.ConnectAsync());
        Assert.Contains("resource_metadata", e.Challenge);
    }

    /// <summary>The whole login: 401, metadata, registration, the browser (here: a request that follows the redirect), the
    /// callback on 127.0.0.1, the token with PKCE, and the same message again with the token.</summary>
    [Fact]
    public async Task OAuthLoginEndToEnd()
    {
        StartServer(true);
        McpSecrets.Store = new McpSecrets.Memory();
        var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true });
        var open = McpOAuth.Shared.OpenBrowser;
        McpOAuth.Shared.OpenBrowser = url => _ = browser.GetAsync(url);   // follows the 302 to the app's callback
        try
        {
            using var c = new McpClient(new HttpTransport(Url, new Dictionary<string, string>(), "http", true, _ => { }));
            await c.ConnectAsync();
            Assert.Equal("http-fake", c.ServerName);
            Assert.Equal("token-123", await McpOAuth.Shared.AccessTokenAsync(Url));
            Assert.Equal(["hello"], (await c.ListToolsAsync()).Select(t => t.Name));
            McpOAuth.LogOut(Url);
            Assert.Null(await McpOAuth.Shared.AccessTokenAsync(Url));
        }
        finally { McpOAuth.Shared.OpenBrowser = open; }
    }
}

public class McpPartsTests : ChatTest
{
    [Fact]
    public void ToolNamesAndDecisions()
    {
        Assert.Equal("GitHub__search_issues", McpManager.ToolName("GitHub", "search_issues"));
        Assert.Equal("Agenda_en_Herinneringen__list_events", McpManager.ToolName("Agenda en Herinneringen", "list.events"));
        Assert.Equal(64, McpManager.ToolName(new string('a', 70), "x").Length);

        var read = McpTool.Make("r", "", readOnly: true);
        var write = McpTool.Make("w", "");
        var destroy = McpTool.Make("d", "", readOnly: true, destructive: true);
        Assert.Equal("allow", McpManager.Decision("ask", read, false));
        Assert.Equal("deny", McpManager.Decision("deny", read, false));
        Assert.Equal("ask", McpManager.Decision("ask", write, false));
        Assert.Equal("allow", McpManager.Decision("allow", write, false));
        Assert.Equal("ask", McpManager.Decision("ask", destroy, false));   // destructive beats read-only
    }

    [Fact]
    public void ConfigFileRoundTripInClaudeDesktopFormat()
    {
        var json = """{"mcpServers":{"files":{"command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","C:\\temp"]},"GitHub":{"url":"https://api.githubcopilot.com/mcp/","headers":{"Authorization":"keychain:github"},"right":"allow","enabled":false}}}""";
        var list = McpConfigFile.Parse(json);
        Assert.Equal(["files", "GitHub"], list.Select(c => c.Name));
        Assert.True(list[0].IsEnabled);
        Assert.Equal("ask", list[0].RightValue);
        Assert.Equal("C:\\temp", list[0].Args![2]);
        Assert.False(list[1].IsEnabled);
        Assert.True(list[1].IsRemote);
        var back = McpConfigFile.Parse(McpConfigFile.Encode(list));
        Assert.True(back.Zip(list).All(p => p.First.Same(p.Second)));
        Assert.DoesNotContain("\"name\"", McpConfigFile.Encode(list));   // the name is the key, as in Claude Desktop
    }

    [Fact]
    public async Task ManagerWithStdioConnector()
    {
        using var m = new McpManager(Path.Combine(Root, "mcp.json"));
        m.Upsert(new McpServerConfig { Name = "Fake", Command = McpFixtures.Python, Args = ["-I", McpFixtures.Script("fake_mcp_stdio.py")], Description = "a test connector" });
        Assert.Equal(3, (await m.StartAsync("fake")).Count);
        Assert.Equal(3, m.Definitions(["Fake"]).Count);
        Assert.Empty(m.Definitions([]));   // not loaded in this conversation: no tools in the request
        var route = m.Route("Fake__echo")!;
        Assert.StartsWith("echo: x", await m.CallAsync(route, new JsonObject { ["text"] = "x" }));
        Assert.Contains("- Fake: a test connector", m.PromptSection());
        Assert.Contains("ready: 3 tools", m.LogOf("Fake"));
        Assert.Contains("hello from the login shell", m.LogOf("Fake"));   // what is no JSON-RPC goes to the log
        // a changed connector stops, and starts again with its new settings
        m.Upsert(new McpServerConfig { Name = "Fake", Command = McpFixtures.Python, Args = ["-I", McpFixtures.Script("fake_mcp_stdio.py")], Right = "allow" });
        Assert.Equal(McpManager.Phase.Stopped, m.StateOf("Fake").Phase);
        Assert.Single(new McpManager(m.File).Servers);
    }

    [Fact]
    public void ServerSentEventsParsing()
    {
        var msgs = HttpTransport.Events("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\ndata: \"result\":{}}\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"x\"}\n");
        Assert.Equal(2, msgs.Count);
        Assert.Equal("x", msgs[^1]["method"]!.GetValue<string>());
    }

    [Fact]
    public void PKCEAndMetadataAddresses()
    {
        // RFC 7636, appendix B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", McpOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.True(McpOAuth.PkcePair().Verifier.Length >= 43);
        Assert.Equal(["https://example.com/.well-known/oauth-protected-resource/mcp", "https://example.com/.well-known/oauth-protected-resource"],
            McpOAuth.WellKnown(new Uri("https://example.com/mcp"), "oauth-protected-resource").Select(u => u.AbsoluteUri));
        Assert.Equal("https://a.b/.well-known/oauth-protected-resource",
            McpOAuth.ResourceMetadataUrl("Bearer realm=\"x\", resource_metadata=\"https://a.b/.well-known/oauth-protected-resource\"")?.AbsoluteUri);
    }

    /// <summary>Tokens go to the Credential Manager, also when they are longer than one credential holds. Uses a name of its own.</summary>
    [Fact]
    public void SecretsInTheCredentialManager()
    {
        var saved = McpSecrets.Store;
        McpSecrets.Store = McpSecrets.Windows;
        try
        {
            var account = "test-" + Guid.NewGuid().ToString("N")[..8];
            foreach (var value in new[] { "kort", new string('x', 1200), new string('y', 5000) + "z" })
            {
                McpSecrets.Set(account, value);
                Assert.Equal(value, McpSecrets.Get(account));
            }
            Assert.Equal(new string('y', 5000) + "z", McpSecrets.Resolve("keychain:" + account));
            Assert.Equal("Bearer x", McpSecrets.Resolve("Bearer x"));
            McpSecrets.Delete(account);
            Assert.Null(McpSecrets.Get(account));
        }
        finally { McpSecrets.Store = saved; }
    }

    [Fact]
    public void ImportFromClaudeComesInSwitchedOff()
    {
        var appData = NewFolder("roaming");
        Directory.CreateDirectory(Path.Combine(appData, "Claude"));
        File.WriteAllText(Path.Combine(appData, "Claude", "claude_desktop_config.json"), """{"mcpServers":{"a":{"command":"npx","args":["x"]}}}""");
        var home = NewFolder("home");
        File.WriteAllText(Path.Combine(home, ".claude.json"), """{"other":1,"mcpServers":{"b":{"url":"https://example.com/mcp"},"a":{"command":"dubbel"}}}""");
        using var m = new McpManager(Path.Combine(Root, "mcp.json"));
        var found = m.Importable(appData, home);
        Assert.Equal(["a", "b"], found.Select(c => c.Name));
        Assert.True(found.TrueForAll(c => c.Enabled == false));
        Assert.Equal("npx", found[0].Command);
    }
}

/// <summary>The chat with a connector: use_connector, a read-only tool without a card, a destructive one with its arguments on the card.</summary>
public class ConnectorChatTests : ChatTest
{
    [Fact]
    public async Task TheModelLoadsAConnectorAndAsksBeforeAChange()
    {
        using var m = new McpManager(Path.Combine(Root, "mcp.json"));
        m.Upsert(new McpServerConfig { Name = "Fake", Command = McpFixtures.Python, Args = ["-I", McpFixtures.Script("fake_mcp_stdio.py")], Description = "test" });
        McpManager.Shared = m;
        try
        {
            var store = new ConversationStore();
            var engine = new FakeEngine(
                () => Call("use_connector", new JsonObject { ["name"] = "fake" }, "c1"),
                () => Call("Fake__echo", new JsonObject { ["text"] = "hoi/daar" }, "c2"),
                () => Call("Fake__delete_all", new JsonObject { ["path"] = "C:/x" }, "c3"),
                () => Say("Gedaan."));
            var asked = new List<Approval>();
            var chat = new ChatClient(engine.Client) { AskUser = (a, _) => { asked.Add(a); return Task.FromResult(new ApprovalAnswer(true)); } };
            var id = store.Conversations[0].Id;
            chat.Send("Ruim op", [], id, store, Base);
            await chat.Completion;

            var first = engine.Requests[0];
            Assert.Contains("- Fake: test", first["messages"]![0]!["content"]!.GetValue<string>());
            var names = first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
            Assert.Contains("use_connector", names);
            Assert.Contains("task_list", names);   // a connector is work of several steps
            Assert.DoesNotContain("Fake__echo", names);   // not loaded yet
            Assert.Contains("Fake__echo", engine.Requests[1]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
            Assert.Equal(["Fake"], store.Conversations[0].Connectors);

            var results = store.Conversations[0].Messages.Where(x => x.Role == ChatRole.Tool).Select(x => x.Text).ToList();
            Assert.Contains("is loaded with 3 tools", results[0]);
            Assert.Equal("echo: hoi/daar\n[image, image/png]", results[1]);
            var card = Assert.Single(asked);   // the read-only echo ran without a card
            Assert.Equal("Fake: delete_all", card.Title);
            Assert.Contains("\"path\": \"C:/x\"", card.Detail);   // the arguments as JSON, slashes as they are
            Assert.Contains(card.Warnings, w => w.Contains("wijzigen of verwijderen"));
            Assert.Contains("unknown tool", results[2]);
        }
        finally { McpManager.Shared = null; }
    }
}
