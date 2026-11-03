using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Engine;

/// <summary>What this PC has: processor, the three memory tiers, free disk. A plain record, so the planner stays a pure function
/// and tests can describe any PC.</summary>
public sealed record SystemInfo
{
    public string Cpu { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string WindowsVersion { get; init; } = "";
    public ulong RamTotal { get; init; }
    public ulong RamAvailable { get; init; }
    public ulong PageFile { get; init; }
    /// <summary>The page file this card asks for; below it the engine pins that much less RAM.</summary>
    public ulong RequiredPageFile { get; init; }
    public int PerformanceCores { get; init; } = 4;
    public ulong VramTotal { get; init; }
    /// <summary>What is free on the card right now, when known; such a reading can be about 1 GB too high.</summary>
    public ulong? VramFree { get; init; }
    /// <summary>VRAM kept free beside the engine (profile or setting).</summary>
    public ulong VramReserve { get; init; }
    /// <summary>RAM left to Windows and other programs.</summary>
    public ulong RamMargin { get; init; }
    public bool DrivesDisplay { get; init; }
    public bool UnifiedMemory { get; init; }
    public bool HasGpu { get; init; }
    /// <summary>Why the main engine cannot run here, or null when it can.</summary>
    public string? TieredUnavailable { get; init; }
    public ulong FreeDisk { get; init; }

    /// <summary>The real PC with the chosen backend.</summary>
    public static SystemInfo Current(IGpuBackend backend)
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        ulong pageFile = mem.ullTotalPageFile > mem.ullTotalPhys ? mem.ullTotalPageFile - mem.ullTotalPhys : 0;
        var gpu = backend.Adapters.FirstOrDefault();
        ulong? vramFree = gpu != null && GpuMemoryUsed(gpu.Luid) is ulong used ? (backend.VramTotal > used ? backend.VramTotal - used : 0) : null;
        return new SystemInfo
        {
            Cpu = CpuName(),
            Gpu = gpu == null ? "" : string.Join(" + ", backend.Adapters.Select(a => a.Name)),
            WindowsVersion = WindowsName(),
            RamTotal = mem.ullTotalPhys,
            RamAvailable = mem.ullAvailPhys,
            PageFile = pageFile,
            RequiredPageFile = backend.RequiredPageFile(mem.ullTotalPhys),
            PerformanceCores = PerformanceCoreCount(),
            VramTotal = backend.VramTotal,
            VramFree = vramFree,
            VramReserve = backend.VramReserve(Settings.Shared.VramReserveMib),
            RamMargin = backend.RamMargin(mem.ullTotalPhys),
            DrivesDisplay = backend.DrivesDisplay,
            UnifiedMemory = backend.UnifiedMemory,
            HasGpu = backend.HasGpu,
            TieredUnavailable = backend.TieredUnavailable,
            FreeDisk = DriveReport.For(Paths.ModelsRoot).FreeBytes,
        };
    }

    /// <summary>A PC described by its backend and RAM (tests, and the plans for other PCs).</summary>
    public static SystemInfo Simulated(IGpuBackend backend, int ramGb, int cores = 8, ulong? vramFree = null) => new()
    {
        Cpu = "Test CPU",
        Gpu = string.Join(" + ", backend.Adapters.Select(a => a.Name)),
        WindowsVersion = "Windows 11",
        RamTotal = (ulong)ramGb << 30,
        RamAvailable = (ulong)ramGb << 30,
        PageFile = Math.Max(32UL << 30, backend.RequiredPageFile((ulong)ramGb << 30)),
        RequiredPageFile = backend.RequiredPageFile((ulong)ramGb << 30),
        PerformanceCores = cores,
        VramTotal = backend.VramTotal,
        VramFree = vramFree,
        VramReserve = backend.VramReserve(-1),
        RamMargin = backend.RamMargin((ulong)ramGb << 30),
        DrivesDisplay = backend.DrivesDisplay,
        UnifiedMemory = backend.UnifiedMemory,
        HasGpu = backend.HasGpu,
        TieredUnavailable = backend.TieredUnavailable,
        FreeDisk = 1_000_000_000_000,
    };

    /// <summary>Pages Windows would hand out right now without swapping.</summary>
    public static ulong AvailableMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) ? mem.ullAvailPhys : 0;
    }

    internal static (ulong Total, ulong Available) RamNow()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) ? (mem.ullTotalPhys, mem.ullAvailPhys) : (0, 0);
    }

    private static ulong? GpuMemoryUsed(long luid)
    {
        using var q = new PdhCounters();
        return q.GpuMemoryOnce(luid);
    }

    public static string CpuName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
    }

    public static string WindowsName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build = int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        string name = build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} ({build})";
    }

    /// <summary>The cores of the fastest efficiency class: hyperthreads and small cores only get in the way of the experts.</summary>
    public static unsafe int PerformanceCoreCount()
    {
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        if (size == 0) return Math.Max(1, Environment.ProcessorCount / 2);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) return Math.Max(1, Environment.ProcessorCount / 2);
            var counts = new Dictionary<byte, int>();
            for (uint at = 0; at < size;)
            {
                byte* p = (byte*)buffer + at;
                uint entry = *(uint*)(p + 4);
                // PROCESSOR_RELATIONSHIP: Flags at +8, EfficiencyClass at +9
                byte efficiency = p[9];
                counts[efficiency] = counts.GetValueOrDefault(efficiency) + 1;
                at += entry;
            }
            return counts.Count == 0 ? Math.Max(1, Environment.ProcessorCount / 2) : counts[counts.Keys.Max()];
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint size);
}

public static class ByteFormat
{
    /// <summary>A capacity as it says on the box: a 16 GB card reports just under 16 GiB and should not read as 15.</summary>
    public static ulong Gib(this ulong bytes) => (ulong)Math.Round(bytes / (double)(1UL << 30));

    /// <summary>LiveMetrics counts in 1e9 bytes; memory is read in the units it is sold in.</summary>
    public const double GibPerGb = 1e9 / (1UL << 30);

    /// <summary>"12.3 GB", in the app's language.</summary>
    public static string Gb(this ulong bytes) =>
        (bytes / 1e9).ToString("0.0", AppLanguages.Current.Culture()) + " GB";

    /// <summary>"12 GB".</summary>
    public static string GbShort(this ulong bytes) =>
        (bytes / 1e9).ToString("0", AppLanguages.Current.Culture()) + " GB";
}
