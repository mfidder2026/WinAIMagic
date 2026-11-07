using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>The second label of the plan is the core of the product: a model that does not fit in VRAM + RAM still starts.</summary>
public enum PlanLabel { FitsInVramRam, StreamsFromSsd }

/// <summary>The two engines; which runs a model follows from the catalog and the backend.</summary>
public enum EngineKind { Tiered, Llama }

/// <summary>
/// The memory plan for a large MoE model on three tiers: VRAM, RAM and SSD.
///
/// - Dense weights (attention, shared experts, router, embeddings), the KV cache and compute buffers go on the card.
/// - Main engine: it divides the experts itself (--expert-cache auto); the plan predicts how: VRAM, then pinned RAM, then
///   the SSD. KV streaming keeps a window of the cache in VRAM and the rest in RAM.
/// - llama.cpp, a model that fits: routed experts go on the card layer by layer while they fit; the first layers keep
///   their experts in RAM for the CPU (--n-cpu-moe). A full card does not fail on Windows, WDDM pages and the speed
///   collapses (measured on a 16 GB card with Qwen3.6, 64K: 39 tok/s at 14.0 GB used, 9 tok/s at 15.5 GB), so the plan
///   keeps used VRAM at least `DisplayMargin` under the total on the card with the desktop.
/// - llama.cpp, a model larger than VRAM + RAM: every expert stays mapped from the file, and part of the card becomes a
///   cache of the experts the conversation uses most (--moe-cache-mib).
/// - In a cluster the helpers hold whole layers on their GPU, the first layers (llama.cpp puts RPC devices first), and
///   this PC the rest.
/// - DeepSeek's Engram tables are never resident: the engine reads the few rows a token needs from the SSD.
/// </summary>
public sealed record MemoryPlan
{
    public EngineKind Engine { get; init; }
    public ulong VramTotal { get; init; }
    /// <summary>What the plan may use on the card.</summary>
    public ulong VramBudget { get; init; }
    public ulong RamTotal { get; init; }
    public ulong RamBudget { get; init; }
    public ulong VramReserve { get; init; }
    public ulong Dense { get; init; }
    public ulong Experts { get; init; }
    public ulong Engram { get; init; }
    /// <summary>KV cache and compute buffers, on the card (and for the main engine also the KV part in RAM: `KvRam`).</summary>
    public ulong Overhead { get; init; }
    public ulong KvRam { get; init; }
    public int Layers { get; init; }
    /// <summary>Layers whose experts stay in RAM instead of on the card.</summary>
    public IReadOnlyList<int> HostExpertLayers { get; init; } = Array.Empty<int>();
    /// <summary>Layers on the card (llama.cpp); 999: all. Below all only when not even the dense weights fit.</summary>
    public int GpuLayers { get; init; } = 999;
    public ulong GpuExperts { get; init; }
    public ulong RamExperts { get; init; }
    public ulong SsdExperts { get; init; }
    /// <summary>llama.cpp, larger than VRAM + RAM: VRAM that keeps the experts used most; 0 when everything fits.</summary>
    public ulong ExpertCache { get; init; }
    /// <summary>Cluster: layers per device in llama.cpp's order (the helpers in --rpc order, then this PC with the output layer).</summary>
    public IReadOnlyList<int> TensorSplit { get; init; } = Array.Empty<int>();
    public int Threads { get; init; }
    public int ContextSize { get; init; }
    public bool Vision { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>A hard minimum is missing, and exactly which; null: the model starts. There is no third label "too big".</summary>
    public string? Blocked { get; init; }

    public int CpuMoELayers => HostExpertLayers.Count;
    public bool FitsInMemory { get; init; }
    /// <summary>The second engine keeps its KV cache in 8 bits (q8_0) instead of 16: see `MemoryPlanner.EightBitKv`.</summary>
    public bool EightBitKv { get; init; }
    public PlanLabel Label => FitsInMemory ? PlanLabel.FitsInVramRam : PlanLabel.StreamsFromSsd;
    public string LabelText => Label == PlanLabel.FitsInVramRam ? L("Past in VRAM + RAM") : L("Streamt van SSD");
    /// <summary>Expert bytes not in VRAM or RAM, which come from the SSD when a token needs them.</summary>
    public ulong StreamedExperts => SsdExperts;
    /// <summary>What the plan predicts on the card.</summary>
    public ulong VramPredicted => Dense + Overhead + GpuExperts + ExpertCache;
}

/// <summary>A pure function of the model, the PC and the settings; no state, no hardware calls.</summary>
public static class MemoryPlanner
{
    public enum Fit { Fits, Streams }

