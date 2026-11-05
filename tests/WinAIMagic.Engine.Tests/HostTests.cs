using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

public class LiveMetricsTests : EngineTest
{
    [Fact]
    public void EnginesOwnRateAndItsCountGiveTheSpeed()
    {
        int? last = null;
        Assert.Null(LiveMetrics.NextRate(null, ref last, 0.5, 3));
        Assert.Equal(0, LiveMetrics.NextRate(new EngineActivity { Busy = false }, ref last, 0.5, 3));
        Assert.Equal(17.5, LiveMetrics.NextRate(new EngineActivity { Busy = true, TokS = 17.5 }, ref last, 0.5, null));

        // the first look at a busy slot has nothing to compare with
        Assert.Null(LiveMetrics.NextRate(new EngineActivity { Busy = true, Decoded = 100 }, ref last, 0.5, null));
        Assert.Equal(100, last);
        Assert.Equal(20, LiveMetrics.NextRate(new EngineActivity { Busy = true, Decoded = 110 }, ref last, 0.5, null));
        // a new answer starts again from 0
        Assert.Equal(10, LiveMetrics.NextRate(new EngineActivity { Busy = true, Decoded = 5 }, ref last, 0.5, 20));
        // tiered, reading a prompt: no figure yet, the rate stays
        Assert.Equal(8, LiveMetrics.NextRate(new EngineActivity { Busy = true }, ref last, 0.5, 8));
    }

    [Fact]
    public void GpuLoadIsTheBusiestEngineSummedOverProcesses()
    {
        var values = new (string, double)[]
        {
            ("pid_100_luid_0x00000000_0x0000C1D2_phys_0_eng_0_engtype_3D", 20),
            ("pid_200_luid_0x00000000_0x0000C1D2_phys_0_eng_0_engtype_3D", 30),
            ("pid_100_luid_0x00000000_0x0000C1D2_phys_0_eng_3_engtype_Compute_0", 40),
            ("pid_100_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 1),
            ("garbage", 99),
        };
        Assert.Equal(50, PdhCounters.GpuPercent(values));
        Assert.Equal(0, PdhCounters.GpuPercent(Array.Empty<(string, double)>()));
        Assert.Equal(100, PdhCounters.GpuPercent(new (string, double)[] { ("luid_1_eng_0_engtype_3D", 150) }));
    }

    [Fact]
    public void VramIsTheChosenCards()
    {
        var values = new (string, double)[] { ("luid_0x00000000_0x0000C1D2_phys_0", 5e9), ("luid_0x00000000_0x0000AAAA_phys_0", 1e9) };
        Assert.Equal(1_000_000_000UL, PdhCounters.VramUsed(values, 0xAAAA));
        Assert.Equal(5_000_000_000UL, PdhCounters.VramUsed(values, 0));
        Assert.Equal(5_000_000_000UL, PdhCounters.VramUsed(values, 0x1234));
    }

    [Fact]
    public void DiskIsTheModelsDriveAndTheTotal()
    {
        var values = new (string, double)[] { ("0 C:", 100), ("1 D: E:", 200), ("_Total", 300) };
        Assert.Equal((100, 300), PdhCounters.DiskRates(values, @"C:\"));
        Assert.Equal((200, 300), PdhCounters.DiskRates(values, @"E:\models"));
        Assert.Equal((0, 300), PdhCounters.DiskRates(values, @"Z:\"));
    }

    /// <summary>The English counter names work on this Windows, whatever its language; nothing throws and RAM is read.</summary>
    [Fact]
    public void ARealSampleCanBeTaken()
    {
        using var m = new LiveMetrics(() => null, 0, 16UL << 30);
        m.TakeSample();
        Thread.Sleep(600);
        m.TakeSample();
        var s = m.Current!;
        Assert.True(s.RamUsedGB > 0);
        Assert.True(s.RamTotalGB >= s.RamUsedGB);
        Assert.InRange(s.Cpu, 0, 100);
        Assert.InRange(s.Gpu, 0, 100);
        Assert.True(s.VramTotalGB > 17);
        Assert.Equal(2, m.Samples.Count);
    }

    [Fact]
    public void TokensFromTheChatCountWithoutAnEngineAndDropToZero()
    {
        using var m = new LiveMetrics(() => null);
        m.AddTokens(10);
        m.TakeSample();
        Assert.True(m.Current!.TokensPerSec > 0);
        Thread.Sleep(1600);
        m.TakeSample();
        Assert.Equal(0, m.Current!.TokensPerSec);
    }

    [Fact]
    public void IntervalsAsOnTheMac()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.5), LiveMetrics.ActiveInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), LiveMetrics.IdleInterval);
        Assert.Equal(90, LiveMetrics.WindowSeconds);
    }

    [Fact]
    public async Task ARunningEngineFeedsTheMeter()
    {
        int port = FreePort();
        var card = FakeGpuBackend.Nvidia(16);
        var model = ModelCatalog.Shared.Model("glm-5.3-flash-compact-24")!;
        var plan = MemoryPlanner.Plan(TestModels.IndexOf(model), SystemInfo.Simulated(card, 128), 65_536, false, EngineKind.Tiered);
        using var e = new TieredEngine(port) { ExecutableOverride = FakeExe, HealthInterval = TimeSpan.FromMilliseconds(50) };
        e.ArgumentsOverride = new[] { "--port", port.ToString(), "--parent-pipe" };
        e.Start(new EngineStart(model, Array.Empty<string>(), plan, card));
        Assert.True(WaitUntil(() => e.State.IsRunning));
        using var m = new LiveMetrics(() => e) { Fast = true };
        Assert.True(WaitUntil(() =>
        {
            m.TakeSample();
            return m.EngineBusy && m.Current?.TokensPerSec > 0;
        }));
        Assert.Equal(0.8, m.Current!.VramHit);
        Assert.Equal(61, m.Current!.GpuTemperature);
        await e.StopAndWait();
    }
}

public class SystemInfoTests : EngineTest
{
    [Fact]
    public void ThisPcReadsAsPlainNumbers()
    {
        var s = SystemInfo.Current(FakeGpuBackend.Nvidia(16));
        Assert.True(s.RamTotal > 0);
        Assert.True(s.RamAvailable > 0);
        Assert.True(s.PerformanceCores >= 1);
        Assert.False(string.IsNullOrEmpty(s.Cpu));
        Assert.StartsWith("Windows", s.WindowsVersion);
        Assert.True(SystemInfo.AvailableMemory() > 0);
    }

