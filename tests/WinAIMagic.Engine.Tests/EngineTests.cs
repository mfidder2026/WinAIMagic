using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

public class ChildProcessTests : EngineTest
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("C:\\no\\space\\", "C:\\no\\space\\")]
    public void ArgumentsAreQuotedAsWindowsReadsThem(string raw, string quoted) =>
        Assert.Equal(quoted, ChildProcess.QuoteArgument(raw));

    [Fact]
    public void OutputAndExitCodeComeBack()
    {
        var r = ChildProcess.RunToEnd(FakeExe, new[] { "--exit", "7" }, TimeSpan.FromSeconds(20));
        Assert.Equal(7, r!.Value.Status);
        Assert.Contains("loading the model failed", r.Value.Output);
    }

    [Fact]
    public void ATimeoutEndsTheProcess()
    {
        var sw = Stopwatch.StartNew();
        Assert.Null(ChildProcess.RunToEnd(FakeExe, new[] { "--sleep" }, TimeSpan.FromSeconds(1)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void AMissingExeThrowsAWin32Error() =>
        Assert.Throws<System.ComponentModel.Win32Exception>(() => ChildProcess.RunToEnd(Path.Combine(Root, "nope.exe"), Array.Empty<string>(), TimeSpan.FromSeconds(5)));

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The job: whatever a child started ends with it, also when the app goes away without saying anything.</summary>
    [Fact]
    public void ClosingTheJobEndsTheWholeTree()
    {
        var output = new StringBuilder();
        var c = ChildProcess.Start(new ChildStart(FakeExe, new[] { "--spawn-child" })
        {
            OnOutput = (b, n) => { lock (output) output.Append(Encoding.UTF8.GetString(b, 0, n)); },
        });
        int grandchild = 0;
        Assert.True(WaitUntil(() =>
        {
            lock (output)
            {
                var m = System.Text.RegularExpressions.Regex.Match(output.ToString(), @"child (\d+)");
                if (m.Success) grandchild = int.Parse(m.Groups[1].Value);
                return m.Success;
            }
        }));
        Assert.True(Alive(c.Pid));
        Assert.True(Alive(grandchild));
        // the two, and their console hosts
        Assert.True(c.ActiveProcesses >= 2);
        int pid = c.Pid;
        c.Dispose();
        Assert.True(WaitUntil(() => !Alive(pid) && !Alive(grandchild)));
    }

    /// <summary>A break reaches only the child's own group, also from a process without a console, and the child can finish.</summary>
    [Fact]
    public void ABreakStopsTheChildAndNotUs()
    {
        var marker = Path.Combine(Root, "marker.txt");
        var ready = new ManualResetEventSlim();
        using var c = ChildProcess.Start(new ChildStart(FakeExe, new[] { "--marker", marker })
        {
            OnOutput = (_, _) => ready.Set(),
        });
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(c.RequestBreak());
        Assert.True(c.Exited.Wait(TimeSpan.FromSeconds(10)), "the child did not end on the break");
        Assert.Equal(0, c.Exited.Result);
        Assert.StartsWith("break", File.ReadAllText(marker));
    }

    /// <summary>--parent-pipe: the end of the pipe says the program that started it ended.</summary>
    [Fact]
    public void ClosingStdinIsTheParentPipe()
    {
        var marker = Path.Combine(Root, "marker.txt");
        var ready = new ManualResetEventSlim();
        using var c = ChildProcess.Start(new ChildStart(FakeExe, new[] { "--parent-pipe", "--marker", marker })
        {
            StdinPipe = true,
            OnOutput = (_, _) => ready.Set(),
        });
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
        Assert.False(c.Exited.Wait(TimeSpan.FromSeconds(1)));
        c.CloseStdin();
        Assert.True(c.Exited.Wait(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("pipe", File.ReadAllText(marker));
    }

    [Fact]
    public void TerminateEndsOnlyThatProcess()
    {
        var ready = new ManualResetEventSlim();
        using var c = ChildProcess.Start(new ChildStart(FakeExe, new[] { "--ignore-break" }) { OnOutput = (_, _) => ready.Set() });
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
        c.Terminate();
        Assert.True(c.Exited.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(15, c.Exited.Result);
    }

    [Fact]
    public void TheEnvironmentReachesTheChildAndCanRemove()
    {
        var marker = Path.Combine(Root, "m.txt");
        var ready = new ManualResetEventSlim();
        using var c = ChildProcess.Start(new ChildStart(FakeExe, new[] { "--marker", marker, "--parent-pipe" })
        {
            StdinPipe = true,
            Env = new Dictionary<string, string?> { ["LLAMA_API_KEY"] = "secret", ["NOT_THERE"] = null },
            OnOutput = (_, _) => ready.Set(),
        });
        Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
        c.CloseStdin();
        Assert.True(c.Exited.Wait(TimeSpan.FromSeconds(10)));
        Assert.Contains("key=secret", File.ReadAllText(marker));
    }

    [Fact]
    public void SleepGuardCounts()
    {
        int before = SleepGuard.Holds;
        var a = SleepGuard.Hold();
        var b = SleepGuard.Hold();
        Assert.Equal(before + 2, SleepGuard.Holds);
        a.Dispose();
        a.Dispose();
        b.Dispose();
        Assert.Equal(before, SleepGuard.Holds);
    }
}

public class LogFilterTests
{
    [Theory]
    [InlineData("[Maya] loading", "[engine] loading")]
    [InlineData("MAYA_GLM_X and maya-glm.exe", "engine_GLM_X and engine-glm.exe")]
    [InlineData("general.name = GLM-5.3-Flash-mAyA-S24", "general.name = GLM-5.3-Flash-engine-S24")]
    [InlineData("nothing here", "nothing here")]
    public void TheNameNeverLeaves(string raw, string clean) => Assert.Equal(clean, LogFilter.Clean(raw));
}

public class OrphanTests
{
    private const string Engine = @"C:\Program Files\WinAIMagic\engine", App = @"C:\Program Files\WinAIMagic";

    [Fact]
    public void OnlyEnginesUnderOurFolderWithoutOurAppAsParent()
    {
        var list = new[]
        {
            new EngineOrphans.Entry(1, 100, @"C:\Program Files\WinAIMagic\WinAIMagic.exe"),
            new EngineOrphans.Entry(2, 1, @"C:\Program Files\WinAIMagic\engine\cuda\llama-server.exe"),      // our app is the parent: alive
            new EngineOrphans.Entry(3, 999, @"C:\Program Files\WinAIMagic\engine\python\python.exe"),         // parent gone: orphan
            new EngineOrphans.Entry(4, 3, @"C:\Program Files\WinAIMagic\engine\tiered\bin\wam-glm.exe"),      // under a python of ours: follows it
            new EngineOrphans.Entry(5, 999, @"C:\other\llama-server.exe"),                                    // someone else's
            new EngineOrphans.Entry(6, 7, @"C:\Program Files\WinAIMagic\engine\cuda\ggml-rpc-server.exe"),    // started by another program
            new EngineOrphans.Entry(7, 1000, @"C:\Windows\explorer.exe"),
        };
        Assert.Equal(new[] { 3, 6 }, EngineOrphans.Pids(list, Engine, App));
    }

    [Fact]
    public void ASimilarFolderNameIsNotOurs()
    {
        var list = new[] { new EngineOrphans.Entry(1, 9, @"C:\Program Files\WinAIMagic\engine2\llama-server.exe") };
        Assert.Empty(EngineOrphans.Pids(list, Engine, App));
    }

    [Fact]
    public void TheRealProcessListCanBeRead() => Assert.NotEmpty(EngineOrphans.Snapshot());
}

public class LlamaEngineTests : EngineTest
{
    private static readonly ModelInfo Qwen = ModelCatalog.Shared.Model("qwen3.6-35b-a3b-q4")!;
    private static readonly FakeGpuBackend Card = FakeGpuBackend.Nvidia(16);
    private static MemoryPlan Plan(int context = 65_536, ulong? cache = null) =>
        MemoryPlanner.Plan(TestModels.Index("qwen35moe", 40, 2_678_180_352, 491_782_144), SystemInfo.Simulated(Card, 128, 10), context, false);
    private static EngineStart Start(MemoryPlan? plan = null) => new(Qwen, new[] { @"C:\models\q.gguf" }, plan ?? Plan(), Card);

    private LlamaEngine Fake(params string[] args)
    {
        int port = FreePort();
        var engine = new LlamaEngine(port) { ExecutableOverride = FakeExe, HealthInterval = TimeSpan.FromMilliseconds(50) };
        engine.ArgumentsOverride = args.Concat(new[] { "--port", port.ToString() }).ToList();
        return engine;
    }

    [Fact]
    public void ArgumentsAsOnTheMac()
    {
        var e = new LlamaEngine(port: 9000);
        var args = e.Arguments(Start()).ToList();
        string Value(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Equal(@"C:\models\q.gguf", Value("--model"));
        Assert.Equal("127.0.0.1", Value("--host"));
        Assert.Equal("9000", Value("--port"));
        Assert.Equal("65536", Value("--ctx-size"));
        Assert.Equal("999", Value("--n-gpu-layers"));
        Assert.Equal("on", Value("--flash-attn"));
        Assert.Equal("1024", Value("--ubatch-size"));
        Assert.Equal("2048", Value("--batch-size"));
        Assert.Equal("off", Value("--fit"));
        Assert.Equal("1", Value("--parallel"));
        Assert.Contains("--jinja", args);
        Assert.Equal("deepseek", Value("--reasoning-format"));
        Assert.Contains("--metrics", args);
        Assert.Equal(Qwen.Id, Value("--alias"));
        Assert.Contains("--n-cpu-moe", args);
        Assert.DoesNotContain("--no-repack", args);
    }

    /// <summary>The second engine gets the 8-bit cache with a card, and keeps 16 bits on the processor alone.</summary>
    [Fact]
    public void TheCacheTypeFollowsThePlan()
    {
        var model = ModelCatalog.Shared.Model("ministral-3-8b-q4")!;
        var index = TestModels.IndexOf(model);
        var e = new LlamaEngine(port: 9000);
        List<string> For(SystemInfo s, IGpuBackend b) => e.Arguments(new EngineStart(model, new[] { "a" }, MemoryPlanner.Plan(index, s, 8192, false), b)).ToList();
        var card = For(SystemInfo.Simulated(FakeGpuBackend.Nvidia(8), 16, 8), FakeGpuBackend.Nvidia(8));
        Assert.Equal("q8_0", card[card.IndexOf("--cache-type-k") + 1]);
        Assert.Equal("q8_0", card[card.IndexOf("--cache-type-v") + 1]);
        var cpu = For(SystemInfo.Simulated(FakeGpuBackend.Cpu(), 16, 8), FakeGpuBackend.Cpu());
        Assert.DoesNotContain("--cache-type-k", cpu);
        Assert.DoesNotContain("--cache-type-v", cpu);
    }

    [Fact]
    public void AModelLargerThanMemoryStreamsWithACache()
    {
        var model = ModelCatalog.Shared.Model("deepseek-v4.1-flash-q4")!;
        var s = SystemInfo.Simulated(FakeGpuBackend.Nvidia(48), 128, 10);
        var plan = MemoryPlanner.Plan(TestModels.IndexOf(model), s, 32_768, false);
        var start = new EngineStart(model, new[] { "a" }, plan, FakeGpuBackend.Nvidia(48));
        var e = new LlamaEngine(port: 9000);
        var args = e.Arguments(start).ToList();
        Assert.Contains("--no-repack", args);
        Assert.Equal((plan.ExpertCache >> 20).ToString(), args[args.IndexOf("--moe-cache-mib") + 1]);
        Assert.Equal("4096", args[args.IndexOf("--ubatch-size") + 1]);
        var env = e.Environment(start);
        Assert.Equal("0", env["LLAMA_MMAP_PREFETCH"]);
        Assert.Equal("8", env["LLAMA_MOE_CACHE_MAX_BATCH"]);
        Assert.Equal("9", env["GGML_OP_OFFLOAD_MIN_BATCH"]);
        Assert.StartsWith(Paths.ModelDir(model.Id), env["LLAMA_MOE_CACHE_FILE"]);
        Assert.Equal("PCI_BUS_ID", env["CUDA_DEVICE_ORDER"]);
    }

    /// <summary>An address typed in Settings that makes no URI stopped the Mac app (Settings sets it per keystroke here too):
    /// it falls back to this PC, and IPv6 gets its brackets.</summary>
    [Fact]
    public void AnAddressThatMakesNoUriFallsBackToThisPc()
    {
        Assert.Equal(new Uri("http://127.0.0.1:8080"), EngineBase.Url("my pc", 8080));
        Assert.Equal(new Uri("http://127.0.0.1:8080"), EngineBase.Url("localhost:", 8080));
        Assert.Equal(new Uri("http://127.0.0.1:8080"), EngineBase.Url("", 8080));
        Assert.Equal(new Uri("http://127.0.0.1:8080"), EngineBase.Url("0.0.0.0", 8080));
        Assert.Equal("[::1]", EngineBase.Url("::1", 8080).Host);
        Assert.Equal(new Uri("http://192.168.1.20:8080"), EngineBase.Url(" 192.168.1.20 ", 8080));
        Assert.Equal(new Uri("http://localhost:8080"), EngineBase.Url("localhost", 8080));
    }

    /// <summary>The engine can save a conversation's place to the SlotKeeper's folder, except on a cluster; a state is keyed by
    /// model, context, vision and draft head.</summary>
    [Fact]
    public void TheEngineGetsTheFolderUnlessItIsACluster()
    {
        var keeper = new SlotKeeper(Path.Combine(Path.GetTempPath(), "wam-slots-" + Guid.NewGuid().ToString("N")[..8]));
        var e = new LlamaEngine(port: 9000, slots: keeper);
        var args = e.Arguments(Start()).ToList();
        Assert.Equal(keeper.Folder, args[args.IndexOf("--slot-save-path") + 1]);
        Assert.Equal(LlamaEngine.KeptCheckpoints, args[args.IndexOf("--ctx-checkpoints") + 1]);
        Assert.DoesNotContain("--slot-save-path", e.Arguments(Start() with { Rpc = "10.0.0.2:50052" }));
        Settings.Shared.KeepEngineMemory = false;
        try { Assert.DoesNotContain("--slot-save-path", e.Arguments(Start())); Assert.DoesNotContain("--ctx-checkpoints", e.Arguments(Start())); }
        finally { Settings.Shared.KeepEngineMemory = true; }
        Assert.NotEqual(SlotKeeper.StateKey(Qwen.Id, 65_536, false, false), SlotKeeper.StateKey(Qwen.Id, 65_536, true, false));
    }

    [Fact]
    public void TheClusterPutsHelpersFirst()
    {
        var idx = TestModels.Index("glm5-next", 46, 9_800_000_000, 4_416_000_000, firstExpertLayer: 3);
        var plan = MemoryPlanner.Plan(idx, SystemInfo.Simulated(Card, 128, 10), 65_536, false, helpers: new ulong[] { 69_000_000_000 });
        var args = new LlamaEngine(port: 9000).Arguments(new EngineStart(ModelCatalog.Shared.Model("glm-5.3-flash-q4")!, new[] { "a" }, plan, Card) { Rpc = "10.0.0.2:50052" }).ToList();
        Assert.Equal("10.0.0.2:50052", args[args.IndexOf("--rpc") + 1]);
        Assert.Equal(string.Join(",", plan.TensorSplit), args[args.IndexOf("--tensor-split") + 1]);
        Assert.DoesNotContain("--n-cpu-moe", args);
        if (plan.HostExpertLayers.Count > 0) Assert.Contains(args, a => a.StartsWith("blk\\.("));
    }

    [Fact]
    public void MtpFollowsTheSettingAndTheCache()
    {
        var glm = ModelCatalog.Shared.Model("glm-5.3-flash-compact")!;
        var plan = MemoryPlanner.Plan(TestModels.IndexOf(glm), SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10), 65_536, false);
        var e = new LlamaEngine(port: 9000);
        var withMtp = e.Arguments(new EngineStart(glm, new[] { "a" }, plan, Card));
        Assert.Contains("draft-mtp", withMtp);
        Settings.Shared.Mtp = false;
        Assert.DoesNotContain("draft-mtp", e.Arguments(new EngineStart(glm, new[] { "a" }, plan, Card)));
        Settings.Shared.Mtp = true;
        var fileMtp = ModelCatalog.Shared.Model("qwen3.8-flash-next-q4")!;
        var qplan = MemoryPlanner.Plan(TestModels.IndexOf(fileMtp), SystemInfo.Simulated(FakeGpuBackend.Nvidia(16), 128, 10), 65_536, false);
        var args = e.Arguments(new EngineStart(fileMtp, new[] { "a" }, qplan, Card) { Draft = "draft.gguf" }).ToList();
        Assert.Equal("draft.gguf", args[args.IndexOf("--model-draft") + 1]);
    }

    /// <summary>A key on the command line is visible to every program on the PC.</summary>
    [Fact]
    public void TheKeyGoesThroughTheEnvironment()
    {
        Settings.Shared.ApiKey = "secret-key";
        var e = new LlamaEngine(port: 9000);
        var args = e.Arguments(Start());
        Assert.DoesNotContain("--api-key", args);
        Assert.DoesNotContain(args, a => a.Contains("secret-key"));
        Assert.Equal("secret-key", e.Environment(Start())["LLAMA_API_KEY"]);
        Settings.Shared.ApiKey = "";
        Assert.Null(e.Environment(Start())["LLAMA_API_KEY"]);
    }

    /// <summary>0.0.0.0 only with a key; the app's own client always talks to this PC.</summary>
    [Fact]
    public void ListeningOnAllAddressesNeedsAKey()
    {
        Settings.Shared.Host = "0.0.0.0";
        var e = new LlamaEngine();
        Assert.Equal("127.0.0.1", e.Host);
        Settings.Shared.ApiKey = "k";
        Assert.Equal("0.0.0.0", e.Host);
        Assert.Equal(new Uri($"http://127.0.0.1:{Settings.Shared.Port}"), e.BaseUrl);
        // a second engine stays on this PC, on its own port
        var fast = new LlamaEngine(port: 8081);
        Assert.Equal("127.0.0.1", fast.Host);
        Assert.Equal(8081, fast.Port);
    }

    [Fact]
    public void AMissingBinaryFailsWithASentence()
    {
        var e = new LlamaEngine(port: FreePort());
        e.Start(Start());
        Assert.Equal(EngineStatus.Failed, e.State.Status);
        Assert.Contains("Engine niet gevonden", e.State.Message);
    }

    [Fact]
    public async Task StartsFollowsHealthAndStopsWithABreak()
    {
        var marker = Path.Combine(Root, "marker.txt");
        using var e = Fake("--marker", marker, "--delay-health", "300");
        var states = new List<EngineState>();
        e.StateChanged += s => { lock (states) states.Add(s); };
        int held = SleepGuard.Holds;
        e.Start(Start());
        Assert.Equal(EngineStatus.Starting, e.State.Status);
        Assert.Equal(held + 1, SleepGuard.Holds);
        Assert.True(WaitUntil(() => e.State.IsRunning), e.State.ToString());
        Assert.Equal(1, e.LoadProgress);
        Assert.Equal(Qwen, e.Model);
        Assert.Contains(e.LogTail, l => l.Contains("load_tensors"));
        lock (states) Assert.Contains(states, s => s.Message == "Gewichten laden…");

        var activity = await e.PollActivity();
        Assert.True(activity!.Busy);
        Assert.True(activity.Decoded > 0);

        await e.StopAndWait();
        Assert.Equal(EngineState.Stopped, e.State);
        Assert.StartsWith("break", File.ReadAllText(marker));
        Assert.Equal(held, SleepGuard.Holds);
    }

    /// <summary>The owner found loading and unloading unclear: a stop now says "Model ontladen…" until the process is gone, and
    /// the times of both are remembered, so the next gauge runs on the clock.</summary>
    [Fact]
    public async Task UnloadingIsAStateOfItsOwnAndBothTimesAreRemembered()
    {
        using var e = Fake("--ignore-break");
        e.StopWait = TimeSpan.FromMilliseconds(600);
        var states = new List<EngineState>();
        e.StateChanged += s => { lock (states) states.Add(s); };
        e.Start(Start());
        Assert.True(WaitUntil(() => e.State.IsRunning));
        Assert.Equal(1, e.LoadProgress);
        e.Stop();
        Assert.Equal(EngineStatus.Stopping, e.State.Status);
        Assert.Equal("Model ontladen…", e.State.Message);
        Assert.False(e.State.IsActive);
        Assert.InRange(e.LoadProgress, 0, 0.99);
        Assert.True(WaitUntil(() => e.State == EngineState.Stopped), e.State.ToString());
        lock (states) Assert.Equal([EngineStatus.Starting, EngineStatus.Running, EngineStatus.Stopping, EngineStatus.Stopped], states.Select(s => s.Status).Distinct());
        Assert.True(LoadClock.Expected("load:" + Qwen.Id, TimeSpan.FromHours(1)) < TimeSpan.FromMinutes(1));
        Assert.True(LoadClock.Expected("unload:" + Qwen.Id, TimeSpan.FromHours(1)) < TimeSpan.FromMinutes(1));
        await e.StopAndWait();
    }

    [Fact]
    public void TheGaugeRunsOnTheClockAndNeverFillsBeforeTheEngineAnswers()
    {
        var expected = TimeSpan.FromSeconds(30);
        Assert.Equal(0.5, LoadClock.Fraction(0, TimeSpan.FromSeconds(15), expected), 3);
        Assert.Equal(0.8, LoadClock.Fraction(0.8, TimeSpan.FromSeconds(3), expected), 3); // the engine's own report when further
        Assert.InRange(LoadClock.Fraction(0, TimeSpan.FromMinutes(5), expected), 0.95, 0.99); // late: creeps, never full
        Assert.True(LoadClock.Fraction(0, TimeSpan.FromSeconds(40), expected) > LoadClock.Fraction(0, TimeSpan.FromSeconds(30), expected));
        Assert.Equal(15, LoadClock.Remaining(TimeSpan.FromSeconds(15), expected));
        Assert.Null(LoadClock.Remaining(TimeSpan.FromSeconds(31), expected));
        // the first guess from the size: Qwen3.6-35B (20.7 GB) took 31 s on the owner's PC
        Assert.InRange(LoadClock.FirstLoad(20_700_000_000).TotalSeconds, 25, 40);
        // remembered, the newest weighing most
        LoadClock.Record("load:test", TimeSpan.FromSeconds(10));
        Assert.Equal(10, LoadClock.Expected("load:test", expected).TotalSeconds, 3);
        LoadClock.Record("load:test", TimeSpan.FromSeconds(20));
        Assert.Equal(16, LoadClock.Expected("load:test", expected).TotalSeconds, 3);
        Assert.Equal(expected, LoadClock.Expected("load:other", expected));
    }

    [Fact]
    public async Task AStopThatIsIgnoredEndsTheHardWay()
    {
        using var e = Fake("--ignore-break");
        e.StopWait = TimeSpan.FromMilliseconds(300);
        e.Start(Start());
        Assert.True(WaitUntil(() => e.State.IsRunning));
        var sw = Stopwatch.StartNew();
        await e.StopAndWait();
        Assert.Equal(EngineState.Stopped, e.State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ACrashSaysWhatTheEngineLastSaid()
    {
        using var e = Fake("--exit", "3");
        e.Start(Start());
        Assert.True(WaitUntil(() => e.State.Status == EngineStatus.Failed), e.State.ToString());
        Assert.Contains("code 3", e.State.Message);
        Assert.Contains("loading the model failed", e.State.Message);
    }

    /// <summary>engine.log and the tail: never the upstream name; the tail not the metadata dump.</summary>
    [Fact]
    public async Task TheLogIsFiltered()
    {
        using var e = Fake("--print-name");
        e.Start(Start());
        Assert.True(WaitUntil(() => e.State.IsRunning));
        Assert.True(WaitUntil(() => e.LogTail.Any(l => l.Contains("[engine] loading"))));
        Assert.DoesNotContain(e.LogTail, l => l.Contains("maya", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(e.LogTail, l => l.Contains("llama_model_loader:"));
        await e.StopAndWait();
        var log = File.ReadAllText(Paths.EngineLog);
        Assert.Contains("[engine] loading", log);
        // the dump stays in the file, cleaned
        Assert.Contains("llama_model_loader:", log);
        Assert.DoesNotContain("maya", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlotsAreRead()
    {
        Assert.Null(LlamaEngine.ParseSlots(null));
        Assert.Null(LlamaEngine.ParseSlots("not json"));
        Assert.False(LlamaEngine.ParseSlots("[{\"id\":0,\"is_processing\":false}]")!.Busy);
        var busy = LlamaEngine.ParseSlots("[{\"id\":0,\"is_processing\":true,\"next_token\":[{\"n_decoded\":42}]}]")!;
        Assert.True(busy.Busy);
        Assert.Equal(42, busy.Decoded);
    }
}

public class TieredEngineTests : EngineTest
{
    private static readonly ModelInfo Glm = ModelCatalog.Shared.Model("glm-5.3-flash-compact-24")!;
    private static readonly FakeGpuBackend Card = FakeGpuBackend.Nvidia(16);

    /// <summary>GLM reads images through the encoder, in the server's "vision" entry, as the setup writes it; no files, no entry.</summary>
    [Fact]
    public void GlmsConfigNamesTheEncoderWhenItHasTheFiles()
    {
        Assert.Null(new TieredEngine(1).Config(Start(), "log")["vision"]);
        var vision = new TieredEngine(1).Config(Start() with { Vision = @"C:\v\encoder.gguf", VisionVocab = @"C:\v\vocab.gguf" }, "log")["vision"]!;
        Assert.Equal(TieredEngine.EncoderExe, vision["exe"]!.GetValue<string>());
        Assert.Equal("wam-vision.exe", Path.GetFileName(TieredEngine.EncoderExe));
        Assert.Equal(@"C:\v\encoder.gguf", vision["mmproj"]!.GetValue<string>());
        Assert.Equal(@"C:\v\vocab.gguf", vision["model"]!.GetValue<string>());
        Assert.True(vision["gpu"]!.GetValue<bool>());
    }

    /// <summary>DeepSeek and MiniMax run their tower in the engine: a flag, no encoder.</summary>
    [Fact]
    public void DeepSeekAndMiniMaxTurnTheirTowerOnWithTheFiles()
    {
        var ds = ModelCatalog.Shared.Model("deepseek-v4.1-flash-compact");
        var mm = ModelCatalog.Shared.Model("minimax-m3.1-flash-compact");
        Assert.DoesNotContain("--vision", TieredEngine.EngineArguments(Start(ds) with { Vision = "x" }, 0));
        Assert.Null(new TieredEngine(1).Config(Start(ds) with { Vision = "x" }, "log")["vision"]);
        Assert.DoesNotContain("--vision", TieredEngine.EngineArguments(Start(mm), 0));
        var on = TieredEngine.EngineArguments(Start(mm) with { Vision = "x" }, 0);
        Assert.Equal("on", on[on.IndexOf("--vision") + 1]);
    }

    /// <summary>What the engine says about itself: both engines' /props have it, and a tower that did not load says no.</summary>
    [Fact]
    public void ModalitiesAreRead()
    {
        Assert.True(EngineBase.ParseVisionModality("{\"modalities\":{\"vision\":true,\"audio\":false}}"));
        Assert.False(EngineBase.ParseVisionModality("{\"modalities\":{\"vision\":false}}"));
        Assert.Null(EngineBase.ParseVisionModality("{\"modalities\":{}}"));
        Assert.Null(EngineBase.ParseVisionModality("{}"));
        Assert.Null(EngineBase.ParseVisionModality("[]"));
        Assert.Null(EngineBase.ParseVisionModality("not json"));
        Assert.Null(EngineBase.ParseVisionModality(null));
    }

    private static EngineStart Start(ModelInfo? model = null, int context = 65_536)
    {
        var m = model ?? Glm;
        var s = SystemInfo.Simulated(Card, 128, 10);
        return new EngineStart(m, Array.Empty<string>(), MemoryPlanner.Plan(TestModels.IndexOf(m), s, context, false, EngineKind.Tiered), Card);
    }

    [Fact]
    public void GlmArgumentsAsMeasured()
    {
        var args = TieredEngine.EngineArguments(Start(), 1500UL << 20);
        var pack = Path.Combine(Paths.ModelDir(Glm.Id), "pack");
        Assert.Equal(new[]
        {
            "--glm-pack", pack, "--max-context", "65536", "--expert-cache", "auto", "--prefill", "auto", "--kv-streaming", "on",
            "--expert-profile-save", Path.Combine(Paths.ModelDir(Glm.Id), "expert-profile.bin"), "--vram-reserve-mib", "1500",
        }, args);
    }

    /// <summary>The engine fails on a profile that is not there.</summary>
    [Fact]
    public void TheExpertProfileIsOnlyPassedWhenItExists()
    {
        var profile = Path.Combine(Paths.ModelDir(Glm.Id), "expert-profile.bin");
        Assert.DoesNotContain("--expert-profile", TieredEngine.EngineArguments(Start(), 1UL << 30));
        Directory.CreateDirectory(Paths.ModelDir(Glm.Id));
        File.WriteAllText(profile, "x");
        var args = TieredEngine.EngineArguments(Start(), 1UL << 30);
        Assert.Equal(profile, args[args.IndexOf("--expert-profile") + 1]);
        Assert.Equal(profile, args[args.IndexOf("--expert-profile-save") + 1]);
    }

    [Fact]
    public void DeepSeekAndMiniMaxArguments()
    {
        var ds = TieredEngine.EngineArguments(Start(ModelCatalog.Shared.Model("deepseek-v4.1-flash-compact")), 1500UL << 20);
        Assert.Equal("--pack", ds[0]);
        Assert.Equal("1", ds[ds.IndexOf("--tp") + 1]);
        Assert.Equal("on", ds[ds.IndexOf("--threads") + 1]);
        Assert.Equal("none", ds[ds.IndexOf("--dspark") + 1]);
        Assert.Equal("off", ds[ds.IndexOf("--vision") + 1]);
        Assert.Equal("1500", ds[ds.IndexOf("--reserve-mib") + 1]);

        var mm = TieredEngine.EngineArguments(Start(ModelCatalog.Shared.Model("minimax-m3.1-flash-compact")), 1536UL << 20);
        Assert.Equal("--pack", mm[0]);
        Assert.Equal("on", mm[mm.IndexOf("--kv-streaming") + 1]);
        Assert.Equal("1.5", mm[mm.IndexOf("--reserve-gb") + 1]);
    }

    [Fact]
    public void TheConfigHoldsWhatTheServerReads()
    {
        Settings.Shared.ApiKey = "the-key";
        Settings.Shared.ReasoningEffort = "high";
        var e = new TieredEngine(port: 9001);
        var cfg = e.Config(Start(), @"C:\log.raw");
        Assert.Equal(Path.Combine(Paths.EngineRoot, "tiered", "bin", "wam-glm.exe"), cfg["exe"]!.GetValue<string>());
        Assert.Equal(Path.Combine(Paths.EngineRoot, "tiered"), cfg["cwd"]!.GetValue<string>());
        Assert.Equal(Path.Combine(Paths.ModelDir(Glm.Id), "pack", "tokenizer"), cfg["tokenizer"]!.GetValue<string>());
        Assert.Equal(Glm.Id, cfg["model_name"]!.GetValue<string>());
        Assert.Equal(0, cfg["gpu"]![0]!.GetValue<int>());
        Assert.Equal(0.95, cfg["sampling"]!["top_p"]!.GetValue<double>());
        Assert.Equal("high", cfg["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(Path.Combine(Paths.EngineRoot, "tiered", "bin"), cfg["lib_dirs"]![0]!.GetValue<string>());
        Assert.Equal(9001, cfg["port"]!.GetValue<int>());
        Assert.Equal("127.0.0.1", cfg["host"]!.GetValue<string>());
        Assert.Equal(@"C:\log.raw", cfg["log"]!.GetValue<string>());
        Assert.Equal("PCI_BUS_ID", cfg["env"]!["CUDA_DEVICE_ORDER"]!.GetValue<string>());
        Assert.Equal(0, cfg["thinking_budget"]!.GetValue<int>());
        Assert.Equal("the-key", cfg["api_key"]!.GetValue<string>());
        Assert.DoesNotContain(cfg["args"]!.AsArray().Select(a => a!.GetValue<string>()), a => a.Contains("the-key"));
    }

    [Fact]
    public void SkippingExpertsIsAChoiceAndOnlyForGlm()
    {
        var engine = new TieredEngine(1);
        var off = engine.Config(Start(), "log")["env"]!.AsObject();
        Assert.Equal("off", engine.SkipExpertsInUse);
        Assert.False(off.ContainsKey("STRATA_GLM_TRUNK_KEEP_MAP"));
        Assert.False(off.ContainsKey("STRATA_GLM_MISS_SKIP_W"));
        Settings.Shared.SkipExperts = "light";
        Assert.Equal("off", engine.SkipExpertsInUse);   // the running engine keeps what it started with
        var light = engine.Config(Start(), "log")["env"]!.AsObject();
        Assert.Equal("light", engine.SkipExpertsInUse);
        Assert.Equal(46, light["STRATA_GLM_TRUNK_KEEP_MAP"]!.GetValue<string>().Length);
        Assert.False(light.ContainsKey("STRATA_GLM_MISS_SKIP_W"));
        Settings.Shared.SkipExperts = "strong";
        var strong = new TieredEngine(1).Config(Start(), "log")["env"]!.AsObject();
        Assert.Equal("0.14", strong["STRATA_GLM_MISS_SKIP_W"]!.GetValue<string>());
        Assert.Equal("23", strong["STRATA_GLM_MISS_SKIP_FROM"]!.GetValue<string>());
        Assert.Empty(TieredEngine.SkipExpertsEnvironment("deepseek", "strong"));
        Assert.Empty(TieredEngine.SkipExpertsEnvironment("minimax", "light"));
    }

    private TieredEngine Fake(string marker, params string[] extra)
    {
        int port = FreePort();
        var e = new TieredEngine(port) { ExecutableOverride = FakeExe, HealthInterval = TimeSpan.FromMilliseconds(50) };
        e.ArgumentsOverride = new[] { "--port", port.ToString(), "--parent-pipe", "--marker", marker }.Concat(extra).ToList();
        return e;
    }

    /// <summary>Stop by the parent pipe: that is the path on which the server gives the engine QUIT and the profile is saved.</summary>
    [Fact]
    public async Task StopsThroughTheParentPipeAndLeavesNoConfigBehind()
    {
        Settings.Shared.ApiKey = "the-key";
        var marker = Path.Combine(Root, "marker.txt");
        using var e = Fake(marker);
        e.Start(Start());
        Assert.True(WaitUntil(() => e.State.IsRunning), e.State.ToString());
        var config = Path.Combine(Paths.ModelDir(Glm.Id), "engine-config.json");
        Assert.True(File.Exists(config));
        Assert.Contains("the-key", File.ReadAllText(config));

        var a = await e.PollActivity();
        Assert.True(a!.Busy);
        Assert.Equal(12.5, a.TokS);
        Assert.Equal(0.8, a.VramHit);
        Assert.Equal(61, a.GpuTemperature);

        await e.StopAndWait();
        Assert.Equal(EngineState.Stopped, e.State);
        Assert.StartsWith("pipe", File.ReadAllText(marker));
        Assert.False(File.Exists(config));
    }

    /// <summary>What the engine exe writes to the file in the config reaches the log only through the filter.</summary>
    [Fact]
    public async Task TheRawLogGoesThroughTheFilter()
    {
        var marker = Path.Combine(Root, "marker.txt");
        using var e = Fake(marker);
        e.Start(Start());
        Assert.True(WaitUntil(() => e.State.IsRunning));
        var raw = Paths.EngineLog + ".raw";
        File.WriteAllText(raw, "maya serve: expert profile saved\nglm: VRAM LOW, 200 MiB free\n");
        Assert.True(WaitUntil(() => e.LogTail.Any(l => l.Contains("engine serve: expert profile saved"))), string.Join("\n", e.LogTail));
        Assert.Contains(e.Warnings, w => w.Contains("VRAM bijna vol"));
        await e.StopAndWait();
        Assert.False(File.Exists(raw));
        var log = File.ReadAllText(Paths.EngineLog);
        Assert.Contains("engine serve: expert profile saved", log);
        Assert.DoesNotContain("maya", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutPackOrBinaryItFailsWithASentence()
    {
        var e = new TieredEngine(port: FreePort());
        e.Start(Start());
        Assert.Equal(EngineStatus.Failed, e.State.Status);
        Assert.Contains("Engine niet gevonden", e.State.Message);
    }

    [Fact]
    public void MetricsAreRead()
    {
        Assert.Null(TieredEngine.ParseMetrics(null));
        Assert.Null(TieredEngine.ParseMetrics("{\"not\":\"it\"}"));
        var idle = TieredEngine.ParseMetrics("{\"live\":{\"state\":\"idle\",\"tok_s\":null}}")!;
        Assert.False(idle.Busy);
        Assert.Equal(0, idle.TokS);
        var writing = TieredEngine.ParseMetrics("{\"live\":{\"state\":\"generating\",\"tok_s\":17.5},\"tiers\":{\"now\":{\"vram_hit\":0.9}},\"hardware\":{\"gpu\":{\"temp\":70}}}")!;
        Assert.True(writing.Busy);
        Assert.Equal(17.5, writing.TokS);
        Assert.Equal(0.9, writing.VramHit);
        Assert.Equal(70, writing.GpuTemperature);
        var reading = TieredEngine.ParseMetrics("{\"live\":{\"state\":\"reading\",\"tok_s\":null}}")!;
        Assert.True(reading.Busy);
        Assert.Null(reading.TokS);
    }
}
