using Xunit;

namespace WinAIMagic.Engine.Tests;

public class MemoryPlannerTests : EngineTest
{
    /// <summary>DeepSeek Compact as measured from its headers: ~9.3 GB dense, 40 layers of ~2.65 GB experts.</summary>
    private static GGUFIndex CompactDeepSeek() => TestModels.Index("deepseek41", 40, 9_300_000_000, 2_652_000_000, engram: 80_600_000_000);

    private static SystemInfo Pc(int vramGb, int ramGb) => SystemInfo.Simulated(FakeGpuBackend.Nvidia(vramGb), ramGb, 12);

    [Fact]
    public void SmallCardPutsSomeExpertsInRam()
    {
        var s = Pc(24, 128);
        var p = MemoryPlanner.Plan(CompactDeepSeek(), s, 65_536, true);
        Assert.InRange(p.CpuMoELayers, 1, 39);
        Assert.True(p.Dense + p.Overhead + p.GpuExperts <= p.VramBudget);
        Assert.Equal(p.Experts, p.GpuExperts + p.RamExperts);
        Assert.Equal(PlanLabel.FitsInVramRam, p.Label);
    }

    [Fact]
    public void LargerCardMovesMoreToTheGpu()
    {
        var low = MemoryPlanner.Plan(CompactDeepSeek(), Pc(16, 128), 65_536, true);
        var high = MemoryPlanner.Plan(CompactDeepSeek(), Pc(48, 128), 65_536, true);
        Assert.True(high.CpuMoELayers < low.CpuMoELayers);
        Assert.Equal(80_600_000_000UL, high.Engram);
    }

    private static GGUFIndex Q4() => TestModels.Index("deepseek41", 40, 9_000_000_000, 7_250_000_000);

    /// <summary>A Q4-sized model (~290 GB of experts): every expert stays mapped, the card caches the most used.</summary>
    [Fact]
    public void LargerThanMemory()
    {
        var p = MemoryPlanner.Plan(Q4(), SystemInfo.Simulated(FakeGpuBackend.Nvidia(110), 128, 12), 32_768, false);
        Assert.False(p.FitsInMemory);
        Assert.Equal(40, p.CpuMoELayers);
        Assert.Empty(p.TensorSplit);
        Assert.True(p.ExpertCache > 90_000_000_000);
        Assert.True(p.RamExperts + p.StreamedExperts > 150_000_000_000);
        Assert.True(p.Dense + p.Overhead + p.ExpertCache <= p.VramBudget);
    }

    /// <summary>A smaller card has a smaller cache, and the rest comes from RAM and SSD.</summary>
    [Fact]
    public void SmallerCardShrinksTheCache()
    {
        var big = MemoryPlanner.Plan(Q4(), Pc(48, 128), 32_768, false);
        var small = MemoryPlanner.Plan(Q4(), Pc(24, 128), 32_768, false);
        Assert.True(small.ExpertCache < big.ExpertCache);
        Assert.Contains(big.Warnings, w => w.Contains("komt van RAM en SSD"));
    }

    [Fact]
    public void ACacheLimitIsRespected()
    {
        var p = MemoryPlanner.Plan(Q4(), Pc(48, 128), 32_768, false, expertCacheLimit: 3_000_000_000);
        Assert.Equal(3_000_000_000UL, p.ExpertCache);
    }

    /// <summary>A PC with little memory never underflows when the reserves are larger than what it has.</summary>
    [Fact]
    public void VerySmallPcDoesNotCrash()
    {
        var idx = TestModels.Index("x", 1, 9_000_000_000, 100_000_000_000);
        var p = MemoryPlanner.Plan(idx, SystemInfo.Simulated(FakeGpuBackend.Nvidia(1), 8, 4), 4096, false);
        Assert.Equal(0UL, p.ExpertCache);
        Assert.False(p.FitsInMemory);
        Assert.True(p.GpuLayers < 999);
        var t = MemoryPlanner.Plan(idx, SystemInfo.Simulated(FakeGpuBackend.Nvidia(1), 8, 4), 4096, false, EngineKind.Tiered);
        Assert.Equal(0UL, t.GpuExperts);
    }

    private static readonly ulong[] Helpers = { 69_000_000_000, 18_000_000_000 };

