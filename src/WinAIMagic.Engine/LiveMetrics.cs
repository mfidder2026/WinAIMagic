using System.Runtime.InteropServices;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Engine;

/// <summary>Samples the PC for the live charts: GPU and CPU load, VRAM, RAM, disk reads, and the model's writing speed. The speed
/// is the engine's own, so the meter also moves when another program uses the API. Samples arrive on a timer thread.</summary>
public sealed class LiveMetrics : IDisposable
{
    public sealed record Sample
    {
        public DateTime T { get; init; }
        public double Gpu { get; init; }            // %
        public double Cpu { get; init; }            // %
        public double VramUsedGB { get; init; }
        public double VramTotalGB { get; init; }
        public double RamUsedGB { get; init; }
        public double RamTotalGB { get; init; }
        /// <summary>MB/s read from the drive with the model folder, and from all drives.</summary>
        public double SsdMBs { get; init; }
        public double DiskTotalMBs { get; init; }
        public double TokensPerSec { get; init; }
        /// <summary>From the main engine, when it says: experts found in VRAM (0..1) and the card's temperature.</summary>
        public double? VramHit { get; init; }
        public double? GpuTemperature { get; init; }
    }

    public const double WindowSeconds = 90;
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(0.5), IdleInterval = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly List<Sample> samples = new();
    private readonly PdhCounters counters = new();
    private readonly Timer timer;
    private readonly Func<IEngine?> engine;
    private readonly long adapterLuid;
    private readonly double vramTotalGb;

    private int tokenCount;
    private double lastTokenRate;
    private DateTime lastStream = DateTime.MinValue;
    private int? lastDecoded;
    private double? engineRate;
    private int? lastDecodedSecond;
    private double? secondRate;
    private bool secondActive;

    /// <summary>The second engine (documents, a description for an image); its writing counts on the gauge too.</summary>
    public Func<IEngine?>? Second { get; set; }

    /// <summary>The second engine runs: sample twice a second and ask it what it writes.</summary>
    public bool SecondActive
    {
        get => secondActive;
        set
        {
            if (secondActive == value) return;
            secondActive = value;
            if (!value) { secondRate = null; lastDecodedSecond = null; }
            Retime();
        }
    }
    private EngineActivity? lastActivity;
    private bool polling, sampling;
    private (ulong Busy, ulong Total)? lastCpu;
    private DateTime lastTick = DateTime.UtcNow;
    private bool fast;

    public LiveMetrics(Func<IEngine?> engine, long adapterLuid = 0, ulong vramTotal = 0)
    {
        this.engine = engine;
        this.adapterLuid = adapterLuid;
        vramTotalGb = vramTotal / 1e9;
        timer = new Timer(_ => TakeSample(), null, IdleInterval, IdleInterval);
    }

    public event Action? Sampled;
    public IReadOnlyList<Sample> Samples { get { lock (gate) return samples.ToList(); } }
    public Sample? Current { get { lock (gate) return samples.Count == 0 ? null : samples[^1]; } }
    /// <summary>The engine is answering a request (from this app or from another client, such as the API).</summary>
    public bool EngineBusy { get; private set; }

    /// <summary>Twice a second while the engine runs, every 2 s when it does not (the charts redraw on every sample).</summary>
    public bool Fast
    {
        get => fast;
        set
        {
            if (fast == value) return;
            fast = value;
            Retime();
        }
    }

    private double? imageRate;

    /// <summary>Image tokens per second while an image is made (the image engine has no /slots); null otherwise.</summary>
    public double? ImageRate
    {
        get => imageRate;
        set
        {
            bool changed = (imageRate == null) != (value == null);
            imageRate = value;
            if (changed) Retime();
        }
    }

    public bool MakingImage => imageRate != null;

    private void Retime()
    {
        var interval = fast || secondActive || imageRate != null ? ActiveInterval : IdleInterval;
        timer.Change(interval, interval);
    }

    /// <summary>The top of a speed scale: `base` for text, a round number above the speeds of the last minutes otherwise.</summary>
    public double Scale(double @base)
    {
        double top;
        lock (gate) top = samples.Count == 0 ? 0 : samples.Max(s => s.TokensPerSec);
        if (top <= @base * 0.95) return @base;
        foreach (var x in new[] { 100.0, 250, 500, 1000, 2500, 5000 })
            if (top <= x * 0.95) return x;
        return 10000;
    }