    /// <summary>KV for a small context, the compute buffers and the server: the least a model needs next to its weights.</summary>
    public const ulong MinimumOverhead = 2_900_000_000;
    /// <summary>Below this much RAM a model that streams gives its room to the expert cache: a smaller context, no vision.</summary>
    public const ulong RoomyMemory = 64UL << 30;
    /// <summary>The context of a model that streams on a PC with less than `RoomyMemory`.</summary>
    public const int SmallPcContext = 32_768;
    /// <summary>Below this much RAM a model that still reads from the SSD also gives up context. A PC with 24 GB reports a little
    /// under 24 GiB, and belongs with the larger ones. The figures for these PCs are estimates: not measured on a real one.</summary>
    public const ulong SmallMemory = 23UL << 30;
    /// <summary>The context of a model that still reads from the SSD on a PC with less than `SmallMemory`.</summary>
    public const int TinyPcContext = 16_384;
    /// <summary>The smallest expert cache worth recommending a model that streams for.</summary>
    public const ulong SmallestCache = 2_000_000_000;
    public const int VisionMinimumContext = 16_384;
    public const ulong VisionBytes = 1_500_000_000;
    /// <summary>llama.cpp's compute buffers at ubatch 1024, measured on the large models, and on a model with small dense weights.</summary>
    public const ulong ComputeBuffers = 2_500_000_000, SmallComputeBuffers = 1_000_000_000;
    /// <summary>The same on a model without experts, which copies none while it reads a prompt: Ministral uses less than this at 8K.</summary>
    public const ulong PlainComputeBuffers = 300_000_000;
    /// <summary>What llama.cpp's KV cache takes beside its cost per token.</summary>
    private const ulong KvBase = 400_000_000;

    /// <summary>The second engine keeps its KV cache in 8 bits where that cache is on the card. Measured with Ministral on the 5060 Ti:
    /// at 32K context 8.6 instead of 10.6 GB of VRAM, writing 72.5 instead of 74.7 tok/s (51.4 and 52.4 after 16K tokens), prompts 1-2%
    /// slower, and the same next token in 98.8% of the positions of a 28K-token text (perplexity 0.05% higher). Not on the processor:
    /// there, with 4 threads, a prompt after 4K tokens read at 8 instead of 23 tok/s.</summary>
    public static bool EightBitKv(SystemInfo s) => s.HasGpu;

    /// <summary>The KV cache per token as the second engine stores it: in 8 bits 0.55 of the 16 (measured 77,200 against 140,300 B for
    /// Ministral; q8_0 itself takes 34 bytes per 32 values against 64).</summary>
    public static ulong KvPerToken(GGUFIndex index, SystemInfo s) =>
        EightBitKv(s) ? index.KvBytesPerToken * 11 / 20 : index.KvBytesPerToken;
    /// <summary>The context of a model without experts where memory is short: its cache would push out weights that every token reads.
    /// Not less: the app's system prompt and its tools take part of it, and 4K saves nothing. With a limit on the working set, Ministral writes
    /// 5 tok/s at 5 GB and 0.7 tok/s at 4.5 GB, at 8K and at 4K alike. Not measured on a real PC, whose SSD would make the second figure worse.</summary>
    public const int PlainSmallContext = 8_192;
    /// <summary>What Windows holds on a PC running little else: a model without experts is worth recommending when its weights fit beside it.</summary>
    public const ulong WindowsRoom = 2_500_000_000;
    /// <summary>Main engine: prompt chunks are lent from the expert pool, so the buffers proper stay small.</summary>
    public const ulong TieredBuffers = 1_500_000_000;
    /// <summary>Main engine: the smallest expert pool worth running with.</summary>
    public const ulong MinimumPool = 1_000_000_000;
    /// <summary>Used VRAM stays this far under the total on the card with the desktop (see the measurement above).</summary>
    public const ulong DisplayMargin = 2UL << 30;
    /// <summary>What the desktop holds on its card when no reading says otherwise.</summary>
    public const ulong DesktopVram = 800_000_000;

