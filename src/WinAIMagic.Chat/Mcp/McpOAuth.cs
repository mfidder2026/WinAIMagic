using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinAIMagic.Chat;

/// <summary>Logging in to a connector on the internet, the way the MCP authorization spec describes it: the server's 401 points
/// to its protected-resource metadata, that names the authorization server, the app registers itself there when the server
/// allows it (dynamic client registration), and the user logs in in their own browser with the authorization code and PKCE.
/// The answer comes back to a small listener on 127.0.0.1. Tokens go to the Credential Manager and are refreshed when they
/// expire. The app never sees the user's password.</summary>
public sealed class McpOAuth
{
    public static McpOAuth Shared { get; } = new();

    public sealed record Tokens(string Access, string? Refresh, DateTimeOffset? Expires, string TokenEndpoint, string ClientId, string? ClientSecret, string Resource);
    private sealed record Client(string ClientId, string? ClientSecret, int Port);

    public sealed class OAuthException(string message) : Exception(message);

    public HttpClient Http { get; set; } = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    /// <summary>Opens the login page in the user's browser; tests replace it.</summary>
    public Action<Uri> OpenBrowser { get; set; } = url => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    private readonly SemaphoreSlim one = new(1, 1);

    public static string Key(Uri server) => "oauth:" + server.GetLeftPart(UriPartial.Path);

    public static Tokens? TokensFor(Uri server)
    {
        try { return McpSecrets.Get(Key(server)) is { } s ? JsonSerializer.Deserialize<Tokens>(s) : null; }
        catch (JsonException) { return null; }
    }

    private static void Save(Tokens t, Uri server) => McpSecrets.Set(Key(server), JsonSerializer.Serialize(t));

    public static void LogOut(Uri server) => McpSecrets.Delete(Key(server));