    /// <summary>Tokens this app streamed; used when the engine gives no count of its own.</summary>
    public void AddTokens(int n)
    {
        Interlocked.Add(ref tokenCount, n);
        lastStream = DateTime.UtcNow;
    }

    /// <summary>One sample now (the timer calls this; tests too).</summary>
    public void TakeSample()
    {
        lock (gate)
        {
            if (sampling) return;
            sampling = true;
        }
        try
        {
            var now = DateTime.UtcNow;
            double dt = Math.Max((now - lastTick).TotalSeconds, 0.1);
            lastTick = now;

            var read = counters.Read(Paths.ModelsRoot, adapterLuid);
            var cpuNow = CpuTicks();
            double cpu = 0;
            if (lastCpu is { } last && cpuNow.Total > last.Total)
                cpu = (double)(cpuNow.Busy - last.Busy) / (cpuNow.Total - last.Total) * 100;
            lastCpu = cpuNow;

            if (fast) PollEngine(dt);
            if (secondActive) PollSecond(dt);
            else secondRate = null;
            int streamed = Interlocked.Exchange(ref tokenCount, 0);
            // the engine's own count when it answers (any client: this app, the API, tools), else the tokens this app streamed
            double? engines = engineRate is null && secondRate is null ? null : (engineRate ?? 0) + (secondRate ?? 0);
            double inst = imageRate ?? engines ?? streamed / dt;
            bool idle = imageRate is double ir ? ir == 0 : engines is double r ? r == 0 : (now - lastStream).TotalSeconds > 1.5;
            // smooth the token rate a little; drop to zero shortly after the stream stops
            lastTokenRate = idle ? 0 : lastTokenRate * 0.6 + inst * 0.4;

            var (total, available) = SystemInfo.RamNow();
            var s = new Sample
            {
                T = now,
                Gpu = read.GpuPercent,
                Cpu = cpu,
                VramUsedGB = read.VramUsedBytes / 1e9,
                VramTotalGB = vramTotalGb,
                RamUsedGB = (total - available) / 1e9,
                RamTotalGB = total / 1e9,
                SsdMBs = read.ModelDiskBytesPerSec / 1e6,
                DiskTotalMBs = read.TotalDiskBytesPerSec / 1e6,
                TokensPerSec = lastTokenRate,
                VramHit = lastActivity?.VramHit,
                GpuTemperature = lastActivity?.GpuTemperature,
            };
            lock (gate)
            {
                samples.Add(s);
                var cutoff = now.AddSeconds(-WindowSeconds);
                int first = samples.FindIndex(x => x.T >= cutoff);
                if (first > 0) samples.RemoveRange(0, first);
            }
            Sampled?.Invoke();
        }
        finally
        {
            lock (gate) sampling = false;
        }
    }

    /// <summary>The next rate from what the engine says: its own figure, or the difference in tokens written since the last look;
    /// a new answer starts again from 0. Null: the engine does not answer.</summary>
    internal static double? NextRate(EngineActivity? a, ref int? lastDecoded, double dt, double? previous)
    {
        if (a == null) { lastDecoded = null; return null; }
        if (!a.Busy) { lastDecoded = null; return 0; }
        if (a.TokS is double t) { lastDecoded = null; return t; }
        double? rate = previous;
        if (a.Decoded is int decoded)
        {
            if (lastDecoded is int last) rate = (decoded >= last ? decoded - last : decoded) / dt;
            lastDecoded = decoded;
        }
        return rate;
    }

    private bool pollingSecond;

    private void PollSecond(double dt)
    {
        var e = Second?.Invoke();
        if (e == null || pollingSecond) return;
        pollingSecond = true;
        Task.Run(async () =>
        {
            try { secondRate = NextRate(await e.PollActivity(), ref lastDecodedSecond, dt, secondRate); }
            finally { pollingSecond = false; }
        });
    }

    private void PollEngine(double dt)
    {
        var e = engine();
        if (e == null || polling) return;
        polling = true;
        Task.Run(async () =>
        {
            try
            {
                var a = await e.PollActivity();
                lastActivity = a;
                EngineBusy = a?.Busy ?? false;
                engineRate = NextRate(a, ref lastDecoded, dt, engineRate);
            }
            finally { polling = false; }
        });
    }