    /// <summary>What the second engine may use on the card: never more than the card less the margin and what others hold.</summary>
    public static ulong LlamaVramLimit(SystemInfo s)
    {
        if (!s.HasGpu) return 0;
        ulong margin = s.DrivesDisplay ? Math.Max(s.VramReserve, DisplayMargin) : s.VramReserve;
        ulong others = s.VramFree is ulong free ? (s.VramTotal > free ? s.VramTotal - free : 0) : s.DrivesDisplay ? DesktopVram : 0;
        return Sub(s.VramTotal, margin + others);
    }

    /// <summary>What the main engine's pool can use: it measures free VRAM itself and keeps the reserve.</summary>
    public static ulong TieredVramLimit(SystemInfo s) => s.HasGpu ? Sub(s.VramTotal, s.VramReserve) : 0;

    public static ulong RamBudget(SystemInfo s) => Sub(s.RamTotal, s.RamMargin);

    public static bool IsSmallPc(SystemInfo s) => s.RamTotal < SmallMemory;

    private static ulong Sub(ulong a, ulong b) => a > b ? a - b : 0;

    /// <summary>Which engine runs this model on this PC; null: none can, see `Blocked`.</summary>
    public static EngineKind? EngineFor(ModelInfo model, SystemInfo s)
    {
        if (model.Engine != "tiered") return EngineKind.Llama;
        if (s.TieredUnavailable == null && s.HasGpu && TieredMinimum(model) <= TieredVramLimit(s)) return EngineKind.Tiered;
        return model.Tier?.Llama == false ? null : EngineKind.Llama;
    }

    /// <summary>The hard minimum a model misses on this PC, in words; null when it starts.</summary>
    public static string? Blocked(ModelInfo model, SystemInfo s)
    {
        if (EngineFor(model, s) != null) return null;
        if (s.TieredUnavailable != null) return s.TieredUnavailable;
        return L($"{model.ShortName} heeft minstens {(TieredMinimum(model) + s.VramReserve).Gb()} VRAM nodig; deze videokaart heeft {s.VramTotal.Gb()}.");
    }

    /// <summary>What has to be on the card for the main engine: dense weights, buffers and the KV window (upstream's figures).</summary>
    private static ulong TieredMinimum(ModelInfo model)
    {
        ulong dense = model.Dense ?? (model.Memory ?? model.TotalSize) / 10;
        ulong window = model.Family switch
        {
            "minimax" => 61_000UL * 32_768,
            "glm" => 20_000UL * 32_768,
            _ => 5_600UL * 32_768,
        };
        return dense + TieredBuffers + window;
    }

    /// <summary>Before a download: does the model fit in this PC's VRAM + RAM, or does it stream from the SSD? (After the
    /// download `Plan` decides from the files.)</summary>
    public static Fit FitFor(ModelInfo model, SystemInfo s)
    {
        ulong memory = model.Memory ?? model.TotalSize;
        return memory + MinimumOverhead <= VramLimitFor(model, s) + RamBudget(s) ? Fit.Fits : Fit.Streams;
    }

    private static ulong VramLimitFor(ModelInfo model, SystemInfo s) =>
        EngineFor(model, s) == EngineKind.Tiered ? TieredVramLimit(s) : LlamaVramLimit(s);

