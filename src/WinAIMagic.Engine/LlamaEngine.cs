using System.Globalization;
using System.Text.Json;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>Runs llama-server (the patched llama.cpp engine) with the memory plan: small models, the cluster, the limited mode.</summary>
public sealed class LlamaEngine : EngineBase
{
    public LlamaEngine(int? port = null, string? logFile = null) : base(port, logFile) { }

    public override EngineKind Kind => EngineKind.Llama;
    public override EngineCapabilities Capabilities => new(Vision: ReadsImages, Cluster: true, ExpertTiers: false);

    public static string Binary(IGpuBackend backend, string name = "llama-server.exe") =>
        Path.Combine(Paths.EngineRoot, backend.EngineFolder, name);

    protected override string? Missing(EngineStart s) =>
        File.Exists(ExecutableOverride ?? Binary(s.Backend)) ? null : L("Engine niet gevonden. Installeer WinAIMagic opnieuw.");

    public IReadOnlyList<string> Arguments(EngineStart s)
    {
        var settings = Settings.Shared;
        var plan = s.Plan;
        var model = s.Model;
        var args = new List<string>
        {
            "--model", s.Shards[0],
            "--host", Host, "--port", Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", plan.ContextSize.ToString(CultureInfo.InvariantCulture),
            "--n-gpu-layers", plan.GpuLayers.ToString(CultureInfo.InvariantCulture),
            "--flash-attn", "on",
            // measured on an M3 Max: ubatch 1024 reads prompts ~20% faster than 512; for a model that fits, 2048 grows the
            // compute buffers past what fits and writing drops from 20 to 3 tok/s. A model larger than memory reads its
            // uncached experts from the SSD once per ubatch, so there 4096 reads a 2,500-token prompt in one pass;
            // the plan makes room for the buffers
            "--ubatch-size", plan.ExpertCache > 0 ? "4096" : "1024", "--batch-size", plan.ExpertCache > 0 ? "4096" : "2048",
            "--fit", "off",               // the plan above already decided where every tensor goes
            "--threads", plan.Threads.ToString(CultureInfo.InvariantCulture),
            "--parallel", "1",            // one conversation at a time keeps the whole KV and compute budget for it
            "--jinja",
            "--reasoning-format", "deepseek",   // thinking goes to reasoning_content, for both DeepSeek and GLM
            "--metrics",
            "--alias", model.Id,
        };
        if (plan.TensorSplit.Count == 0)
        {
            if (plan.GpuLayers > 0) args.AddRange(new[] { "--n-cpu-moe", plan.CpuMoELayers.ToString(CultureInfo.InvariantCulture) });
        }
        else
        {
            // cluster: llama.cpp puts the helpers first, so the layers kept in this PC's memory are the last ones
            args.AddRange(new[] { "--tensor-split", string.Join(",", plan.TensorSplit) });
            if (plan.HostExpertLayers.Count > 0)
                args.AddRange(new[] { "--override-tensor", $"blk\\.({string.Join("|", plan.HostExpertLayers)})\\.ffn_(up|down|gate|gate_up)_(ch|)exps=CPU" });
        }
        // larger than VRAM + RAM: the experts stay mapped from the file, never repacked into RAM, and the card caches the
        // ones the conversation uses most. A model without experts neither: measured with Ministral on 4 threads, the same
        // 5.3 tok/s with 3.6 GB less private memory
        if (!plan.FitsInMemory || plan.Experts == 0) args.Add("--no-repack");
        if (plan.ExpertCache > 0) args.AddRange(new[] { "--moe-cache-mib", (plan.ExpertCache >> 20).ToString(CultureInfo.InvariantCulture) });
        if (s.Vision != null) args.AddRange(new[] { "--mmproj", s.Vision });
        // cluster: the helpers' GPUs join as RPC devices
        if (s.Rpc != null) args.AddRange(new[] { "--rpc", s.Rpc });
        // multi-token prediction: a small head guesses the next tokens, the model checks them in one pass. Not for a model
        // larger than memory: checking 3 tokens needs more experts from the SSD, and the head runs without the expert cache
        if (settings.Mtp && model.HasMtp && plan.ExpertCache == 0)
        {
            if (model.Mtp == "file" && s.Draft != null) args.AddRange(new[] { "--spec-type", "draft-mtp", "--model-draft", s.Draft, "--spec-draft-n-max", "2" });
            else if (model.Mtp == "builtin") args.AddRange(new[] { "--spec-type", "draft-mtp", "--spec-draft-n-max", "2" });
        }
        return args;
    }

    /// <summary>The key goes through the environment (common/arg.cpp reads LLAMA_API_KEY): process arguments are visible to every program.</summary>
    public IReadOnlyDictionary<string, string?> Environment(EngineStart s)
    {
        var env = new Dictionary<string, string?>();
        foreach (var (k, v) in s.Backend.Environment) env[k] = v;
        env["LLAMA_ARG_LAZY_MODE"] = "auto";   // tables over 4 GB (DeepSeek's Engram) are read row by row from the SSD
        // larger than memory: reading the whole file ahead at load would take minutes and evict itself
        if (!s.Plan.FitsInMemory) env["LLAMA_MMAP_PREFETCH"] = "0";
        if (s.Plan.ExpertCache > 0)
        {
            // the experts in the cache are saved when the engine stops and read back at the next start, so the first
            // answer is as fast as the later ones
            env["LLAMA_MOE_CACHE_FILE"] = Path.Combine(Paths.ModelDir(s.Model.Id), "expert-cache.bin");
            // writing (up to 8 tokens at once) reads its experts from the cache; prompts copy the experts they use to the
            // GPU without pushing the conversation's experts out of the cache
            env["LLAMA_MOE_CACHE_MAX_BATCH"] = "8";
            env["GGML_OP_OFFLOAD_MIN_BATCH"] = "9";
        }
        env["LLAMA_API_KEY"] = string.IsNullOrEmpty(Settings.Shared.ApiKey) ? null : Settings.Shared.ApiKey;
        return env;
    }

    protected override ChildStart Command(EngineStart s) =>
        new(Binary(s.Backend), Arguments(s)) { Env = Environment(s), Cwd = Path.GetDirectoryName(Binary(s.Backend)) };

    protected override string Headline(ChildStart c) => "$ " + Path.GetFileName(c.Exe) + " " + string.Join(" ", c.Args);

    protected override void ConsumeLine(string line)
    {
        if (line.Contains("load_tensors")) Say(L("Gewichten laden…"));
        if (line.Contains("warming up")) { Say(L("Opwarmen…")); SetProgress(0.95); }
        if (line.Contains("lazy read enabled")) Say(L("Engram-tabellen koppelen…"));
    }

    /// <summary>/slots: whether a slot is busy and n_decoded of the busy one.</summary>
    public override async Task<EngineActivity?> PollActivity() => ParseSlots(await GetJson("slots"));

    internal static EngineActivity? ParseSlots(string? json)
    {
        if (json == null) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var slot in d.RootElement.EnumerateArray())
            {
                if (!slot.TryGetProperty("is_processing", out var p) || !p.GetBoolean()) continue;
                int decoded = slot.TryGetProperty("next_token", out var next) && next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0
                              && next[0].TryGetProperty("n_decoded", out var n) ? n.GetInt32() : 0;
                return new EngineActivity { Busy = true, Decoded = decoded };
            }
            return new EngineActivity { Busy = false };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}
