using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Windows.Devices.Enumeration;
using Windows.Networking;
using Windows.Networking.ServiceDiscovery.Dnssd;
using Windows.Networking.Sockets;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>
/// Several PCs as one memory. A helper PC runs llama.cpp's RPC server (ggml-rpc-server) on its GPU and announces itself with DNS-SD;
/// the main PC finds the helpers, and its engine spreads the model's layers over its own GPU and theirs (--rpc). Only the second
/// engine can do this: the main engine has no network clustering. A helper's budget is its VRAM.
/// </summary>
public sealed class ClusterController : IDisposable
{
    public const string ServiceType = "_mdsrpc._tcp";
    public const int DefaultPort = 50052;

    public sealed record Helper(string Name, string Chip, double MemoryGb, double GpuGb, string Host, int Port, bool Manual = false, bool Unreachable = false)
    {
        public string Id => $"{Host}:{Port}";
        /// <summary>A link-local address: a direct USB4 or Thunderbolt cable.</summary>
        public bool ViaDirectLink => Host.StartsWith("169.254.");
    }

    private sealed record Stored(List<string> Selected, List<string> Manual);

    private readonly object gate = new();
    private readonly List<Helper> helpers = new();
    private HashSet<string> selected = new();
    private readonly List<string> helperLog = new();
    private readonly IGpuBackend backend;
    private DeviceWatcher? watcher;
    private DnssdServiceInstance? published;
    private StreamSocketListener? publishListener;
    private ChildProcess? server;
    private IDisposable? sleep;

    public event Action? Changed;

    public ClusterController(IGpuBackend backend, bool browse = true)
    {
        this.backend = backend;
        var stored = Load();
        selected = new HashSet<string>(stored.Selected);
        foreach (var manual in stored.Manual) AddManual(manual, save: false);
        if (browse) Browse();
    }

    private static string StoreFile => Path.Combine(Paths.AppData, "cluster.json");

