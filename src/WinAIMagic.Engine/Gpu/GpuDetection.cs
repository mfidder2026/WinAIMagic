using System.Runtime.InteropServices;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>The backend chosen, why, and what was tried before it.</summary>
public sealed record BackendChoice(IGpuBackend Backend, string Reason, IReadOnlyList<string> Tried);

/// <summary>Finds the cards and picks the fastest backend that runs on them (brief 3a, "Herkenning en keuze").</summary>
public static class GpuDetection
{
    /// <summary>The real machine: DXGI, nvidia-smi in its own process, the setting that overrides the choice.</summary>
    public static BackendChoice Detect() => Choose(EnumerateAdapters(), new BackendProbe(), Settings.Shared.GpuBackendOverride);

    public static BackendChoice Choose(IReadOnlyList<GpuAdapter> adapters, BackendProbe probe, string preferred = "")
    {
        var targets = adapters.Where(a => !a.IsIntegrated).ToList();
        var byMake = targets.GroupBy(a => a.VendorId).ToDictionary(g => g.Key, g => (IReadOnlyList<GpuAdapter>)g.ToList());

        IGpuBackend? Native(uint make) => !byMake.TryGetValue(make, out var cards) ? null
            : make == GpuAdapter.NvidiaId ? new NvidiaBackend(cards, probe)
            : make == GpuAdapter.AmdId ? new AmdBackend(cards)
            : null;

        // the make with the most usable VRAM wins; the setting can say otherwise
        var order = byMake.OrderByDescending(kv => kv.Value.Aggregate(0UL, (n, a) => n + a.VramBytes)).Select(kv => kv.Key).ToList();
        var chain = new List<IGpuBackend>();
        foreach (var make in order) if (Native(make) is { } b) chain.Add(b);
        if (order.Count > 0) chain.Add(new VulkanBackend(byMake[order[0]], probe));
        chain.Add(new CpuBackend(probe));
        if (!string.IsNullOrEmpty(preferred))
        {
            var wanted = chain.Where(b => b.Id == preferred).ToList();
            chain.RemoveAll(b => b.Id == preferred);
            chain.InsertRange(0, wanted);
        }

        var tried = new List<string>();
        foreach (var b in chain)
        {
            if (b.Unavailable is { } why)
            {
                tried.Add($"{b.DisplayName}: {why}");
                continue;
            }
            return new BackendChoice(b, ReasonFor(b, tried, preferred), tried);
        }
        // the installation is broken: the last in the chain is CPU, the system check says what is missing
        return new BackendChoice(chain[^1], L("Geen enkele engine is aanwezig.") + " " + string.Join(" ", tried), tried);
    }

    private static string ReasonFor(IGpuBackend b, List<string> tried, string preferred)
    {
        string first = b.Id == preferred ? L($"{b.DisplayName}: gekozen onder Instellingen.")
            : !b.HasGpu ? L("Geen bruikbare videokaart: de engine rekent op de processor.")
            : b.Adapters.Count > 1 ? L($"{b.DisplayName}: {b.Adapters.Count} videokaarten samen, {b.VramTotal.Gb()} VRAM.")
            : b.TieredUnavailable == null ? L($"{b.DisplayName} op {b.Adapters[0].Name}, {b.Capability}.")
            : L($"{b.DisplayName} op {b.Adapters[0].Name}. Beperkte modus: {b.TieredUnavailable}");
        if (b.Experimental && b.HasGpu) first += " " + L("Experimenteel op deze kaart.");
        return tried.Count == 0 ? first : first + " " + L($"Eerder geprobeerd: {string.Join(" ", tried)}");
    }

    /// <summary>nvidia-smi in a child process: NVML must never run in this process, a crash there took the upstream server down.</summary>
    public static string? ReadComputeCapability()
    {
        try
        {
            var r = ChildProcess.RunToEnd("nvidia-smi", new[] { "--query-gpu=compute_cap", "--format=csv,noheader" }, TimeSpan.FromSeconds(20));
            if (r is not { Status: 0 }) return null;
            var line = r.Value.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(line) || !char.IsDigit(line[0]) ? null : line;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    // MARK: - DXGI, through the vtables (IDXGIFactory1, IDXGIAdapter1): no COM interop layer needed

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private const uint FlagSoftware = 2;

    public static unsafe IReadOnlyList<GpuAdapter> EnumerateAdapters()
    {
        var list = new List<GpuAdapter>();
        var iid = IidFactory1;
        if (CreateDXGIFactory1(in iid, out var factory) < 0) return list;
        try
        {
            var vt = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vt[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break;
                var avt = *(IntPtr**)adapter;
                AdapterDesc1 d;
                if (((delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)avt[10])(adapter, &d) >= 0 && (d.Flags & FlagSoftware) == 0 && d.VendorId != 0x1414)
                {
                    string name = new string(d.Description);
                    long umd = 0;
                    var dev = IidDevice;
                    ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, long*, int>)avt[9])(adapter, &dev, &umd);
                    string driver = umd == 0 ? "" : $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
                    // an adapter with an output attached drives the desktop
                    IntPtr output;
                    bool display = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)avt[7])(adapter, 0, &output) >= 0;
                    if (display) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)output)[2])(output);
                    long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                    list.Add(new GpuAdapter(name, d.VendorId, d.DeviceId, d.DedicatedVideoMemory, d.SharedSystemMemory, FriendlyDriver(d.VendorId, driver), luid, display));
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)avt[2])(adapter);
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)factory)[2])(factory);
        }
        return list;
    }

    /// <summary>32.0.16.1088 is the Windows form of NVIDIA's 610.88: the last five digits.</summary>
    internal static string FriendlyDriver(uint vendor, string driver)
    {
        if (vendor != GpuAdapter.NvidiaId) return driver;
        var p = driver.Split('.');
        if (p.Length != 4) return driver;
        string digits = (p[2] + p[3].PadLeft(4, '0')).TrimStart('0');
        if (digits.Length < 5) return driver;
        digits = digits[^5..];
        return digits[..3] + "." + digits[3..];
    }
}