    /// <summary>A valid access token, refreshed when it expired; null when there is none (the server's 401 then starts a login).</summary>
    public async Task<string?> AccessTokenAsync(Uri server, CancellationToken ct = default)
    {
        if (TokensFor(server) is not { } t) return null;
        if (t.Expires is { } exp && exp < DateTimeOffset.UtcNow.AddSeconds(60))
        {
            if (t.Refresh is not { } refresh) return null;
            try
            {
                var fresh = await TokenRequestAsync(t.TokenEndpoint, new()
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = t.ClientId, ["resource"] = t.Resource,
                }, t.ClientSecret, ct).ConfigureAwait(false);
                t = t with { Access = fresh.Access, Refresh = fresh.Refresh ?? refresh, Expires = fresh.Expires };
                Save(t, server);
            }
            catch (Exception e) when (e is OAuthException or HttpRequestException or OperationCanceledException) { return null; }
        }
        return t.Access;
    }

    // MARK: - discovery

    /// <summary>The resource_metadata address in a WWW-Authenticate header.</summary>
    public static Uri? ResourceMetadataUrl(string? challenge)
    {
        if (challenge == null) return null;
        var m = Regex.Match(challenge, "resource_metadata=\"([^\"]+)\"");
        return m.Success && Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u) ? u : null;
    }

    /// <summary>Where well-known metadata stands: https://host/.well-known/&lt;name&gt;[/path].</summary>
    public static List<Uri> WellKnown(Uri b, string name)
    {
        var root = b.GetLeftPart(UriPartial.Authority);
        var path = b.AbsolutePath == "/" ? "" : b.AbsolutePath.TrimEnd('/');
        var o = new List<Uri>();
        if (path.Length > 0) o.Add(new Uri($"{root}/.well-known/{name}{path}"));
        o.Add(new Uri($"{root}/.well-known/{name}"));
        return o;
    }

    private async Task<JsonObject?> GetJsonAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }

    // MARK: - logging in

    public async Task AuthorizeAsync(Uri server, string? challenge, string name, CancellationToken ct = default)
    {
        await one.WaitAsync(ct).ConfigureAwait(false);
        try { await AuthorizeOnce(server, challenge, name, ct).ConfigureAwait(false); }
        finally { one.Release(); }
    }

    private async Task AuthorizeOnce(Uri server, string? challenge, string name, CancellationToken ct)
    {
        // 1. the protected resource: which authorization server, which scopes
        JsonObject? resourceMeta = null;
        foreach (var u in new[] { ResourceMetadataUrl(challenge) }.OfType<Uri>().Concat(WellKnown(server, "oauth-protected-resource")))
            if (await GetJsonAsync(u, ct).ConfigureAwait(false) is { } m) { resourceMeta = m; break; }
        var resource = (resourceMeta == null ? null : McpTool.Str(resourceMeta, "resource")) ?? server.AbsoluteUri;
        var asString = (resourceMeta?["authorization_servers"] as JsonArray)?.FirstOrDefault()?.GetValue<string>()
            ?? (resourceMeta == null ? server.GetLeftPart(UriPartial.Authority) : null);
        if (asString == null || !Uri.TryCreate(asString, UriKind.Absolute, out var asUrl)) throw new OAuthException("The server asks to log in but does not say where.");
        // 2. the authorization server
        JsonObject? meta = null;
        foreach (var u in WellKnown(asUrl, "oauth-authorization-server").Concat(WellKnown(asUrl, "openid-configuration")))
            if (await GetJsonAsync(u, ct).ConfigureAwait(false) is { } m) { meta = m; break; }
        if (meta == null || McpTool.Str(meta, "authorization_endpoint") is not { } authEndpoint || McpTool.Str(meta, "token_endpoint") is not { } tokenEndpoint)
            throw new OAuthException("The login server could not be found.");
        // 3. this app as a client of that server: registered once, kept with its loopback port
        var clientKey = "oauth-client:" + asString;
        Client? client = null;
        try { client = McpSecrets.Get(clientKey) is { } s ? JsonSerializer.Deserialize<Client>(s) : null; }
        catch (JsonException) { }
        using var listener = new CallbackListener(client?.Port ?? 0);
        if (client == null || client.Port != listener.Port)
        {
            if (McpTool.Str(meta, "registration_endpoint") is not { } reg) throw new OAuthException("The login server does not let WinAIMagic register itself; add a token by hand instead.");
            client = await RegisterAsync(reg, listener.Port, ct).ConfigureAwait(false);
            McpSecrets.Set(clientKey, JsonSerializer.Serialize(client));
        }
        // 4. the login in the browser, with PKCE and a state that must come back
        var (verifier, codeChallenge) = PkcePair();
        var state = RandomString(24);
        var redirect = $"http://127.0.0.1:{listener.Port}/callback";
        var query = new List<(string, string)>
        {
            ("response_type", "code"), ("client_id", client.ClientId), ("redirect_uri", redirect), ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"), ("state", state), ("resource", resource),
        };
        if (resourceMeta?["scopes_supported"] is JsonArray scopes && scopes.Count > 0)
            query.Add(("scope", string.Join(" ", scopes.Select(x => x?.GetValue<string>()))));
        var login = authEndpoint + (authEndpoint.Contains('?') ? "&" : "?") + string.Join("&", query.Select(q => FormEscape(q.Item1) + "=" + FormEscape(q.Item2)));
        OpenBrowser(new Uri(login));
        var answer = await listener.WaitAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (answer.GetValueOrDefault("state") != state) throw new OAuthException("The answer from the browser did not belong to this login.");
        if (answer.TryGetValue("error", out var err)) throw new OAuthException("Logging in was refused: " + (answer.GetValueOrDefault("error_description") ?? err));
        if (answer.GetValueOrDefault("code") is not { } code) throw new OAuthException("Logging in was refused: no code");
        // 5. the code for tokens
        var t = await TokenRequestAsync(tokenEndpoint, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = client.ClientId, ["code_verifier"] = verifier, ["resource"] = resource,
        }, client.ClientSecret, ct).ConfigureAwait(false);
        Save(new Tokens(t.Access, t.Refresh, t.Expires, tokenEndpoint, client.ClientId, client.ClientSecret, resource), server);
    }

    private async Task<Client> RegisterAsync(string url, int port, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["client_name"] = "WinAIMagic",
            ["redirect_uris"] = new JsonArray($"http://127.0.0.1:{port}/callback"),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        try
        {
            using var resp = await Http.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            if (!resp.IsSuccessStatusCode || json == null || McpTool.Str(json, "client_id") is not { } id) throw new OAuthException("The login server does not let WinAIMagic register itself; add a token by hand instead.");
            return new Client(id, McpTool.Str(json, "client_secret"), port);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException) { throw new OAuthException("The login server does not let WinAIMagic register itself: " + e.Message); }
    }

    private async Task<(string Access, string? Refresh, DateTimeOffset? Expires)> TokenRequestAsync(string endpoint, Dictionary<string, string> form, string? secret, CancellationToken ct)
    {
        if (secret != null) form["client_secret"] = secret;
        var body = string.Join("&", form.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => FormEscape(k.Key) + "=" + FormEscape(k.Value)));
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        JsonObject json;
        try { json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? []; }
        catch (JsonException) { json = []; }
        if (!resp.IsSuccessStatusCode || McpTool.Str(json, "access_token") is not { } access)
            throw new OAuthException("No token: " + (McpTool.Str(json, "error_description") ?? McpTool.Str(json, "error") ?? "HTTP error"));
        DateTimeOffset? expires = json["expires_in"] is JsonValue v && v.TryGetValue<double>(out var secs) ? DateTimeOffset.UtcNow.AddSeconds(secs) : null;
        return (access, McpTool.Str(json, "refresh_token"), expires);
    }

    // MARK: - helpers

    public static string FormEscape(string s) => Uri.EscapeDataString(s);

    public static string RandomString(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static (string Verifier, string Challenge) PkcePair()
    {
        var verifier = RandomString(32);
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>Waits for the browser to come back to http://127.0.0.1:&lt;port&gt;/callback?code=…&amp;state=…, answers with a short
/// page, and gives the query parameters.</summary>
public sealed class CallbackListener : IDisposable
{
    private readonly TcpListener listener;
    private readonly TaskCompletionSource<Dictionary<string, string>> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stop = new();
    public int Port { get; }

    public CallbackListener(int wanted)
    {
        TcpListener l;
        try
        {
            l = new TcpListener(IPAddress.Loopback, wanted);
            l.Start();
        }
        catch (SocketException)
        {
            // the port of an earlier registration is taken: another one (the client registers again)
            l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
        }
        listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                var stream = c.GetStream();
                var buffer = new byte[16_384];
                int n = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                var first = Encoding.ASCII.GetString(buffer, 0, n).Split("\r\n")[0];
                var parts = first.Split(' ');
                var query = new Dictionary<string, string>();
                if (parts.Length >= 2 && Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var u) && u.AbsolutePath == "/callback")
                    foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = pair.Split('=', 2);
                        query[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                    }
                bool ok = query.Count > 0;
                var body = ok
                    ? "<html><body style=\"font-family:Segoe UI,sans-serif;padding:40px\"><h2>WinAIMagic</h2><p>Ingelogd. Je kunt dit venster sluiten. · Logged in. You can close this window.</p></body></html>"
                    : "<html><body>Not found</body></html>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, stop.Token).ConfigureAwait(false);
                if (ok) result.TrySetResult(query);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public async Task<Dictionary<string, string>> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { return await result.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new McpOAuth.OAuthException("Logging in took longer than 5 minutes."); }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
