using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using Xunit;
using Xunit.Abstractions;

namespace WinAIMagic.Chat.Tests;

/// <summary>Against a running engine, only with WAM_REAL_URL set (brief §14): a whole agent task in a work folder, and the
/// three API shapes on the engine that really runs. Results go to the test output.</summary>
public class RealEngineTests(ITestOutputHelper log) : ChatTest
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("WAM_REAL_URL");

    [Fact]
    public async Task AWholeTaskRunsInAFolder()
    {
        if (Url == null) return;
        var store = new ConversationStore();
        var work = NewFolder("work");
        store.Conversations[0].Workspace = work;
        Settings.Shared.RightRead = Settings.Shared.RightWrite = Settings.Shared.RightRun = "allow";
        Settings.Shared.ThinkingBudget = 4000;
        var chat = new ChatClient { ContextLimit = 65_536, ModelName = "real" };
        var started = Stopwatch.StartNew();
        chat.Send("Maak in de werkmap een Python-programma priem.py met een functie is_priem(n) en daarna een bestand test_priem.py "
            + "dat met assert controleert dat 2, 3, 97 priem zijn en 1, 4, 100 niet. Voer test_priem.py uit met python. Herstel eventuele fouten "
            + "tot het slaagt. Schrijf daarna de priemgetallen tot en met 50, gescheiden door komma's, naar uitkomst.txt, en zeg KLAAR.",
            [], store.Conversations[0].Id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(55));

        var c = store.Conversations[0];
        foreach (var m in c.Messages.Where(m => m.Role == ChatRole.Assistant))
            log.WriteLine($"[assistant] tools={string.Join(",", m.ToolCalls.Select(t => t.Name))} tok/s={m.TokensPerSecond} text={m.Text[..Math.Min(160, m.Text.Length)]}");
        log.WriteLine($"minutes {started.Elapsed.TotalMinutes:F1}, context {c.ContextTokens}, files: {string.Join(", ", Directory.GetFiles(work).Select(Path.GetFileName))}");
        var result = File.ReadAllText(Path.Combine(work, "uitkomst.txt")).Replace(" ", "").Trim();
        log.WriteLine("uitkomst: " + result);
        Assert.Equal("2,3,5,7,11,13,17,19,23,29,31,37,41,43,47", result);
        Assert.True(File.Exists(Path.Combine(work, "test_priem.py")));
    }

    [Fact]
    public async Task AConversationIsSummarizedByTheRealModel()
    {
        if (Url == null) return;
        var store = new ConversationStore();
        var id = store.Conversations[0].Id;
        Settings.Shared.ThinkingBudget = 2000;
        var chat = new ChatClient { ContextLimit = 65_536, ModelName = "real" };
        chat.Send("Ik heet Marit en mijn project heet Kompas. Onthoud dat en antwoord met één zin.", [], id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(15));
        chat.Send("De oplevering van Kompas is op 12 maart. Antwoord met één zin.", [], id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(15));
        chat.Send("Noem één risico voor zo'n oplevering, in één zin.", [], id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(15));
        await chat.CompactAsync(id, store, new Uri(Url));
        var c = store.Conversations[0];
        log.WriteLine("summary: " + c.Summary);
        Assert.False(string.IsNullOrWhiteSpace(c.Summary));
        chat.Send("Hoe heet ik, en wanneer is de oplevering? Antwoord kort.", [], id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(15));
        var answer = c.Messages.Last(m => m.Role == ChatRole.Assistant).Text;
        log.WriteLine("after summary: " + answer);
        Assert.Contains("Marit", answer);
        Assert.Contains("12", answer);
    }

    /// <summary>A task that does not fit: more file text than the 64K context holds, so the app must summarize on the way and
    /// still finish. Only with WAM_REAL_LONG as well: about an hour.</summary>
    [Fact]
    public async Task ASessionThatFillsTheContext()
    {
        if (Url == null || Environment.GetEnvironmentVariable("WAM_REAL_LONG") == null) return;
        var store = new ConversationStore();
        var work = NewFolder("work");
        store.Conversations[0].Workspace = work;
        Settings.Shared.RightRead = Settings.Shared.RightWrite = Settings.Shared.RightRun = "allow";
        Settings.Shared.ThinkingBudget = 2000;
        var random = new Random(64);
        string[] words = ["gemeente", "zaak", "besluit", "archief", "koppeling", "melding", "planning", "budget", "risico", "leverancier", "contract", "beheer", "gegevens", "proces", "oplevering", "toets"];
        var codes = new List<string>();
        for (int i = 1; i <= 30; i++)
        {
            string Noise(int n) => string.Join(" ", Enumerable.Range(0, n).Select(_ => words[random.Next(words.Length)] + random.Next(1000)));
            var code = $"K{i:00}-{random.Next(1000, 9999)}";
            codes.Add(code);
            File.WriteAllText(Path.Combine(work, $"deel{i:00}.txt"), Noise(700) + $"\nDE CODE VAN DIT DEEL IS {code}\n" + Noise(700));
        }
        var chat = new ChatClient { ContextLimit = 65_536, ModelName = "real" };
        var rounds = new List<string>();
        chat.Notice += n => log.WriteLine("notice: " + n);
        var started = Stopwatch.StartNew();
        chat.Send("In de werkmap staan deel01.txt tot en met deel30.txt. Lees ze een voor een in volgorde met read_file, zonder offset of tail. "
            + "In elk bestand staat een regel 'DE CODE VAN DIT DEEL IS ...'. Voeg na elk gelezen bestand die code als nieuwe regel toe aan codes.txt, "
            + "voordat je het volgende bestand leest. Gebruik geen run_command. Stop niet voordat alle dertig delen klaar zijn, en zeg dan KLAAR.",
            [], store.Conversations[0].Id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(170));

        var c = store.Conversations[0];
        var calls = c.Messages.Where(m => m.Role == ChatRole.Assistant).SelectMany(m => m.ToolCalls).ToList();
        var written = File.Exists(Path.Combine(work, "codes.txt")) ? File.ReadAllText(Path.Combine(work, "codes.txt")) : "";
        var found = codes.Count(written.Contains);
        log.WriteLine($"minutes {started.Elapsed.TotalMinutes:F1}, messages {c.Messages.Count}, tool calls {calls.Count} (read_file {calls.Count(t => t.Name == "read_file")}), context {c.ContextTokens}");
        log.WriteLine($"summarized: {c.Summary != null}, summary chars {c.Summary?.Length}, codes found {found} of {codes.Count}");
        log.WriteLine("summary start: " + (c.Summary is { } s ? s[..Math.Min(600, s.Length)] : ""));
        log.WriteLine("last answer: " + c.Messages.Last(m => m.Role == ChatRole.Assistant).Text);
        foreach (var m in c.Messages.Where(m => m.Role == ChatRole.Assistant).TakeLast(3))
            log.WriteLine($"[assistant] tools={string.Join(",", m.ToolCalls.Select(t => t.Name))} tok/s={m.TokensPerSecond} text={m.Text[..Math.Min(120, m.Text.Length)]}");
        Assert.False(string.IsNullOrWhiteSpace(c.Summary));
        Assert.Equal(codes.Count, found);
    }

    [Fact]
    public async Task TheThreeApisAnswer()
    {
        if (Url == null) return;
        using var http = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromMinutes(10) };
        var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Zeg alleen het woord hallo." });

        var openai = await Post(http, "/v1/chat/completions", new JsonObject { ["messages"] = messages.DeepClone(), ["max_tokens"] = 800 });
        log.WriteLine("openai: " + openai["choices"]![0]!["message"]!["content"]);
        Assert.False(string.IsNullOrWhiteSpace(openai["choices"]![0]!["message"]!["content"]?.GetValue<string>()));
        Assert.NotNull(openai["timings"]?["predicted_per_second"]);
        Assert.NotNull(openai["usage"]?["total_tokens"]);

        var claude = await Post(http, "/v1/messages", new JsonObject
        {
            ["model"] = "any", ["max_tokens"] = 800, ["messages"] = messages.DeepClone(),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "noop", ["description"] = "Does nothing.",
                ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            }),
        });
        var blocks = claude["content"]!.AsArray();
        log.WriteLine("anthropic: " + claude.ToJsonString()[..Math.Min(300, claude.ToJsonString().Length)]);
        Assert.Contains(blocks, b => b!["type"]!.GetValue<string>() is "text" or "tool_use");
        Assert.Equal("message", claude["type"]!.GetValue<string>());

        var responses = await Post(http, "/v1/responses", new JsonObject { ["input"] = "Zeg alleen het woord hallo.", ["max_output_tokens"] = 800 });
        log.WriteLine("responses: " + responses.ToJsonString()[..Math.Min(300, responses.ToJsonString().Length)]);
        Assert.Equal("completed", responses["status"]!.GetValue<string>());

        var tooLong = await http.PostAsJsonAsync("/v1/chat/completions", new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = string.Concat(Enumerable.Repeat("woord ", 90_000)) }),
            ["max_tokens"] = 10,
        });
        var refusal = await tooLong.Content.ReadAsStringAsync();
        log.WriteLine($"too long: {(int)tooLong.StatusCode} {refusal[..Math.Min(200, refusal.Length)]}");
        Assert.Equal(400, (int)tooLong.StatusCode);
        Assert.Contains("context", refusal, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] TestPicture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vision-test.png"));

    /// <summary>The picture says ZEPPELIN 4821 above a green triangle: an answer that names them has really seen it.</summary>
    private void AssertSeen(string answer)
    {
        log.WriteLine("answer: " + answer);
        Assert.Contains("4821", answer);
        Assert.Contains("zeppelin", answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(answer.Contains("green", StringComparison.OrdinalIgnoreCase) || answer.Contains("groen", StringComparison.OrdinalIgnoreCase), answer);
        Assert.True(answer.Contains("triangle", StringComparison.OrdinalIgnoreCase) || answer.Contains("driehoek", StringComparison.OrdinalIgnoreCase), answer);
    }

    [Fact]
    public async Task TheApiReadsAPicture()
    {
        if (Url == null) return;
        using var http = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromMinutes(20) };
        var answer = await Post(http, "/v1/chat/completions", new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(TestPicture) } },
                    new JsonObject { ["type"] = "text", ["text"] = "What text is written in this picture, and what colour and shape is the figure below it? Answer in one short sentence." }),
            }),
            ["max_tokens"] = 400,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["thinking_budget_tokens"] = 0,
        });
        AssertSeen(answer["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    /// <summary>The composer's path: scaled to a JPEG by ImageScaler, sent by ChatClient as data content before the text.</summary>
    [Fact]
    public async Task TheChatSendsAnAttachedPicture()
    {
        if (Url == null) return;
        var picture = await ImageScaler.MakeAsync(TestPicture, "vision-test.png");
        Assert.NotNull(picture);
        ChatClient.SeesImages = () => true;     // the real engine was started with vision
        var store = new ConversationStore();
        Settings.Shared.Thinking = false;
        var chat = new ChatClient { ContextLimit = 65_536, ModelName = "real" };
        chat.Send("Welke tekst staat er in deze afbeelding, en welke kleur en vorm heeft de figuur eronder? Antwoord in één korte zin.",
            [picture!], store.Conversations[0].Id, store, new Uri(Url));
        await chat.Completion.WaitAsync(TimeSpan.FromMinutes(20));
        AssertSeen(store.Conversations[0].Messages.Last(m => m.Role == ChatRole.Assistant).Text);
    }

    private static async Task<JsonNode> Post(HttpClient http, string path, JsonObject body)
    {
        var r = await http.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{path}: {(int)r.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }
}