    [Fact]
    public void GigabytesAsOnTheMac()
    {
        Settings.Shared.Language = "en";
        Assert.Equal("12.3 GB", 12_345_678_901UL.Gb());
        Assert.Equal("12 GB", 12_345_678_901UL.GbShort());
        Settings.Shared.Language = "nl";
        Assert.Equal("12,3 GB", 12_345_678_901UL.Gb());
    }
}

public class SystemCheckTests : EngineTest
{
    [Fact]
    public void PageFileOfVramPlusNineIsAskedFor()
    {
        var todo = SystemCheck.PageFileItem(8UL << 30, 25UL << 30);
        Assert.Equal(SystemCheck.CheckState.Todo, todo.State);
        Assert.Equal(SystemCheck.CheckAction.SetPageFile, todo.Action);
        Assert.Equal("Stel in", todo.ActionLabel);
        Assert.Contains("herstart", todo.Detail);
        Assert.Contains("25 GB", todo.Detail);
        Assert.Equal(SystemCheck.CheckState.Ok, SystemCheck.PageFileItem(25UL << 30, 25UL << 30).State);
        Assert.Equal(SystemCheck.CheckState.Ok, SystemCheck.PageFileItem(0, 0).State);
    }

    [Fact]
    public void ThePageFileScriptAsksForAFixedSize()
    {
        var script = SystemCheck.PageFileScript(25_600, @"C:\");
        Assert.Contains("AutomaticManagedPagefile = $false", script);
        Assert.Contains("InitialSize = [uint32]25600", script);
        Assert.Contains("MaximumSize = [uint32]25600", script);
        Assert.Contains(@"'C:\pagefile.sys'", script);
    }

    [Fact]
    public void ASlowDriveWarnsAndDoesNotBlock()
    {
        var nvme = new DriveReport(@"C:\", 400_000_000_000, 2_000_000_000_000, true, true, true);
        Assert.Equal(SystemCheck.CheckState.Ok, SystemCheck.NvmeItem(nvme).State);
        Assert.Equal(SystemCheck.CheckState.Optional, SystemCheck.NvmeItem(nvme with { IsNvme = false }).State);
        Assert.Equal(SystemCheck.CheckState.Failed, SystemCheck.NvmeItem(nvme with { Ready = false }).State);
    }

    [Fact]
    public void DefenderOffersItsSettings()
    {
        var fine = new SystemCheck.Item("engine", "Engine", "x", SystemCheck.CheckState.Ok);
        Assert.Equal(SystemCheck.CheckState.Optional, SystemCheck.DefenderItem(fine).State);
        var blocked = fine with { State = SystemCheck.CheckState.Failed, Action = SystemCheck.CheckAction.OpenDefender };
        Assert.Equal(SystemCheck.CheckState.Failed, SystemCheck.DefenderItem(blocked).State);
    }

    [Fact]
    public void TheMarkOfTheWebComesOff()
    {
        var dir = Path.Combine(Root, "engine");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "x.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(Path.Combine(dir, "note.txt"), "x");
        File.WriteAllText(exe + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3\n");
        File.WriteAllText(Path.Combine(dir, "note.txt") + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3\n");
        Assert.True(File.Exists(exe + ":Zone.Identifier"));
        Assert.Equal(1, SystemCheck.RemoveMarkOfTheWeb(dir));
        Assert.False(File.Exists(exe + ":Zone.Identifier"));
        Assert.True(File.Exists(Path.Combine(dir, "note.txt") + ":Zone.Identifier"));
        Assert.Equal(0, SystemCheck.RemoveMarkOfTheWeb(Path.Combine(Root, "none")));
    }

    [Fact]
    public void AMissingEngineFails()
    {
        var item = SystemCheck.EngineItem(FakeGpuBackend.Nvidia(16));
        Assert.Equal(SystemCheck.CheckState.Failed, item.State);
        Assert.Contains("Installeer WinAIMagic opnieuw", item.Detail);
    }

    private EngineHost Host(SystemInfo s, IGpuBackend b, ModelCatalog? catalog = null)
    {
        var store = new ModelStore(catalog ?? ModelCatalog.Shared, new HttpClient(new FakeServer()), timer: false);
        return new EngineHost(new BackendChoice(b, "reden", Array.Empty<string>()), store, new ClusterController(b, browse: false)) { SimulatedSystem = s };
    }

    [Fact]
    public void WithoutAModelTheCheckRecommendsOne()
    {
        var b = FakeGpuBackend.Nvidia(16);
        using var host = Host(SystemInfo.Simulated(b, 128, 10), b);
        var items = SystemCheck.ModelItems(host);
        var model = items.Single(i => i.Id == "model");
        Assert.Equal(SystemCheck.CheckState.Todo, model.State);
        Assert.Contains("GLM-5.3-Flash Compact 24", model.Detail);
        Assert.Contains("streamen van de SSD", model.Detail);
        Assert.Equal(SystemCheck.CheckAction.OpenModels, model.Action);
        Assert.Equal(SystemCheck.CheckState.Ok, items.Single(i => i.Id == "disk").State);
    }

    [Fact]
    public void TooLittleDiskFails()
    {
        var b = FakeGpuBackend.Nvidia(16);
        using var host = Host(SystemInfo.Simulated(b, 128, 10) with { FreeDisk = 10_000_000_000 }, b);
        var disk = SystemCheck.ModelItems(host).Single(i => i.Id == "disk");
        Assert.Equal(SystemCheck.CheckState.Failed, disk.State);
        Assert.Contains("andere schijf", disk.Detail);
    }

    /// <summary>A small PC streams, it is never told that a model is too big; it can also lend its GPU.</summary>
    [Fact]
    public void ASmallPcIsToldItStreams()
    {
        var b = FakeGpuBackend.Amd(8);
        using var host = Host(SystemInfo.Simulated(b, 16, 8), b);
        var model = SystemCheck.ModelItems(host).Single(i => i.Id == "model");
        Assert.Equal(SystemCheck.CheckState.Optional, model.State);
        Assert.Contains("streamen", model.Detail);
        Assert.DoesNotContain("te groot", model.Detail);
        Assert.Equal(SystemCheck.CheckAction.OpenCluster, model.Action);
    }

    /// <summary>The recommendation on a PC too small for any model says that it streams, and that this has not been measured.</summary>
    [Fact]
    public void ASmallPcIsRecommendedTheModelThatStreams()
    {
        var card = FakeGpuBackend.Nvidia(8);
        using (var host = Host(SystemInfo.Simulated(card, 16, 8), card))
        {
            var model = SystemCheck.ModelItems(host).Single(i => i.Id == "model");
            Assert.Equal(SystemCheck.CheckState.Todo, model.State);
            Assert.Equal(SystemCheck.CheckAction.OpenModels, model.Action);
            Assert.Contains("8 GB VRAM en 16 GB RAM: Qwen3.6", model.Detail);
            Assert.Contains("nog niet gemeten", model.Detail);
            Assert.DoesNotContain("helemaal", model.Detail);
            Assert.Equal("qwen3.6-35b-a3b-q4", host.SelectedModelId);
        }
        var none = FakeGpuBackend.Cpu();
        using (var host = Host(SystemInfo.Simulated(none, 16, 8), none))
        {
            var model = SystemCheck.ModelItems(host).Single(i => i.Id == "model");
            Assert.Contains("deze pc met 16 GB RAM: Qwen3.6", model.Detail);
            Assert.DoesNotContain("VRAM", model.Detail);
        }
    }

    [Fact]
    public void AMissingFolderShowsAndDeletesNothing()
    {
        var b = FakeGpuBackend.Nvidia(16);
        using var host = Host(SystemInfo.Simulated(b, 128, 10), b);
        Settings.Shared.ModelFolder = Path.Combine(Root, "gone");
        var model = SystemCheck.ModelItems(host).Single();
        Assert.Equal(SystemCheck.CheckState.Failed, model.State);
        Assert.Contains("niet (meer) op deze pc", model.Detail);
    }

    [Fact]
    public async Task AllRowsOfTheBrief()
    {
        var b = FakeGpuBackend.Nvidia(16);
        using var host = Host(SystemInfo.Simulated(b, 128, 10), b);
        var check = new SystemCheck();
        await check.Run(host);
        var ids = check.Items.Select(i => i.Id).ToList();
        Assert.Equal(new[] { "app", "hardware", "engine", "download", "model", "disk", "pagefile", "nvme", "defender", "devtools" }, ids);
        // a test run has no engine folder
        Assert.True(check.NeedsAttention);
        Assert.Contains("reden", check.Items.Single(i => i.Id == "hardware").Detail);
        Assert.False(check.Running);
    }
}

public class BenchmarkTests : EngineTest
{
    [Fact]
    public void TheLongPromptAsOnTheMac()
    {
        var p = BenchmarkRunner.LongPrompt();
        Assert.Equal(36, Regex.Matches(p, @"Notitie \d+:").Count);
        Assert.Matches(@"^\[\d+\.\d+\] Lees de volgende notities en vat ze samen in drie zinnen\.\n\n", p);
        Assert.Contains("Notitie 36:", p);
        Assert.Contains("rapport 1245", p);
        Assert.Contains("kansen voor gemeenten", p);
        Assert.DoesNotContain("Apple", p);
    }

    [Fact]
    public void TheVersionShownIsTheOneInTheBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props"));
        var version = Regex.Match(props, "<Version>(.+?)</Version>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal(version, Paths.AppVersion);
        Assert.Contains(version, SystemCheck.AppItem().Detail);
    }

    [Fact]
    public void MeasurementsWithExpertsSkippedStandBesideTheFullModel()
    {
        const string glm = "glm-5.3-flash-compact-24";
        // written by 0.2.0: no field for it, so the full model
        File.WriteAllText(Paths.BenchmarksFile, "[{\"modelID\":\"" + glm + "\",\"modelName\":\"GLM\",\"decodeTPS\":8.9}]");
        Assert.Equal("off", new BenchmarkRunner().Results[0].SkipExperts);

        File.WriteAllText(Paths.BenchmarksFile, JsonSerializer.Serialize(new[]
        {
            new BenchResult { ModelId = glm, ModelName = "GLM", DecodeTps = 12, SkipExperts = "strong" },
            new BenchResult { ModelId = glm, ModelName = "GLM", DecodeTps = 8 },
            new BenchResult { ModelId = glm, ModelName = "GLM", DecodeTps = 11, SkipExperts = "light" },
            new BenchResult { ModelId = glm, ModelName = "GLM", DecodeTps = 9 },
        }));
        var latest = new BenchmarkRunner().Latest(ModelCatalog.Shared);
        Assert.Equal(new[] { "off", "light", "strong" }, latest.Select(r => r.SkipExperts));
        Assert.Equal(new double[] { 9, 11, 12 }, latest.Select(r => r.DecodeTps));
    }

    [Fact]
    public void ResultsStayAndTheExportHasNoPersonalData()
    {
        var bench = new BenchmarkRunner();
        Assert.Empty(bench.Results);
        File.WriteAllText(Paths.BenchmarksFile, JsonSerializer.Serialize(new[]
        {
            new BenchResult { ModelId = "qwen3.6-35b-a3b-q4", ModelName = "Qwen", LoadSeconds = 1, DecodeTps = 39, VramGb = 15, RamGb = 30 },
            new BenchResult { ModelId = "qwen3.6-35b-a3b-q4", ModelName = "Qwen", DecodeTps = 40 },
            new BenchResult { ModelId = "glm-5.3-flash-compact-24", ModelName = "GLM", DecodeTps = 17 },
        }));
        var again = new BenchmarkRunner();
        Assert.Equal(3, again.Results.Count);
        var latest = again.Latest(ModelCatalog.Shared);
        Assert.Equal(new[] { "glm-5.3-flash-compact-24", "qwen3.6-35b-a3b-q4" }, latest.Select(r => r.ModelId));
        Assert.Equal(40, latest[1].DecodeTps);

        var b = FakeGpuBackend.Nvidia(16);
        var path = Path.Combine(Root, "meting.json");
        again.Export(path, SystemInfo.Simulated(b, 128, 10), b);
        var text = File.ReadAllText(path);
        Assert.Contains("nvidia/cc7/16", text);
        Assert.Contains("\"vramGB\"", text);
        Assert.Contains("\"decodeTPS\": 39", text);
        Assert.DoesNotContain(Environment.UserName, text);
        Assert.DoesNotContain(Environment.MachineName, text);
        Assert.DoesNotContain("\\\\", text);

        again.Clear();
        Assert.Empty(new BenchmarkRunner().Results);
    }

    [Fact]
    public async Task AFullRunAgainstAFakeEngine()
    {
        Settings.Shared.Port = FreePort();
        var bytes = TestModels.TinyModel();
        var (store, model) = HostTests.TinyStore(bytes);
        var b = FakeGpuBackend.Nvidia(16);
        using var host = new EngineHost(new BackendChoice(b, "reden", Array.Empty<string>()), store, new ClusterController(b, browse: false)) { SimulatedSystem = SystemInfo.Simulated(b, 128, 10) };
        host.PrepareEngine = e => HostTests.ToFake(e, Settings.Shared.Port);
        var bench = new BenchmarkRunner(new HttpClient());
        await bench.Run(new[] { model.Id, "not-there" }, host);
        Assert.Null(bench.Running);
        Assert.Equal("", bench.Step);
        var r = Assert.Single(bench.Results);
        Assert.Equal(model.Id, r.ModelId);
        Assert.Equal(100.5, r.PromptTps);
        Assert.Equal(2500, r.PromptTokens);
        Assert.Equal(20.25, r.DecodeTps);
        Assert.Equal(300, r.DecodeTokens);
        Assert.True(File.Exists(Paths.BenchmarksFile));
        await host.Engine!.StopAndWait();
    }
}

public class ClusterTests : EngineTest
{
    private static readonly FakeGpuBackend Card = FakeGpuBackend.Nvidia(16);
    private static ClusterController Make(IGpuBackend? b = null) => new(b ?? Card, browse: false);

    [Fact]
    public void ServiceNameAndPortAsOnTheMac()
    {
        Assert.Equal("_mdsrpc._tcp", ClusterController.ServiceType);
        Assert.Equal(50052, ClusterController.DefaultPort);
    }

    [Fact]
    public void AnnouncedAttributesBecomeAHelper()
    {
        var h = ClusterController.ParseHelper("Studio", new[] { "ip=169.254.10.2", "chip=RTX 4090", "mem=192", "gpu=24" }, 50052)!;
        Assert.Equal("Studio", h.Name);
        Assert.Equal("RTX 4090", h.Chip);
        Assert.Equal(192, h.MemoryGb);
        Assert.Equal(24, h.GpuGb);
        Assert.Equal("169.254.10.2:50052", h.Id);
        Assert.True(h.ViaDirectLink);
        Assert.Null(ClusterController.ParseHelper("x", new[] { "chip=y" }, 1));
        Assert.Equal(50052, ClusterController.ParseHelper("x", new[] { "ip=1.2.3.4" }, 0)!.Port);
        Assert.Equal(40000, ClusterController.ParseHelper("x", new[] { "ip=1.2.3.4" }, 40000)!.Port);
        Assert.Equal(50052, ClusterController.ParseHelper("x", new[] { "ip=1.2.3.4", "port=50052" }, 40000)!.Port);
        Assert.False(ClusterController.ParseHelper("x", new[] { "ip=1.2.3.4" }, 0)!.ViaDirectLink);
    }

    [Fact]
    public void ManualHelpersStayAndTakePart()
    {
        using (var c = Make())
        {
            c.AddManual("192.168.1.5");
            c.AddManual("192.168.1.6:40000");
            c.AddManual("");
            Assert.Equal(new[] { "192.168.1.5:50052", "192.168.1.6:40000" }, c.Helpers.Select(h => h.Id));
            Assert.Equal("192.168.1.5:50052,192.168.1.6:40000", c.RpcArgument);
            // unknown memory for a helper added by hand
            Assert.Equal(new ulong[] { 0, 0 }, c.HelperBudgets);
            c.Remove(c.Helpers[0]);
        }
        using var again = Make();
        Assert.Equal("192.168.1.6:40000", Assert.Single(again.Helpers).Id);
        Assert.Contains("192.168.1.6:40000", again.Selected);
    }

    [Fact]
    public void AHelperNeverUsesHelpers()
    {
        using var c = Make();
        c.AddManual("192.168.1.5");
        Settings.Shared.ClusterRole = "helper";
        Assert.Empty(c.Active);
        Assert.Null(c.RpcArgument);
        Settings.Shared.ClusterRole = "main";
        Assert.Single(c.Active);
        c.Selected = new HashSet<string>();
        Assert.Empty(c.Active);
    }

    [Fact]
    public void AHelpersBudgetIsItsVramLessItsOwnRoom()
    {
        using var c = Make();
        c.BudgetOverride = new ulong[] { 5 };
        Assert.Equal(new ulong[] { 5 }, c.HelperBudgets);
    }

    /// <summary>The helper listens on a direct link; the LAN only after the switch.</summary>
    [Fact]
    public void BindAddressRules()
    {
        var direct = ("Thunderbolt", "169.254.1.1", true);
        var lan = ("Ethernet", "192.168.1.2", false);
        Assert.Equal("169.254.1.1", ClusterController.BindAddress(new[] { direct, lan }, false));
        Assert.Null(ClusterController.BindAddress(new[] { lan }, false));
        Assert.Equal("192.168.1.2", ClusterController.BindAddress(new[] { lan }, true));
        Assert.Null(ClusterController.BindAddress(Array.Empty<(string, string, bool)>(), true));
        Assert.True(ClusterController.IsDirect("Intel Thunderbolt Networking"));
        Assert.True(ClusterController.IsDirect("USB4 P2P Network Adapter"));
        Assert.False(ClusterController.IsDirect("Realtek Gaming 2.5GbE"));
    }

    [Fact]
    public void WithoutABinaryOrAConnectionItSaysSo()
    {
        using var c = Make();
        c.StartHelper("127.0.0.1");
        Assert.False(c.HelperRunning);
        Assert.Contains("ggml-rpc-server niet gevonden", c.HelperLog[0]);

        c.ExecutableOverride = EngineTest.FakeExe;
        // only when this PC has no direct link does the message appear: the switch is off in a fresh settings file
        if (ClusterController.Addresses().All(a => !a.Direct))
        {
            c.StartHelper();
            Assert.False(c.HelperRunning);
            Assert.Contains("Geen directe verbinding", c.HelperLog[0]);
        }
    }

    [Fact]
    public void AHelperAddedByHandSaysWhatItCanHold()
    {
        Assert.Equal(16310, ClusterController.ParseHelperMemory("Available devices:\n  CUDA0: Kaart (16310 MiB, 15172 MiB free)\n  RPC0: 10.0.0.2:50052 (16310 MiB, 1 MiB free)\n"));
        Assert.Null(ClusterController.ParseHelperMemory("Failed to connect to 10.0.0.2:50052"));
        using var c = Make();
        c.ProbeOverride = _ => 16310;
        c.AddManual("10.0.0.2:50052");
        Assert.True(WaitUntil(() => c.Helpers[0].GpuGb > 17));
        Assert.False(c.Helpers[0].Unreachable);
        Assert.True(c.HelperBudgets[0] > 13_000_000_000);
        c.ProbeOverride = _ => null;
        c.AddManual("10.0.0.3");
        Assert.True(WaitUntil(() => c.Helpers.Single(h => h.Host == "10.0.0.3").Unreachable));
        Assert.DoesNotContain(c.Active, h => h.Host == "10.0.0.3");     // the engine would not start with it
        c.ProbeOverride = _ => 8000;
        c.RecheckManual();
        Assert.True(WaitUntil(() => c.Active.Any(h => h.Host == "10.0.0.3")));   // turned on since
    }

    /// <summary>The real server and the real announcement, only with WAM_REAL_ENGINE set to the installation's engine folder.</summary>
    [Fact]
    public void TheRealHelperServesAndIsFound()
    {
        if (Environment.GetEnvironmentVariable("WAM_REAL_ENGINE") is not { } root) return;
        using var helper = Make();
        helper.ExecutableOverride = Path.Combine(root, "cpu", "ggml-rpc-server.exe");
        helper.StartHelper("127.0.0.1");
        Assert.True(helper.HelperRunning);
        Assert.True(WaitUntil(() =>
        {
            try { using var s = new System.Net.Sockets.TcpClient("127.0.0.1", ClusterController.DefaultPort); return true; }
            catch (System.Net.Sockets.SocketException) { return false; }
        }));
        using var main = new ClusterController(Card, browse: true);
        bool found = WaitUntil(() => main.Helpers.Any(h => h.Host == "127.0.0.1" && !h.Manual), 25);
        Console.WriteLine("found through DNS-SD: " + found + "; " + string.Join(" | ", main.Helpers.Select(h => $"{h.Name} {h.Chip} {h.GpuGb} {h.Id}")));
        Console.WriteLine("helper log: " + string.Join(" | ", helper.HelperLog.TakeLast(6)));
        helper.StopHelper();
        Assert.True(WaitUntil(() => !helper.HelperRunning));
        Assert.Throws<System.Net.Sockets.SocketException>(() => new System.Net.Sockets.TcpClient("127.0.0.1", ClusterController.DefaultPort));
        Assert.True(found, "not found through DNS-SD");
    }

    [Fact]
    public void TheHelperRunsAndStopsInItsJob()
    {
        using var c = Make();
        c.ExecutableOverride = EngineTest.FakeExe;
        c.PublishService = false;
        int held = SleepGuard.Holds;
        c.StartHelper("127.0.0.1");
        Assert.True(c.HelperRunning);
        Assert.Equal("127.0.0.1:50052", c.HelperAddress);
        Assert.Equal(held + 1, SleepGuard.Holds);
        Assert.True(WaitUntil(() => c.HelperLog.Any(l => l.Contains("load_tensors"))));
        c.StopHelper();
        Assert.True(WaitUntil(() => !c.HelperRunning));
        Assert.Equal("", c.HelperAddress);
        Assert.Equal(held, SleepGuard.Holds);
    }
}

public class HostTests : EngineTest
{
    /// <summary>A store with a real tiny model on disk.</summary>
    public static (ModelStore Store, ModelInfo Model) TinyStore(byte[] bytes, string engine = "llama")
    {
        var model = new ModelInfo
        {
            Id = "tiny", Name = "Tiny", Family = "qwen", Summary = "s", Engine = engine,
            Pack = engine == "tiered" ? new PackInfo("glm") : null,
            Tier = engine == "tiered" ? new TierInfo { Exe = "wam-glm.exe" } : null,
            Files = new[] { new ModelFile { Local = "tiny.gguf", Size = (ulong)bytes.Length, Sha256 = new[] { TestModels.Sha(bytes) }, Url = "https://x.test/a" } },
            Memory = (ulong)bytes.Length, Dense = (ulong)bytes.Length / 3,
        };
        Directory.CreateDirectory(Paths.ModelDir(model.Id));
        File.WriteAllBytes(Path.Combine(Paths.ModelDir(model.Id), "tiny.gguf"), bytes);
        var store = new ModelStore(new ModelCatalog { Models = new[] { model } }, new HttpClient(new FakeServer()), timer: false);
        return (store, model);
    }

    /// <summary>A GLM on the main engine with its image files named, none of them here yet.</summary>
    private static (ModelStore Store, ModelInfo Model, TieredVisionInfo Vision) GlmStore()
    {
        var bytes = TestModels.TinyModel();
        var model = new ModelInfo
        {
            Id = "tiny", Name = "Tiny", Family = "glm", Summary = "s", Engine = "tiered", Vision = "vv", Pack = new PackInfo("glm"),
            Tier = new TierInfo { Exe = "wam-glm.exe" },
            Files = new[] { new ModelFile { Local = "tiny.gguf", Size = (ulong)bytes.Length, Sha256 = new[] { TestModels.Sha(bytes) }, Url = "https://x.test/a" } },
            Memory = (ulong)bytes.Length, Dense = (ulong)bytes.Length / 3,
        };
        var vision = new TieredVisionInfo
        {
            Id = "vv",
            Files = new[]
            {
                new ModelFile { Local = "encoder.gguf", Size = 4, Sha256 = new[] { "" }, Url = "https://x.test/e" },
                new ModelFile { Local = "vocab.gguf", Size = 2, Sha256 = new[] { "" }, Url = "https://x.test/w" },
            },
        };
        Directory.CreateDirectory(Paths.ModelDir(model.Id));
        File.WriteAllBytes(Path.Combine(Paths.ModelDir(model.Id), "tiny.gguf"), bytes);
        var catalog = new ModelCatalog { Models = new[] { model }, TieredVision = new[] { vision } };
        return (new ModelStore(catalog, new HttpClient(new FakeServer()), timer: false), model, vision);
    }

    /// <summary>The composer's reasons, in the order someone would fix them; none left, and the plan has vision.</summary>
    [Fact]
    public void TheHostSaysWhyThereAreNoImages()
    {
        var (store, _, vision) = GlmStore();
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Assert.Equal(EngineKind.Tiered, host.EngineForSelected);
        Settings.Shared.Vision = false;
        Assert.Contains("instellingen", host.WhyNoImages);
        Settings.Shared.Vision = true;
        // a model without them still starts
        Assert.Contains("ontbreken", host.WhyNoImages);
        Assert.True(host.CanStart);
        Assert.False(host.Plan!.Vision);

        Directory.CreateDirectory(vision.Folder);
        File.WriteAllBytes(Path.Combine(vision.Folder, "encoder.gguf"), new byte[4]);
        store.Refresh();
        Assert.Contains("ontbreken", host.WhyNoImages);
        File.WriteAllBytes(Path.Combine(vision.Folder, "vocab.gguf"), new byte[2]);
        store.Refresh();
        Assert.Contains("encoder", host.WhyNoImages);
        Assert.False(host.Plan!.Vision);

        Directory.CreateDirectory(Path.GetDirectoryName(TieredEngine.EncoderExe)!);
        File.WriteAllBytes(TieredEngine.EncoderExe, new byte[1]);
        try
        {
            Assert.Null(host.WhyNoImages);
            Assert.True(host.Plan!.Vision);
        }
        finally { File.Delete(TieredEngine.EncoderExe); }
    }

    [Fact]
    public void AModelWithoutImageFilesOnThisEngineSaysSo()
    {
        var (store, _) = TinyStore(TestModels.TinyModel());
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Assert.Contains("geen afbeeldingen", host.WhyNoImages);
    }

    /// <summary>A running engine answers for itself: started without the files it has no images, whatever the disk holds now.</summary>
    [Fact]
    public async Task ARunningEngineWithoutImagesAsksForARestart()
    {
        Settings.Shared.Port = FreePort();
        var bytes = TestModels.TinyModel();
        var (_, model) = TinyStore(bytes);
        var with = model with { Vision = "vv" };
        var catalog = new ModelCatalog
        {
            Models = new[] { with },
            Vision = new[] { new VisionInfo { Id = "vv", Local = "v.gguf", Size = 3, Sha256 = new[] { "" }, Url = "https://x.test/v" } },
        };
        using var store = new ModelStore(catalog, new HttpClient(new FakeServer()), timer: false);
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        host.StartEngine();
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning), host.Engine!.State.ToString());
        Assert.False(host.Engine!.Capabilities.Vision);
        Assert.Contains("ontbreken", host.WhyNoImages);

        Directory.CreateDirectory(Paths.VisionDir);
        File.WriteAllBytes(Path.Combine(Paths.VisionDir, "v.gguf"), new byte[3]);
        store.Refresh();
        Assert.Contains("opnieuw", host.WhyNoImages);
        await host.Engine.StopAndWait();

        Assert.Null(host.WhyNoImages);
        host.StartEngine();
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning), host.Engine!.State.ToString());
        Assert.True(host.Engine!.Capabilities.Vision);
        Assert.Null(host.WhyNoImages);
        await host.Engine.StopAndWait();
    }

