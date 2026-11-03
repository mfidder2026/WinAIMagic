using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

public enum EngineStatus { Stopped, Starting, Running, Failed }

/// <summary>Stopped, Starting(text), Running or Failed(text), as EngineController.State on the Mac.</summary>
public readonly record struct EngineState(EngineStatus Status, string Message = "")
{
    public static readonly EngineState Stopped = new(EngineStatus.Stopped);
    public static readonly EngineState Running = new(EngineStatus.Running);
    public static EngineState Starting(string text) => new(EngineStatus.Starting, text);
    public static EngineState Failed(string text) => new(EngineStatus.Failed, text);
    public bool IsRunning => Status == EngineStatus.Running;
    public bool IsActive => Status is EngineStatus.Running or EngineStatus.Starting;
}

/// <summary>What the engine can do for the app.</summary>
public sealed record EngineCapabilities(bool Vision, bool Cluster, bool ExpertTiers);

/// <summary>A moment of work as the engine itself reports it, whoever asked for the answer.</summary>
public sealed record EngineActivity
{
    public bool Busy { get; init; }
    /// <summary>The engine's own rate, when it gives one.</summary>
    public double? TokS { get; init; }
    /// <summary>Tokens written in the current answer, when it gives that instead.</summary>
    public int? Decoded { get; init; }
    /// <summary>The real division over the tiers, from the main engine: the share of experts found in VRAM (0..1).</summary>
    public double? VramHit { get; init; }
    public double? GpuTemperature { get; init; }
}

public sealed record EngineStart(ModelInfo Model, IReadOnlyList<string> Shards, MemoryPlan Plan, IGpuBackend Backend)
{
    public string? Vision { get; init; }
    public string? Draft { get; init; }
    public string? Rpc { get; init; }
}

/// <summary>One of the two engines. State and log change on background threads; the UI marshals the events itself.</summary>
public interface IEngine : IDisposable
{
    EngineKind Kind { get; }
    EngineState State { get; }
    event Action<EngineState>? StateChanged;
    /// <summary>The state, the log or the progress changed.</summary>
    event Action? Changed;
    double LoadProgress { get; }
    IReadOnlyList<string> LogTail { get; }
    IReadOnlyList<string> Warnings { get; }
    /// <summary>The model the engine runs (or last ran).</summary>
    ModelInfo? Model { get; }
    Uri BaseUrl { get; }
    EngineCapabilities Capabilities { get; }
    void Start(EngineStart start);
    void Stop();
    Task StopAndWait();
    /// <summary>Tokens per second and whether it is busy; null when the engine does not answer.</summary>
    Task<EngineActivity?> PollActivity();
}

