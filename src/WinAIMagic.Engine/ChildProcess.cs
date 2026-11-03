using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinAIMagic.Engine;

/// <summary>What to start.</summary>
public sealed record ChildStart(string Exe, IReadOnlyList<string> Args)
{
    public string? Cwd { get; init; }
    /// <summary>On top of this process's environment; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Env { get; init; }
    /// <summary>Keep a pipe to the child's stdin open (--parent-pipe); else stdin is the null device.</summary>
    public bool StdinPipe { get; init; }
    /// <summary>stdout and stderr together, in chunks, on a thread of its own.</summary>
    public Action<byte[], int>? OnOutput { get; init; }
    /// <summary>The exit code; called once, on a pool thread.</summary>
    public Action<int>? OnExit { get; init; }
}

/// <summary>
/// A child process in a Job Object with kill-on-close and in its own process group: whatever happens to the app, the
/// child and everything it started end with it; and it can be asked to stop with CTRL_BREAK_EVENT without touching
/// anything else. Process.Start offers neither the group nor the suspended start that closes the gap before the job.
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly SafeWaitHandle process;
    private readonly IntPtr job;
    private readonly Thread? reader;
    private FileStream? stdin;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;
    private bool disposed => Volatile.Read(ref closed) != 0;

    public int Pid { get; }
    public bool HasExited => exited.Task.IsCompleted;
    public Task<int> Exited => exited.Task;

    private ChildProcess(SafeWaitHandle process, int pid, IntPtr job, SafeFileHandle? stdinWrite, SafeFileHandle outRead, ChildStart spec)
    {
        this.process = process;
        Pid = pid;
        this.job = job;
        if (stdinWrite != null) stdin = new FileStream(stdinWrite, FileAccess.Write, 1);
        var onOutput = spec.OnOutput;
        reader = new Thread(() =>
        {
            var buffer = new byte[64 * 1024];
            using var s = new FileStream(outRead, FileAccess.Read, 1);
            try
            {
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) onOutput?.Invoke(buffer, n);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "child-output" };
        reader.Start();

        var onExit = spec.OnExit;
        new Thread(() =>
        {
            WaitForSingleObject(process, uint.MaxValue);
            GetExitCodeProcess(process, out var code);
            // what is still in the pipe comes first; a grandchild can hold the pipe open, so not forever
            reader!.Join(TimeSpan.FromSeconds(2));
            exited.TrySetResult(unchecked((int)code));
            onExit?.Invoke(unchecked((int)code));
            process.Dispose();
        }) { IsBackground = true, Name = "child-exit" }.Start();
    }

    public static ChildProcess Start(ChildStart spec)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0)) throw new Win32Exception();
        SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
        SafeFileHandle? inRead = null, inWrite = null, nul = null;
        if (spec.StdinPipe)
        {
            if (!CreatePipe(out inRead, out inWrite, ref sa, 0)) throw new Win32Exception();
            SetHandleInformation(inWrite, HANDLE_FLAG_INHERIT, 0);
        }
        else
        {
            nul = CreateFileW("NUL", GENERIC_READ, 3, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        var stdinHandle = (inRead ?? nul)!;

        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new Win32Exception();

        // only these handles go to the child: an inherited handle of another child would keep that one's pipes open
        var handles = new[] { stdinHandle.DangerousGetHandle(), outWrite.DangerousGetHandle() };
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrs = Marshal.AllocHGlobal(size);
        var pinned = GCHandle.Alloc(handles, GCHandleType.Pinned);
        var envBlock = BuildEnvironment(spec.Env);
        var envPin = GCHandle.Alloc(envBlock, GCHandleType.Pinned);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, pinned.AddrOfPinnedObject(), (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = stdinHandle.DangerousGetHandle();
            si.StartupInfo.hStdOutput = outWrite.DangerousGetHandle();
            si.StartupInfo.hStdError = outWrite.DangerousGetHandle();
            si.lpAttributeList = attrs;

            var cmd = new StringBuilder(QuoteArgument(spec.Exe));
            foreach (var a in spec.Args) cmd.Append(' ').Append(QuoteArgument(a));

            uint flags = CREATE_SUSPENDED | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT;
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, envPin.AddrOfPinnedObject(), spec.Cwd, ref si, out var pi))
            {
                var e = new Win32Exception();
                CloseHandle(job);
                outRead.Dispose();
                inWrite?.Dispose();
                throw e;
            }
            if (!AssignProcessToJobObject(job, pi.hProcess))
            {
                var e = new Win32Exception();
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                CloseHandle(job);
                throw e;
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
            return new ChildProcess(new SafeWaitHandle(pi.hProcess, true), pi.dwProcessId, job, inWrite, outRead, spec);
        }
        finally
        {
            DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
            pinned.Free();
            envPin.Free();
            outWrite.Dispose();
            inRead?.Dispose();
            nul?.Dispose();
        }
    }

    /// <summary>Runs a tool to its end and gives its exit status and output; stops it after `timeout` (then null).</summary>
    public static (int Status, string Output)? RunToEnd(string exe, IReadOnlyList<string> args, TimeSpan timeout, string? cwd = null)
    {
        var output = new MemoryStream();
        using var p = Start(new ChildStart(exe, args)
        {
            Cwd = cwd,
            OnOutput = (b, n) => { lock (output) output.Write(b, 0, n); },
        });
        if (!p.Exited.Wait(timeout))
        {
            p.KillTree();
            return null;
        }
        lock (output) return (p.Exited.Result, Encoding.UTF8.GetString(output.ToArray()));
    }

    /// <summary>Asks the group to stop as Ctrl+Break does. The app may have no console, so it borrows the child's for the call.</summary>
    public bool RequestBreak() => SendBreak(Pid);

    private static readonly object ConsoleGate = new();

    internal static bool SendBreak(int pid)
    {
        lock (ConsoleGate)
        {
            bool hadConsole = GetConsoleWindow() != IntPtr.Zero;
            FreeConsole();
            bool ok = false;
            if (AttachConsole((uint)pid))
            {
                // this process does not belong to the group, but handlers are per console: leave nothing to chance
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                ok = GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)pid);
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
            // a test host or a console build gets its console back; a windowed app never had one
            if (hadConsole) AttachConsole(ATTACH_PARENT_PROCESS);
            return ok;
        }
    }

    /// <summary>--parent-pipe: this is how the server hears that the program that started it ended.</summary>
    public void CloseStdin()
    {
        var s = Interlocked.Exchange(ref stdin, null);
        try { s?.Dispose(); } catch (IOException) { }
    }

    /// <summary>Only this process, not what it started: those get the end of their input and may finish their work.</summary>
    public void Terminate(uint code = 15)
    {
        try { if (!HasExited) TerminateProcess(process, code); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Everything in the job, at once.</summary>
    public void KillTree()
    {
        if (!disposed) TerminateJobObject(job, 15);
    }

    /// <summary>Processes still alive in the job (the child and what it started).</summary>
    public int ActiveProcesses
    {
        get
        {
            if (disposed) return 0;
            return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
                                             Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses : 0;
        }
    }

    /// <summary>Closing the job ends whatever still runs in it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        CloseStdin();
        CloseHandle(job);
        // the process ended with the job; its handle stays with the thread that waits for it
        exited.Task.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>The rules of CommandLineToArgvW: a backslash only counts before a double quote.</summary>
    public static string QuoteArgument(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return a;
        var o = new StringBuilder("\"");
        for (int i = 0; i < a.Length; i++)
        {
            int slashes = 0;
            while (i < a.Length && a[i] == '\\') { slashes++; i++; }
            if (i == a.Length) { o.Append('\\', slashes * 2); break; }
            if (a[i] == '"') o.Append('\\', slashes * 2 + 1).Append('"');
            else o.Append('\\', slashes).Append(a[i]);
        }
        return o.Append('"').ToString();
    }

    private static char[] BuildEnvironment(IReadOnlyDictionary<string, string?>? extra)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value ?? "";
        if (extra != null)
            foreach (var kv in extra)
            {
                if (kv.Value == null) env.Remove(kv.Key);
                else env[kv.Key] = kv.Value;
            }
        var o = new StringBuilder();
        foreach (var kv in env) o.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        o.Append('\0');
        return o.ToString().ToCharArray();
    }

    // MARK: - Win32

    private const uint CREATE_SUSPENDED = 0x4, CREATE_NEW_PROCESS_GROUP = 0x200, CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x400, EXTENDED_STARTUPINFO_PRESENT = 0x80000;
    private const int STARTF_USESTDHANDLES = 0x100;
    private const uint HANDLE_FLAG_INHERIT = 1, GENERIC_READ = 0x80000000, OPEN_EXISTING = 3;
    private const uint CTRL_BREAK_EVENT = 1, ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectBasicAccountingInformation = 1, JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeHandle h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int cls, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeWaitHandle h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeWaitHandle h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}

/// <summary>Keeps the system awake while an engine or helper lives; the screen may go off (ES_SYSTEM_REQUIRED only). The flag
/// belongs to the thread that set it, so one thread of its own holds it.</summary>
public static class SleepGuard
{
    private static readonly object Gate = new();
    private static int holds;
    private static Thread? thread;
    private static readonly AutoResetEvent Wake = new(false);

    public static IDisposable Hold()
    {
        lock (Gate)
        {
            holds++;
            if (thread == null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "sleep-guard" };
                thread.Start();
            }
            Wake.Set();
        }
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            lock (Gate) holds--;
            Wake.Set();
        }
    }

    internal static int Holds { get { lock (Gate) return holds; } }

    private static void Run()
    {
        bool on = false;
        for (;;)
        {
            int n;
            lock (Gate) n = holds;
            if (n > 0 && !on) { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); on = true; }
            else if (n == 0 && on) { SetThreadExecutionState(ES_CONTINUOUS); on = false; }
            Wake.WaitOne();
        }
    }

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
