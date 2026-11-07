using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>The connectors: which there are, which run, their tools, and calling one. Programs start the first time a
/// conversation needs them and keep running until the app stops.</summary>
public sealed class McpManager : IDisposable
{
    /// <summary>The app's connectors; null in tests that do not set one, and then the chat offers none.</summary>
    public static McpManager? Shared { get; set; }
    public const string LoadTool = "use_connector";

    public enum Phase { Stopped, Starting, Ready, Failed }
    public sealed record State(Phase Phase, int Tools = 0, string? Error = null)
    {
        public bool IsReady => Phase == Phase.Ready;
    }

    private readonly object sync = new();
    private List<McpServerConfig> servers = [];
    private readonly Dictionary<string, State> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpClient> clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<McpTool>> tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> starting = new(StringComparer.OrdinalIgnoreCase);
    public string File { get; }

    /// <summary>The list, a state or a log changed; on whatever thread made the change.</summary>
    public event Action? Changed;

    public McpManager(string? file = null)
    {
        File = file ?? Paths.McpConfig;
        Reload();
    }

    public void Reload()
    {
        List<McpServerConfig> list;
        try { list = System.IO.File.Exists(File) ? McpConfigFile.Parse(System.IO.File.ReadAllText(File)) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { list = []; }
        lock (sync) servers = list;
        Changed?.Invoke();
    }

    public IReadOnlyList<McpServerConfig> Servers { get { lock (sync) return [.. servers]; } }
    public IReadOnlyList<McpServerConfig> Enabled { get { lock (sync) return [.. servers.Where(s => s.IsEnabled)]; } }
    public McpServerConfig? Config(string name) { lock (sync) return servers.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
    public State StateOf(string name) { lock (sync) return states.GetValueOrDefault(name) ?? new State(Phase.Stopped); }
    public string LogOf(string name) { lock (sync) return logs.GetValueOrDefault(name) ?? ""; }

    public void Save(List<McpServerConfig> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicFile.Write(File, McpConfigFile.Encode(list));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        List<McpServerConfig> old;
        lock (sync) { old = servers; servers = [.. list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)]; }
        // a changed connector starts again with its new settings
        foreach (var o in old)
            if (list.Find(n => n.Name == o.Name) is not { } now || !now.Same(o)) Stop(o.Name);
        Changed?.Invoke();
    }

    public void Upsert(McpServerConfig config, string? replacing = null)
    {
        var list = Servers.Where(s => s.Name != (replacing ?? config.Name) && s.Name != config.Name).ToList();
        list.Add(config);
        Save(list);
    }

    public void Remove(string name)
    {
        Stop(name);
        Save([.. Servers.Where(s => s.Name != name)]);
    }

    // MARK: - running

    private void AppendLog(string name, string text)
    {
        lock (sync)
        {
            var l = logs.GetValueOrDefault(name, "") + text;
            if (!l.EndsWith('\n')) l += "\n";
            logs[name] = l.Length > 20_000 ? l[^20_000..] : l;
        }
        Changed?.Invoke();
    }

    private void SetState(string name, State state)
    {
        lock (sync) states[name] = state;
        Changed?.Invoke();
    }

    /// <summary>Starts the connector when it does not run yet and returns its tools.</summary>
    public async Task<List<McpTool>> StartAsync(string name, CancellationToken ct = default)
    {
        var config = Config(name) ?? throw McpException.NotRunning($"no connector named {name}");
        Task task;
        lock (sync)
        {
            if (states.GetValueOrDefault(config.Name)?.IsReady == true && tools.TryGetValue(config.Name, out var t)) return [.. t];
            if (!starting.TryGetValue(config.Name, out task!))
            {
                task = ConnectAsync(config);
                starting[config.Name] = task;
            }
        }
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        finally { lock (sync) if (starting.GetValueOrDefault(config.Name) == task && task.IsCompleted) starting.Remove(config.Name); }
        lock (sync) return [.. tools.GetValueOrDefault(config.Name) ?? []];
    }

    private async Task ConnectAsync(McpServerConfig config)
    {
        await Task.Yield();
        var name = config.Name;
        SetState(name, new State(Phase.Starting));
        McpClient? client = null;
        try
        {
            if (config.Builtin is { } b) throw McpException.NotRunning($"the built-in connector {b} is not available on Windows");
            client = new McpClient(MakeTransport(config));
            client.Transport.Notification += method =>
            {
                if (method != "notifications/tools/list_changed") return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var list = await client.ListToolsAsync().ConfigureAwait(false);
                        lock (sync) tools[name] = list;
                        SetState(name, new State(Phase.Ready, list.Count));
                    }
                    catch (Exception e) when (e is McpException or IOException) { }
                });
            };
            await client.ConnectAsync().ConfigureAwait(false);
            var found = await client.ListToolsAsync().ConfigureAwait(false);
            lock (sync)
            {
                clients[name] = client;
                tools[name] = found;
            }
            SetState(name, new State(Phase.Ready, found.Count));
            AppendLog(name, $"ready: {found.Count} tools");
        }
        catch (Exception e) when (e is McpException or McpOAuth.OAuthException or IOException or InvalidOperationException or HttpRequestException)
        {
            client?.Dispose();
            lock (sync) clients.Remove(name);
            SetState(name, new State(Phase.Failed, 0, e.Message));
            AppendLog(name, "failed: " + e.Message);
            throw;
        }
    }

    public IMcpTransport MakeTransport(McpServerConfig config)
    {
        var name = config.Name;
        void Log(string text) => AppendLog(name, text);
        if (config.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var headers = (config.Headers ?? []).ToDictionary(k => k.Key, k => McpSecrets.Resolve(k.Value));
            return new HttpTransport(u, headers, name, config.Oauth ?? true, Log);
        }
        if (string.IsNullOrWhiteSpace(config.Command)) throw McpException.NotRunning("no command or address");
        var env = (config.Env ?? []).ToDictionary(k => k.Key, k => McpSecrets.Resolve(k.Value));
        return new StdioTransport(config.Command, config.Args ?? [], env, Log);
    }

    public void Stop(string name)
    {
        McpClient? c;
        lock (sync)
        {
            clients.Remove(name, out c);
            tools.Remove(name);
            if (states.ContainsKey(name)) states[name] = new State(Phase.Stopped);
        }
        c?.Dispose();
        Changed?.Invoke();
    }

    public void StopAll()
    {
        List<string> names;
        lock (sync) names = [.. clients.Keys];
        foreach (var n in names) Stop(n);
    }

    public void Dispose() => StopAll();

    /// <summary>Starts it, lists its tools and stops it again (Instellingen → Connectors → Test).</summary>
    public async Task<(List<McpTool>? Tools, string? Error)> TestAsync(McpServerConfig config, CancellationToken ct = default)
    {
        try
        {
            if (config.Builtin is { } b) return (null, $"The built-in connector {b} is not available on Windows.");
            using var client = new McpClient(MakeTransport(config));
            await client.ConnectAsync(ct).ConfigureAwait(false);
            return (await client.ListToolsAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is McpException or McpOAuth.OAuthException or IOException or InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            return (null, e.Message);
        }
    }

    // MARK: - the model's side

    /// <summary>The tool name the model sees: "&lt;connector&gt;__&lt;tool&gt;", only letters, digits, _ and -, at most 64 characters.</summary>
    public static string ToolName(string server, string tool)
    {
        static string Clean(string s) => new([.. s.Select(c => char.IsAscii(c) && (char.IsLetterOrDigit(c) || c is '_' or '-') ? c : '_')]);
        var full = Clean(server) + "__" + Clean(tool);
        return full.Length <= 64 ? full : full[..64];
    }

    /// <summary>The connector and tool behind a name the model used, for connectors that are loaded.</summary>
    public McpRoute? Route(string name)
    {
        lock (sync)
            foreach (var s in servers.Where(s => s.IsEnabled))
                foreach (var t in tools.GetValueOrDefault(s.Name) ?? [])
                    if (ToolName(s.Name, t.Name) == name) return new McpRoute(s, t);
        return null;
    }

    private HashSet<string> Wanted(IEnumerable<string> loaded)
    {
        var names = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);
        foreach (var s in Enabled.Where(s => s.AlwaysLoaded == true)) names.Add(s.Name);
        return names;
    }

    /// <summary>The connectors whose tools go in the request: those loaded in this conversation and those always loaded, when they run.</summary>
    public List<JsonObject> Definitions(IEnumerable<string> loaded)
    {
        var names = Wanted(loaded);
        var o = new List<JsonObject>();
        lock (sync)
            foreach (var s in servers.Where(s => s.IsEnabled && names.Contains(s.Name)))
                foreach (var t in tools.GetValueOrDefault(s.Name) ?? [])
                {
                    var desc = (t.Title is { } title ? title + ". " : "") + t.Description;
                    if (desc.Length > 600) desc = desc[..600] + "…";
                    o.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = ToolName(s.Name, t.Name),
                            ["description"] = $"[{s.Name}] " + desc,
                            ["parameters"] = t.SchemaObject,
                        },
                    });
                }
        return o;
    }

    /// <summary>Before a request: the connectors a conversation loaded earlier run again (after a restart of the app).</summary>
    public async Task EnsureRunningAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        foreach (var n in Wanted(names))
        {
            if (Config(n) is not { IsEnabled: true } c || StateOf(c.Name).IsReady) continue;
            try { await StartAsync(c.Name, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is McpException or McpOAuth.OAuthException or IOException or InvalidOperationException or HttpRequestException) { }
        }
    }

    public static JsonObject LoadDefinition => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = LoadTool,
            ["description"] = "Load the tools of a connector from the list in the system prompt, so you can use them in this conversation. Do this once per connector, before you need it.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The connector's name, exactly as listed" } },
                ["required"] = new JsonArray("name"),
            },
        },
    };

    public string PromptSection()
    {
        var list = Enabled;
        if (list.Count == 0) return "";
        return L("Je hebt connectors: gereedschap van andere programma's en diensten. Laad de tools van een connector met use_connector voordat je hem gebruikt; daarna staan ze voor de rest van het gesprek klaar. Een connector op internet stuurt wat je meegeeft naar die dienst.")
            + "\n" + string.Join("\n", list.Select(c => "- " + c.Name + (string.IsNullOrEmpty(c.Description) ? "" : ": " + c.Description) + (c.AlwaysLoaded == true ? " " + L("(al geladen)") : "")));
    }

    /// <summary>use_connector: starts it and says which tools there are now.</summary>
    public async Task<(string Text, string? Name)> LoadAsync(string wanted, CancellationToken ct = default)
    {
        wanted = wanted.Trim();
        if (Config(wanted) is not { IsEnabled: true } c)
            return ($"There is no connector named \"{wanted}\". Available: " + string.Join(", ", Enabled.Select(s => s.Name)), null);
        try
        {
            var t = await StartAsync(c.Name, ct).ConfigureAwait(false);
            var list = t.Take(60).Select(x => $"- {ToolName(c.Name, x.Name)}: {TextLimit.Prefix(x.Title ?? x.Description, 120)}");
            return ($"Connector {c.Name} is loaded with {t.Count} tools; they are available from your next step:\n" + string.Join("\n", list), c.Name);
        }
        catch (Exception e) when (e is McpException or McpOAuth.OAuthException or IOException or InvalidOperationException or HttpRequestException)
        {
            return ($"Connector {c.Name} did not start: {e.Message}. Tell the user; it can be checked under Instellingen → Connectors.", null);
        }
    }

    /// <summary>Calls a tool and returns its answer as text.</summary>
    public async Task<string> CallAsync(McpRoute route, JsonObject arguments, CancellationToken ct = default)
    {
        var name = route.Server.Name;
        try
        {
            if (!StateOf(name).IsReady) await StartAsync(name, ct).ConfigureAwait(false);
            McpClient? client;
            lock (sync) client = clients.GetValueOrDefault(name);
            if (client == null) throw McpException.NotRunning(name);
            var (text, isError) = await client.CallToolAsync(route.Tool.Name, arguments, ct: ct).ConfigureAwait(false);
            return (isError ? "The tool reported an error:\n" : "") + (text.Length == 0 ? "(no output)" : text);
        }
        catch (Exception e) when (e is McpException or McpOAuth.OAuthException or IOException or InvalidOperationException or HttpRequestException)
        {
            AppendLog(name, $"call {route.Tool.Name} failed: {e.Message}");
            return "Error: " + e.Message;
        }
    }

    /// <summary>Whether this call may run without a card, needs one, or is refused. Deny wins; a read-only tool that is not
    /// destructive runs; otherwise the connector's right.</summary>
    public static string Decision(string right, McpTool tool, bool autoApprove)
    {
        if (right == "deny") return "deny";
        if (tool.ReadOnly == true && tool.Destructive != true) return "allow";
        if (right == "ask" && autoApprove) return "allow";
        return right == "allow" ? "allow" : "ask";
    }

    public static string Label(McpRoute r) => $"{r.Server.Name}: {r.Tool.Title ?? r.Tool.Name}";

    /// <summary>The arguments as the card shows them: indented, keys sorted, slashes as they are.</summary>
    public static string Pretty(JsonObject args)
    {
        static JsonNode? Sorted(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => KeyValuePair.Create(k.Key, Sorted(k.Value)))),
            JsonArray a => new JsonArray([.. a.Select(Sorted)]),
            _ => n?.DeepClone(),
        };
        return Sorted(args)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // MARK: - import and examples

    /// <summary>Connectors from Claude Desktop's and Claude Code's settings that are not here yet; they come in switched off.</summary>
    public List<McpServerConfig> Importable(string? appData = null, string? home = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<McpServerConfig>();
        foreach (var file in new[] { Path.Combine(appData, "Claude", "claude_desktop_config.json"), Path.Combine(home, ".claude.json") })
        {
            try
            {
                if (!System.IO.File.Exists(file)) continue;
                if (JsonNode.Parse(System.IO.File.ReadAllText(file))?["mcpServers"] is JsonObject s)
                    found.AddRange(McpConfigFile.Parse(new JsonObject { ["mcpServers"] = s.DeepClone() }.ToJsonString()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var have = new HashSet<string>(Servers.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. found.Where(c => !have.Contains(c.Name) && seen.Add(c.Name)).Select(c => c with { Enabled = false })];
    }

    /// <summary>A connector that comes ready to switch on; Needs: a program that must be on the PC (npx, uvx); AskToken: the
    /// editor opens for a token first.</summary>
    public sealed record Preset(McpServerConfig Config, string? Needs = null, bool AskToken = false);

    /// <summary>Ready to switch on: the official file server for Documents, Downloads and the desktop, reading web pages, and
    /// GitHub with a token. The Mac's calendar connector uses macOS itself and has no counterpart here.</summary>
    public static List<Preset> Presets()
    {
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var downloads = Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads");
        return
        [
            new(new McpServerConfig
            {
                Name = L("Bestanden"), Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-filesystem", Folder(Environment.SpecialFolder.MyDocuments), downloads, Folder(Environment.SpecialFolder.DesktopDirectory)],
                Description = L("Bestanden lezen, zoeken en schrijven in Documenten, Downloads en Bureaublad"), Enabled = true, Right = "ask",
            }, Needs: "npx"),
            new(new McpServerConfig
            {
                Name = L("Web ophalen"), Command = "uvx", Args = ["mcp-server-fetch"],
                Description = L("Een webpagina ophalen als tekst"), Enabled = true, Right = "ask",
            }, Needs: "uvx"),
            new(new McpServerConfig
            {
                Name = "GitHub", Url = "https://api.githubcopilot.com/mcp/",
                Description = L("Repositories, issues en pull requests op GitHub"), Enabled = true, Right = "ask", Oauth = false,
            }, AskToken: true),
        ];
    }

    /// <summary>The program is on this PC (in PATH, as an .exe or .cmd).</summary>
    public static bool HasTool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => new[] { ".exe", ".cmd", ".bat" }.Any(ext => { try { return System.IO.File.Exists(Path.Combine(dir.Trim('"'), name + ext)); } catch (ArgumentException) { return false; } }));

    public static string Install(string tool) => tool == "uvx" ? "winget install astral-sh.uv" : "winget install OpenJS.NodeJS.LTS";

    /// <summary>A token typed in the editor: kept in the Credential Manager, and the header names it.</summary>
    public static McpServerConfig WithToken(McpServerConfig c, string token)
    {
        if (token.Length == 0) return c;
        var account = "mcp-" + c.Name;
        McpSecrets.Set(account, token.StartsWith("Bearer ", StringComparison.Ordinal) ? token : "Bearer " + token);
        var h = new Dictionary<string, string>(c.Headers ?? []) { ["Authorization"] = "keychain:" + account };
        return c with { Headers = h };
    }
}