/// <summary>Follows one engine process: its log and health, its way of stopping, and the rule that it ends with the app.</summary>
public abstract class EngineBase : IEngine
{
    /// <summary>A stop that does not end the process within this time ends it the hard way (the Mac's 10 s).</summary>
    protected internal TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>StopAndWait's longest wait for the memory to come back (the Mac's 60 x 0.5 s).</summary>
    protected internal TimeSpan StopAndWaitLimit { get; set; } = TimeSpan.FromSeconds(30);
    protected internal TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(1);

    private readonly int? fixedPort;
    private readonly string logFile;
    private readonly object gate = new();
    private readonly List<string> tail = new();
    private readonly List<string> warnings = new();
    private readonly LineSplitter lines = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private ChildProcess? process;
    private IDisposable? sleep;
    private StreamWriter? log;
    private CancellationTokenSource? healthCts;
    private volatile bool stopRequested;
    private EngineState state = EngineState.Stopped;
    private double loadProgress;

    /// <summary>A second engine next to the app's own: its own port, only reachable from this PC, and its own log.</summary>
    protected EngineBase(int? port, string? logFile)
    {
        fixedPort = port;
        this.logFile = logFile ?? Paths.EngineLog;
    }

    public abstract EngineKind Kind { get; }
    public abstract EngineCapabilities Capabilities { get; }
    public event Action<EngineState>? StateChanged;
    public event Action? Changed;
    public ModelInfo? Model { get; private set; }
    public double LoadProgress => loadProgress;
    public IReadOnlyList<string> LogTail { get { lock (gate) return tail.ToList(); } }
    public IReadOnlyList<string> Warnings { get { lock (gate) return warnings.ToList(); } }
    protected string LogPath => logFile;

    public EngineState State
    {
        get => state;
        protected set
        {
            if (state == value) return;
            state = value;
            StateChanged?.Invoke(value);
            Changed?.Invoke();
        }
    }

    /// <summary>Only the app's own engine can be open to the network: 0.0.0.0 only with a key (brief 10, API).</summary>
    public string Host
    {
        get
        {
            if (fixedPort != null) return "127.0.0.1";
            var host = Settings.Shared.Host;
            return host == "0.0.0.0" && string.IsNullOrEmpty(Settings.Shared.ApiKey) ? "127.0.0.1" : host;
        }
    }

    public int Port => fixedPort ?? Settings.Shared.Port;
    /// <summary>The app's own client always uses this PC's address, also when the engine listens on all of them.</summary>
    public Uri BaseUrl => new($"http://{(Host == "0.0.0.0" ? "127.0.0.1" : Host)}:{Port}");

    /// <summary>The command for this model: exe, arguments, environment.</summary>
    protected abstract ChildStart Command(EngineStart start);
    /// <summary>Why this start cannot go ahead, or null.</summary>
    protected abstract string? Missing(EngineStart start);
    /// <summary>The first line of the log tail.</summary>
    protected virtual string Headline(ChildStart c) => "$ " + Path.GetFileName(c.Exe);
    /// <summary>A log line, for the state and progress it says.</summary>
    protected abstract void ConsumeLine(string line);
    /// <summary>Asks the process to stop. Llama: CTRL_BREAK_EVENT.</summary>
    protected virtual void AskToStop(ChildProcess child) => child.RequestBreak();
    /// <summary>A last look after the process ended: files of the run.</summary>
    protected virtual void CleanUp() { }

    /// <summary>Tests start a fake process instead.</summary>
    internal string? ExecutableOverride { get; set; }
    internal IReadOnlyList<string>? ArgumentsOverride { get; set; }

    public void Start(EngineStart s)
    {
        lock (gate) { if (process != null) return; }
        if (Missing(s) is { } why)
        {
            State = EngineState.Failed(why);
            return;
        }
        var cmd = Command(s);
        cmd = cmd with
        {
            Exe = ExecutableOverride ?? cmd.Exe,
            Args = ArgumentsOverride ?? cmd.Args,
            Cwd = ExecutableOverride != null ? null : cmd.Cwd,
            OnOutput = Consume,
            OnExit = Terminated,
        };
        Model = s.Model;
        stopRequested = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log = null; }
        lock (gate)
        {
            tail.Clear();
            warnings.Clear();
            tail.Add(Headline(cmd));
            loadProgress = 0;
        }
        // before the start: a process that fails at once must not have its failure overwritten
        sleep = SleepGuard.Hold();
        State = EngineState.Starting(L("Model laden…"));
        PollHealth();
        try
        {
            lock (gate) process = ChildProcess.Start(cmd);
        }
        catch (Win32Exception e)
        {
            healthCts?.Cancel();
            sleep.Dispose();
            sleep = null;
            log?.Dispose();
            log = null;
            CleanUp();
            // ERROR_VIRUS_INFECTED, ERROR_VIRUS_DELETED: Defender holds it back, and the app says so
            State = EngineState.Failed(e.NativeErrorCode is 225 or 226
                ? L($"Windows Defender houdt de engine tegen: {e.Message}") : L($"Kon de engine niet starten: {e.Message}"));
            return;
        }
    }

    /// <summary>Stop and wait until the process and all it started are gone (switching models needs the memory back first).</summary>
    public async Task StopAndWait()
    {
        ChildProcess? p;
        lock (gate) p = process;
        if (p == null)
        {
            State = EngineState.Stopped;
            return;
        }
        Stop();
        var until = DateTime.UtcNow + StopAndWaitLimit;
        while (DateTime.UtcNow < until)
        {
            lock (gate) if (process == null) return;
            await Task.Delay(100);
        }
        p.KillTree();
    }

    public void Stop()
    {
        healthCts?.Cancel();
        ChildProcess? p;
        lock (gate) p = process;
        if (p == null) return;
        stopRequested = true;
        AskToStop(p);
        var wait = StopWait;
        Task.Run(async () =>
        {
            if (await Task.WhenAny(p.Exited, Task.Delay(wait)) != p.Exited) p.Terminate();
        });
    }

    private void Consume(byte[] data, int count)
    {
        // llama.cpp prints a dot per loaded chunk
        bool dots = true;
        int dotCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (data[i] == (byte)'.') dotCount++;
            else if (data[i] != (byte)'\n') { dots = false; break; }
        }
        if (dots && dotCount > 0) loadProgress = Math.Min(0.9, loadProgress + 0.002 * dotCount);
        foreach (var raw in lines.Add(data, count)) AddLine(raw);
        Changed?.Invoke();
    }

    /// <summary>A line of the engine's: filtered into the log file and the tail; the metadata dump is noise, and carries the file's original names.</summary>
    protected void AddLine(string raw)
    {
        var line = LogFilter.Clean(raw);
        try { log?.WriteLine(line); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        if (line.Length == 0 || line.Contains("llama_model_loader:") || line.Contains("print_info: general.") || line.Contains("quantize.")) return;
        lock (gate)
        {
            tail.Add(line);
            if (tail.Count > 400) tail.RemoveRange(0, tail.Count - 400);
        }
        // a full card: WDDM pages and the speed collapses (brief 7)
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\bLOW\b"))
            AddWarning(L("De engine meldt dat het VRAM bijna vol is. Verhoog de VRAM-reserve of sluit andere programma's met een venster op de videokaart."));
        ConsumeLine(line);
    }

    protected void AddWarning(string text)
    {
        lock (gate) { if (!warnings.Contains(text)) warnings.Add(text); }
    }

    protected void SetProgress(double value) => loadProgress = value;

    /// <summary>What the start is doing now; late lines never take a running engine back to starting.</summary>
    protected void Say(string text)
    {
        if (state.Status == EngineStatus.Starting) State = EngineState.Starting(text);
    }

    private void Terminated(int code)
    {
        healthCts?.Cancel();
        ChildProcess? p;
        lock (gate) { p = process; }
        // what it started ends with it: the engine under the server finishes its work after its input closes, within the limit
        if (p != null)
        {
            var until = DateTime.UtcNow + (stopRequested ? StopAndWaitLimit - StopWait : TimeSpan.Zero);
            while (p.ActiveProcesses > 0 && DateTime.UtcNow < until) Thread.Sleep(100);
            p.Dispose();
        }
        sleep?.Dispose();
        sleep = null;
        try { log?.Dispose(); } catch (IOException) { }
        log = null;
        CleanUp();
        lock (gate) process = null;
        // 0: clean exit, 2 / 15: our break or terminate; 130 and STATUS_CONTROL_C_EXIT: how a break ends a process on Windows
        if (stopRequested || code is 0 or 2 or 15 or 130 or unchecked((int)0xC000013A))
        {
            State = EngineState.Stopped;
        }
        else
        {
            string last;
            lock (gate) last = tail.LastOrDefault(l => l.Contains("error") || l.Contains("failed")) ?? tail.LastOrDefault() ?? "";
            State = EngineState.Failed(L($"Engine gestopt (code {code}): {last}"));
        }
    }

    private void PollHealth()
    {
        healthCts?.Cancel();
        var cts = healthCts = new CancellationTokenSource();
        var url = new Uri(BaseUrl, "health");
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HealthInterval, cts.Token);
                    using var response = await Http.GetAsync(url, cts.Token);
                    if ((int)response.StatusCode == 200)
                    {
                        SetProgress(1);
                        if (state.Status == EngineStatus.Starting) State = EngineState.Running;
                        return;
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            }
        });
    }

    public abstract Task<EngineActivity?> PollActivity();

    /// <summary>GET at the engine, with the key when one is set; null when it does not answer.</summary>
    protected async Task<string?> GetJson(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl, path));
        if (!string.IsNullOrEmpty(Settings.Shared.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.Shared.ApiKey);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var response = await Http.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cts.Token) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return null; }
    }

    public void Dispose()
    {
        healthCts?.Cancel();
        ChildProcess? p;
        lock (gate) p = process;
        p?.Dispose();
        sleep?.Dispose();
    }
}