    /// <summary>GLM-5.3-Flash Q4 (~190 GB of experts) alone streams from the SSD; with the helpers it fits in memory.</summary>
    [Fact]
    public void HelpersMakeQ4Fit()
    {
        var idx = TestModels.Index("glm5-next", 46, 9_800_000_000, 4_416_000_000, firstExpertLayer: 3);
        var s = Pc(16, 128);
        var alone = MemoryPlanner.Plan(idx, s, 65_536, false);
        Assert.False(alone.FitsInMemory);
        var cluster = MemoryPlanner.Plan(idx, s, 65_536, false, helpers: Helpers);
        Assert.True(cluster.FitsInMemory);
        Assert.Equal(0UL, cluster.StreamedExperts);
        // one entry per helper and one for this PC, together every layer plus the output layer
        Assert.Equal(3, cluster.TensorSplit.Count);
        Assert.Equal(47, cluster.TensorSplit.Sum());
        // the helpers take the first layers, so the layers this PC keeps in memory are among its own, the last ones
        int mine = 46 - (cluster.TensorSplit[^1] - 1);
        Assert.All(cluster.HostExpertLayers, l => Assert.InRange(l, mine, 45));
        Assert.True(cluster.VramPredicted <= cluster.VramBudget);
    }

    /// <summary>DeepSeek-V4.1-Flash Q4 does not fit even in the cluster: this PC keeps its layers mapped and caches the most used.</summary>
    [Fact]
    public void DeepSeekQ4StreamsLessInCluster()
    {
        var s = Pc(48, 128);
        var alone = MemoryPlanner.Plan(Q4(), s, 32_768, false);
        var cluster = MemoryPlanner.Plan(Q4(), s, 32_768, false, helpers: Helpers);
        Assert.True(cluster.ExpertCache > 0);
        Assert.True(cluster.Experts + 70_000_000_000 < alone.Experts);
        Assert.Equal(cluster.TensorSplit[^1] - 1, cluster.HostExpertLayers.Count);
        Assert.Equal(40 - cluster.HostExpertLayers.Count, cluster.HostExpertLayers[0]);
    }
}

/// <summary>The second engine on the measured PC: RTX 5060 Ti 16 GB, Qwen3.6, 64K. n-cpu-moe 20 gives 39 tok/s at 14.0 GB;
/// n-cpu-moe 10 reaches 15.5 GB and collapses to 9 tok/s; all on the card 4.9 tok/s.</summary>
public class MeasuredVramTests : EngineTest
{
    private static GGUFIndex Qwen36() => TestModels.Index("qwen35moe", 40, 2_678_180_352, 491_782_144, contextLength: 262144);

    [Fact]
    public void UsedVramStaysUnderTheCollapse()
    {
        var p = MemoryPlanner.Plan(Qwen36(), SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10), 65_536, false);
        Assert.Equal(EngineKind.Llama, p.Engine);
        Assert.Equal(PlanLabel.FitsInVramRam, p.Label);
        Assert.InRange(p.CpuMoELayers, 20, 26);
        // the desktop's share and the engine's together stay 2 GB under the total
        Assert.True(p.VramPredicted + MemoryPlanner.DesktopVram <= (16UL << 30) - (2UL << 30), $"{p.VramPredicted}");
    }

    [Fact]
    public void AnExactReadingOfOtherProgramsCounts()
    {
        ulong total = 16UL << 30;
        var quiet = MemoryPlanner.Plan(Qwen36(), SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10, vramFree: total - 500_000_000), 65_536, false);
        var busy = MemoryPlanner.Plan(Qwen36(), SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10, vramFree: total - 3_000_000_000), 65_536, false);
        Assert.True(busy.CpuMoELayers > quiet.CpuMoELayers);
        Assert.True(quiet.VramPredicted + 500_000_000 <= total - (2UL << 30));
        Assert.True(busy.VramPredicted + 3_000_000_000 <= total - (2UL << 30));
    }

    [Fact]
    public void AComputeOnlyCardKeepsOnlyItsReserve()
    {
        var display = MemoryPlanner.LlamaVramLimit(SystemInfo.Simulated(FakeGpuBackend.Nvidia(16, drivesDisplay: true), 128));
        var compute = MemoryPlanner.LlamaVramLimit(SystemInfo.Simulated(FakeGpuBackend.Nvidia(16, drivesDisplay: false), 128));
        Assert.Equal((16UL << 30) - (700UL << 20), compute);
        Assert.True(display < compute);
    }

    [Fact]
    public void ArgumentsCarryThePlannedLayers()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10);
        var plan = MemoryPlanner.Plan(Qwen36(), s, 65_536, false);
        var engine = new LlamaEngine(port: 9000);
        var model = ModelCatalog.Shared.Model("qwen3.6-35b-a3b-q4")!;
        var args = engine.Arguments(new EngineStart(model, new[] { "a.gguf" }, plan, FakeGpuBackend.Nvidia(16))).ToList();
        Assert.Equal(plan.CpuMoELayers.ToString(), args[args.IndexOf("--n-cpu-moe") + 1]);
        Assert.Equal("999", args[args.IndexOf("--n-gpu-layers") + 1]);
    }
}