    /// <summary>The first model in the catalog that fits in this PC's VRAM + RAM; a larger card skips the models meant for small ones,
    /// and a model without experts is only for a PC with little memory: elsewhere a model that reads a little from the SSD does more.
    /// If none fits, the smallest that streams, when this PC has room for what that has to hold. Otherwise none: a model still
    /// starts, but this PC is better off as a helper in a cluster.</summary>
    public static ModelInfo? Recommended(IEnumerable<ModelInfo> models, SystemInfo s)
    {
        var usable = models.Where(m => m.Recommend && EngineFor(m, s) != null).ToList();
        double vramGb = s.VramTotal / (double)(1UL << 30);
        return usable.FirstOrDefault(m => FitFor(m, s) == Fit.Fits && (!m.IsPlain || IsSmallPc(s))
                                          && ((m.Tier?.RecommendMaxVramGb ?? m.RecommendMaxVramGb) is not double max || !s.HasGpu || vramGb <= max))
            ?? usable.Where(m => FitFor(m, s) == Fit.Streams && HasRoomToStream(m, s)).MinBy(m => m.TotalSize);
    }

    /// <summary>The dense weights, the buffers and a small expert cache, before anything comes from the SSD. A model without experts
    /// has no cache: its weights have to fit in the PC's memory beside Windows, or every token reads them from the SSD.</summary>
    private static bool HasRoomToStream(ModelInfo model, SystemInfo s)
    {
        ulong dense = model.Dense ?? (model.Memory ?? model.TotalSize) / 10;
        if (model.IsPlain) return dense + WindowsRoom <= s.RamTotal + VramLimitFor(model, s);
        return dense + ComputeBuffers + SmallestCache <= VramLimitFor(model, s) + RamBudget(s);
    }

    /// <summary>Does the whole model stay in VRAM + RAM, with the smallest context?</summary>
    public static bool FitsWhole(GGUFIndex index, SystemInfo s, EngineKind engine) =>
        index.DenseBytes + index.ExpertBytes + MinimumOverhead < (engine == EngineKind.Tiered ? TieredVramLimit(s) : LlamaVramLimit(s)) + RamBudget(s);

    /// <summary>The largest context (a multiple of 4096) for this model on this PC: what still lets a whole model stay in VRAM +
    /// RAM, and on a small PC what leaves a model that streams its expert cache. Null: the setting decides.</summary>
    public static int? ContextRoom(GGUFIndex index, SystemInfo s, bool vision, EngineKind engine)
    {
        if (!FitsWhole(index, s, engine))
        {
            if (s.RamTotal >= RoomyMemory) return null;
            if (index.ExpertBytes == 0) return PlainSmallContext;
            // 16K gives experts about 0.26 GB on Qwen3.6, or four more layers on a 6 GB card. A PC that reads nothing from the SSD
            // at 32K (a 16 GB card) would give up half its context for almost nothing.
            return IsSmallPc(s) && !Plan(index, s, SmallPcContext, vision, engine).FitsInMemory ? TinyPcContext : SmallPcContext;
        }
        ulong weights = index.DenseBytes + index.ExpertBytes;
        ulong visionBytes = vision ? VisionBytes : 0;
        if (engine == EngineKind.Tiered)
        {
            // tokens whose KV window has to stay on the card, then tokens whose KV lives in RAM beside the experts
            ulong total = TieredVramLimit(s) + RamBudget(s);
            ulong fixedPart = weights + MinimumOverhead + visionBytes;
            if (fixedPart >= total) return 0;
            ulong perTokenRam = index.TieredKvRam(1 << 20) / (1 << 20), perTokenVram = index.TieredKvVram(1 << 20) / (1 << 20);
            ulong room = total - fixedPart;
            ulong tokens;
            if (perTokenRam > 0)
            {
                tokens = room / perTokenRam;
            }
            else
            {
                // the whole cache on the card (DeepSeek): it may not push the dense weights and a small pool off
                ulong card = Sub(TieredVramLimit(s), index.DenseBytes + TieredBuffers + MinimumPool);
                tokens = Math.Min(room, card) / Math.Max(perTokenVram, 1);
            }
            return Math.Max(4096, (int)Math.Min(tokens, int.MaxValue) / 4096 * 4096);
        }
        ulong limit = LlamaVramLimit(s);
        ulong budget = limit + RamBudget(s), fixedBytes = weights + MinimumOverhead + visionBytes;
        ulong perToken = KvPerToken(index, s);
        if (fixedBytes >= budget || perToken == 0) return 0;
        ulong kvTokens = (budget - fixedBytes) / perToken;
        ulong fixedOnCard = index.DenseBytes + Buffers(index) + visionBytes;
        // without experts the whole model on the card comes first, and on a smaller card a part of it, with a small context
        if (s.HasGpu && index.ExpertBytes == 0)
            kvTokens = Math.Min(kvTokens, Math.Max(Sub(limit, fixedOnCard + KvBase) / perToken, PlainSmallContext));
        // the KV cache lives on the card, unless not even the dense weights do
        else if (s.HasGpu && limit >= fixedOnCard) kvTokens = Math.Min(kvTokens, (limit - fixedOnCard) / perToken);
        return Math.Max(4096, (int)Math.Min(kvTokens, int.MaxValue) / 4096 * 4096);
    }

