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
        using var host = Host(SystemInfo.Simulated(b, 32, 8), b);
        var model = SystemCheck.ModelItems(host).Single(i => i.Id == "model");
        Assert.Equal(SystemCheck.CheckState.Optional, model.State);
        Assert.Contains("streamen", model.Detail);
        Assert.DoesNotContain("te groot", model.Detail);
        Assert.Equal(SystemCheck.CheckAction.OpenCluster, model.Action);
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
    public void TheMainEngineNeedsItsPackAndTheLimitedCardDoesNot()
    {
        var (store, _) = TinyStore(TestModels.TinyModel(), "tiered");
        using var host = Host(store, FakeGpuBackend.Nvidia(16));
        Assert.Equal(EngineKind.Tiered, host.EngineForSelected);
        Assert.False(host.CanStart);
        Assert.False(host.ClusterApplies);
        Assert.Empty(host.ClusterModels);

        using var limited = Host(TinyStore(TestModels.TinyModel(), "tiered").Store, FakeGpuBackend.Vulkan(16));
        Assert.Equal(EngineKind.Llama, limited.EngineForSelected);
        Assert.True(limited.CanStart);
        Assert.True(limited.ClusterApplies);
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