    private static Stored Load()
    {
        try
        {
            if (File.Exists(StoreFile)) return JsonSerializer.Deserialize<Stored>(File.ReadAllText(StoreFile)) ?? new Stored(new(), new());
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new Stored(new(), new());
    }

    private void Save()
    {
        lock (gate)
        {
            try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(new Stored(selected.ToList(), helpers.Where(h => h.Manual).Select(h => h.Id).ToList()))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<Helper> Helpers { get { lock (gate) return helpers.ToList(); } }
    public IReadOnlySet<string> Selected
    {
        get { lock (gate) return new HashSet<string>(selected); }
        set
        {
            lock (gate) selected = new HashSet<string>(value);
            Save();
            Changed?.Invoke();
        }
    }
    public bool HelperRunning => server != null;
    public string HelperAddress { get; private set; } = "";
    public IReadOnlyList<string> HelperLog { get { lock (gate) return helperLog.ToList(); } }

    public bool IsHelperRole => Settings.Shared.ClusterRole == "helper";

    /// <summary>The helpers that take part in the next engine start.</summary>
    public IReadOnlyList<Helper> Active
    {
        // (a helper that does not answer stays out: the second engine would not start with it)
        get { lock (gate) return IsHelperRole ? Array.Empty<Helper>() : helpers.Where(h => selected.Contains(h.Id) && !h.Unreachable).ToList(); }
    }

    /// <summary>What each active helper's GPU can hold for the model, in --rpc order (unknown for a helper added by hand: 0).</summary>
    public IReadOnlyList<ulong> HelperBudgets => BudgetOverride ?? Active.Select(h => (ulong)(Math.Max(h.GpuGb - 3, 0) * 1e9)).ToList();
    /// <summary>Tests: a helper on this same PC shares its memory, so the test says what the helper may take.</summary>
    public IReadOnlyList<ulong>? BudgetOverride { get; set; }
    public string? RpcArgument => Active.Count == 0 ? null : string.Join(",", Active.Select(h => $"{h.Host}:{h.Port}"));

    // MARK: - main PC: find helpers

    /// <summary>Discovery goes through the device watcher, which is how Windows offers DNS-SD browsing.</summary>
    private void Browse()
    {
        const string protocol = "{4526e8c1-8aac-4153-9b16-55e86ada0e54}";
        string aqs = $"System.Devices.AepService.ProtocolId:=\"{protocol}\" AND System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"{ServiceType}\"";
        string[] properties = { "System.Devices.Dnssd.InstanceName", "System.Devices.Dnssd.PortNumber", "System.Devices.Dnssd.TextAttributes" };
        try
        {
            watcher = DeviceInformation.CreateWatcher(aqs, properties, DeviceInformationKind.AssociationEndpointService);
            watcher.Added += (_, d) => Found(d.Name, d.Properties);
            watcher.Updated += (_, u) => { if (u.Properties.ContainsKey("System.Devices.Dnssd.TextAttributes")) Found(null, u.Properties); };
            watcher.Removed += (_, u) => Lost(u.Id);
            watcher.Start();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { watcher = null; }
    }

    public void AddManual(string text, bool save = true)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length == 0 || parts[0].Length == 0) return;
        int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : DefaultPort;
        var h = new Helper(parts[0], "?", 0, 0, parts[0], port, Manual: true);
        lock (gate)
        {
            if (!helpers.Any(x => x.Id == h.Id)) helpers.Add(h);
            selected.Add(h.Id);
        }
        if (save) Save();
        Changed?.Invoke();
        Probe(h);
    }

    /// <summary>Tests: what a helper added by hand reports, in MiB; null when it cannot be reached.</summary>
    public Func<string, long?>? ProbeOverride { get; set; }

    /// <summary>Ask the helpers added by hand again: one may have been turned on since.</summary>
    public void RecheckManual()
    {
        List<Helper> manual;
        lock (gate) manual = helpers.Where(h => h.Manual).ToList();
        foreach (var h in manual) Probe(h);
    }

    /// <summary>A helper added by hand announces nothing: ask it what it can hold, as the second engine would see it.</summary>
    private void Probe(Helper h)
    {
        if (ProbeOverride == null && !File.Exists(LlamaEngine.Binary(backend))) return;
        Task.Run(() =>
        {
            long? mib = ProbeOverride != null ? ProbeOverride(h.Id) : AskHelper(h.Id);
            lock (gate)
            {
                int i = helpers.FindIndex(x => x.Id == h.Id);
                if (i < 0) return;
                helpers[i] = h with { GpuGb = (mib ?? 0) * 1048576 / 1e9, Unreachable = mib == null };
            }
            Changed?.Invoke();
        });
    }

    private long? AskHelper(string id)
    {
        var r = ChildProcess.RunToEnd(LlamaEngine.Binary(backend), new[] { "--rpc", id, "--list-devices" }, TimeSpan.FromSeconds(20));
        return r == null ? null : ParseHelperMemory(r.Value.Output);
    }

    /// <summary>The MiB of the first RPC device in --list-devices: "RPC0: 10.0.0.2:50052 (16310 MiB, 15172 MiB free)".</summary>
    internal static long? ParseHelperMemory(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"RPC\d*:\s+\S+\s+\((\d+) MiB");
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public void Remove(Helper helper)
    {
        lock (gate)
        {
            helpers.RemoveAll(h => h.Id == helper.Id);
            selected.Remove(helper.Id);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>"key=value" attributes into a helper. The port from the record, or an extra "port" attribute (Windows publishes
    /// the port of the socket it was handed, not the server's), else the default.</summary>
    internal static Helper? ParseHelper(string name, IEnumerable<string> txt, int port)
    {
        var dict = new Dictionary<string, string>();
        foreach (var a in txt)
        {
            int i = a.IndexOf('=');
            if (i > 0) dict[a[..i]] = a[(i + 1)..];
        }
        if (!dict.TryGetValue("ip", out var host) || host.Length == 0) return null;
        static double Number(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        int effective = dict.TryGetValue("port", out var ps) && int.TryParse(ps, out var pv) ? pv : port > 0 ? port : DefaultPort;
        return new Helper(name, dict.GetValueOrDefault("chip", "?"), Number(dict, "mem"), Number(dict, "gpu"), host, effective);
    }

    private void Found(string? name, IReadOnlyDictionary<string, object> properties)
    {
        var txt = properties.TryGetValue("System.Devices.Dnssd.TextAttributes", out var t) ? t as string[] : null;
        if (txt == null) return;
        int port = properties.TryGetValue("System.Devices.Dnssd.PortNumber", out var p) && p is ushort u ? u : 0;
        string instance = name ?? (properties.TryGetValue("System.Devices.Dnssd.InstanceName", out var n) ? n as string ?? "" : "");
        var h = ParseHelper(instance, txt, port);
        if (h == null) return;
        lock (gate)
        {
            helpers.RemoveAll(x => x.Id == h.Id);
            helpers.Add(h);
            helpers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }
        Changed?.Invoke();
    }

    private void Lost(string id)
    {
        lock (gate) helpers.RemoveAll(h => !h.Manual && id.Contains(h.Name, StringComparison.OrdinalIgnoreCase));
        Changed?.Invoke();
    }

    // MARK: - helper PC: serve this GPU

    /// <summary>IPv4 addresses of this PC by interface, a direct link (USB4, Thunderbolt) first.</summary>
    public static IReadOnlyList<(string Interface, string Ip, bool Direct)> Addresses()
    {
        var result = new List<(string, string, bool)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = a.Address.ToString();
                result.Add((nic.Name, ip, IsDirect(nic.Description + " " + nic.Name)));
            }
        }
        return result.OrderBy(x => x.Item3 ? 0 : 1).ThenBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    internal static bool IsDirect(string description) =>
        new[] { "thunderbolt", "usb4", "usb 4" }.Any(n => description.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address the helper listens on: the direct link unless the user allows the local network.</summary>
    public static string? BindAddress(IReadOnlyList<(string Interface, string Ip, bool Direct)> all, bool allowLan)
    {
        var direct = all.FirstOrDefault(a => a.Direct);
        if (direct != default) return direct.Ip;
        return allowLan && all.Count > 0 ? all[0].Ip : null;
    }

    /// <summary>Tests: a helper on this same PC binds to 127.0.0.1.</summary>
    internal string? ExecutableOverride { get; set; }
    internal bool PublishService { get; set; } = true;

    public void StartHelper(string? bind = null)
    {
        lock (gate) { if (server != null) return; }
        var bin = ExecutableOverride ?? LlamaEngine.Binary(backend, "ggml-rpc-server.exe");
        if (!File.Exists(bin))
        {
            SetLog(L("ggml-rpc-server niet gevonden: installeer WinAIMagic opnieuw."));
            return;
        }
        var ip = bind ?? BindAddress(Addresses(), Settings.Shared.ClusterAllowLan);
        if (ip == null)
        {
            SetLog(L("Geen directe verbinding gevonden. Sluit de pc's aan met een USB4- of Thunderbolt-kabel, of sta het lokale netwerk toe. Let op: RPC heeft geen wachtwoord."));
            return;
        }
        // -c: keep the model parts this helper receives on its own disk, so the next start does not send them again
        var args = new List<string> { "-H", ip, "-p", DefaultPort.ToString(CultureInfo.InvariantCulture) };
        if (backend.RpcDevice.Length > 0) args.AddRange(new[] { "-d", backend.RpcDevice });
        args.Add("-c");
        var split = new LineSplitter();
        try
        {
            lock (gate)
            {
                server = ChildProcess.Start(new ChildStart(bin, args)
                {
                    OnOutput = (b, n) =>
                    {
                        var lines = split.Add(b, n);
                        lock (gate)
                        {
                            helperLog.AddRange(lines.Select(LogFilter.Clean));
                            if (helperLog.Count > 200) helperLog.RemoveRange(0, helperLog.Count - 200);
                        }
                        Changed?.Invoke();
                    },
                    OnExit = _ => HelperStopped(),
                });
            }
        }
        catch (Win32Exception e)
        {
            SetLog(L($"Kon de helper niet starten: {e.Message}"));
            return;
        }
        HelperAddress = $"{ip}:{DefaultPort}";
        sleep = SleepGuard.Hold();
        if (PublishService) _ = Publish(ip);
        Changed?.Invoke();
    }

    private void SetLog(string line)
    {
        lock (gate) { helperLog.Clear(); helperLog.Add(line); }
        Changed?.Invoke();
    }

    public void StopHelper()
    {
        ChildProcess? p;
        lock (gate) p = server;
        if (p == null) return;
        p.RequestBreak();
        Task.Run(async () =>
        {
            if (await Task.WhenAny(p.Exited, Task.Delay(TimeSpan.FromSeconds(5))) != p.Exited) p.Terminate();
        });
    }

    /// <summary>Stop and start again, so the helper announces what it can hold now.</summary>
    public async Task RestartHelper()
    {
        ChildProcess? p;
        lock (gate) p = server;
        if (p == null) return;
        StopHelper();
        await p.Exited;
        await Task.Delay(100);
        StartHelper();
    }

    private void HelperStopped()
    {
        ChildProcess? p;
        lock (gate) p = server;
        p?.Dispose();
        HelperAddress = "";
        Unpublish();
        sleep?.Dispose();
        sleep = null;
        lock (gate) server = null;
        Changed?.Invoke();
    }

    private async Task Publish(string ip)
    {
        try
        {
            var system = SystemInfo.Current(backend);
            var hostName = Environment.MachineName;
            // the registration wants a socket of ours; the RPC server holds its own port, the "port" attribute names it
            var listener = new StreamSocketListener();
            await listener.BindServiceNameAsync("");
            var service = new DnssdServiceInstance($"{hostName}.{ServiceType}.local", new HostName(hostName + ".local"), (ushort)DefaultPort);
            service.TextAttributes["ip"] = ip;
            service.TextAttributes["chip"] = system.Gpu;
            service.TextAttributes["mem"] = (system.RamTotal / 1e9).ToString("0", CultureInfo.InvariantCulture);
            service.TextAttributes["gpu"] = (system.VramTotal / 1e9).ToString("0", CultureInfo.InvariantCulture);
            service.TextAttributes["port"] = DefaultPort.ToString(CultureInfo.InvariantCulture);
            await service.RegisterStreamSocketListenerAsync(listener);
            published = service;
            publishListener = listener;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
        {
            lock (gate) helperLog.Add(L($"Aankondigen op het netwerk lukte niet ({e.Message}). Voeg deze helper op de hoofd-pc met de hand toe."));
            Changed?.Invoke();
        }
    }

    private void Unpublish()
    {
        publishListener?.Dispose();
        publishListener = null;
        published = null;
    }

    public void Dispose()
    {
        try { if (watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) watcher.Stop(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        ChildProcess? p;
        lock (gate) p = server;
        p?.Dispose();
        Unpublish();
        sleep?.Dispose();
    }
}
