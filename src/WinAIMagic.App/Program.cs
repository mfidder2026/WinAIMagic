using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinAIMagic.Core.Support;

namespace WinAIMagic.App;

/// <summary>One instance: the mutex decides, a second start wakes the first through an event and ends.</summary>
public static class Program
{
    private const string MutexName = @"Local\WinAIMagic.Instance", WakeName = @"Local\WinAIMagic.Wake";
    private const int AsfwAny = -1;

    /// <summary>A second start asks for this.</summary>
    public static event Action? Woken;

    [STAThread]
    public static int Main(string[] args)
    {
        // hidden, for tests: another data folder (before anything reads the settings); such a copy runs beside the real app
        var data = args.FirstOrDefault(a => a.StartsWith("--data=", StringComparison.Ordinal))?[7..];
        if (data != null) Paths.AppData = data;
        string instance = data == null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(data)))[..8];

        using var mutex = new Mutex(true, MutexName + instance, out bool first);
        if (!first)
        {
            AllowSetForegroundWindow(AsfwAny);
            try { using var wake = EventWaitHandle.OpenExisting(WakeName + instance); wake.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        using var woken = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName + instance);
        var registration = ThreadPool.RegisterWaitForSingleObject(woken, (_, _) => Woken?.Invoke(), null, -1, false);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App(args);
        });
        registration.Unregister(null);
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