    private static (ulong Busy, ulong Total) CpuTicks()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return (0, 0);
        // kernel time includes idle time
        return (kernel + user - idle, kernel + user);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    public void Dispose()
    {
        timer.Dispose();
        counters.Dispose();
    }
}

/// <summary>Windows' performance counters by their English names: the names are translated on a non-English Windows, and
/// PdhAddEnglishCounter takes the same path on every one. These work for every make of card.</summary>
internal sealed class PdhCounters : IDisposable
{
    public readonly record struct Reading(double GpuPercent, ulong VramUsedBytes, double ModelDiskBytesPerSec, double TotalDiskBytesPerSec);

    private IntPtr query;
    private IntPtr gpuEngine, gpuMemory, disk;
    private readonly object gate = new();

    public PdhCounters()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
        PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuEngine);
        PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out gpuMemory);
        PdhAddEnglishCounterW(query, @"\PhysicalDisk(*)\Disk Read Bytes/sec", IntPtr.Zero, out disk);
        PdhCollectQueryData(query);
    }

    /// <summary>The next values. Rates need a former collect; the first call after opening gives zero for them.</summary>
    public Reading Read(string modelFolder, long adapterLuid)
    {
        lock (gate)
        {
            if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return default;
            double gpu = GpuPercent(Values(gpuEngine));
            ulong vram = VramUsed(Values(gpuMemory), adapterLuid);
            var disks = Values(disk);
            var (modelDisk, total) = DiskRates(disks, Path.GetPathRoot(Path.GetFullPath(modelFolder)) ?? "");
            return new Reading(gpu, vram, modelDisk, total);
        }
    }

    /// <summary>A single VRAM reading (the plan, just before an engine starts).</summary>
    public ulong? GpuMemoryOnce(long adapterLuid)
    {
        lock (gate)
        {
            if (query == IntPtr.Zero || gpuMemory == IntPtr.Zero || PdhCollectQueryData(query) != 0) return null;
            var values = Values(gpuMemory);
            return values.Count == 0 ? null : VramUsed(values, adapterLuid);
        }
    }

    /// <summary>Task Manager's figure: per engine of a card the sum over the processes, and the busiest engine counts.
    /// Instances: pid_1234_luid_0x0_0x0_phys_0_eng_3_engtype_3D.</summary>
    internal static double GpuPercent(IReadOnlyList<(string Name, double Value)> values)
    {
        var perEngine = new Dictionary<string, double>();
        foreach (var (name, value) in values)
        {
            int luid = name.IndexOf("luid_", StringComparison.Ordinal);
            int type = name.IndexOf("_engtype_", StringComparison.Ordinal);
            if (luid < 0 || type < luid) continue;
            var key = name[luid..type];
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }
        return perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());
    }

    /// <summary>Instances: luid_0x00000000_0x0000C1D2_phys_0. The chosen card's when known, else the fullest.</summary>
    internal static ulong VramUsed(IReadOnlyList<(string Name, double Value)> values, long adapterLuid)
    {
        if (adapterLuid != 0)
        {
            string wanted = $"luid_0x{(uint)(adapterLuid >> 32):X8}_0x{(uint)adapterLuid:X8}";
            foreach (var (name, value) in values)
                if (name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return (ulong)value;
        }
        return values.Count == 0 ? 0 : (ulong)values.Max(v => v.Value);
    }

    /// <summary>Instances: "_Total", "0 C:", "1 D: E:".</summary>
    internal static (double ModelDisk, double Total) DiskRates(IReadOnlyList<(string Name, double Value)> values, string driveRoot)
    {
        double model = 0, total = 0;
        string letter = driveRoot.Length >= 2 ? driveRoot[..2] : "";
        foreach (var (name, value) in values)
        {
            if (name == "_Total") total = value;
            else if (letter.Length > 0 && name.Split(' ').Contains(letter, StringComparer.OrdinalIgnoreCase)) model = value;
        }
        return (model, total);
    }

    private static unsafe List<(string Name, double Value)> Values(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0, count = 0;
        int status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, buffer) != 0) return list;
            var items = (Item*)buffer;
            for (uint i = 0; i < count; i++)
                if (items[i].Status == 0) list.Add((Marshal.PtrToStringUni(items[i].Name) ?? "", items[i].Value));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item { public IntPtr Name; public uint Status; public double Value; }

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