    /// <summary>The engine a host just made runs the fake process.</summary>
    public static void ToFake(IEngine engine, int port)
    {
        var e = (EngineBase)engine;
        e.ExecutableOverride = FakeExe;
        e.HealthInterval = TimeSpan.FromMilliseconds(50);
        // only the main engine holds a pipe to the process's stdin
        e.ArgumentsOverride = engine is TieredEngine ? new[] { "--port", port.ToString(), "--parent-pipe" } : new[] { "--port", port.ToString() };
    }

    private static EngineHost Host(ModelStore store, IGpuBackend b, int ram = 128)
    {
        var host = new EngineHost(new BackendChoice(b, "reden", Array.Empty<string>()), store, new ClusterController(b, browse: false))
        {
            SimulatedSystem = SystemInfo.Simulated(b, ram, 10),
        };
        host.PrepareEngine = e => ToFake(e, Settings.Shared.Port);
        return host;
    }

    [Fact]
    public async Task StartRunStopWithThePlan()
    {
        Settings.Shared.Port = FreePort();
        var (store, model) = TinyStore(TestModels.TinyModel());
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Assert.Equal(model.Id, host.SelectedModelId);
        Assert.True(host.CanStart);
        var plan = host.Plan!;
        Assert.Equal(EngineKind.Llama, plan.Engine);
        Assert.Equal(PlanLabel.FitsInVramRam, plan.Label);
        Assert.Equal(65_536, plan.ContextSize);
        Assert.False(plan.Vision);

        host.StartEngine();
        Assert.IsType<LlamaEngine>(host.Engine);
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning), host.Engine!.State.ToString());
        Assert.True(host.Metrics.Fast);
        Assert.Equal(model, host.Engine.Model);

