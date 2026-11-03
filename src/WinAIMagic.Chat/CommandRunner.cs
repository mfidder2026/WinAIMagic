using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinAIMagic.Chat;

/// <summary>Runs one PowerShell command in its own Job Object. The wait is for the shell, not for the pipe: a program the
/// command left running may hold the pipe open for hours, and the time-out and the result must not depend on that.</summary>
internal static class CommandRunner
{
    private const int MaxOutputBytes = 400_000;
    /// <summary>What the shell wrote is in the pipe when it exits; a program still holding the pipe is not waited for.</summary>
    private static readonly TimeSpan DrainTime = TimeSpan.FromMilliseconds(300);

    /// <summary>Tests choose the shell; else pwsh if present, else Windows PowerShell 5.1. A pwsh from the Store does not count:
    /// what it starts leaves the Job Object (measured), so a time-out or Stop would not stop the tree.</summary>
    public static string? ShellOverride { get; set; }
    private static readonly Lazy<string> FoundShell = new(FindShell);
    public static string Shell => ShellOverride ?? FoundShell.Value;

    public static string WindowsPowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string FindShell()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(Path.Combine(dir.Trim(), "pwsh.exe"))) return Path.Combine(dir.Trim(), "pwsh.exe"); }
            catch (ArgumentException) { }
        }
        var seven = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(seven) ? seven : WindowsPowerShell;
    }

    /// <summary>UTF-8 in and out on both PowerShells, then the command, then the exit code of what ran last.</summary>
    private static string Script(string command) =>
        "try { $__utf8 = New-Object System.Text.UTF8Encoding $false; [Console]::OutputEncoding = $__utf8; $OutputEncoding = $__utf8 } catch { }\n"
        + "$PSDefaultParameterValues['*:Encoding'] = 'utf8'; $env:PYTHONIOENCODING = 'utf-8'\n"
        + command + "\n"
        + "if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }\nexit 0\n";

    public static async Task<string> RunAsync(string command, string folder, int timeout, CancellationToken ct)
    {
        // a file, so the length of a command has no limit and errors stay plain text (-EncodedCommand reports them as XML)
        var script = Path.Combine(Path.GetTempPath(), "winaimagic-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(script, Script(command), new UTF8Encoding(true), CancellationToken.None).ConfigureAwait(false);
            return await RunScriptAsync(script, folder, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
        {
            return $"Error: could not start the command: {e.Message}";
        }
        finally
        {
            try { File.Delete(script); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> RunScriptAsync(string script, string folder, int timeout, CancellationToken ct)
    {
        var name = "winaimagic-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var job = new KillOnCloseJob();
        IntPtr process;
        using (var write = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Anonymous, HandleInheritability.Inheritable))
        {
            var connected = pipe.WaitForConnectionAsync(CancellationToken.None);
            write.Connect(5000);
            await connected.ConfigureAwait(false);
            process = Start($"\"{Shell}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"", folder, write.SafePipeHandle, job);
        }
        try
        {
            using var stopReading = new CancellationTokenSource();
            var output = new MemoryStream();
            var reader = ReadAsync(pipe, output, stopReading.Token);

            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            bool timedOut = false, stopped = false;
            while (WaitForSingleObject(process, 0) != 0)
            {
                if (ct.IsCancellationRequested) { stopped = true; break; }
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
            if (stopped || timedOut)
            {
                job.Terminate();
                WaitForSingleObject(process, 5000);
            }
            else job.Release();   // what it started on purpose goes on, also after this job's handle is gone

            await Task.WhenAny(reader, Task.Delay(DrainTime, CancellationToken.None)).ConfigureAwait(false);
            stopReading.Cancel();
            await reader.ConfigureAwait(false);

            GetExitCodeProcess(process, out var code);
            var text = AtomicFile.Utf8.GetString(output.GetBuffer(), 0, (int)output.Length);
            if (text.Length > 30_000)
                text = TextLimit.Prefix(text, 12_000) + $"\n… ({text.Length - 24_000} characters cut) …\n" + TextLimit.Suffix(text, 12_000);
            var status = stopped ? "stopped by the user" : timedOut ? $"stopped after {timeout} s (timeout)" : $"exit code {unchecked((int)code)}";
            return (text.Length == 0 ? "(no output)" : text) + $"\n[{status}]";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static async Task ReadAsync(Stream pipe, MemoryStream output, CancellationToken stop)
    {
        var buffer = new byte[16_384];
        try
        {
            while (true)
            {
                int n = await pipe.ReadAsync(buffer, stop).ConfigureAwait(false);
                if (n == 0) return;
                if (output.Length < MaxOutputBytes) output.Write(buffer, 0, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>Starts the shell inside the job from its first instruction (suspended until it is in: a job given as a
    /// start attribute fails on a pwsh from the Store), with only these handles inherited: another inheritable handle
    /// of the app (an engine's pipe) would stay open as long as a program started here runs.</summary>
    private static IntPtr Start(string commandLine, string folder, SafePipeHandle output, KillOnCloseJob job)
    {
        using var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var self = GetCurrentProcess();
        if (!DuplicateHandle(self, nul, self, out var input, 0, true, DuplicateSameAccess)) throw new Win32Exception();
        using (input)
        {
            var handles = new[] { output.DangerousGetHandle(), input.DangerousGetHandle() };
            var handleList = GCHandle.Alloc(handles, GCHandleType.Pinned);
            IntPtr size = IntPtr.Zero, attributes = IntPtr.Zero;
            try
            {
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                if (!UpdateProcThreadAttribute(attributes, 0, AttributeHandleList, handleList.AddrOfPinnedObject(), handles.Length * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
                var info = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfoEx>(),
                        dwFlags = StartfUseStdHandles,
                        hStdInput = input.DangerousGetHandle(),
                        hStdOutput = output.DangerousGetHandle(),
                        hStdError = output.DangerousGetHandle(),
                    },
                    lpAttributeList = attributes,
                };
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | ExtendedStartupInfoPresent | CreateSuspended, IntPtr.Zero, folder, ref info, out var created))
                    throw new Win32Exception();
                try
                {
                    job.Assign(created.hProcess);
                    ResumeThread(created.hThread);
                }
                catch (Win32Exception)
                {
                    TerminateProcess(created.hProcess, 1);
                    CloseHandle(created.hProcess);
                    throw;
                }
                finally
                {
                    CloseHandle(created.hThread);
                }
                return created.hProcess;
            }
            finally
            {
                if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                handleList.Free();
            }
        }
    }

    private const int StartfUseStdHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000, ExtendedStartupInfoPresent = 0x00080000, CreateSuspended = 4, DuplicateSameAccess = 2;
    private static readonly IntPtr AttributeHandleList = 0x20002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string? directory, ref StartupInfoEx info, out ProcessInformation created);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
}
