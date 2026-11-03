using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinAIMagic.Engine;

/// <summary>A drive as the model folder's card shows it: free space and whether it is an NVMe.</summary>
public sealed record DriveReport(string Root, ulong FreeBytes, ulong TotalBytes, bool Ready, bool IsNvme, bool? IsSsd)
{
    /// <summary>The drive of a folder (the folder itself need not exist).</summary>
    public static DriveReport For(string path)
    {
        string? root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { root = null; }
        if (string.IsNullOrEmpty(root)) return new DriveReport("", 0, 0, false, false, null);
        ulong free = 0, total = 0;
        bool ready = GetDiskFreeSpaceExW(root, out free, out total, out _);
        var (nvme, ssd) = ready ? Storage(root) : (false, (bool?)null);
        return new DriveReport(root, free, total, ready, nvme, ssd);
    }

    /// <summary>The fixed drives of this PC, for the choice of a model folder.</summary>
    public static IReadOnlyList<DriveReport> All() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => For(d.RootDirectory.FullName)).Where(d => d.Ready).ToList();

    /// <summary>For a model that does not fit on the folder's drive: the drive with the most room that does, or null.</summary>
    public static DriveReport? BetterDrive(string folder, ulong needed)
    {
        var current = For(folder);
        if (current.Ready && current.FreeBytes >= needed) return null;
        return All().Where(d => d.Root != current.Root && d.FreeBytes >= needed)
                    .OrderByDescending(d => d.IsNvme).ThenByDescending(d => d.FreeBytes).FirstOrDefault();
    }

    /// <summary>Bus type and seek penalty through IOCTL_STORAGE_QUERY_PROPERTY; access 0 needs no administrator.</summary>
    private static unsafe (bool Nvme, bool? Ssd) Storage(string root)
    {
        if (root.Length < 2 || root[1] != ':') return (false, null);
        using var h = CreateFileW($@"\\.\{root[..2]}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return (false, null);
        var query = stackalloc uint[3];
        query[0] = 0; query[1] = 0; query[2] = 0;   // StorageDeviceProperty, PropertyStandardQuery
        var buffer = stackalloc byte[1024];
        bool nvme = false;
        if (DeviceIoControl(h, IoctlStorageQueryProperty, query, 12, buffer, 1024, out var n, IntPtr.Zero) && n >= 32)
        {
            string Text(int at)
            {
                uint offset = *(uint*)(buffer + at);
                return offset == 0 || offset >= n ? "" : Marshal.PtrToStringAnsi((IntPtr)(buffer + offset)) ?? "";
            }
            nvme = IsNvmeDevice(*(uint*)(buffer + 28), Text(12), Text(16));
        }
        bool? ssd = null;
        query[0] = StorageDeviceSeekPenaltyProperty;
        if (DeviceIoControl(h, IoctlStorageQueryProperty, query, 12, buffer, 1024, out n, IntPtr.Zero) && n >= 9)
            ssd = buffer[8] == 0;
        return (nvme, nvme ? true : ssd);
    }

    /// <summary>Behind Intel RST the bus says RAID (the reference PC's own NVMe does); the device's name then still says what it is.</summary>
    internal static bool IsNvmeDevice(uint busType, string vendor, string product) =>
        busType == BusTypeNvme || vendor.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || product.Contains("NVMe", StringComparison.OrdinalIgnoreCase);

    private const uint IoctlStorageQueryProperty = 0x2D1400, BusTypeNvme = 17, StorageDeviceSeekPenaltyProperty = 7;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string dir, out ulong free, out ulong total, out ulong totalFree);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, void* input, uint inSize, void* output, uint outSize, out uint returned, IntPtr overlapped);
}
