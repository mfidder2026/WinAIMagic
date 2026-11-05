using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Engine;

public sealed record BenchResult
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();
    [JsonPropertyName("modelID")] public required string ModelId { get; init; }
    [JsonPropertyName("modelName")] public required string ModelName { get; init; }
    [JsonPropertyName("date")] public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
    [JsonPropertyName("loadSeconds")] public double LoadSeconds { get; init; }
    [JsonPropertyName("promptTPS")] public double PromptTps { get; init; }
    [JsonPropertyName("promptTokens")] public int PromptTokens { get; init; }
    [JsonPropertyName("decodeTPS")] public double DecodeTps { get; init; }
    [JsonPropertyName("decodeTokens")] public int DecodeTokens { get; init; }
    /// <summary>Average SSD reads while writing.</summary>
    [JsonPropertyName("ssdMBs")] public double SsdMBs { get; init; }
    /// <summary>The highest VRAM and RAM in use while writing (the Mac's one wiredGB does not exist here).</summary>
    [JsonPropertyName("vramGB")] public double VramGb { get; init; }
    [JsonPropertyName("ramGB")] public double RamGb { get; init; }
    [JsonPropertyName("cpuMoELayers")] public int CpuMoELayers { get; init; }
    /// <summary>Larger than memory: the expert cache on the card.</summary>
    [JsonPropertyName("expertCacheGB")] public double? ExpertCacheGb { get; init; }
    /// <summary>What the engine skipped: off, light or strong. A measurement from before this choice is of the full model.</summary>
    [JsonPropertyName("skipExperts")] public string SkipExperts { get; init; } = "off";
    /// <summary>A model that makes images: the seconds for one image of `ImageSize`, with loading; its image tokens per second are in
    /// DecodeTps. Null for a model that chats.</summary>
    [JsonPropertyName("imageSeconds")] public double? ImageSeconds { get; init; }
    [JsonPropertyName("imageSize")] public string? ImageSize { get; init; }
    [JsonIgnore] public bool MakesImage => ImageSeconds != null;
}

