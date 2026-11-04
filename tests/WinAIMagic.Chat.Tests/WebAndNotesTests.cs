using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

public class SummaryShapeTests : ChatTest
{
    [Fact]
    public void AContinuationOfTheConversationIsNoSummary()
    {
        Assert.True(ChatClient.LooksLikeTranscript("Code gevonden.\nAssistent gebruikt read_file: {}\nResultaat: Wrote 243 bytes"));
        Assert.True(ChatClient.LooksLikeTranscript("Gebruiker: en verder?"));
        Assert.False(ChatClient.LooksLikeTranscript("# Samenvatting\n## Doel\n- De resultaten staan in codes.txt."));
    }
}

public class WebToolTests : ChatTest
{
    private static HttpResponseMessage Ok(string text, string type = "text/html") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, type),
    };

    private FakeWeb Web(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var web = new FakeWeb { Answer = answer };
        WebTools.Http = new HttpClient(web);
        return web;
    }

    private static Task<WebTools.Result> Search(string q) =>
        WebTools.RunAsync(new ToolCall("t", "web_search", new JsonObject { ["query"] = q }.ToJsonString()));

    private const string DuckHtml = "<div class=\"result\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa&amp;rut=x\">Example &amp; Co</a>\n<a class=\"result__snippet\" href=\"x\">Een <b>voorbeeld</b> snippet</a></div>";

    [Fact]
    public void DuckDuckGoParsing()
    {
        var hits = WebTools.ParseDuckDuckGo(DuckHtml);
        Assert.Single(hits);
        Assert.Equal("https://example.com/a", hits[0].Url);
        Assert.Equal("Example & Co", hits[0].Title);
        Assert.Equal("Een voorbeeld snippet", hits[0].Snippet);
    }

    [Fact]
    public void DuckDuckGoKeepsEightAndDropsAds()
    {
        var one = "<a class=\"result__a\" href=\"https://x.example/{0}\">t{0}</a>";
        var html = string.Concat(Enumerable.Range(1, 12).Select(i => string.Format(one, i)));
        Assert.Equal(8, WebTools.ParseDuckDuckGo(html).Count);
        Assert.Empty(WebTools.ParseDuckDuckGo("<a class=\"result__a\" href=\"https://duckduckgo.com/y.js?ad=1\">reclame</a>"));
        Assert.Empty(WebTools.ParseDuckDuckGo("<a class=\"result__a\" href=\"/relatief\">x</a>"));
        Assert.Equal("https://x.example/t", WebTools.ParseDuckDuckGo("<a class=\"result__a\" href=\"https://x.example/t\"><b>vet</b></a>")[0].Title);
    }

    [Fact]
    public void HtmlText() => Assert.Equal("Hallo\nwereld !", HTML.Text("<p>Hallo</p><script>x()</script><p>wereld&nbsp;!</p>"));

    [Fact]
    public void HtmlMoreOfIt()
    {
        Assert.Equal("Titel", HTML.Title("<head><TITLE lang=\"nl\">Titel</title></head>"));
        Assert.Null(HTML.Title("<p>geen</p>"));
        Assert.Equal("<article a=1>inhoud", HTML.MainContent("<body>rand<article a=1>inhoud</article>rand</body>"));
        Assert.Equal("<main>m", HTML.MainContent("<body><main>m</main></body>"));
        Assert.Equal("<body>b", HTML.MainContent("<html><body>b</body></html>"));
        Assert.Equal("kaal", HTML.MainContent("kaal"));
        Assert.Equal("a\n\nb", HTML.Text("a<br><br><br><br>b"));
        Assert.Equal("menu weg", HTML.Text("menu<nav>links</nav> <footer>voet</footer>weg"));
        Assert.Equal("< > \" ' ' & …", HTML.Text("&lt; &gt; &quot; &#39; &#x27; &amp; &hellip;"));
        Assert.Equal("ab", HTML.DecodeEntities("a&#123;b"));
    }

    /// <summary>Refused engines, spacing and blocks: the port of the search route tests.</summary>
    [Fact]
    public async Task ARefusalGoesToTheOtherEngineAndStaysThereForAWhile()
    {
        var route = new SearchRoute(0, 600);
        var log = new List<string>();
        Task<List<WebTools.Hit>> Primary(string _, CancellationToken __)
        {
            log.Add("duck");
            throw new WebTools.Blocked();
        }
        Task<List<WebTools.Hit>> Fallback(string _, CancellationToken __)
        {
            log.Add("bing");
            return Task.FromResult(new List<WebTools.Hit> { new("bing", "https://bing", "") });
        }
        var first = await route.SearchAsync("a", Primary, Fallback);
        var second = await route.SearchAsync("b", Primary, Fallback);
        Assert.Equal("bing", first[0].Title);
        Assert.Equal("bing", second[0].Title);
        Assert.Equal(new[] { "duck", "bing", "bing" }, log);
    }

    [Fact]
    public async Task AnEmptyAnswerAsksTheOtherEngineToo()
    {
        var found = await new SearchRoute(0).SearchAsync("a", (_, _) => Task.FromResult(new List<WebTools.Hit>()),
            (_, _) => Task.FromResult(new List<WebTools.Hit> { new("bing", "https://bing", "") }));
        Assert.Equal("bing", found[0].Title);
    }

    [Fact]
    public async Task AGoodAnswerIsEnough()
    {
        var calls = 0;
        await new SearchRoute(0).SearchAsync("a", (_, _) => Task.FromResult(new List<WebTools.Hit> { new("d", "https://d", "") }),
            (_, _) => { calls++; return Task.FromResult(new List<WebTools.Hit>()); });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SearchesAreSpacedOut()
    {
        var route = new SearchRoute(0.3);
        var hits = new List<WebTools.Hit> { new("d", "https://d", "") };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await route.SearchAsync("a", (_, _) => Task.FromResult(hits), (_, _) => Task.FromResult(new List<WebTools.Hit>()));
        await route.SearchAsync("b", (_, _) => Task.FromResult(hits), (_, _) => Task.FromResult(new List<WebTools.Hit>()));
        Assert.True(sw.Elapsed.TotalSeconds > 0.25);
    }

    [Fact]
    public void BlockPageIsRecognized()
    {
        Assert.True(WebTools.IsBlocked(202, ""));
        Assert.True(WebTools.IsBlocked(403, ""));
        Assert.True(WebTools.IsBlocked(429, ""));
        Assert.True(WebTools.IsBlocked(200, "<div class=\"anomaly-modal__title\">"));
        Assert.True(WebTools.IsBlocked(200, "Unfortunately, bots use DuckDuckGo too."));
        Assert.False(WebTools.IsBlocked(200, "<a class=\"result__a\">"));
    }

    [Fact]
    public void BingResults()
    {
        var target = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://www.example.org/pagina?a=1")).Replace('+', '-').Replace('/', '_').Replace("=", "");
        var html = $"<ol><li class=\"b_algo\" data-x=\"1\"><div>k</div><h2 class=\"t\"><a target=\"_blank\" href=\"https://www.bing.com/ck/a?!&amp;&amp;p=abc&amp;u=a1{target}&amp;ntb=1\" h=\"ID\">Een <strong>titel</strong></a></h2><p>x</p></li><li class=\"b_algo\"><h2><a href=\"https://direct.example/b\">Direct</a></h2></li><li class=\"b_ad\"><h2><a href=\"https://reclame.example\">Reclame</a></h2></li></ol>";
        var hits = WebTools.ParseBing(html);
        Assert.Equal(new[] { "https://www.example.org/pagina?a=1", "https://direct.example/b" }, hits.Select(h => h.Url));
        Assert.Equal("Een titel", hits[0].Title);
        Assert.Null(WebTools.BingTarget("https://www.bing.com/ck/a?u=zonder"));
        Assert.Null(WebTools.BingTarget("/relatief"));
    }

    [Theory]
    [InlineData("nl", "kl=nl-nl", "setlang=nl&cc=NL")]
    [InlineData("en", "kl=us-en", "setlang=en&cc=US")]
    [InlineData("de", "kl=de-de", "setlang=de&cc=DE")]
    [InlineData("fr", "kl=fr-fr", "setlang=fr&cc=FR")]
    public async Task TheRegionFollowsTheLanguage(string language, string kl, string bing)
    {
        Settings.Shared.Language = language;
        var web = Web(r => r.RequestUri!.Host.Contains("duckduckgo") ? Ok("") : Ok(""));
        await Search("x y");
        var duck = web.Requests.FindIndex(r => r.RequestUri!.Host == "html.duckduckgo.com");
        Assert.Contains(kl, web.Bodies[duck]);
        Assert.Contains("q=x%20y", web.Bodies[duck]);
        Assert.Equal(HttpMethod.Post, web.Requests[duck].Method);
        Assert.Contains(bing, web.Requests.Single(r => r.RequestUri!.Host == "www.bing.com").RequestUri!.Query);
    }

    [Fact]
    public async Task TheUserAgentIsEdge()
    {
        var web = Web(_ => Ok("<title>t</title>"));
        await WebTools.FetchAsync("https://x.example/p");
        var agent = web.Requests[0].Headers.UserAgent.ToString();
        Assert.Contains("Edg/", agent);
        Assert.Contains("Windows NT", agent);
        Assert.DoesNotContain("Macintosh", agent);
    }

    [Fact]
    public async Task ResultsAsTextAndNothingFound()
    {
        Web(r => r.RequestUri!.Host == "html.duckduckgo.com" ? Ok(DuckHtml) : Ok(""));
        var found = await Search("voorbeeld");
        Assert.Equal("[1] Example & Co\nhttps://example.com/a\nEen voorbeeld snippet", found.Text);
        Assert.Equal("https://example.com/a", found.Sources[0].Url);
        Web(_ => Ok(""));
        Assert.Equal("No results for \"niets\".", (await Search("niets")).Text);
    }

    /// <summary>Both engines refuse: this exact text, not "no results".</summary>
    [Fact]
    public async Task BothRefuse()
    {
        Web(r => new HttpResponseMessage(r.RequestUri!.Host == "html.duckduckgo.com" ? (HttpStatusCode)202 : HttpStatusCode.Forbidden) { Content = new StringContent("") });
        var o = await Search("x");
        Assert.Equal("Tool error: De zoekmachines weigeren nu; probeer het straks opnieuw of vul onder Instellingen een Brave-sleutel in.", o.Text);
        Assert.Empty(o.Sources);
    }

    [Fact]
    public async Task BlockedDuckGoesToBingForTenMinutes()
    {
        int duck = 0, bing = 0;
        var bingPage = "<li class=\"b_algo\"><h2><a href=\"https://direct.example/b\">Direct</a></h2></li>";
        Web(r =>
        {
            if (r.RequestUri!.Host == "html.duckduckgo.com") { duck++; return Ok("<div class=\"anomaly-modal__title\">"); }
            bing++;
            return Ok(bingPage);
        });
        Assert.Contains("Direct", (await Search("a")).Text);
        Assert.Contains("Direct", (await Search("b")).Text);
        Assert.Equal(1, duck);
        Assert.Equal(2, bing);
    }

    [Fact]
    public async Task BraveWithAKey()
    {
        Settings.Shared.BraveKey = "sleutel";
        var web = Web(_ => Ok("{\"web\":{\"results\":[{\"title\":\"T\",\"url\":\"https://t\",\"description\":\"<b>d</b>\"}]}}", "application/json"));
        var o = await Search("x");
        Assert.Equal("[1] T\nhttps://t\nd", o.Text);
        Assert.Null(o.Notice);
        Assert.Equal("sleutel", web.Requests[0].Headers.GetValues("X-Subscription-Token").Single());
        Assert.Contains("q=x&count=8", web.Requests[0].RequestUri!.Query);
        Assert.Single(web.Requests);
    }

    /// <summary>A failing Brave used to look like "no results": now a message, and DuckDuckGo answers.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task BraveFailingSaysSoAndGoesOnWithDuckDuckGo(int status)
    {
        Settings.Shared.BraveKey = "sleutel";
        Web(r => r.RequestUri!.Host == "api.search.brave.com"
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":\"nee\"}") }
            : Ok(DuckHtml));
        var o = await Search("x");
        Assert.Equal($"Brave Search gaf een fout (HTTP {status}); er wordt gezocht met DuckDuckGo.", o.Notice);
        Assert.StartsWith(o.Notice!, o.Text);
        Assert.Contains("Example & Co", o.Text);
        Assert.Single(o.Sources);
        Assert.DoesNotContain("No results", o.Text);
    }

    [Fact]
    public async Task BraveUnreachableAlsoSaysSo()
    {
        Settings.Shared.BraveKey = "sleutel";
        Web(r => r.RequestUri!.Host == "api.search.brave.com" ? throw new HttpRequestException("weg") : Ok(DuckHtml));
        var o = await Search("x");
        Assert.Contains("Brave Search gaf een fout (weg)", o.Text);
        Assert.Contains("Example & Co", o.Text);
    }

    [Fact]
    public async Task BraveFindingNothingIsNoFailure()
    {
        Settings.Shared.BraveKey = "sleutel";
        var web = Web(_ => Ok("{\"web\":{\"results\":[]}}"));
        Assert.Equal("No results for \"x\".", (await Search("x")).Text);
        Assert.Single(web.Requests);
    }

    [Fact]
    public async Task FetchAPage()
    {
        Web(_ => Ok("<html><head><title>Kop</title></head><body><nav>menu</nav><p>Inhoud " + new string('z', 13_000) + "</p></body></html>"));
        var (title, body) = await WebTools.FetchAsync("https://x.example/p");
        Assert.Equal("Kop", title);
        Assert.StartsWith("Inhoud", body);
        Assert.Equal(12_000, body.Length);
        var o = await WebTools.RunAsync(new ToolCall("t", "fetch_url", "{\"url\":\"https://x.example/p\"}"));
        Assert.StartsWith("# Kop\nInhoud", o.Text);
        Assert.Equal(new Source("Kop", "https://x.example/p"), o.Sources[0]);
    }

    [Fact]
    public async Task FetchSomethingElseThanHtml()
    {
        Web(_ => Ok(new string('t', 20_000), "text/plain"));
        var (title, body) = await WebTools.FetchAsync("https://x.example/map/lees%20mij.txt");
        Assert.Equal("lees mij.txt", title);
        Assert.Equal(12_000, body.Length);
    }

    [Fact]
    public async Task AtMostThreeMiBIsRead()
    {
        // the marker lies behind the 3 MiB: it must not be seen
        var page = "<html><title>Groot</title>" + new string('a', 3 << 20) + "EINDE";
        Web(_ => Ok(page, "text/plain"));
        var (_, body) = await WebTools.FetchAsync("https://x.example/groot");
        Assert.DoesNotContain("EINDE", body);
    }

    [Theory]
    [InlineData("ftp://x.example/a")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("geen url")]
    public async Task OnlyHttp(string url)
    {
        var web = Web(_ => Ok("nooit"));
        var o = await WebTools.RunAsync(new ToolCall("t", "fetch_url", new JsonObject { ["url"] = url }.ToJsonString()));
        Assert.StartsWith("Tool error:", o.Text);
        Assert.Empty(web.Requests);
    }

    [Fact]
    public void Labels()
    {
        Assert.Equal("Zoeken: kat", WebTools.Label(new ToolCall("t", "web_search", "{\"query\":\"kat\"}")));
        Assert.Equal("Lezen: x.example", WebTools.Label(new ToolCall("t", "fetch_url", "{\"url\":\"https://x.example/p\"}")));
        Assert.Equal("Lezen: …", WebTools.Label(new ToolCall("t", "fetch_url", "{}")));
        Assert.Equal("iets", WebTools.Label(new ToolCall("t", "iets", "kapot")));
    }

    [Fact]
    public void Definitions()
    {
        var d = WebTools.Definitions;
        Assert.Equal("web_search", d[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("query", d[0]!["function"]!["parameters"]!["required"]![0]!.GetValue<string>());
        Assert.Equal("fetch_url", d[1]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("url", d[1]!["function"]!["parameters"]!["required"]![0]!.GetValue<string>());
    }
}

/// <summary>WINAIMAGIC.md: made when it is missing, keeps the user's text, and what was learned does not pile up without end.</summary>
public class WorkspaceNotesTests : ChatTest
{
    private readonly string f;
    public WorkspaceNotesTests() { f = NewFolder("notes"); }

    [Fact]
    public void MadeWhenMissingAndLessonsAdded()
    {
        WorkspaceNotes.CreateIfNeeded(f);
        Assert.True(File.Exists(Path.Combine(f, "WINAIMAGIC.md")));
        var text = File.ReadAllText(Path.Combine(f, "WINAIMAGIC.md"));
        Assert.StartsWith("# Notities voor WinAIMagic\n\nHet model leest dit bestand", text);
        Assert.EndsWith("## Geleerd\n<!-- winaimagic:lessons -->\n", text);
        Assert.Equal(2, WorkspaceNotes.Add(["- python werkt hier niet", "- VICE staat in Program Files"], f).Count);
        Assert.Empty(WorkspaceNotes.Add(["- PYTHON werkt hier niet"], f));
        Assert.Contains("- VICE staat in Program Files", WorkspaceNotes.ForPrompt(f));
    }

    /// <summary>A folder from a Mac: its MACAIMAGIC.md stays the notes while it is the only one, and keeps its marker.</summary>
    [Fact]
    public void UsersFileStaysAndTheMacNameWorks()
    {
        var mac = Path.Combine(f, "MACAIMAGIC.md");
        File.WriteAllText(mac, "Start VICE met Start-Process.\n\n## Geleerd\n<!-- macaimagic:lessons -->\n- oud geleerd op de Mac\n");
        WorkspaceNotes.CreateIfNeeded(f);
        Assert.False(File.Exists(Path.Combine(f, "WINAIMAGIC.md")), "the Mac file is the notes file");
        WorkspaceNotes.Add(["- iets geleerd"], f);
        var text = File.ReadAllText(mac);
        Assert.StartsWith("Start VICE met Start-Process.", text);
        Assert.Contains("<!-- macaimagic:lessons -->\n- oud geleerd op de Mac\n- iets geleerd\n", text);
        Assert.DoesNotContain("winaimagic:lessons", text);
        Assert.True(WorkspaceNotes.IsNotes(mac));
        Assert.True(WorkspaceNotes.IsNotes(Path.Combine(f, "winaimagic.md")));
        Assert.False(WorkspaceNotes.IsNotes(Path.Combine(f, "rapport.md")));
    }

    [Fact]
    public void WindowsNameWinsOverMacName()
    {
        File.WriteAllText(Path.Combine(f, "MACAIMAGIC.md"), "mac");
        File.WriteAllText(Path.Combine(f, "WINAIMAGIC.md"), "windows");
        Assert.Equal("windows", WorkspaceNotes.ForPrompt(f));
    }

    [Fact]
    public void AUsersFileWithoutTheSectionGetsItBelowTheirText()
    {
        File.WriteAllText(Path.Combine(f, "WINAIMAGIC.md"), "Eigen tekst");
        WorkspaceNotes.Add(["- nieuw geleerd"], f);
        Assert.Equal("Eigen tekst\n\n## Geleerd\n<!-- winaimagic:lessons -->\n- nieuw geleerd\n", File.ReadAllText(Path.Combine(f, "WINAIMAGIC.md")));
    }

    [Fact]
    public void OldestLessonsGoFirst()
    {
        for (int i = 1; i <= WorkspaceNotes.MaxLessons + 5; i++) WorkspaceNotes.Add([$"- les nummer {i} van de reeks"], f);
        var text = WorkspaceNotes.ForPrompt(f);
        Assert.DoesNotContain("- les nummer 1 van de reeks", text);
        Assert.Contains($"- les nummer {WorkspaceNotes.MaxLessons + 5} van de reeks", text);
        Assert.Equal(60, File.ReadAllLines(WorkspaceNotes.PathIn(f)).Count(l => l.StartsWith("- ")));
    }

    [Fact]
    public void TheUsersOwnLinesBelowTheMarkerStay()
    {
        File.WriteAllText(Path.Combine(f, "WINAIMAGIC.md"), "<!-- winaimagic:lessons -->\r\nmijn eigen regel\r\n");
        for (int i = 1; i <= 62; i++) WorkspaceNotes.Add([$"- les nummer {i} van de reeks"], f);
        var text = File.ReadAllText(Path.Combine(f, "WINAIMAGIC.md"));
        Assert.Contains("mijn eigen regel\n", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void OnlyLessonLinesCount()
    {
        Assert.Empty(WorkspaceNotes.Lessons("GEEN"));
        var answer = "Dit heb ik geleerd:\n- python ontbreekt op deze pc\n- python ontbreekt op deze pc\nTot slot iets anders.";
        Assert.Equal(new[] { "- python ontbreekt op deze pc" }, WorkspaceNotes.Lessons(answer));
    }

    [Fact]
    public void FiveAtMostAndNotTooLong()
    {
        var many = string.Join("\r\n", Enumerable.Range(1, 8).Select(i => $"  - les nummer {i} van de reeks"));
        var lessons = WorkspaceNotes.Lessons(many);
        Assert.Equal(5, lessons.Count);
        Assert.Equal("- les nummer 1 van de reeks", lessons[0]);
        Assert.Empty(WorkspaceNotes.Lessons("- kort"));
        Assert.Empty(WorkspaceNotes.Lessons("- " + new string('x', 299)));
        Assert.Single(WorkspaceNotes.Lessons("- " + new string('x', 298)));
    }

    /// <summary>Long notes: the start (the user's instructions) and the end (the newest lessons), 8000 together.</summary>
    [Fact]
    public void LongNotesForThePrompt()
    {
        File.WriteAllText(Path.Combine(f, "WINAIMAGIC.md"), "BEGIN" + new string('x', 9000) + "EINDE");
        var p = WorkspaceNotes.ForPrompt(f)!;
        Assert.StartsWith("BEGIN", p);
        Assert.EndsWith("EINDE", p);
        Assert.Contains("\n…\n", p);
        Assert.Equal(8000 + 3, p.Length);
        File.WriteAllText(Path.Combine(f, "WINAIMAGIC.md"), string.Concat(Enumerable.Repeat("🙂", 5000)));
        var emoji = WorkspaceNotes.ForPrompt(f)!;
        Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(emoji)));
    }

    [Fact]
    public void ModifiedAndEmpty()
    {
        Assert.Null(WorkspaceNotes.Modified(f));
        Assert.Null(WorkspaceNotes.ForPrompt(f));
        File.WriteAllText(Path.Combine(f, "WINAIMAGIC.md"), "");
        Assert.NotNull(WorkspaceNotes.Modified(f));
        Assert.Null(WorkspaceNotes.ForPrompt(f));
    }
}

public class MarkdownTests
{
    [Fact]
    public void Blocks()
    {
        var blocks = MarkdownLite.Blocks("# Titel\nHallo **wereld**\n\n```swift\nlet x = 1\n```\n- punt");
        Assert.Equal(4, blocks.Count);
        Assert.Equal(new MarkdownBlock.Heading(1, "Titel"), blocks[0]);
        Assert.Equal(new MarkdownBlock.Text("Hallo **wereld**"), blocks[1]);
        Assert.Equal(new MarkdownBlock.Code("swift", "let x = 1"), blocks[2]);
        Assert.Equal(new MarkdownBlock.Text("• punt"), blocks[3]);
    }

    [Fact]
    public void TheDialect()
    {
        // no tables: they stay text
        Assert.Equal(new MarkdownBlock.Text("| a | b |\n|---|---|"), MarkdownLite.Blocks("| a | b |\n|---|---|")[0]);
        // a fence that never closes still is code
        Assert.Equal(new MarkdownBlock.Code("", "a\n\nb"), MarkdownLite.Blocks("```\na\n\nb").Single());
        // fences may be indented, a language has its spaces trimmed
        Assert.Equal(new MarkdownBlock.Code("py", "x"), MarkdownLite.Blocks("  ``` py \nx\n  ```").Single());
        // inside code nothing counts
        Assert.Equal(new MarkdownBlock.Code("", "# geen kop\n- geen punt"), MarkdownLite.Blocks("```\n# geen kop\n- geen punt\n```").Single());
        // heading levels, without a limit; # without a space is text
        Assert.Equal(new MarkdownBlock.Heading(3, "Kop"), MarkdownLite.Blocks("### Kop")[0]);
        Assert.Equal(new MarkdownBlock.Heading(7, "x"), MarkdownLite.Blocks("####### x")[0]);
        Assert.Equal(new MarkdownBlock.Text("#geen"), MarkdownLite.Blocks("#geen")[0]);
        Assert.Equal(new MarkdownBlock.Text("#a b"), MarkdownLite.Blocks("#a b")[0]);
        // bullets keep their indent, * too
        Assert.Equal(new MarkdownBlock.Text("  • a\n• b\n-c"), MarkdownLite.Blocks("  - a\n* b\n-c")[0]);
        // lines of a paragraph stay together, a blank one splits
        Assert.Equal(2, MarkdownLite.Blocks("a\nb\n \nc").Count);
        Assert.Empty(MarkdownLite.Blocks(""));
        // a model on Windows may write \r\n
        Assert.Equal(new MarkdownBlock.Code("x", "y"), MarkdownLite.Blocks("```x\r\ny\r\n```\r\n").Single());
    }

    [Fact]
    public void Inline()
    {
        Assert.Equal(new[] { new InlineRun("Hallo "), new InlineRun("wereld", Bold: true) }, MarkdownLite.Inline("Hallo **wereld**"));
        Assert.Equal(new[] { new InlineRun("a", Italic: true) }, MarkdownLite.Inline("*a*"));
        Assert.Equal(new[] { new InlineRun("a", Italic: true) }, MarkdownLite.Inline("_a_"));
        Assert.Equal(new[] { new InlineRun("a_b_c") }, MarkdownLite.Inline("a_b_c"));
        Assert.Equal(new[] { new InlineRun("x **y**", Code: true) }, MarkdownLite.Inline("`x **y**`"));
        Assert.Equal(new[] { new InlineRun("weg", Strike: true) }, MarkdownLite.Inline("~~weg~~"));
        Assert.Equal(new[] { new InlineRun("t", Link: "https://x") }, MarkdownLite.Inline("[t](https://x)"));
        Assert.Equal(new[] { new InlineRun("t", Bold: true, Link: "https://x") }, MarkdownLite.Inline("[**t**](https://x)"));
        Assert.Equal(new[] { new InlineRun("*a*") }, MarkdownLite.Inline(@"\*a\*"));
        Assert.Equal(new[] { new InlineRun("2 * 3 * 4") }, MarkdownLite.Inline("2 * 3 * 4"));
        Assert.Equal(new[] { new InlineRun("a  \n b") }, MarkdownLite.Inline("a  \n b"));
        Assert.Equal(new[] { new InlineRun("[a] (b)") }, MarkdownLite.Inline("[a] (b)"));
        Assert.Equal(new[] { new InlineRun("**open") }, MarkdownLite.Inline("**open"));
        Assert.Equal(new[] { new InlineRun("a", Bold: true), new InlineRun(" "), new InlineRun("b", Italic: true) }, MarkdownLite.Inline("**a** *b*"));
        Assert.Equal(new[] { new InlineRun("v", Bold: true), new InlineRun("i", Bold: true, Italic: true) }, MarkdownLite.Inline("**v*i***"));
    }
}

public class ImageScalerTests : ChatTest
{
    /// <summary>A PNG in memory: half transparent if asked.</summary>
    private static async Task<byte[]> Png(uint width, uint height, byte alpha)
    {
        var pixels = new byte[4 * width * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;                         // blue
            pixels[i + 1] = 0;
            pixels[i + 2] = (byte)(alpha == 255 ? 200 : 0);   // red, premultiplied
            pixels[i + 3] = alpha;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        var result = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
        return result;
    }

    private static async Task<(uint W, uint H, byte[] Bgra, Guid Format)> Decode(byte[] jpeg)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var d = await BitmapDecoder.CreateAsync(stream);
        var p = await d.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (d.PixelWidth, d.PixelHeight, p.DetachPixelData(), d.DecoderInformation.CodecId);
    }

    [Fact]
    public async Task ScalesTheLongestSideTo1344()
    {
        var o = await Decode((await ImageScaler.JpegAsync(await Png(2688, 1000, 255)))!);
        Assert.Equal(1344u, o.W);
        Assert.Equal(500u, o.H);
        Assert.Equal(BitmapDecoder.JpegDecoderId, o.Format);
        var tall = await Decode((await ImageScaler.JpegAsync(await Png(100, 2000, 255)))!);
        Assert.Equal(1344u, tall.H);
        Assert.Equal(67u, tall.W);
    }

    [Fact]
    public async Task NeverEnlarges()
    {
        var o = await Decode((await ImageScaler.JpegAsync(await Png(100, 50, 255)))!);
        Assert.Equal(100u, o.W);
        Assert.Equal(50u, o.H);
    }

    [Fact]
    public async Task TransparentBecomesWhite()
    {
        var o = await Decode((await ImageScaler.JpegAsync(await Png(64, 64, 0)))!);
        Assert.True(o.Bgra[0] > 250 && o.Bgra[1] > 250 && o.Bgra[2] > 250, $"{o.Bgra[0]} {o.Bgra[1]} {o.Bgra[2]}");
    }

    [Fact]
    public async Task NotAnImage()
    {
        Assert.Null(await ImageScaler.JpegAsync(Encoding.UTF8.GetBytes("dit is geen afbeelding")));
        Assert.Null(await ImageScaler.MakeAsync(Path.Combine(Root, "bestaat-niet.png")));
    }

    [Fact]
    public async Task AnAttachmentKeepsItsName()
    {
        var file = Path.Combine(Root, "foto.png");
        await File.WriteAllBytesAsync(file, await Png(10, 10, 255));
        var a = await ImageScaler.MakeAsync(file);
        Assert.Equal("foto.png", a!.Filename);
        Assert.Equal(0xFF, a.Jpeg[0]);
        Assert.Equal(0xD8, a.Jpeg[1]);
    }
}

public class TextLimitTests
{
    [Fact]
    public void ASurrogatePairNeverGoesInHalf()
    {
        var s = "ab🙂cd";
        Assert.Equal("ab", TextLimit.Prefix(s, 3));
        Assert.Equal("ab🙂", TextLimit.Prefix(s, 4));
        Assert.Equal("cd", TextLimit.Suffix(s, 3));
        Assert.Equal("🙂cd", TextLimit.Suffix(s, 4));
        Assert.Equal(s, TextLimit.Prefix(s, 100));
        Assert.Equal("", TextLimit.Prefix(s, 0));
    }
}
