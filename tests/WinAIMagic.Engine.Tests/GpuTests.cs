using System.Text.RegularExpressions;
using Xunit;

namespace WinAIMagic.Engine.Tests;

public class GpuChoiceTests : EngineTest
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public GpuChoiceTests(Xunit.Abstractions.ITestOutputHelper output) { this.output = output; }

    private static GpuAdapter Card(string name, uint vendor, int gb, bool display = true) =>
        new(name, vendor, 1, (ulong)gb << 30, 0, "1.2.3", 7, display);

    private static GpuAdapter Rtx(int gb = 16, bool display = true) => Card("GeForce RTX 5060 Ti", GpuAdapter.NvidiaId, gb, display);

    [Fact]
    public void ModernCardGetsItsNativeBackendAndTheMainEngine()
    {
        var c = GpuDetection.Choose(new[] { Rtx() }, BackendProbe.AllPresent("9.0"));
        Assert.Equal("cuda", c.Backend.Id);
        Assert.Null(c.Backend.TieredUnavailable);
        Assert.False(c.Backend.Experimental);
        Assert.Contains("compute capability 9.0", c.Reason);
        Assert.Empty(c.Tried);
        Assert.Equal("nvidia/cc7/16", c.Backend.ProfileKey);
    }

    [Fact]
    public void PascalRunsAsAnExperiment()
    {
        var c = GpuDetection.Choose(new[] { Rtx(8) }, BackendProbe.AllPresent("6.1"));
        Assert.Equal("cuda", c.Backend.Id);
        Assert.True(c.Backend.Experimental);
        Assert.Contains("Experimenteel", c.Reason);
    }

    [Theory]
    [InlineData("5.0")]
    [InlineData(null)]
    public void DriverOrCardTooOldFallsToVulkanAndSaysWhy(string? cc)
    {
        var probe = new BackendProbe { EngineFileExists = (_, _) => true, ComputeCapability = () => cc };
        var c = GpuDetection.Choose(new[] { Rtx() }, probe);
        Assert.Equal("vulkan", c.Backend.Id);
        Assert.NotNull(c.Backend.TieredUnavailable);
        Assert.Single(c.Tried);
        Assert.Contains("Eerder geprobeerd", c.Reason);
    }

    [Fact]
    public void AmdSaysNotAvailableInThisVersionAndGoesToVulkan()
    {
        var c = GpuDetection.Choose(new[] { Card("AMD Radeon RX 7900 XTX", GpuAdapter.AmdId, 24) }, BackendProbe.AllPresent());
        Assert.Equal("vulkan", c.Backend.Id);
        Assert.Contains(c.Tried, t => t.Contains("Niet beschikbaar in deze versie"));
        Assert.Contains("Niet beschikbaar in deze versie", c.Reason);
    }

    [Fact]
    public void IntelArcRunsOnVulkan()
    {
        var c = GpuDetection.Choose(new[] { Card("Intel Arc B580", GpuAdapter.IntelId, 12) }, BackendProbe.AllPresent());
        Assert.Equal("vulkan", c.Backend.Id);
        Assert.Contains("Beperkte modus", c.Reason);
    }

    [Fact]
    public void NoCardMeansTheProcessor()
    {
        var c = GpuDetection.Choose(Array.Empty<GpuAdapter>(), BackendProbe.AllPresent());
        Assert.Equal("cpu", c.Backend.Id);
        Assert.False(c.Backend.HasGpu);
        Assert.Equal(0UL, c.Backend.VramTotal);
        Assert.Equal(0UL, c.Backend.RequiredPageFile(64UL << 30));
    }

    [Fact]
    public void IntegratedGpuIsNoTargetButAHaloApuIs()
    {
        var igpu = Card("AMD Radeon(TM) Graphics", GpuAdapter.AmdId, 0) with { DedicatedBytes = 512UL << 20 };
        Assert.True(igpu.IsIntegrated);
        var c = GpuDetection.Choose(new[] { igpu, Rtx() }, BackendProbe.AllPresent());
        Assert.Equal("cuda", c.Backend.Id);
        Assert.Single(c.Backend.Adapters);

        var intel = new GpuAdapter("Intel UHD Graphics 630", GpuAdapter.IntelId, 1, 128UL << 20, 8UL << 30);
        Assert.True(intel.IsIntegrated);
        Assert.Equal("cpu", GpuDetection.Choose(new[] { intel }, BackendProbe.AllPresent()).Backend.Id);

        var halo = Card("AMD Radeon 8060S Graphics", GpuAdapter.AmdId, 96);
        Assert.True(halo.IsHaloApu);
        Assert.False(halo.IsIntegrated);
        Assert.Equal("vulkan", GpuDetection.Choose(new[] { halo }, BackendProbe.AllPresent()).Backend.Id);
    }

    [Fact]
    public void MixedMakesTheMostVramWinsAndTheSettingOverrides()
    {
        var amd = Card("AMD Radeon RX 7900 XT", GpuAdapter.AmdId, 20);
        var cards = new[] { Rtx(8), amd };
        // the most usable VRAM: the AMD card, which is not available in this version, so NVIDIA is next
        var c = GpuDetection.Choose(cards, BackendProbe.AllPresent());
        Assert.Equal("cuda", c.Backend.Id);
        Assert.Contains(c.Tried, t => t.StartsWith("HIP"));
        Assert.Equal("vulkan", GpuDetection.Choose(cards, BackendProbe.AllPresent(), "vulkan").Backend.Id);
        Assert.Equal("cpu", GpuDetection.Choose(cards, BackendProbe.AllPresent(), "cpu").Backend.Id);
    }

    [Fact]
    public void MissingEngineFilesFallBack()
    {
        var probe = new BackendProbe { EngineFileExists = (folder, _) => folder == "cpu", ComputeCapability = () => "9.0" };
        var c = GpuDetection.Choose(new[] { Rtx() }, probe);
        Assert.Equal("cpu", c.Backend.Id);
        Assert.Equal(2, c.Tried.Count);

        var noMain = new BackendProbe { EngineFileExists = (folder, _) => folder != "tiered\\bin", ComputeCapability = () => "9.0" };
        var d = GpuDetection.Choose(new[] { Rtx() }, noMain);
        Assert.Equal("cuda", d.Backend.Id);
        Assert.NotNull(d.Backend.TieredUnavailable);
    }

    [Fact]
    public void SeveralCardsOfOneMakeWorkTogetherUpToSixteen()
    {
        var cards = Enumerable.Range(0, 20).Select(_ => Rtx(24, false)).ToArray();
        var c = GpuDetection.Choose(cards, BackendProbe.AllPresent());
        Assert.Equal(16, c.Backend.Adapters.Count);
        Assert.Equal(Enumerable.Range(0, 16), c.Backend.GpuIndices);
        Assert.Equal(16UL * (24UL << 30), c.Backend.VramTotal);
    }

    [Fact]
    public void RealDxgiEnumerationGivesCardsNotTheSoftwareRenderer()
    {
        foreach (var a in GpuDetection.EnumerateAdapters())
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.NotEqual(0x1414u, a.VendorId);
        }
    }

    /// <summary>What this PC really reports, when it has the reference card: the vtable offsets and the counters are right.</summary>
    [Fact]
    public void TheRealCardReadsAsItIs()
    {
        var card = GpuDetection.EnumerateAdapters().FirstOrDefault(a => a.VendorId == GpuAdapter.NvidiaId);
        if (card == null) return;
        output.WriteLine(card.ToString());
        Assert.False(card.IsIntegrated);
        Assert.True(card.VramBytes > 1UL << 30);
        Assert.Matches(@"^\d+\.\d+$", card.Driver);
        Assert.NotEqual(0, card.Luid);
        var choice = GpuDetection.Choose(new[] { card }, new BackendProbe { EngineFileExists = (_, _) => true });
        output.WriteLine(choice.Reason);
        Assert.Equal("cuda", choice.Backend.Id);
        var s = SystemInfo.Current(choice.Backend);
        output.WriteLine($"{s.Cpu}, {s.PerformanceCores} cores, {s.WindowsVersion}, RAM {s.RamTotal >> 30} GB, page file {s.PageFile >> 30} GB, VRAM free {s.VramFree}, required page file {s.RequiredPageFile >> 30} GB");
        Assert.NotNull(s.VramFree);
        Assert.InRange(s.VramFree!.Value, 1UL, card.VramBytes);
        output.WriteLine(DriveReport.For(@"C:\").ToString());
    }

    [Fact]
    public void ComputeCapabilityOfAChildProcessIsANumberOrNothing()
    {
        var cc = GpuDetection.ReadComputeCapability();
        if (cc != null) Assert.Matches(@"^\d+\.\d+", cc);
    }

    [Theory]
    [InlineData("32.0.16.1088", "610.88")]
    [InlineData("32.0.15.6614", "566.14")]
    [InlineData("garbage", "garbage")]
    public void NvidiaDriverVersionReadsAsPeopleKnowIt(string windows, string friendly) =>
        Assert.Equal(friendly, GpuDetection.FriendlyDriver(GpuAdapter.NvidiaId, windows));

    [Theory]
    [InlineData("AMD Radeon RX 7900 GRE", "gfx1100")]
    [InlineData("AMD Radeon RX 7800 XT", "gfx1101")]
    [InlineData("AMD Radeon AI PRO R9700", "gfx1201")]
    [InlineData("AMD Radeon RX 9070", "gfx1201")]
    [InlineData("AMD Radeon RX 6900 XT", "gfx1030")]
    [InlineData("AMD Radeon 8060S Graphics", "gfx1151")]
    [InlineData("AMD Radeon RX 580", "unknown")]
    public void AmdArchitectures(string name, string gfx) =>
        Assert.Equal(gfx, AmdBackend.Gfx(new GpuAdapter(name, GpuAdapter.AmdId, 1, 16UL << 30, 0)));
}

public class GpuProfileTests : EngineTest
{
    [Fact]
    public void StartValuesOfTheBrief()
    {
        var n = GpuProfiles.For("nvidia", "cc7", 16UL << 30);
        Assert.Equal(700, n.VramReserveMib);
        Assert.Equal(1500, n.VramReserveDisplayMib);
        Assert.Equal(6, n.RamMarginGib);
        Assert.Equal("auto", n.Prefill);
        Assert.Equal("cuda", n.EngineFolder);
        Assert.Equal("PCI_BUS_ID", n.Env["CUDA_DEVICE_ORDER"]);
        Assert.False(n.Measured);

        var a = GpuProfiles.For("amd", "gfx1100", 16UL << 30);
        Assert.Equal(3072, a.VramReserveMib);
        Assert.Equal(16, a.RamMarginGib);
        Assert.True(a.Tiered);
        Assert.Equal("1", a.Env["GPU_FLUSH_ON_EXECUTION"]);
        Assert.False(a.Env.ContainsKey("STRATA_GLM_PREFILL_SUB"));

        var big = GpuProfiles.For("amd", "gfx1100", 24UL << 30);
        Assert.Equal("1024", big.Env["STRATA_GLM_PREFILL_SUB"]);
        Assert.Equal("4096", big.Env["STRATA_GLM_PREFILL_MB"]);

        Assert.False(GpuProfiles.For("amd", "gfx1101", 16UL << 30).Tiered);
        Assert.True(GpuProfiles.For("amd", "gfx1151", 96UL << 30).Unified);
        Assert.True(GpuProfiles.For("amd", "gfx1030", 16UL << 30).Experimental);
        Assert.True(GpuProfiles.For("nvidia", "pascal", 8UL << 30).Experimental);
    }

    [Theory]
    [InlineData(8, "8")]
    [InlineData(12, "12")]
    [InlineData(16, "16")]
    [InlineData(20, "24")]
    [InlineData(24, "24")]
    [InlineData(48, "32+")]
    public void VramClasses(int gb, string cls) => Assert.Equal(cls, GpuProfiles.VramClass((ulong)gb << 30));

    [Fact]
    public void ReserveDependsOnTheDesktopAndTheSettingWins()
    {
        Assert.Equal(1500UL << 20, FakeGpuBackend.Nvidia(16).VramReserve(-1));
        Assert.Equal(700UL << 20, FakeGpuBackend.Nvidia(16, drivesDisplay: false).VramReserve(-1));
        Assert.Equal(3072UL << 20, FakeGpuBackend.Amd(16).VramReserve(-1));
        Assert.Equal(900UL << 20, FakeGpuBackend.Nvidia(16).VramReserve(900));
    }

    [Fact]
    public void PageFileRules()
    {
        // VRAM + 9 GB: 25 GB on the reference PC
        Assert.Equal(25UL << 30, FakeGpuBackend.Nvidia(16).RequiredPageFile(128UL << 30));
        Assert.Equal(25UL << 30, FakeGpuBackend.Amd(16).RequiredPageFile(128UL << 30));
        // an APU: the GPU's share + max(8 GB, 10% of the RAM)
        Assert.Equal((96UL + 8) << 30, FakeGpuBackend.StrixHalo(96).RequiredPageFile(32UL << 30));
        var realApu = new AmdBackend(new[] { new GpuAdapter("AMD Radeon 8060S Graphics", GpuAdapter.AmdId, 1, 64UL << 30, 0) });
        Assert.Equal((64UL + 13) << 30, realApu.RequiredPageFile(128UL << 30));
        Assert.Equal(0UL, FakeGpuBackend.Cpu().RequiredPageFile(64UL << 30));
    }

    [Fact]
    public void RamMarginPerMake()
    {
        Assert.Equal(6UL << 30, FakeGpuBackend.Nvidia(16).RamMargin(128UL << 30));
        Assert.Equal(16UL << 30, FakeGpuBackend.Amd(16).RamMargin(128UL << 30));
        Assert.Equal(8UL << 30, FakeGpuBackend.StrixHalo(96).RamMargin(32UL << 30));
    }

    [Fact]
    public void EveryProfileOfTheTableHasAFake()
    {
        var keys = GpuProfiles.Keys.ToList();
        foreach (var k in new[] { "nvidia/*/*", "nvidia/pascal/*", "amd/*/*", "amd/gfx1100/*", "amd/gfx1201/*", "amd/gfx1151/*", "amd/gfx1030/*", "vulkan/*/*", "cpu/*/*" })
            Assert.Contains(k, keys);
        IGpuBackend[] fakes =
        {
            FakeGpuBackend.Nvidia(16), FakeGpuBackend.Nvidia(8, architecture: "pascal"),
            FakeGpuBackend.Amd(16, "gfx1100"), FakeGpuBackend.Amd(16, "gfx1201"), FakeGpuBackend.Amd(16, "gfx1030"), FakeGpuBackend.Amd(16, "gfx1101"),
            FakeGpuBackend.StrixHalo(96), FakeGpuBackend.Vulkan(12), FakeGpuBackend.Cpu(),
        };
        foreach (var f in fakes)
        {
            Assert.Null(f.Unavailable);
            Assert.False(string.IsNullOrEmpty(f.EngineFolder));
        }
        Assert.NotNull(FakeGpuBackend.Amd(16, "gfx1101").TieredUnavailable);
        Assert.Null(FakeGpuBackend.Amd(16, "gfx1100").TieredUnavailable);
    }
}

/// <summary>Make differences live in the Gpu folder and gpu-profiles.json only, anywhere else in the code a search finds them (brief 3a).</summary>
public class NoMakeOutsideGpuLayerTests : EngineTest
{
    private static readonly Regex Make = new(@"\b(nvidia|amd|radeon|geforce|cuda|hip|0x10de|0x1002)\b", RegexOptions.IgnoreCase);

    [Fact]
    public void NoMakeInCodeOutsideTheGpuFolder()
    {
        var hits = new List<string>();
        char sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot, file);
            if (rel.Contains($"{sep}Gpu{sep}") || rel.Contains($"{sep}bin{sep}") || rel.Contains($"{sep}obj{sep}")) continue;
            int n = 0;
            foreach (var raw in File.ReadLines(file))
            {
                n++;
                // what a comment says is no branch
                var code = Regex.Replace(raw, @"(^|\s)//.*$", "");
                if (Make.IsMatch(code)) hits.Add($"{rel}:{n}: {raw.Trim()}");
            }
        }
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }
}