public class ThreeTierTests : EngineTest
{
    public static IEnumerable<object[]> Machines() =>
        new[] { (8, 32), (12, 64), (16, 128), (24, 64), (24, 192), (48, 256) }
            .SelectMany(m => new[] { "nvidia", "amd" }.Select(make => new object[] { m.Item1, m.Item2, make }));

    private static FakeGpuBackend Backend(int vram, string make) => make == "nvidia" ? FakeGpuBackend.Nvidia(vram) : FakeGpuBackend.Amd(vram);

    [Theory]
    [MemberData(nameof(Machines))]
    public void EveryModelGetsAPlanAndNoneIsTooBig(int vram, int ram, string make)
    {
        var s = SystemInfo.Simulated(Backend(vram, make), ram, 8);
        foreach (var m in ModelCatalog.Shared.Models)
        {
            var kind = MemoryPlanner.EngineFor(m, s);
            if (kind == null)
            {
                // the one exact refusal: a model only the main engine runs, on a card its dense weights do not fit
                Assert.Equal("minimax-m3.1-flash-compact", m.Id);
                Assert.Contains("VRAM nodig", MemoryPlanner.Blocked(m, s));
                continue;
            }
            Assert.Null(MemoryPlanner.Blocked(m, s));
            var idx = TestModels.IndexOf(m);
            bool vision = MemoryPlanner.UseVision(idx, s, true, kind.Value);
            int context = MemoryPlanner.ContextSize(idx, s, 65_536, vision, kind.Value);
            var p = MemoryPlanner.Plan(idx, s, context, vision, kind.Value);
            Assert.Null(p.Blocked);
            Assert.Equal(p.Experts, p.GpuExperts + p.RamExperts + p.SsdExperts + (p.ExpertCache > 0 ? Math.Min(p.Experts, p.ExpertCache) : 0));
            Assert.True(p.RamExperts <= p.RamBudget);
            // the card is never planned over its budget, once the dense weights fit
            if (p.GpuLayers == 999 && kind == EngineKind.Llama) Assert.True(p.VramPredicted <= p.VramBudget, $"{m.Id}: {p.VramPredicted} > {p.VramBudget}");
            if (kind == EngineKind.Tiered) Assert.True(p.GpuExperts + p.Dense + p.Overhead <= p.VramBudget, m.Id);
            Assert.True(p.ContextSize >= 4096, m.Id + " " + p.ContextSize);
            Assert.DoesNotContain(p.Warnings, w => w.Contains("te groot") || w.Contains("tok/s"));
        }
    }

