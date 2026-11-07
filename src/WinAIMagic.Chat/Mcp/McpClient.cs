using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Chat;

/// <summary>How the client reaches a server: a program on this PC (stdio) or an address on the internet (HTTP).</summary>
public interface IMcpTransport : IDisposable
{
    Task StartAsync(CancellationToken ct);
    /// <summary>Sends a request and returns its result.</summary>
    Task<JsonObject> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct);
    Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct);
    /// <summary>Notifications from the server (for example notifications/tools/list_changed).</summary>
    event Action<string>? Notification;
}

/// <summary>One connection to an MCP server: the handshake, the list of tools and calling one.</summary>
public sealed class McpClient(IMcpTransport transport) : IDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    public IMcpTransport Transport { get; } = transport;
    public string ServerName { get; private set; } = "";
    public string? Instructions { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await Transport.StartAsync(ct).ConfigureAwait(false);
        var result = await Transport.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "WinAIMagic", ["version"] = Paths.AppVersion },
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        ServerName = (result["serverInfo"] as JsonObject) is { } info ? McpTool.Str(info, "name") ?? "" : "";
        Instructions = McpTool.Str(result, "instructions");
        await Transport.NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
    }

    /// <summary>Every tool, over as many pages as the server uses.</summary>
    public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var result = await Transport.RequestAsync("tools/list", cursor == null ? null : new JsonObject { ["cursor"] = cursor }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (result["tools"] is JsonArray list)
                foreach (var t in list)
                    if (t is JsonObject o && McpTool.FromJson(o) is { } tool) tools.Add(tool);
            cursor = McpTool.Str(result, "nextCursor");
        } while (cursor != null && tools.Count < 1000);
        return tools;
    }

    /// <summary>The tool's answer as text; IsError when the tool says it failed.</summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(string name, JsonObject arguments, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await Transport.RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() },
            timeout ?? TimeSpan.FromSeconds(300), ct).ConfigureAwait(false);
        return (Text(result), McpTool.Bool(result, "isError") ?? false);
    }

    /// <summary>The content of a tool result in words: text as it is, other kinds named.</summary>
    public static string Text(JsonObject result)
    {
        var parts = new List<string>();
        if (result["content"] is JsonArray content)
            foreach (var node in content)
            {
                if (node is not JsonObject item) continue;
                switch (McpTool.Str(item, "type"))
                {
                    case "text": parts.Add(McpTool.Str(item, "text") ?? ""); break;
                    case "image": parts.Add($"[image, {McpTool.Str(item, "mimeType") ?? ""}]"); break;
                    case "audio": parts.Add("[audio]"); break;
                    case "resource":
                        var r = item["resource"] as JsonObject;
                        parts.Add((r == null ? null : McpTool.Str(r, "text")) ?? $"[resource {(r == null ? "" : McpTool.Str(r, "uri"))}]");
                        break;
                    case "resource_link": parts.Add($"[link {McpTool.Str(item, "name")} {McpTool.Str(item, "uri")}]"); break;
                }
            }
        if (parts.Count == 0 && result["structuredContent"] is { } structured)
            parts.Add(structured.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return string.Join("\n", parts);
    }

    public void Dispose() => Transport.Dispose();
}

/// <summary>A program on this PC that speaks MCP on its standard input and output, one JSON message per line. A program
/// found as an .exe starts directly; anything else (npx.cmd, a .bat, a name cmd knows) through cmd /c, so npx and uvx are
/// found as in a command prompt. Lines that are no JSON-RPC and everything on stderr go to the log. The program and what
/// it starts stop with the connector, and with the app.</summary>
public sealed class StdioTransport(string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, Action<string> log) : IMcpTransport
{
    private readonly object sync = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    private Process? process;
    private KillOnCloseJob? job;
    private StreamWriter? input;
    private int nextId = 1;
    private string? ended;

    public event Action<string>? Notification;

    /// <summary>The executable this command runs, when it is an .exe that can start without a shell.</summary>
    internal static string? FindExe(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) && command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command : null;
        if (command.Contains('\\') || command.Contains('/')) return null;
        var exts = Path.HasExtension(command) ? [""] : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                string full;
                try { full = Path.Combine(dir.Trim('"'), command + ext); } catch (ArgumentException) { continue; }
                if (File.Exists(full)) return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
            }
        return null;
    }

    /// <summary>A command line argument as the C runtime reads it back.</summary>
    internal static string Quote(string s)
    {
        if (s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"')) return s;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    public Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (FindExe(command) is { } exe)
        {
            psi.FileName = exe;
            foreach (var a in arguments) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"" + string.Join(" ", new[] { command }.Concat(arguments).Select(Quote)) + "\"";
        }
        foreach (var (k, v) in environment) psi.Environment[k] = v;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { throw McpException.NotRunning(e.Message); }
        try
        {
            job = new KillOnCloseJob();
            job.Assign(p.Handle);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { job?.Dispose(); job = null; }
        lock (sync)
        {
            process = p;
            input = p.StandardInput;
            input.AutoFlush = true;
            input.NewLine = "\n";
            ended = null;
        }
        p.Exited += (_, _) =>
        {
            int code;
            try { code = p.ExitCode; } catch (InvalidOperationException) { code = -1; }
            Finish($"the program stopped (exit code {code})");
        };
        _ = Task.Run(() => ReadOutput(p.StandardOutput));
        _ = Task.Run(() => ReadErrors(p.StandardError));
        return Task.CompletedTask;
    }

    private async Task ReadOutput(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (line.Length > 0) Handle(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        Finish("the program closed its output");
    }

    private async Task ReadErrors(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) log(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private void Handle(string line)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { msg = null; }
        if (msg == null || McpTool.Str(msg, "jsonrpc") != "2.0")
        {
            log(line);
            return;
        }
        if (McpTool.Str(msg, "method") is { } method)
        {
            if (msg["id"] is { } id)
            {
                // a request from the server (ping, or something this client does not offer)
                var reply = method == "ping"
                    ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject() }
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by WinAIMagic" } };
                Write(reply);
            }
            else Notification?.Invoke(method);
            return;
        }
        if (msg["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var number)) return;
        if (!pending.TryRemove(number, out var waiting)) return;
        if (msg["error"] is JsonObject error)
            waiting.TrySetException(McpException.Server(error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : 0, McpTool.Str(error, "message") ?? ""));
        else waiting.TrySetResult(msg["result"] as JsonObject ?? new JsonObject());
    }

    private void Write(JsonObject msg)
    {
        StreamWriter? w;
        lock (sync) w = input;
        if (w == null) return;
        try { lock (w) w.WriteLine(msg.ToJsonString()); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct)
    {
        int id;
        lock (sync)
        {
            if (ended != null) throw McpException.NotRunning(ended);
            id = nextId++;
        }
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) msg["params"] = parameters;
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        Write(msg);
        try
        {
            return await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw McpException.Timeout(method);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters != null) msg["params"] = parameters;
        Write(msg);
        return Task.CompletedTask;
    }

    private void Finish(string why)
    {
        lock (sync) ended ??= why;
        foreach (var id in pending.Keys)
            if (pending.TryRemove(id, out var w)) w.TrySetException(McpException.NotRunning(why));
    }

    public void Dispose()
    {
        Process? p;
        lock (sync) { p = process; process = null; }
        try { input?.Close(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        job?.Terminate();
        job?.Dispose();
        job = null;
        try { if (p is { HasExited: false }) p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p?.Dispose();
        Finish("closed");
    }
}