        var refused = await host.MoveModels(Path.Combine(Root, "elsewhere"));
        Assert.Contains("Stop eerst de engine", refused);

        await host.Engine.StopAndWait();
        Assert.False(host.Metrics.Fast);
        Assert.Equal(EngineState.Stopped, host.Engine.State);
    }

    [Fact]
    public void WhatIsMissingKeepsItFromStarting()
    {
        var (store, _) = TinyStore(TestModels.TinyModel());
        File.Delete(Path.Combine(Paths.ModelDir("tiny"), "tiny.gguf"));
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        host.LoadIndex();
        Assert.Null(host.Index);
        Assert.Null(host.Plan);
        Assert.False(host.CanStart);
        host.StartEngine();
        Assert.Null(host.Engine);
    }

    [Fact]
    public void ABrokenFileShowsAnError()
    {
        var (store, _) = TinyStore(new byte[1000]);
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Assert.Null(host.Index);
        Assert.Contains("geen GGUF-bestand", host.IndexError);
    }

    [Fact]
    public void TheMainEngineBuildsItsPackAtTheStartAndTheLimitedCardNeedsNone()
    {
        var (store, _) = TinyStore(TestModels.TinyModel(), "tiered");
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Assert.Equal(EngineKind.Tiered, host.EngineForSelected);
        Assert.True(host.CanStart);
        Assert.False(host.ClusterApplies);
        Assert.Empty(host.ClusterModels);

        using var limited = Host(TinyStore(TestModels.TinyModel(), "tiered").Store, FakeGpuBackend.Vulkan(16));
        Assert.Equal(EngineKind.Llama, limited.EngineForSelected);
        Assert.True(limited.CanStart);
        Assert.True(limited.ClusterApplies);
    }

    [Fact]
    public void SkippingExpertsIsOfferedForGlmOnTheMainEngineOnly()
    {
        var glm = ModelCatalog.Shared.Models.First(m => m.Family == "glm" && m.Engine == "tiered");
        var other = ModelCatalog.Shared.Models.First(m => m.Family == "deepseek" && m.Engine == "tiered");
        var (store, _) = TinyStore(TestModels.TinyModel(), "tiered");
        using var nvidia = Host(store, FakeGpuBackend.Nvidia(16));
        using var vulkan = Host(TinyStore(TestModels.TinyModel(), "tiered").Store, FakeGpuBackend.Vulkan(16));
        Assert.True(nvidia.OffersSkipExperts(glm));
        Assert.False(nvidia.OffersSkipExperts(other));
        Assert.False(vulkan.OffersSkipExperts(glm));

        Settings.Shared.SkipExperts = "light";
        Assert.Equal("light", nvidia.SkipExpertsFor(glm));
        Assert.Equal("off", nvidia.SkipExpertsFor(other));
        Assert.Equal("off", vulkan.SkipExpertsFor(glm));
    }

    [Fact]
    public void AChosenModelSurvivesAndAnUnknownOneFallsBack()
    {
        var (store, model) = TinyStore(TestModels.TinyModel());
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Settings.Shared.SelectedModelId = "gone";
        Assert.Equal(model.Id, host.SelectedModelId);
    }

    [Fact]
    public async Task SelectingAnotherModelRestartsTheEngine()
    {
        Settings.Shared.Port = FreePort();
        var bytes = TestModels.TinyModel();
        ModelInfo Tiny(string id) => new()
        {
            Id = id, Name = id, Family = "qwen", Summary = "s",
            Files = new[] { new ModelFile { Local = id + ".gguf", Size = (ulong)bytes.Length, Sha256 = new[] { "" }, Url = "u" } },
            Memory = (ulong)bytes.Length,
        };
        var catalog = new ModelCatalog { Models = new[] { Tiny("one"), Tiny("two") } };
        foreach (var m in catalog.Models)
        {
            Directory.CreateDirectory(Paths.ModelDir(m.Id));
            File.WriteAllBytes(Path.Combine(Paths.ModelDir(m.Id), m.Files[0].Local), bytes);
        }
        using var host = Host(new ModelStore(catalog, new HttpClient(new FakeServer()), timer: false), FakeGpuBackend.Nvidia(16));
        host.SelectedModelId = "one";
        host.StartEngine();
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning));
        var first = host.Engine;
        await host.Select("two");
        Assert.NotSame(first, host.Engine);
        Assert.Equal("two", host.Engine!.Model!.Id);
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning));
        await host.Engine.StopAndWait();
    }

    [Fact]
    public async Task TheModelsMoveThroughTheHostAndAreFoundThere()
    {
        var (store, model) = TinyStore(TestModels.TinyModel());
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        var to = Path.Combine(Root, "new-place");
        Assert.Null(await host.MoveModels(to));
        Assert.Equal(to, Paths.ModelsRoot);
        Assert.True(File.Exists(Path.Combine(to, model.Id, "tiny.gguf")));
        Assert.True(host.CanStart);
    }

    [Fact]
    public void AHelperAddsToThePlanOfTheSecondEngine()
    {
        var (store, _) = TinyStore(TestModels.TinyModel("qwen35moe", 4));
        var b = FakeGpuBackend.Nvidia(16);
        using var host = Host(store, b);
        host.Cluster.AddManual("10.0.0.2");
        host.Cluster.BudgetOverride = new ulong[] { 50_000_000_000 };
        var plan = host.Plan!;
        Assert.Equal(2, plan.TensorSplit.Count);
    }
}