    [Theory]
    [InlineData(8, 32, "nvidia", "qwen3.6-35b-a3b-q4")]
    [InlineData(12, 64, "nvidia", "qwen3.6-35b-a3b-q4")]
    [InlineData(16, 128, "nvidia", "glm-5.3-flash-compact-24")]
    [InlineData(24, 64, "nvidia", "qwen3.6-35b-a3b-q4")]
    [InlineData(24, 192, "nvidia", "glm-5.3-flash-compact-24")]
    [InlineData(48, 256, "nvidia", "deepseek-v4.1-flash-compact")]
    [InlineData(8, 32, "amd", "qwen3.6-35b-a3b-q4")]
    [InlineData(12, 64, "amd", "qwen3.6-35b-a3b-q4")]
    [InlineData(16, 128, "amd", "glm-5.3-flash-compact-24")]
    [InlineData(24, 64, "amd", "qwen3.6-35b-a3b-q4")]
    [InlineData(24, 192, "amd", "glm-5.3-flash-compact-24")]
    [InlineData(48, 256, "amd", "deepseek-v4.1-flash-compact")]
    public void RecommendedPerPc(int vram, int ram, string make, string? id)
    {
        var s = SystemInfo.Simulated(Backend(vram, make), ram, 8);
        Assert.Equal(id, MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s)?.Id);
    }

    [Fact]
    public void TargetContextsOn16GbWith128Gb()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10);
        int Room(string id)
        {
            var m = ModelCatalog.Shared.Model(id)!;
            return MemoryPlanner.ContextRoom(TestModels.IndexOf(m), s, false, MemoryPlanner.EngineFor(m, s)!.Value) ?? int.MaxValue;
        }
        Assert.True(Room("deepseek-v4.1-flash-compact") >= 256 * 1024);
        Assert.True(Room("minimax-m3.1-flash-compact") >= 128 * 1024);
        Assert.True(Room("qwen3.6-35b-a3b-q4") >= 128 * 1024);
        Assert.True(Room("glm-5.3-flash-compact-24") >= 64 * 1024);
    }

    [Fact]
    public void FitsAndStreamsPerPc()
    {
        var byId = ModelCatalog.Shared.Models.ToDictionary(m => m.Id);
        var small = SystemInfo.Simulated(FakeGpuBackend.Nvidia(8), 32);
        var big = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128);
        Assert.Equal(MemoryPlanner.Fit.Fits, MemoryPlanner.FitFor(byId["qwen3.6-35b-a3b-q4"], small));
        // larger than memory is what the app is for: everything else streams on 32 GB, nothing is too big
        Assert.Equal(MemoryPlanner.Fit.Fits, MemoryPlanner.FitFor(byId["ministral-3-8b-q4"], small));
        foreach (var m in ModelCatalog.Shared.Models.Where(m => m.Id is not ("qwen3.6-35b-a3b-q4" or "ministral-3-8b-q4")))
            Assert.Equal(MemoryPlanner.Fit.Streams, MemoryPlanner.FitFor(m, small));
        Assert.Equal(MemoryPlanner.Fit.Fits, MemoryPlanner.FitFor(byId["deepseek-v4.1-flash-compact"], big));
        Assert.Equal(MemoryPlanner.Fit.Streams, MemoryPlanner.FitFor(byId["deepseek-v4.1-flash-q4"], big));
        Assert.Equal(MemoryPlanner.Fit.Fits, MemoryPlanner.FitFor(byId["glm-5.3-flash-compact"], big));
    }

    [Fact]
    public void ThereIsNoThirdLabel()
    {
        Assert.Equal(new[] { "Fits", "Streams" }, Enum.GetNames<MemoryPlanner.Fit>());
        Assert.Equal(2, Enum.GetNames<PlanLabel>().Length);
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128);
        var fits = MemoryPlanner.Plan(TestModels.Index("x", 4, 1_000_000_000, 1_000_000_000), s, 32_768, false);
        Assert.Equal("Past in VRAM + RAM", fits.LabelText);
        var streams = MemoryPlanner.Plan(TestModels.Index("x", 40, 1_000_000_000, 8_000_000_000), s, 32_768, false);
        Assert.Equal("Streamt van SSD", streams.LabelText);
    }

    [Fact]
    public void TieredDividesExpertsOverTheThreeTiers()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 64, 10);
        var m = ModelCatalog.Shared.Model("glm-5.3-flash-compact-24")!;
        var idx = TestModels.IndexOf(m);
        var p = MemoryPlanner.Plan(idx, s, 65_536, false, EngineKind.Tiered);
        Assert.Equal(EngineKind.Tiered, p.Engine);
        Assert.True(p.GpuExperts > 0);
        Assert.True(p.RamExperts > 0);
        Assert.True(p.SsdExperts > 0);
        Assert.Equal(PlanLabel.StreamsFromSsd, p.Label);
        Assert.Equal(p.Experts, p.GpuExperts + p.RamExperts + p.SsdExperts);
        Assert.Contains(p.Warnings, w => w.Contains("komt van de SSD"));
        Assert.Contains(p.Warnings, w => w.Contains("enkele minuten"));

        var roomy = MemoryPlanner.Plan(idx, SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10), 65_536, false, EngineKind.Tiered);
        Assert.Equal(PlanLabel.FitsInVramRam, roomy.Label);
        Assert.Equal(0UL, roomy.SsdExperts);
    }

    /// <summary>"System managed" can stay at 8 GB on a 128 GB PC, and then the engine pins too little (brief 7).</summary>
    [Fact]
    public void ASmallPageFileShrinksTheRamTier()
    {
        var m = ModelCatalog.Shared.Model("glm-5.3-flash-q4")!;
        var good = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10);
        Assert.Equal(25UL << 30, good.RequiredPageFile);
        var small = good with { PageFile = 8UL << 30 };
        var a = MemoryPlanner.Plan(TestModels.IndexOf(m), good, 65_536, false, EngineKind.Tiered);
        var b = MemoryPlanner.Plan(TestModels.IndexOf(m), small, 65_536, false, EngineKind.Tiered);
        Assert.Equal(17UL << 30, a.RamExperts - b.RamExperts);
        Assert.Contains(b.Warnings, w => w.Contains("wisselbestand", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("wisselbestand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void KvStreamingKeepsAWindowOnTheCard()
    {
        var glm = TestModels.Index("glm5-next", 1, 1, 1);
        Assert.Equal(20_000UL * 32_768, glm.TieredKvVram(128 * 1024));
        Assert.Equal(12_000UL * 128 * 1024, glm.TieredKvRam(128 * 1024));
        Assert.Equal(0UL, glm.TieredKvRam(32_768));
        var ds = TestModels.Index("deepseek41", 1, 1, 1);
        Assert.Equal(5_600UL * 256 * 1024, ds.TieredKvVram(256 * 1024));
        Assert.Equal(0UL, ds.TieredKvRam(256 * 1024));
    }

    [Fact]
    public void StrixHaloApu()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.StrixHalo(96), 32, 16);
        Assert.True(s.UnifiedMemory);
        Assert.Equal(EngineKind.Tiered, MemoryPlanner.EngineFor(ModelCatalog.Shared.Model("deepseek-v4.1-flash-compact")!, s));
        Assert.Equal("deepseek-v4.1-flash-compact", MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s)?.Id);
        var p = MemoryPlanner.Plan(TestModels.IndexOf(ModelCatalog.Shared.Model("glm-5.3-flash-q4")!), s, 65_536, false, EngineKind.Tiered);
        Assert.Equal(p.Experts, p.GpuExperts + p.RamExperts + p.SsdExperts);
    }

    [Fact]
    public void NoGpuRunsOnTheProcessor()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Cpu(), 64, 16);
        var q = ModelCatalog.Shared.Model("qwen3.6-35b-a3b-q4")!;
        Assert.Equal(EngineKind.Llama, MemoryPlanner.EngineFor(q, s));
        var p = MemoryPlanner.Plan(TestModels.IndexOf(q), s, 65_536, false);
        Assert.Equal(0, p.GpuLayers);
        Assert.Equal(PlanLabel.FitsInVramRam, p.Label);
        Assert.Equal(0UL, p.GpuExperts);
        var args = new LlamaEngine(port: 9000).Arguments(new EngineStart(q, new[] { "a" }, p, FakeGpuBackend.Cpu()));
        Assert.DoesNotContain("--n-cpu-moe", args);
        Assert.Equal("qwen3.6-35b-a3b-q4", MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s)?.Id);

        // the one hard minimum: a model only the main engine runs, and no card for it
        var mm = ModelCatalog.Shared.Model("minimax-m3.1-flash-compact")!;
        Assert.Null(MemoryPlanner.EngineFor(mm, s));
        Assert.NotNull(MemoryPlanner.Blocked(mm, s));
    }

    [Fact]
    public void ALimitedCardRunsTheSecondEngine()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Amd(16, "gfx1101"), 128, 10);
        Assert.Equal(EngineKind.Llama, MemoryPlanner.EngineFor(ModelCatalog.Shared.Model("glm-5.3-flash-compact-24")!, s));
        // too little VRAM for the dense weights of the main engine: the second engine with fewer layers on the card
        var ds = ModelCatalog.Shared.Model("deepseek-v4.1-flash-compact")!;
        var small = SystemInfo.Simulated(FakeGpuBackend.Nvidia(8), 128, 10);
        Assert.Equal(EngineKind.Llama, MemoryPlanner.EngineFor(ds, small));
    }

    [Fact]
    public void ContextFollowsSettingModelAndRoom()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10);
        var qwen = TestModels.Index("qwen35moe", 40, 2_678_180_352, 491_782_144, contextLength: 32_768);
        Assert.Equal(32_768, MemoryPlanner.ContextSize(qwen, s, 65_536, false, EngineKind.Llama));
        var open = TestModels.Index("qwen35moe", 40, 2_678_180_352, 491_782_144);
        Assert.Equal(65_536, MemoryPlanner.ContextSize(open, s, 65_536, false, EngineKind.Llama));
    }

    /// <summary>The main engine, the same rule (brief 6): vision stays on while it leaves the model 16K of context, and goes first when not.</summary>
    [Fact]
    public void TheMainEngineGivesUpVisionBeforeTheModelsRoom()
    {
        var glm = TestModels.IndexOf(ModelCatalog.Shared.Model("glm-5.3-flash-compact-24")!);
        var card = FakeGpuBackend.Nvidia(16);
        Assert.True(MemoryPlanner.UseVision(glm, SystemInfo.Simulated(card, 128, 10), true, EngineKind.Tiered));
        Assert.False(MemoryPlanner.UseVision(glm, SystemInfo.Simulated(card, 128, 10), false, EngineKind.Tiered));
        var tight = Enumerable.Range(64, 128).Select(gb => SystemInfo.Simulated(card, gb, 10))
            .Where(s => MemoryPlanner.FitsWhole(glm, s, EngineKind.Tiered)).First();
        Assert.True((MemoryPlanner.ContextRoom(glm, tight, true, EngineKind.Tiered) ?? 0) < MemoryPlanner.VisionMinimumContext);
        Assert.False(MemoryPlanner.UseVision(glm, tight, true, EngineKind.Tiered));
        // on that PC the model keeps its context; with vision on there is less of it
        Assert.True(MemoryPlanner.ContextRoom(glm, tight, false, EngineKind.Tiered) >= MemoryPlanner.ContextRoom(glm, tight, true, EngineKind.Tiered));
        Assert.Equal(16_384, MemoryPlanner.VisionMinimumContext);
    }

    /// <summary>On little memory: a smaller context, no vision, the whole model in memory; a model that streams gets 32K (the Mac's rules).</summary>
    [Fact]
    public void SmallPcContextAndVision()
    {
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(8), 32, 8);
        var qwen = TestModels.Index("qwen35moe", 40, 2_678_180_352, 491_782_144);
        Assert.True(MemoryPlanner.FitsWhole(qwen, s, EngineKind.Llama));
        int? withVision = MemoryPlanner.ContextRoom(qwen, s, true, EngineKind.Llama);
        int? without = MemoryPlanner.ContextRoom(qwen, s, false, EngineKind.Llama);
        Assert.True(without >= (withVision ?? 0));
        Assert.Equal(withVision >= MemoryPlanner.VisionMinimumContext, MemoryPlanner.UseVision(qwen, s, true, EngineKind.Llama));

        var large = TestModels.IndexOf(ModelCatalog.Shared.Model("deepseek-v4.1-flash-q4")!);
        Assert.False(MemoryPlanner.FitsWhole(large, s, EngineKind.Llama));
        Assert.Equal(MemoryPlanner.SmallPcContext, MemoryPlanner.ContextRoom(large, s, false, EngineKind.Llama));
        Assert.False(MemoryPlanner.UseVision(large, s, true, EngineKind.Llama));
        var tooLittle = SystemInfo.Simulated(FakeGpuBackend.Nvidia(4), 24, 8);
        Assert.False(MemoryPlanner.UseVision(qwen, tooLittle, true, EngineKind.Llama));
        var roomy = SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 8);
        Assert.Null(MemoryPlanner.ContextRoom(large, roomy, false, EngineKind.Llama));
        Assert.True(MemoryPlanner.UseVision(large, roomy, true, EngineKind.Llama));
        Assert.False(MemoryPlanner.UseVision(large, roomy, false, EngineKind.Llama));
        Assert.True(MemoryPlanner.UseVision(large, s, true, EngineKind.Llama, cluster: true));
    }

    [Fact]
    public void ThreadsAreAtLeastFour()
    {
        var idx = TestModels.Index("x", 1, 1, 1);
        Assert.Equal(4, MemoryPlanner.Plan(idx, SystemInfo.Simulated(FakeGpuBackend.Nvidia(8), 16, 2), 4096, false).Threads);
        Assert.Equal(12, MemoryPlanner.Plan(idx, SystemInfo.Simulated(FakeGpuBackend.Nvidia(8), 16, 12), 4096, false).Threads);
    }
}

