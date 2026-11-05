using System.Runtime.InteropServices;

namespace WinAIMagic.Core.Support;

/// <summary>Where WinAIMagic keeps its models, its engine and its conversations.</summary>
public static class Paths
{
    private static string? appData;

    /// <summary>Tests point this at a temporary folder.</summary>
    public static string AppData
    {
        get
        {
            if (appData == null)
            {
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAIMagic");
                Directory.CreateDirectory(appData);
            }
            return appData;
        }
        set { appData = value; Directory.CreateDirectory(value); }
    }

    public static string DefaultModelsRoot => Path.Combine(AppData, "models");

    /// <summary>The folder with one subfolder per model: chosen by the user, else the default.</summary>
    public static string ModelsRoot => string.IsNullOrEmpty(Settings.Shared.ModelFolder) ? DefaultModelsRoot : Settings.Shared.ModelFolder;

    public static string ModelDir(string id) => Path.Combine(ModelsRoot, id);
    public static string VisionDir => Path.Combine(ModelsRoot, "vision");

    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string ConversationsFile => Path.Combine(AppData, "conversations.json");
    public static string BenchmarksFile => Path.Combine(AppData, "benchmarks.json");
    public static string EngineLog => Path.Combine(AppData, "engine.log");
    public static string EngineFastLog => Path.Combine(AppData, "engine-fast.log");
    public static string ImageLog => Path.Combine(AppData, "image.log");
    public static string GeneralNotes => Path.Combine(AppData, "WINAIMAGIC.md");

    /// <summary>Documents\WinAIMagic, through the Known Folder API so a OneDrive redirection is followed.</summary>
    public static string DocumentsRoot
    {
        get
        {
            var dir = Path.Combine(KnownDocuments(), "WinAIMagic");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Pictures\WinAIMagic, where the images go; made when the first image is. A OneDrive redirection is followed here too.</summary>
    public static string PicturesFolder =>
        Path.Combine(Known(new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"), Environment.SpecialFolder.MyPictures), "WinAIMagic");

    /// <summary>The folder of the installed app: the engine folder lies beside WinAIMagic.exe.</summary>
    public static string AppRoot => AppContext.BaseDirectory;

    /// <summary>The number in Directory.Build.props: the sidebar, the system check and an exported measurement show it.</summary>
    public static string AppVersion => typeof(Paths).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>The installation's engine folder: cuda\, vulkan\, cpu\, tiered\, python\ (installer\WinAIMagic.iss).</summary>
    public static string EngineRoot => Path.Combine(AppRoot, "engine");

    private static string KnownDocuments() => Known(new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), Environment.SpecialFolder.MyDocuments);

    private static string Known(Guid folder, Environment.SpecialFolder otherwise)
    {
        if (SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p) ?? Environment.GetFolderPath(otherwise); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Environment.GetFolderPath(otherwise);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