    /// <summary>The chosen context, at most what the model was made for, and on a small PC at most what leaves the model its room.</summary>
    public static int ContextSize(GGUFIndex index, SystemInfo s, int setting, bool vision, EngineKind engine, bool cluster = false)
    {
        int chosen = index.ContextLength > 0 ? Math.Min(setting, index.ContextLength) : setting;
        if (cluster || ContextRoom(index, s, vision, engine) is not int room) return chosen;
        return Math.Min(chosen, room);
    }

    /// <summary>Vision when it is on and downloaded, unless it would leave the model too little room: then the model goes first.</summary>
    public static bool UseVision(GGUFIndex index, SystemInfo s, bool wanted, EngineKind engine, bool cluster = false)
    {
        if (!wanted) return false;
        if (cluster) return true;
        if (!FitsWhole(index, s, engine)) return s.RamTotal >= RoomyMemory;
        return (ContextRoom(index, s, true, engine) ?? 0) >= VisionMinimumContext;
    }

    private static ulong Buffers(GGUFIndex index) =>
        index.ExpertBytes == 0 ? PlainComputeBuffers : index.DenseBytes < 4_000_000_000 ? SmallComputeBuffers : ComputeBuffers;

    /// <summary>A model without experts that does not fit: what the plan says, in place of the words about experts.</summary>
    private static string PlainWarning() =>
        L("Dit model past net niet in VRAM en RAM. Wat niet past komt van de SSD. Sluit andere programma's, anders wordt het veel trager.");

    /// <summary>`helpers`: what each helper's GPU can hold for the model, in --rpc order. `expertCacheLimit`: an upper limit for the
    /// cache, for a PC that runs more next to the model.</summary>
    public static MemoryPlan Plan(GGUFIndex index, SystemInfo s, int contextSize, bool vision, EngineKind engine = EngineKind.Llama,
                                  IReadOnlyList<ulong>? helpers = null, ulong? expertCacheLimit = null) =>
        engine == EngineKind.Tiered ? PlanTiered(index, s, contextSize, vision) : PlanLlama(index, s, contextSize, vision, helpers ?? Array.Empty<ulong>(), expertCacheLimit);