/// <summary>A PC with 16 GB of RAM: the numbers only. Its behaviour has not been measured on a real one.</summary>
public class SmallPcTests : EngineTest
{
    private static readonly ModelInfo Qwen = ModelCatalog.Shared.Model("qwen3.6-35b-a3b-q4")!;
    private static readonly GGUFIndex QwenIndex = TestModels.IndexOf(Qwen);
    private static readonly ModelInfo Ministral = ModelCatalog.Shared.Model("ministral-3-8b-q4")!;
    private static readonly GGUFIndex MinistralIndex = TestModels.IndexOf(Ministral);

    /// <summary>`vram` 0: no card, which is also what a laptop with only graphics in its processor gets.</summary>
    private static SystemInfo Pc(int vram, int ram = 16) =>
        SystemInfo.Simulated(vram == 0 ? FakeGpuBackend.Cpu() : FakeGpuBackend.Nvidia(vram), ram, 8);

    private static MemoryPlan PlanAt(SystemInfo s, int context) => MemoryPlanner.Plan(QwenIndex, s, context, false);

    /// <summary>A large card next to 16 GB of RAM still does best with the larger model, which reads little from the SSD there.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(12)]
    public void Qwen36StreamsOn16GBAndIsRecommended(int vram)
    {
        var s = Pc(vram);
        Assert.Equal(MemoryPlanner.Fit.Streams, MemoryPlanner.FitFor(Qwen, s));
        Assert.Equal(Qwen.Id, MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s)?.Id);
    }

    /// <summary>A laptop gets the small model. It streams on 8 GB, where nothing else would be recommended at all.</summary>
    [Theory]
    [InlineData(8, 16, MemoryPlanner.Fit.Fits)]
    [InlineData(6, 16, MemoryPlanner.Fit.Fits)]
    [InlineData(0, 16, MemoryPlanner.Fit.Fits)]
    [InlineData(0, 12, MemoryPlanner.Fit.Streams)]
    [InlineData(4, 8, MemoryPlanner.Fit.Streams)]
    [InlineData(0, 8, MemoryPlanner.Fit.Streams)]
    public void ALaptopIsRecommendedMinistral(int vram, int ram, MemoryPlanner.Fit fit)
    {
        var s = Pc(vram, ram);
        Assert.Equal(fit, MemoryPlanner.FitFor(Ministral, s));
        Assert.Equal(Ministral.Id, MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s)?.Id);
    }

    /// <summary>What Windows reports for a laptop sold with 8 GB, part of which goes to the graphics in its processor.</summary>
    [Theory]
    [InlineData(7.8)]
    [InlineData(7.4)]
    public void TheRecommendationSurvivesWhatWindowsReports(double gib)
    {
        var s = Pc(0, 8) with { RamTotal = (ulong)(gib * (1UL << 30)) };
        Assert.Equal(Ministral.Id, MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s)?.Id);
    }

    /// <summary>Less than this: nothing is recommended, and a model still starts.</summary>
    [Fact]
    public void APcWithLessGetsNoRecommendationAndStillStarts()
    {
        foreach (var s in new[] { Pc(0, 6), Pc(0, 4) })
        {
            Assert.Null(MemoryPlanner.Recommended(ModelCatalog.Shared.Models, s));
            Assert.Null(MemoryPlanner.Blocked(Ministral, s));
            Assert.Equal(EngineKind.Llama, MemoryPlanner.EngineFor(Qwen, s));
        }
    }

    /// <summary>On no card and 16 GB: all of it in RAM, with the room that leaves for the context.</summary>
    [Fact]
    public void MinistralWithoutACardOn16GB()
    {
        var s = Pc(0);
        int context = MemoryPlanner.ContextSize(MinistralIndex, s, 65_536, false, EngineKind.Llama);
        Assert.Equal(16_384, context);
        var p = MemoryPlanner.Plan(MinistralIndex, s, context, false);
        Assert.Equal(PlanLabel.FitsInVramRam, p.Label);
        Assert.Equal(0, p.GpuLayers);
        Assert.Empty(p.Warnings);
        Assert.False(MemoryPlanner.UseVision(MinistralIndex, s, true, EngineKind.Llama));
    }

    /// <summary>On no card and 8 GB it streams, with the context it cannot do without and words about what to do.</summary>
    [Fact]
    public void MinistralWithoutACardOn8GB()
    {
        var s = Pc(0, 8);
        int context = MemoryPlanner.ContextSize(MinistralIndex, s, 65_536, false, EngineKind.Llama);
        Assert.Equal(MemoryPlanner.PlainSmallContext, context);
        var p = MemoryPlanner.Plan(MinistralIndex, s, context, false);
        Assert.Equal(PlanLabel.StreamsFromSsd, p.Label);
        Assert.Equal(0UL, p.ExpertCache);
        Assert.Contains(p.Warnings, w => w.Contains("Sluit andere programma's"));
        Assert.DoesNotContain(p.Warnings, w => w.Contains("experts") || w.Contains("te groot"));
        Assert.False(MemoryPlanner.UseVision(MinistralIndex, s, true, EngineKind.Llama));
    }

    /// <summary>On an 8 GB card the layers that fit go on it: measured on the 5060 Ti, 24 of 34 at 8K use 4430 MiB and write 14 tok/s.</summary>
    [Fact]
    public void MinistralOnAnEightGBCard()
    {
        var s = Pc(8);
        int context = MemoryPlanner.ContextSize(MinistralIndex, s, 65_536, false, EngineKind.Llama);
        Assert.Equal(MemoryPlanner.PlainSmallContext, context);
        var p = MemoryPlanner.Plan(MinistralIndex, s, context, false);
        Assert.Equal(PlanLabel.FitsInVramRam, p.Label);
        Assert.Equal(24, p.GpuLayers);
        Assert.Contains(p.Warnings, w => w.Contains("op de processor"));
    }

    /// <summary>A 16 GB card holds all of it, with a large context and without going over its budget.</summary>
    [Fact]
    public void MinistralOnASixteenGBCard()
    {
        var s = Pc(16, 32);
        int context = MemoryPlanner.ContextSize(MinistralIndex, s, 65_536, false, EngineKind.Llama);
        var p = MemoryPlanner.Plan(MinistralIndex, s, context, false);
        Assert.Equal(999, p.GpuLayers);
        Assert.True(context >= 32_768, context.ToString());
        Assert.True(p.VramPredicted <= p.VramBudget, $"{p.VramPredicted} > {p.VramBudget}");
        Assert.True(MemoryPlanner.UseVision(MinistralIndex, s, true, EngineKind.Llama));
    }

    /// <summary>No experts, so nothing to cache and nothing to repack, also where the plan is otherwise for experts.</summary>
    [Theory]
    [InlineData(0, 8)]
    [InlineData(0, 16)]
    [InlineData(4, 8)]
    [InlineData(8, 16)]
    [InlineData(16, 32)]
    public void TheEngineGetsNoExpertArguments(int vram, int ram)
    {
        var s = Pc(vram, ram);
        var backend = vram == 0 ? FakeGpuBackend.Cpu() : FakeGpuBackend.Nvidia(vram);
        int context = MemoryPlanner.ContextSize(MinistralIndex, s, 65_536, false, EngineKind.Llama);
        var p = MemoryPlanner.Plan(MinistralIndex, s, context, false);
        var args = new LlamaEngine(port: 9000).Arguments(new EngineStart(Ministral, new[] { "a" }, p, backend));
        Assert.Contains("--no-repack", args);
        Assert.DoesNotContain("--moe-cache-mib", args);
        Assert.Equal("1024", args[args.ToList().IndexOf("--ubatch-size") + 1]);
    }

    [Fact]
    public void LargerPcsKeepTheirPlan()
    {
        var large = TestModels.IndexOf(ModelCatalog.Shared.Model("deepseek-v4.1-flash-q4")!);
        foreach (int gb in new[] { 24, 32 })
        {
            Assert.False(MemoryPlanner.IsSmallPc(Pc(8, gb)));
            Assert.Equal(MemoryPlanner.SmallPcContext, MemoryPlanner.ContextRoom(large, Pc(8, gb), false, EngineKind.Llama));
        }
        foreach (int gb in new[] { 64, 128 })
            Assert.Null(MemoryPlanner.ContextRoom(large, Pc(8, gb), false, EngineKind.Llama));
        // what Windows reports for a PC sold with 24 GB
        Assert.False(MemoryPlanner.IsSmallPc(Pc(8, 24) with { RamTotal = (ulong)(23.7 * (1UL << 30)) }));
        Assert.True(MemoryPlanner.IsSmallPc(Pc(8, 16) with { RamTotal = (ulong)(15.7 * (1UL << 30)) }));
    }

    /// <summary>What goes on the card stays within its budget, and Windows keeps its margin of the RAM.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(8)]
    [InlineData(0)]
    public void ThePlanOn16GBLeavesMemoryFree(int vram)
    {
        var s = Pc(vram);
        var context = MemoryPlanner.ContextSize(QwenIndex, s, 65_536, false, EngineKind.Llama);
        Assert.Equal(MemoryPlanner.TinyPcContext, context);
        var plan = PlanAt(s, context);
        Assert.False(plan.FitsInMemory);
        Assert.True(plan.SsdExperts > 0);
        Assert.True(plan.RamExperts <= MemoryPlanner.RamBudget(s));
        Assert.True(s.RamTotal - MemoryPlanner.RamBudget(s) >= 6UL << 30);
        if (vram > 0) Assert.True(plan.VramPredicted <= plan.VramBudget, $"{plan.VramPredicted} > {plan.VramBudget}");
    }

    /// <summary>A 16 GB card reads nothing from the SSD at 32K, so halving the context would gain almost nothing.</summary>
    [Fact]
    public void ACardThatHoldsTheRestKeeps32K()
    {
        var s = Pc(16);
        Assert.Equal(MemoryPlanner.SmallPcContext, MemoryPlanner.ContextSize(QwenIndex, s, 65_536, false, EngineKind.Llama));
        Assert.Equal(0UL, PlanAt(s, MemoryPlanner.SmallPcContext).SsdExperts);
    }

    /// <summary>Why 16K is worth half the context where the SSD is in use: the room goes to the experts.</summary>
    [Fact]
    public void A16KContextGivesTheExpertsMoreRoom()
    {
        (MemoryPlan Tiny, MemoryPlan Small) At(int vram) => (PlanAt(Pc(vram), MemoryPlanner.TinyPcContext), PlanAt(Pc(vram), MemoryPlanner.SmallPcContext));

        foreach (int vram in new[] { 12, 8 })
        {
            var (tiny, small) = At(vram);
            Assert.True(tiny.ExpertCache > small.ExpertCache);
            Assert.True(tiny.SsdExperts < small.SsdExperts);
        }
        // the dense weights do not fit on this card: more of the layers do
        var (tiny6, small6) = At(6);
        Assert.True(tiny6.GpuLayers > small6.GpuLayers);
        // no card: the context lives in RAM
        var (tinyCpu, smallCpu) = At(0);
        Assert.True(tinyCpu.RamExperts > smallCpu.RamExperts);
    }

    /// <summary>The Mac's limit of 3 GB protects memory the card here does not share with Windows; it would only send more to the SSD.</summary>
    [Fact]
    public void TheCacheIsNotLimitedOnASeparateCard()
    {
        Assert.True(PlanAt(Pc(12), MemoryPlanner.TinyPcContext).ExpertCache > 4_000_000_000);
    }
}
