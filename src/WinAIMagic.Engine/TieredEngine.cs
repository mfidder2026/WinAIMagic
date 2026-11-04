using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>
/// The main engine (VRAM + RAM + SSD): the bundled Python runs the serve layer, which runs the engine exe. The API key and the
/// rest of the settings go through a config file in the model's folder, never over the command line.
///
/// Stopping: the serve layer reads --parent-pipe as "the program that started it ended" and stops as on Ctrl+C: the request in
/// progress answers, the engine gets QUIT and saves its expert profile. A CTRL_BREAK_EVENT would not do here: the engine exe shares
/// the server's process group and has no handler, so it would die on the spot and its profile with it. So closing the pipe is the
/// stop. After the wait the server is ended; the exe then sees the end of its input, saves as on QUIT, and ends by itself.
/// </summary>
public sealed class TieredEngine : EngineBase
{
    /// <summary>The upstream server's name for this engine family; an argument between us and the server, it is not shown.</summary>
    private const string ServerEngineName = "wam";
    private string? configFile;
    private string RawLog => LogPath + ".raw";
    private CancellationTokenSource? tailCts;
    private long rawOffset;

    public TieredEngine(int? port = null, string? logFile = null) : base(port, logFile) { }

    public override EngineKind Kind => EngineKind.Tiered;
    public override EngineCapabilities Capabilities => new(Vision: ReadsImages, Cluster: false, ExpertTiers: true);

    public static string Root => Path.Combine(Paths.EngineRoot, "tiered");
    /// <summary>GLM's image encoder: a process of its own, started when a picture arrives. DeepSeek and MiniMax run their tower in the engine.</summary>
    public static string EncoderExe => Path.Combine(Root, "bin", "wam-vision.exe");

    protected override string? Missing(EngineStart s)
    {
        if (ExecutableOverride != null) return null;
        if (s.Model.Tier == null) return L($"{s.Model.Name} draait niet op de hoofdengine.");
        if (!File.Exists(PackBuilder.PythonExe) || !File.Exists(Path.Combine(Root, "bin", s.Model.Tier.Exe)))
            return L("Engine niet gevonden. Installeer WinAIMagic opnieuw.");
        return PackBuilder.WhyNotReady(s.Model);
    }

    /// <summary>The engine's own arguments per model, as upstream's setup writes them.</summary>
    public static List<string> EngineArguments(EngineStart s, ulong vramReserve)
    {
        var plan = s.Plan;
        var dir = Paths.ModelDir(s.Model.Id);
        var pack = PackBuilder.PackDir(s.Model);
        string context = plan.ContextSize.ToString(CultureInfo.InvariantCulture);
        string reserveMib = (vramReserve >> 20).ToString(CultureInfo.InvariantCulture);
        var args = new List<string>();
        switch (s.Model.Family)
        {
            case "deepseek":
                args.AddRange(new[] { "--pack", pack, "--max-context", context, "--tp", Math.Min(2, s.Backend.Adapters.Count).ToString(CultureInfo.InvariantCulture) });
                if (s.Backend.Adapters.Count <= 1) args.AddRange(new[] { "--threads", "on" });
                args.AddRange(new[] { "--dspark", "none" });
                if (s.Vision == null) args.AddRange(new[] { "--vision", "off" });
                args.AddRange(new[] { "--reserve-mib", reserveMib });
                break;
            case "minimax":
                args.AddRange(new[] { "--pack", pack, "--max-context", context });
                var profile = Path.Combine(Root, "data", "expert-profile-mm3.bin");
                if (File.Exists(profile)) args.AddRange(new[] { "--profile", profile });
                if (s.Vision != null) args.AddRange(new[] { "--vision", "on" });
                args.AddRange(new[] { "--kv-streaming", "on",
                                      "--reserve-gb", (vramReserve / (double)(1UL << 30)).ToString("0.0", CultureInfo.InvariantCulture) });
                break;
            default:
                string saved = Path.Combine(dir, "expert-profile.bin");
                args.AddRange(new[] { "--glm-pack", pack, "--max-context", context, "--expert-cache", "auto",
                                      "--prefill", s.Backend.Profile.Prefill, "--kv-streaming", "on", "--expert-profile-save", saved });
                // the engine fails on a profile that is not there
                if (File.Exists(saved)) args.AddRange(new[] { "--expert-profile", saved });
                args.AddRange(new[] { "--vram-reserve-mib", reserveMib });
                break;
        }
        return args;
    }