    private static MemoryPlan PlanTiered(GGUFIndex index, SystemInfo s, int context, bool vision)
    {
        ulong dense = index.DenseBytes, experts = index.ExpertBytes;
        ulong kvVram = index.TieredKvVram(context), kvRam = index.TieredKvRam(context);
        ulong overhead = kvVram + TieredBuffers;
        ulong limit = TieredVramLimit(s);
        ulong pool = Sub(limit, dense + overhead);
        // VRAM and the pinned tier both count for commit: a page file that is too small keeps that much of the RAM from the tier
        ulong pageShort = Sub(s.RequiredPageFile, s.PageFile);
        ulong ram = Sub(RamBudget(s), kvRam + pageShort + (vision ? VisionBytes : 0));
        ulong inVram = Math.Min(experts, pool);
        ulong inRam = Math.Min(experts - inVram, ram);
        ulong onSsd = experts - inVram - inRam;

        var warnings = new List<string>
        {
            L($"De engine zet bijna al het RAM vast, op {s.RamMargin.GbShort()} na. Het starten duurt daardoor enkele minuten en de pc kan even traag reageren."),
        };
        if (pageShort > 0)
            warnings.Add(L($"Het wisselbestand is {pageShort.GbShort()} te klein: zoveel minder RAM kan de engine vastzetten. Stel het in onder Systeem."));
        if (onSsd > 0)
            warnings.Add(L($"Dit model past niet helemaal in VRAM en RAM. De experts die het gesprek het meest gebruikt ({(inVram + inRam).Gb()}) blijven in VRAM en RAM; de rest ({onSsd.Gb()}) komt van de SSD als een woord ze nodig heeft."));
        if (dense + overhead > limit)
            warnings.Add(L("Zelfs de vaste gewichten passen niet in het VRAM; verklein de context of de VRAM-reserve."));

        return new MemoryPlan
        {
            Engine = EngineKind.Tiered,
            VramTotal = s.VramTotal, VramBudget = limit, RamTotal = s.RamTotal, RamBudget = RamBudget(s), VramReserve = s.VramReserve,
            Dense = dense, Experts = experts, Engram = index.EngramBytes, Overhead = overhead, KvRam = kvRam,
            Layers = Math.Max(index.LayerCount, LastLayer(index) + 1),
            GpuExperts = inVram, RamExperts = inRam, SsdExperts = onSsd, FitsInMemory = onSsd == 0,
            Threads = Math.Max(4, s.PerformanceCores), ContextSize = context, Vision = vision, Warnings = warnings,
        };
    }

    private static int LastLayer(GGUFIndex index)
    {
        var per = index.ExpertBytesPerLayer;
        return per.Count == 0 ? -1 : per.Keys.Max();
    }

