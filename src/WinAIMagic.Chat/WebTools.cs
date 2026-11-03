using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>The tools the model can call: web search and reading a page. They run in the app, not in the engine.</summary>
public static class WebTools
{
    public static JsonArray Definitions =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "web_search",
                ["description"] = "Search the web for current information. Returns titles, URLs and snippets. Use it for news, facts that may have changed, prices, documentation, or anything you are not sure about.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The search query" } },
                    ["required"] = new JsonArray("query"),
                },
            },
        },
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "fetch_url",
                ["description"] = "Read a web page and return its main text. Use it after web_search to read a result in full.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The http(s) URL to read" } },
                    ["required"] = new JsonArray("url"),
                },
            },
        },
    ];

    /// <summary>Notice: something the user should see, such as Brave failing while another engine answered.</summary>
    public sealed record Result(string Text, List<Source> Sources, string? Notice = null);

    /// <summary>All web traffic; tests put a fake behind it.</summary>
    public static HttpClient Http { get; set; } = NewClient();

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<Result> RunAsync(ToolCall call, CancellationToken ct = default)
    {
        var args = WorkspaceTools.Args(call);
        string? notice = null;
        try
        {
            switch (call.Name)
            {
                case "web_search":
                    var q = WorkspaceTools.Str(args, "query") ?? "";
                    List<Hit> hits;
                    (hits, notice) = await SearchNoting(q, n => notice = n, ct).ConfigureAwait(false);
                    var before = notice == null ? "" : notice + "\n\n";
                    if (hits.Count == 0) return new Result($"{before}No results for \"{q}\".", [], notice);
                    var text = string.Join("\n\n", hits.Select((h, i) => $"[{i + 1}] {h.Title}\n{h.Url}\n{h.Snippet}"));
                    return new Result(before + text, [.. hits.Select(h => new Source(h.Title, h.Url))], notice);
                case "fetch_url":
                    var u = WorkspaceTools.Str(args, "url") ?? "";
                    var (title, body) = await FetchAsync(u, ct).ConfigureAwait(false);
                    return new Result($"# {title}\n{body}", [new Source(title, u)]);
                default:
                    return new Result($"Unknown tool {call.Name}", []);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new Result("Tool error: stopped by the user.", []); }
        catch (Exception e)
        {
            return new Result((notice == null ? "" : notice + "\n\n") + $"Tool error: {e.Message}", [], notice);
        }
    }

    public static string Label(ToolCall call)
    {
        var args = WorkspaceTools.Args(call);
        return call.Name switch
        {
            "web_search" => L($"Zoeken: {WorkspaceTools.Str(args, "query") ?? ""}"),
            "fetch_url" => L($"Lezen: {(Uri.TryCreate(WorkspaceTools.Str(args, "url") ?? "", UriKind.Absolute, out var u) ? u.Host : "…")}"),
            _ => call.Name,
        };
    }

    // MARK: - search

    public sealed record Hit(string Title, string Url, string Snippet);

    public static async Task<List<Hit>> SearchAsync(string query, CancellationToken ct = default) =>
        (await SearchNoting(query, _ => { }, ct).ConfigureAwait(false)).Hits;

    /// <summary>A failing Brave is said out loud (no "no results"), and DuckDuckGo takes over.</summary>
    private static async Task<(List<Hit> Hits, string? Notice)> SearchNoting(string query, Action<string> note, CancellationToken ct)
    {
        string? notice = null;
        var key = Settings.Shared.BraveKey;
        if (key.Length > 0)
        {
            try { return (await Brave(query, key, ct).ConfigureAwait(false), null); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or JsonException or IOException or OperationCanceledException)
            {
                notice = ChatTexts.BraveFailed(e is OperationCanceledException ? "time-out" : e.Message);
                note(notice);
            }
        }
        return (await SearchRoute.Shared.SearchAsync(query, DuckDuckGo, Bing, ct).ConfigureAwait(false), notice);
    }

    /// <summary>DuckDuckGo answers with a page for bots after a few quick searches; that is no empty result.</summary>
    public sealed class Blocked : Exception
    {
        public Blocked() : base(L("De zoekmachines weigeren nu; probeer het straks opnieuw of vul onder Instellingen een Brave-sleutel in.")) { }
    }

    public static bool IsBlocked(int status, string html) =>
        status == 202 || status == 403 || status == 429 || html.Contains("anomaly-modal", StringComparison.Ordinal) || html.Contains("bots use DuckDuckGo", StringComparison.Ordinal);

    /// <summary>The search region follows the language of the app.</summary>
    public static string DuckDuckGoRegion(AppLanguage language) => language switch
    {
        AppLanguage.Nl => "nl-nl",
        AppLanguage.De => "de-de",
        AppLanguage.Fr => "fr-fr",
        _ => "us-en",
    };

    public static (string Language, string Country) BingRegion(AppLanguage language) => language switch
    {
        AppLanguage.Nl => ("nl", "NL"),
        AppLanguage.De => ("de", "DE"),
        AppLanguage.Fr => ("fr", "FR"),
        _ => ("en", "US"),
    };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private static async Task<(int Status, string Text)> Send(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var resp = await Http.SendAsync(req, limit.Token).ConfigureAwait(false);
        return ((int)resp.StatusCode, AtomicFile.Utf8.GetString(await resp.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false)));
    }

    /// <summary>Brave Search API, when the user has a key (more reliable than scraping).</summary>
    private static async Task<List<Hit>> Brave(string query, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) + "&count=8");
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        var (status, text) = await Send(req, RequestTimeout, ct).ConfigureAwait(false);
        if (status != 200) throw new HttpRequestException($"HTTP {status}");
        var hits = new List<Hit>();
        if (JsonNode.Parse(text)?["web"]?["results"] is JsonArray results)
            foreach (var r in results)
                hits.Add(new Hit(r?["title"]?.GetValue<string>() ?? "", r?["url"]?.GetValue<string>() ?? "", HTML.Text(r?["description"]?.GetValue<string>() ?? "")));
        return hits;
    }

    /// <summary>DuckDuckGo's HTML endpoint: no key needed.</summary>
    private static async Task<List<Hit>> DuckDuckGo(string query, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/");
        req.Headers.TryAddWithoutValidation("User-Agent", HTML.UserAgent);
        req.Content = new StringContent($"q={Uri.EscapeDataString(query)}&kl={DuckDuckGoRegion(AppLanguages.Current)}", Encoding.UTF8, "application/x-www-form-urlencoded");
        var (status, html) = await Send(req, RequestTimeout, ct).ConfigureAwait(false);
        if (IsBlocked(status, html)) throw new Blocked();
        return ParseDuckDuckGo(html);
    }

    /// <summary>Bing, for when DuckDuckGo refuses.</summary>
    private static async Task<List<Hit>> Bing(string query, CancellationToken ct)
    {
        var (language, country) = BingRegion(AppLanguages.Current);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&setlang={language}&cc={country}");
        req.Headers.TryAddWithoutValidation("User-Agent", HTML.UserAgent);
        var (status, html) = await Send(req, RequestTimeout, ct).ConfigureAwait(false);
        if (IsBlocked(status, "")) throw new Blocked();
        return ParseBing(html);
    }

    private static readonly Regex BingResult = new("<li class=\"b_algo\".*?<h2[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static List<Hit> ParseBing(string html)
    {
        var hits = new List<Hit>();
        foreach (Match m in BingResult.Matches(html).Take(8))
        {
            var url = BingTarget(HTML.DecodeEntities(m.Groups[1].Value));
            if (url == null) continue;
            var name = HTML.Text(m.Groups[2].Value);
            hits.Add(new Hit(name.Length == 0 ? url : name, url, ""));
        }
        return hits;
    }

    /// <summary>The value of one query parameter, percent-decoded.</summary>
    private static string? QueryItem(string link, string name)
    {
        int q = link.IndexOf('?');
        if (q < 0) return null;
        foreach (var part in link[(q + 1)..].Split('#')[0].Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == name) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>Result links go through a redirect, with the target in base64 after "a1" in the parameter u.</summary>
    public static string? BingTarget(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("bing.com", StringComparison.Ordinal))
            return link.StartsWith("http", StringComparison.Ordinal) ? link : null;
        var coded = QueryItem(link, "u");
        if (coded == null || !coded.StartsWith("a1", StringComparison.Ordinal)) return null;
        coded = coded[2..].Replace('-', '+').Replace('_', '/');
        coded += new string('=', (4 - coded.Length % 4) % 4);
        try
        {
            var target = AtomicFile.StrictUtf8.GetString(Convert.FromBase64String(coded));
            return target.StartsWith("http", StringComparison.Ordinal) ? target : null;
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { return null; }
    }

    public static List<Hit> ParseDuckDuckGo(string html)
    {
        var hits = new List<Hit>();
        foreach (var block in html.Split("class=\"result__a\"").Skip(1).Take(8))
        {
            int h = block.IndexOf("href=\"", StringComparison.Ordinal);
            if (h < 0) continue;
            var afterHref = block[(h + 6)..];
            int hrefEnd = afterHref.IndexOf('"');
            if (hrefEnd < 0) continue;
            var url = HTML.DecodeEntities(afterHref[..hrefEnd]);
            // result links go through a redirect: //duckduckgo.com/l/?uddg=<target>
            var target = QueryItem(url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url, "uddg");
            if (target != null) url = target;
            var titleHtml = afterHref[(hrefEnd + 1)..].Split('<', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            int gtTitle = titleHtml.IndexOf('>');
            var title = HTML.Text(gtTitle >= 0 ? titleHtml[(gtTitle + 1)..] : "");
            var snippet = "";
            const string snippetClass = "class=\"result__snippet\"";
            int s = block.IndexOf(snippetClass, StringComparison.Ordinal);
            if (s >= 0)
            {
                var rest = block[(s + snippetClass.Length)..];
                int gt = rest.IndexOf('>'), end = rest.IndexOf("</a>", StringComparison.Ordinal);
                if (gt >= 0 && end > gt) snippet = HTML.Text(rest[(gt + 1)..end]);
            }
            if (url.StartsWith("http", StringComparison.Ordinal) && !url.Contains("duckduckgo.com/y.js", StringComparison.Ordinal))
                hits.Add(new Hit(title.Length == 0 ? url : title, url, snippet));
        }
        return hits;
    }

    // MARK: - fetch

    public static async Task<(string Title, string Body)> FetchAsync(string urlString, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url) || !(url.Scheme is "http" or "https"))
            throw new ArgumentException("bad URL");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", HTML.UserAgent);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        var type = resp.Content.Headers.ContentType?.ToString() ?? "";
        await using var stream = await resp.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
        var buffer = new byte[3 << 20];
        int n = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, limit.Token).ConfigureAwait(false);
        var raw = AtomicFile.Utf8.GetString(buffer, 0, n);
        if (!type.Contains("html", StringComparison.Ordinal))
            return (url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1]) : "", TextLimit.Prefix(raw, 12_000));
        var title = HTML.Title(raw) ?? (url.Host.Length > 0 ? url.Host : urlString);
        // keep the context small: the model's prompt reading is the slow part
        return (title, TextLimit.Prefix(HTML.Text(HTML.MainContent(raw)), 12_000));
    }
}