/// <summary>What is shown to the user: no upstream name, no estimated speed, no make outside the GPU layer's own words.</summary>
public class ProductTextTests : EngineTest
{
    private static IEnumerable<(string File, string Text)> LiteralTexts()
    {
        var dir = Path.Combine(RepoRoot, "src", "WinAIMagic.Engine");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bL\(\$?""((?:[^""\\]|\\.)*)""\)"))
                yield return (Path.GetFileName(file), m.Groups[1].Value);
        }
    }

    [Fact]
    public void EveryTextOfTheEngineLayerPassesTheRules()
    {
        var texts = LiteralTexts().ToList();
        Assert.True(texts.Count > 40, texts.Count.ToString());
        foreach (var (file, text) in texts)
        {
            Assert.False(text.Contains("maya", StringComparison.OrdinalIgnoreCase), $"{file}: {text}");
            Assert.False(Regex.IsMatch(text, @"\btok/s\b"), $"{file}: {text}");
            Assert.False(Regex.IsMatch(text, @"te groot"), $"{file}: {text}");
            Assert.False(Regex.IsMatch(text, @"\bMac\b"), $"{file}: {text}");
        }
    }

    [Fact]
    public void TheUpstreamNameOnlyAppearsWhereItIsNeeded()
    {
        var dir = Path.Combine(RepoRoot, "src", "WinAIMagic.Engine");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            int n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (line.Contains("maya", StringComparison.OrdinalIgnoreCase)) hits.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        // the filter itself, the server's argument, and a comment that names upstream's file
        Assert.True(hits.Count <= 4, string.Join(", ", hits));
    }
}