    /// <summary>The config for serve.server.</summary>
    public JsonObject Config(EngineStart s, string logPath)
    {
        var settings = Settings.Shared;
        var engineEnv = new JsonObject();
        foreach (var (k, v) in s.Backend.Environment) engineEnv[k] = v;
        var cfg = new JsonObject
        {
            ["exe"] = Path.Combine(Root, "bin", s.Model.Tier?.Exe ?? ""),
            ["args"] = new JsonArray(EngineArguments(s, s.Plan.VramReserve).Select(a => (JsonNode)a!).ToArray()),
            ["cwd"] = Root,
            ["tokenizer"] = Path.Combine(PackBuilder.PackDir(s.Model), "tokenizer"),
            ["model_name"] = s.Model.Id,
            ["gpu"] = new JsonArray(s.Backend.GpuIndices.Select(i => (JsonNode)i!).ToArray()),
            ["sampling"] = new JsonObject((s.Model.Tier?.Sampling ?? new()).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["reasoning_effort"] = settings.ReasoningEffort,
            ["lib_dirs"] = new JsonArray(s.Backend.TieredLibDirs.Select(d => (JsonNode)d!).ToArray()),
            ["host"] = Host,
            ["port"] = Port,
            ["log"] = logPath,
            ["env"] = engineEnv,
            ["thinking_budget"] = 0,
        };
        foreach (var (k, v) in s.Backend.TieredConfig) cfg[k] = v;
        // the server starts the encoder per request with pictures, in GPU memory the model lends it, and measures once how much that is
        if (s.Vision != null && s.VisionVocab != null)
            cfg["vision"] = new JsonObject { ["exe"] = EncoderExe, ["mmproj"] = s.Vision, ["model"] = s.VisionVocab, ["gpu"] = true };
        if (!string.IsNullOrEmpty(settings.ApiKey)) cfg["api_key"] = settings.ApiKey;
        return cfg;
    }

    protected override ChildStart Command(EngineStart s)
    {
        var dir = Paths.ModelDir(s.Model.Id);
        Directory.CreateDirectory(dir);
        configFile = Path.Combine(dir, "engine-config.json");
        File.WriteAllText(configFile, Config(s, RawLog).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        TailRawLog();
        return new ChildStart(PackBuilder.PythonExe, new[]
        {
            "-m", "serve.server", "--engine", ServerEngineName, "--config", configFile,
            "--port", Port.ToString(CultureInfo.InvariantCulture), "--parent-pipe",
        })
        {
            Cwd = Root,
            StdinPipe = true,
            Env = new Dictionary<string, string?>
            {
                ["PYTHONUTF8"] = "1",
                ["PYTHONUNBUFFERED"] = "1",
                ["PYTHONDONTWRITEBYTECODE"] = "1",
            },
        };
    }

    protected override string Headline(ChildStart c) => "$ " + Path.GetFileName(c.Exe) + " serve";

    protected override void AskToStop(ChildProcess child) => child.CloseStdin();

    /// <summary>The engine exe writes straight to the file named in the config; it reaches the log and the tail only through the filter.</summary>
    private void TailRawLog()
    {
        tailCts?.Cancel();
        try { File.Delete(RawLog); } catch (IOException) { }
        rawOffset = 0;
        var cts = tailCts = new CancellationTokenSource();
        var split = new LineSplitter();
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                ReadRaw(split);
                try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { break; }
            }
            ReadRaw(split);
        });
    }

    private void ReadRaw(LineSplitter split)
    {
        try
        {
            if (!File.Exists(RawLog)) return;
            using var f = new FileStream(RawLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length <= rawOffset) return;
            f.Position = rawOffset;
            var buffer = new byte[f.Length - rawOffset];
            int n = f.Read(buffer, 0, buffer.Length);
            rawOffset += n;
            // (the server's own lines also come over its stdout: only the engine's are new here)
            foreach (var line in split.Add(buffer, n))
                if (!line.StartsWith("[wam]", StringComparison.Ordinal)) AddLine(line);
        }
        catch (IOException) { }
    }

    /// <summary>Nothing of the run stays behind that holds the key or the upstream name.</summary>
    protected override void CleanUp()
    {
        tailCts?.Cancel();
        try
        {
            if (configFile != null) File.Delete(configFile);
            File.Delete(RawLog);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The server's progress messages, on stdout.</summary>
    protected override void ConsumeLine(string line)
    {
        if (line.Contains("measuring the vision encoder")) Say(L("Afbeeldingsencoder meten…"));
        else if (line.Contains("reading the model's weights")) Say(L("Gewichten laden…"));
        else if (line.Contains("loading the experts into RAM")) { Say(L("Experts in het RAM laden…")); SetProgress(0.2); }
        else if (line.Contains("experts loaded")) SetProgress(0.6);
        else if (line.Contains("filling the GPU's expert cache")) { Say(L("Expert-cache vullen…")); SetProgress(0.7); }
        else if (line.Contains("almost ready")) { Say(L("Opwarmen…")); SetProgress(0.95); }
        if (line.Contains("WARNING: ")) AddWarning(line[(line.IndexOf("WARNING: ", StringComparison.Ordinal) + 9)..].Trim());
    }

    /// <summary>/metrics is JSON here (live.tok_s), not Prometheus; /slots has no n_decoded.</summary>
    public override async Task<EngineActivity?> PollActivity() => ParseMetrics(await GetJson("metrics"));

    internal static EngineActivity? ParseMetrics(string? json)
    {
        if (json == null) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("live", out var live)) return null;
            string state = live.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
            double? Number(JsonElement parent, string name) =>
                parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            double? hit = null, temp = null;
            if (root.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Object && tiers.TryGetProperty("now", out var now))
                hit = Number(now, "vram_hit");
            if (root.TryGetProperty("hardware", out var hw) && hw.ValueKind == JsonValueKind.Object)
                temp = Number(hw, "gpu_temp") ?? (hw.TryGetProperty("gpu", out var g) ? Number(g, "temp") : null);
            bool busy = state is "generating" or "reading";
            return new EngineActivity
            {
                Busy = busy,
                TokS = Number(live, "tok_s") ?? (busy ? null : 0),
                VramHit = hit,
                GpuTemperature = temp,
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}