/// <summary>Runs the same measurement on each model in turn: start it, read a ~2.5K-token prompt, write 300 tokens.</summary>
public sealed class BenchmarkRunner
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly HttpClient http;
    private readonly object gate = new();
    private List<BenchResult> results = new();

    public BenchmarkRunner(HttpClient? http = null)
    {
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(1800) };
        try
        {
            if (File.Exists(Paths.BenchmarksFile))
                results = JsonSerializer.Deserialize<List<BenchResult>>(File.ReadAllText(Paths.BenchmarksFile)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public event Action? Changed;
    public IReadOnlyList<BenchResult> Results { get { lock (gate) return results.ToList(); } }
    public string? Running { get; private set; }
    public string Step { get; private set; } = "";

    /// <summary>The latest result per model and per choice of what to skip, in catalog order: they stand side by side.</summary>
    public IReadOnlyList<BenchResult> Latest(ModelCatalog catalog)
    {
        string[] order = ["off", "light", "strong"];
        lock (gate)
            return catalog.All
                .SelectMany(m => results.Where(r => r.ModelId == m.Id).GroupBy(r => r.SkipExperts).OrderBy(g => Array.IndexOf(order, g.Key)).Select(g => g.Last()))
                .ToList();
    }

    public async Task Run(IEnumerable<string> ids, EngineHost host)
    {
        foreach (var id in ids)
        {
            var model = host.Store.Catalog.Model(id);
            if (model == null || !host.Store.StatusOf(id).IsComplete) continue;
            Running = model.Name;
            if (host.Store.Catalog.Images.Contains(model))
            {
                await MeasureImage(model, host);
                continue;
            }
            SetStep("Engine starten…");
            var started = DateTime.UtcNow;
            // an engine started with another choice of what to skip would measure that choice instead
            string running = (host.Engine as TieredEngine)?.SkipExpertsInUse ?? "off";
            if (host.Engine?.Model?.Id != id || host.Engine?.State.IsRunning != true || running != host.SkipExpertsFor(model))
            {
                if (host.Engine != null) await host.Engine.StopAndWait();
                host.SelectedModelId = id;
                started = DateTime.UtcNow;
                host.StartEngine();
            }
            while (host.Engine?.State.IsRunning != true)
            {
                if (host.Engine?.State.Status == EngineStatus.Failed) break;
                if ((DateTime.UtcNow - started).TotalSeconds > 1800) break;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            var plan = host.Plan;
            if (host.Engine?.State.IsRunning != true || plan == null) continue;
            double load = (DateTime.UtcNow - started).TotalSeconds;
            try
            {
                SetStep("Prompt lezen…");
                var p = await Complete(host.Engine.BaseUrl, LongPrompt(), 16);
                SetStep("Schrijven…");
                var t0 = DateTime.UtcNow;
                var d = await Complete(host.Engine.BaseUrl, "Schrijf een verhaal van 300 woorden over een vuurtorenwachter op Terschelling.", 300);
                var during = host.Metrics.Samples.Where(s => s.T >= t0).ToList();
                var r = new BenchResult
                {
                    ModelId = id,
                    ModelName = model.Name,
                    LoadSeconds = load,
                    PromptTps = p.PromptTps,
                    PromptTokens = p.PromptTokens,
                    DecodeTps = d.DecodeTps,
                    DecodeTokens = d.DecodeTokens,
                    SsdMBs = during.Count == 0 ? 0 : during.Average(s => s.SsdMBs),
                    VramGb = during.Count == 0 ? 0 : during.Max(s => s.VramUsedGB),
                    RamGb = during.Count == 0 ? 0 : during.Max(s => s.RamUsedGB),
                    CpuMoELayers = plan.CpuMoELayers,
                    ExpertCacheGb = plan.ExpertCache / 1e9,
                    SkipExperts = (host.Engine as TieredEngine)?.SkipExpertsInUse ?? "off",
                };
                lock (gate) results.Add(r);
                Save();
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
            {
                SetStep("Fout: " + LogFilter.Clean(e.Message));
            }
        }
        Running = null;
        SetStep("");
    }

    /// <summary>The same image on every PC: a fixed description and number, with loading. The chat engine stops first, as on the page.</summary>
    public const string ImagePrompt = "a lighthouse on a rocky coast at dusk, waves, photo";
    public static readonly ImageSize ImageSize = new(1024, 1024);

    private async Task MeasureImage(ModelInfo model, EngineHost host)
    {
        SetStep("Afbeelding maken…");
        string saved = Settings.Shared.ImageModel;
        string? root = host.Images.Root;
        Settings.Shared.ImageModel = model.Id;
        host.Images.Root = Path.Combine(Path.GetTempPath(), "winaimagic-measure");
        var t0 = DateTime.UtcNow;
        try
        {
            var r = await host.MakeImage(ImagePrompt, ImageSize, seed: 7, measuring: true);
            if (r == null)
            {
                SetStep("Fout: " + (host.Images.Error ?? ""));
                return;
            }
            foreach (var f in new[] { r.Path, Path.ChangeExtension(r.Path, ".json") })
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var during = host.Metrics.Samples.Where(s => s.T >= t0).ToList();
            lock (gate)
                results.Add(new BenchResult
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    DecodeTps = r.TokensPerSecond ?? 0,
                    DecodeTokens = ImagePlanner.TokensPerStep(ImageSize) * 4,
                    SsdMBs = during.Count == 0 ? 0 : during.Average(s => s.SsdMBs),
                    VramGb = during.Count == 0 ? 0 : during.Max(s => s.VramUsedGB),
                    RamGb = during.Count == 0 ? 0 : during.Max(s => s.RamUsedGB),
                    ImageSeconds = r.Seconds,
                    ImageSize = ImageSize.Key,
                });
            Save();
        }
        finally
        {
            Settings.Shared.ImageModel = saved;
            host.Images.Root = root;
        }
    }

    private void SetStep(string text)
    {
        Step = text;
        Changed?.Invoke();
    }

    private readonly record struct Timings(double PromptTps, int PromptTokens, double DecodeTps, int DecodeTokens);

    private async Task<Timings> Complete(Uri baseUrl, string prompt, int maxTokens)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "v1/chat/completions"));
        if (!string.IsNullOrEmpty(Settings.Shared.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.Shared.ApiKey);
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.7,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            // one client path for both engines: the budget per request, also 0
            ["thinking_budget_tokens"] = 0,
        };
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        return ParseTimings(await res.Content.ReadAsStringAsync());
    }

    private static Timings ParseTimings(string json)
    {
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("timings", out var t)) return default;
        double D(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        int I(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new Timings(D("prompt_per_second"), I("prompt_n"), D("predicted_per_second"), I("predicted_n"));
    }

    /// <summary>~2.5K tokens of varied text; a fresh first line each run so the prompt cache never hides the reading cost.</summary>
    public static string LongPrompt()
    {
        string[] topics =
        {
            "de geschiedenis van de Nederlandse waterbouw", "hoe een mixture-of-experts taalmodel werkt",
            "de werking van geheugen in een videokaart", "het klimaat in de Waddenzee",
            "de ontwikkeling van open-source software", "de natuurkunde van zonnepanelen",
        };
        string[] groups = { "regio's", "bedrijven", "studenten", "gemeenten" };
        var text = new StringBuilder($"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0:0.000}] Lees de volgende notities en vat ze samen in drie zinnen.\n\n".Replace(",", "."));
        for (int i = 0; i < 36; i++)
        {
            text.Append($"Notitie {i + 1}: over {topics[i % topics.Length]}. Dit onderwerp raakt aan techniek, economie en maatschappij. ");
            text.Append($"Onderzoekers noemen in rapport {1000 + i * 7} vooral de kosten, de betrouwbaarheid en de gevolgen op lange termijn. ");
            text.Append($"In {1950 + i} werd een belangrijke stap gezet, en sindsdien zijn er {i * 3 + 2} grote verbeteringen geweest. ");
            text.Append($"Critici wijzen op risico's; voorstanders op kansen voor {groups[i % 4]}.\n");
        }
        return text.ToString();
    }

    public void Clear()
    {
        lock (gate) results = new();
        Save();
    }

    private void Save()
    {
        try
        {
            var tmp = Paths.BenchmarksFile + ".tmp";
            string json;
            lock (gate) json = JsonSerializer.Serialize(results, Pretty);
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.BenchmarksFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    /// <summary>Exporteer meting: one file with hardware, profile and results, without paths or names. Nothing leaves by itself.</summary>
    public void Export(string path, SystemInfo system, IGpuBackend backend)
    {
        var doc = new JsonObject
        {
            ["app"] = Paths.AppVersion,
            ["hardware"] = new JsonObject
            {
                ["gpu"] = system.Gpu,
                ["vramGB"] = Math.Round(system.VramTotal / 1e9, 1),
                ["ramGB"] = Math.Round(system.RamTotal / 1e9, 1),
                ["cpu"] = system.Cpu,
                ["performanceCores"] = system.PerformanceCores,
                ["windows"] = system.WindowsVersion,
                ["driver"] = backend.Adapters.FirstOrDefault()?.Driver,
            },
            ["backend"] = new JsonObject
            {
                ["id"] = backend.Id,
                ["capability"] = backend.Capability,
                ["profile"] = backend.ProfileKey,
                ["vramReserveMiB"] = (long)(system.VramReserve >> 20),
                ["ramMarginGiB"] = Math.Round(system.RamMargin / (double)(1UL << 30), 1),
            },
            ["results"] = JsonNode.Parse(JsonSerializer.Serialize(Results)),
        };
        File.WriteAllText(path, doc.ToJsonString(Pretty), new UTF8Encoding(false));
    }
}