/// <summary>
/// Engines left behind by an app that was forced to quit: they hold their port and over 100 GB of memory, and a new engine would
/// fail on that. Rare since the job ends them with the app. Engines of an app that still runs have that app as parent, and are left alone.
/// </summary>
public static class EngineOrphans
{
    public readonly record struct Entry(int Pid, int ParentPid, string Path);

    /// <summary>Processes running from under the engine folder whose parent is no process of this installation.</summary>
    public static List<int> Pids(IReadOnlyList<Entry> list, string engineRoot, string appRoot)
    {
        static bool Under(string path, string root) =>
            path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var byPid = list.ToDictionary(e => e.Pid);
        return list.Where(e => Under(e.Path, engineRoot) && !(byPid.TryGetValue(e.ParentPid, out var parent) && Under(parent.Path, appRoot)))
                   .Select(e => e.Pid).ToList();
    }

    /// <summary>Stops them as the app stops its engines (10 s to finish, a server hears the end of its pipe by itself), then for good.</summary>
    public static void Stop()
    {
        var list = Snapshot();
        var orphans = Pids(list, Paths.EngineRoot, Paths.AppRoot);
        foreach (var e in list.Where(e => orphans.Contains(e.Pid) && !e.Path.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase)))
            ChildProcess.SendBreak(e.Pid);
        for (int i = 0; i < 40 && orphans.Any(IsAlive); i++) Thread.Sleep(250);
        foreach (var pid in orphans)
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>Every process with parent and path that this user can see.</summary>
    public static List<Entry> Snapshot()
    {
        var entries = new List<Entry>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return entries;
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref pe)) return entries;
            do
            {
                var path = FullPath(pe.th32ProcessID);
                if (path != null) entries.Add(new Entry((int)pe.th32ProcessID, (int)pe.th32ParentProcessID, path));
            } while (Process32NextW(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return entries;
    }

    private static string? FullPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var b = new StringBuilder(32768);
            int n = b.Capacity;
            return QueryFullProcessImageNameW(h, 0, b, ref n) ? b.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint TH32CS_SNAPPROCESS = 2, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public UIntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W pe);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W pe);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);
}
