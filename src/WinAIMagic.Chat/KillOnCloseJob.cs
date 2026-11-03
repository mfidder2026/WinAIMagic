using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinAIMagic.Chat;

/// <summary>A Job Object with kill-on-close: when the handle closes, also because the app crashed, everything in it stops.
/// Release keeps what still runs alive, for a program the model started on purpose.</summary>
public sealed class KillOnCloseJob : IDisposable
{
    private IntPtr handle;

    public KillOnCloseJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        SetLimit(JobObjectLimitKillOnJobClose);
    }

    public IntPtr Handle => handle;

    public void Assign(IntPtr process)
    {
        if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception();
    }

    /// <summary>Stops every process in the job.</summary>
    public void Terminate(uint exitCode = 1) => TerminateJobObject(handle, exitCode);

    /// <summary>Closing no longer stops what runs in the job.</summary>
    public void Release() => SetLimit(0);

    public void Dispose()
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    private void SetLimit(uint flags)
    {
        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = flags } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
            throw new Win32Exception();
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