/// <summary>Which search engine to ask: not too quickly after each other, and after a refusal the other one for a while.</summary>
public sealed class SearchRoute(double spacing = 2, double pause = 600)
{
    public static SearchRoute Shared { get; set; } = new();

    private readonly object gate = new();
    private DateTime last = DateTime.MinValue;
    private DateTime refusedUntil = DateTime.MinValue;

    public async Task<List<WebTools.Hit>> SearchAsync(string query, Func<string, CancellationToken, Task<List<WebTools.Hit>>> primary,
        Func<string, CancellationToken, Task<List<WebTools.Hit>>> fallback, CancellationToken ct = default)
    {
        TimeSpan wait;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (now >= refusedUntil)
            {
                var at = last == DateTime.MinValue ? now : last.AddSeconds(spacing);
                if (at < now) at = now;
                wait = at - now;
                last = at;
            }
            else wait = TimeSpan.MinValue;
        }
        if (wait == TimeSpan.MinValue) return await fallback(query, ct).ConfigureAwait(false);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        try
        {
            var hits = await primary(query, ct).ConfigureAwait(false);
            return hits.Count == 0 ? await fallback(query, ct).ConfigureAwait(false) : hits;
        }
        catch (WebTools.Blocked)
        {
            lock (gate) refusedUntil = DateTime.UtcNow.AddSeconds(pause);
            return await fallback(query, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            return await fallback(query, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Just enough HTML handling to turn pages into readable text.</summary>
public static class HTML
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36 Edg/156.0.0.0";

    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex TitleOpen = new("<title[^>]*>", Ic);
    private static readonly Regex[] Dropped = [.. new[] { "script", "style", "noscript", "svg", "nav", "footer", "header", "form", "aside" }
        .Select(tag => new Regex($"<{tag}[\\s\\S]*?</{tag}>", Ic))];
    private static readonly Regex LineEnds = new("<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", Ic);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new("[ \\t\\u00a0]+", RegexOptions.Compiled);
    private static readonly Regex AroundLines = new(" *\n *", RegexOptions.Compiled);
    private static readonly Regex ManyLines = new("\n{3,}", RegexOptions.Compiled);
    private static readonly Regex NumericEntity = new("&#(\\d+);", RegexOptions.Compiled);

    public static string? Title(string html)
    {
        var r = TitleOpen.Match(html);
        if (!r.Success) return null;
        int start = r.Index + r.Length, e = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
        return e < 0 ? null : Text(html[start..e]);
    }

    /// <summary>The article or main when the page has one, else the body.</summary>
    public static string MainContent(string html)
    {
        foreach (var tag in new[] { "article", "main", "body" })
        {
            var r = Regex.Match(html, $"<{tag}[\\s>]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!r.Success) continue;
            int e = html.LastIndexOf($"</{tag}>", StringComparison.OrdinalIgnoreCase);
            if (e > r.Index + r.Length) return html[r.Index..e];
        }
        return html;
    }

    public static string Text(string html)
    {
        var s = html;
        foreach (var rx in Dropped) s = rx.Replace(s, " ");
        s = LineEnds.Replace(s, "\n");
        s = Tags.Replace(s, " ");
        s = DecodeEntities(s);
        s = Spaces.Replace(s, " ");
        s = AroundLines.Replace(s, "\n");
        s = ManyLines.Replace(s, "\n\n");
        return s.Trim();
    }

    public static string DecodeEntities(string s)
    {
        var o = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&#x27;", "'")
            .Replace("&nbsp;", " ").Replace("&hellip;", "…").Replace("&amp;", "&");
        return NumericEntity.Replace(o, "");
    }
}