    private static MemoryPlan PlanLlama(GGUFIndex index, SystemInfo s, int context, bool vision, IReadOnlyList<ulong> helpers, ulong? expertCacheLimit)
    {
        var perLayer = index.ExpertBytesPerLayer;
        int layers = Math.Max(index.LayerCount, LastLayer(index) + 1);
        ulong buffers = Buffers(index);
        ulong kv = KvPerToken(index, s) * (ulong)context + KvBase;
        ulong overhead = kv + buffers + (vision ? VisionBytes : 0);
        ulong limit = LlamaVramLimit(s), ram = RamBudget(s);

        // cluster: each helper takes as many whole layers as its GPU holds, this PC keeps at least one
        ulong densePerLayer = index.DenseBytes / (ulong)Math.Max(layers, 1);
        var split = new List<int>();
        int first = 0;
        if (helpers.Count > 0)
        {
            foreach (var budget in helpers)
            {
                int n = 0;
                ulong used = 0;
                while (first + n < layers - 1)
                {
                    ulong bytes = perLayer.GetValueOrDefault(first + n) + densePerLayer;
                    if (used + bytes > budget) break;
                    used += bytes;
                    n++;
                }
                split.Add(n);
                first += n;
            }
            split.Add(layers - first + 1);
        }
        var local = Enumerable.Range(first, Math.Max(layers - first, 0)).ToList();
        ulong experts = local.Aggregate(0UL, (n, il) => n + perLayer.GetValueOrDefault(il));
        ulong dense = index.DenseBytes - densePerLayer * (ulong)first;

        var hostLayers = new List<int>();
        ulong onGpu = experts, cache = 0, inRam = 0, onSsd = 0;
        int gpuLayers = 999;
        bool fits;
        var warnings = new List<string>();

        if (!s.HasGpu)
        {
            // no card: everything runs on the processor, what is not in RAM stays mapped from the file
            gpuLayers = 0;
            hostLayers = local;
            onGpu = 0;
            inRam = Math.Min(experts, Sub(ram, dense + overhead));
            onSsd = experts - inRam;
            overhead = kv;
            fits = onSsd == 0 && dense + kv <= ram;
            if (!fits && experts == 0) warnings.Add(PlainWarning());
        }
        else if (dense + experts + overhead <= limit + ram)
        {
            fits = true;
            // the fewest layers with their experts in RAM whose remainder fits on the card
            long budget = (long)limit - (long)dense - (long)overhead;
            foreach (var il in local)
            {
                if ((long)onGpu <= budget) break;
                onGpu -= perLayer.GetValueOrDefault(il);
                hostLayers.Add(il);
            }
            inRam = experts - onGpu;
            if (dense + overhead > limit)
            {
                gpuLayers = GpuLayersFor(dense, overhead, limit, layers);
                warnings.Add(L("Zelfs de vaste gewichten passen niet helemaal in het VRAM. Een deel van het model rekent op de processor."));
            }
        }
        else if (experts == 0)
        {
            // nothing to cache: as many layers as the card holds, the rest stays mapped from the file
            fits = false;
            onGpu = 0;
            gpuLayers = GpuLayersFor(dense, overhead, limit, layers);
            warnings.Add(PlainWarning());
        }
        else
        {
            fits = false;
            // the compute buffers hold the experts of one layer while a prompt is read (ubatch 4096): measured 6.2 GB for
            // DeepSeek Q4 (7.2 GB of experts per layer), 3.8 GB for GLM Q4 (4.4 GB per layer)
            ulong maxLayer = local.Count == 0 ? 0 : local.Max(il => perLayer.GetValueOrDefault(il));
            ulong promptCopy = 1_500_000_000 + maxLayer * 2 / 3;
            // instead of the generic compute buffers in `overhead`
            ulong promptBuffers = overhead - buffers + promptCopy;
            cache = Math.Min(Sub(limit, dense + promptBuffers), expertCacheLimit ?? ulong.MaxValue);
            hostLayers = local;
            onGpu = 0;
            inRam = Math.Min(experts - Math.Min(experts, cache), ram);
            onSsd = experts - Math.Min(experts, cache) - inRam;
            overhead = promptBuffers;
            if (dense + overhead > limit)
                gpuLayers = GpuLayersFor(dense, overhead, limit, layers);
            warnings.Add(L($"Dit model past niet helemaal in VRAM en RAM. De experts die het gesprek het meest gebruikt ({cache.Gb()}) blijven in het VRAM; de rest ({(experts - Math.Min(experts, cache)).Gb()}) komt van RAM en SSD als een woord ze nodig heeft."));
        }

        return new MemoryPlan
        {
            Engine = EngineKind.Llama,
            VramTotal = s.VramTotal, VramBudget = limit, RamTotal = s.RamTotal, RamBudget = ram, VramReserve = s.VramReserve,
            Dense = dense, Experts = experts, Engram = index.EngramBytes, Overhead = overhead,
            Layers = layers, HostExpertLayers = hostLayers, GpuLayers = gpuLayers,
            GpuExperts = onGpu, RamExperts = inRam, SsdExperts = onSsd, ExpertCache = cache, FitsInMemory = fits,
            TensorSplit = split, Threads = Math.Max(4, s.PerformanceCores), ContextSize = context, Vision = vision, Warnings = warnings,
            EightBitKv = EightBitKv(s),
        };
    }

    /// <summary>A card too small for the dense weights: as many whole layers as fit, the rest on the processor.</summary>
    private static int GpuLayersFor(ulong dense, ulong overhead, ulong limit, int layers)
    {
        ulong room = Sub(limit, overhead);
        return dense == 0 ? layers : (int)Math.Min((ulong)layers, room * (ulong)layers / dense);
    }
}
