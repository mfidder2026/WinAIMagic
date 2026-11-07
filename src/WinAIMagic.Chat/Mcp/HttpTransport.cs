using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinAIMagic.Chat;

/// <summary>An MCP server on the internet (Streamable HTTP): every message is a POST; the answer is JSON or a short stream of
/// server-sent events. The server may give a session id, which goes with every next message. With oauth the app logs in
/// when the server answers 401 (McpOAuth); otherwise the headers carry the token (for example GitHub).</summary>
public sealed class HttpTransport(Uri url, IReadOnlyDictionary<string, string> headers, string serverName, bool oauth, Action<string> log, HttpClient? http = null) : IMcpTransport
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http = http ?? SharedHttp;
    private readonly object sync = new();
    private string? sessionId;
    private int nextId = 1;

    public event Action<string>? Notification;

    private bool HasAuthorization => headers.Keys.Any(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct)
    {
        int id;
        lock (sync) id = nextId++;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) msg["params"] = parameters;
        var messages = await PostAsync(msg, timeout, false, ct).ConfigureAwait(false);
        var answer = messages.Find(m => m["id"] is JsonValue v && v.TryGetValue<int>(out var n) && n == id) ?? throw McpException.BadResponse();
        if (answer["error"] is JsonObject error)
            throw McpException.Server(error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : 0, McpTool.Str(error, "message") ?? "");
        return answer["result"] as JsonObject ?? new JsonObject();
    }

    public async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters != null) msg["params"] = parameters;
        await PostAsync(msg, TimeSpan.FromSeconds(30), false, ct).ConfigureAwait(false);
    }

    /// <summary>Sends one message; returns the JSON-RPC messages that came back (none for a notification).</summary>
    private async Task<List<JsonObject>> PostAsync(JsonObject msg, TimeSpan timeout, bool retried, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpClient.ProtocolVersion);
        string? sid;
        lock (sync) sid = sessionId;
        if (sid != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (oauth && !HasAuthorization && await McpOAuth.Shared.AccessTokenAsync(url, ct).ConfigureAwait(false) is { } token)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw McpException.Timeout(McpTool.Str(msg, "method") ?? "a message"); }
        catch (HttpRequestException e) { throw McpException.NotRunning(e.Message); }
        using (resp)
        {
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) lock (sync) sessionId = ids.First();
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var challenge = resp.Headers.WwwAuthenticate.Count > 0 ? string.Join(", ", resp.Headers.WwwAuthenticate.Select(h => h.ToString())) : "";
                if (!oauth || retried || HasAuthorization) throw McpException.Login(challenge);
                // log in (the browser opens) and send the same message again
                await McpOAuth.Shared.AuthorizeAsync(url, challenge, serverName, ct).ConfigureAwait(false);
                return await PostAsync(msg, timeout, true, ct).ConfigureAwait(false);
            }
            var data = await resp.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Accepted || data.Length == 0) return [];
            if ((int)resp.StatusCode is < 200 or >= 300)
            {
                var text = Encoding.UTF8.GetString(data, 0, Math.Min(500, data.Length));
                log($"HTTP {(int)resp.StatusCode}: {text}");
                throw McpException.Server((int)resp.StatusCode, text);
            }
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            var messages = type.Contains("text/event-stream") ? Events(Encoding.UTF8.GetString(data)) : Json(Encoding.UTF8.GetString(data));
            foreach (var m in messages)
                if (m["id"] == null && McpTool.Str(m, "method") is { } method) Notification?.Invoke(method);
            return messages;
        }
    }

    /// <summary>A JSON answer: one message or a batch.</summary>
    public static List<JsonObject> Json(string text)
    {
        try
        {
            return JsonNode.Parse(text) switch
            {
                JsonObject one => [one],
                JsonArray many => [.. many.OfType<JsonObject>()],
                _ => [],
            };
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The data: lines of server-sent events; each event's lines together are one JSON message.</summary>
    public static List<JsonObject> Events(string text)
    {
        var o = new List<JsonObject>();
        var current = new List<string>();
        void Flush()
        {
            if (current.Count > 0) o.AddRange(Json(string.Join("\n", current)));
            current.Clear();
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) Flush();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) current.Add(line[5..].Trim());
        }
        Flush();
        return o;
    }

    public void Dispose()
    {
        string? sid;
        lock (sync) { sid = sessionId; sessionId = null; }
        if (sid == null) return;
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        _ = http.SendAsync(req).ContinueWith(t => { req.Dispose(); if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }
}
