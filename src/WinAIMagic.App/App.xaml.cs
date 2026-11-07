using Microsoft.UI.Xaml;
using WinAIMagic.Core.Support;

namespace WinAIMagic.App;

public partial class App : Application
{
    private readonly string[] args;
    private ShellWindow? window;

    public App(string[] args)
    {
        this.args = args;
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    /// <summary>What brought the app down stays readable: crash.log in the data folder.</summary>
    public static void Log(Exception? e)
    {
        try { File.AppendAllText(Path.Combine(Paths.AppData, "crash.log"), $"{DateTime.Now:s} {e}{Environment.NewLine}{Environment.NewLine}"); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs _)
    {
        TakeModelFolderFromSetup();
        var state = new AppState();
        window = new ShellWindow(state, args);
        Program.Woken += () => state.Ui.TryEnqueue(() => window.ShowFromTray());
        window.Activate();
        // started with Windows: straight to the notification area, when there is something to do in the background
        if (args.Contains("--background") && state.HasBackgroundWork && Settings.Shared.StayInTray) window.HideToTray(stopModel: false);
    }

    /// <summary>Inno Setup writes the system's ANSI code page; a folder name with an accent is not valid UTF-8 then.</summary>
    private static string ReadSetupText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    /// <summary>The installer's choice of model folder counts once.</summary>
    private static void TakeModelFolderFromSetup()
    {
        var file = Path.Combine(Paths.AppData, "model-folder-from-setup.txt");
        if (!File.Exists(file)) return;
        try
        {
            var folder = ReadSetupText(file).Trim();
            if (folder.Length > 0 && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(Paths.DefaultModelsRoot), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(folder);
                Settings.Shared.ModelFolder = folder;
            }
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}
