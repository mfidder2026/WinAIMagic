namespace WinAIMagic.Core.Support;

/// <summary>WinAIMagic Portable, from a USB drive (the Mac's 7702cf5): portable.txt beside WinAIMagic.exe keeps everything in
/// WinAIMagic-data beside it (models, conversations, the engine's saved conversations, the work folder Werkmap, Documenten,
/// Afbeeldingen, the settings), so nothing of it lands in %LOCALAPPDATA%, the user's folders or the registry. It starts offline, on a
/// port of its own, so it never meets an installed WinAIMagic. A drive that cannot be written falls back to
/// %LOCALAPPDATA%\WinAIMagic Portable, never the installed app's folder.</summary>
public static class Portable
{
    public const string Marker = "portable.txt", DataName = "WinAIMagic-data", FallbackName = "WinAIMagic Portable";
    /// <summary>The engine's port: an installed WinAIMagic uses 8080.</summary>
    public const int Port = 8090;

    public static bool IsOn(string appRoot) => File.Exists(Path.Combine(appRoot, Marker));

    /// <summary>Where the portable app keeps its data: WinAIMagic-data beside the app (or `data`, the name before 1.14.16, when that
    /// is there), and only when that cannot be written the fallback in the user's own app data.</summary>
    public static (string Folder, bool OnDrive) DataFolder(string appRoot, string localAppData)
    {
        var older = Path.Combine(appRoot, "data");
        var wanted = Directory.Exists(older) ? older : Path.Combine(appRoot, DataName);
        return CanWrite(wanted) ? (wanted, true) : (Path.Combine(localAppData, FallbackName), false);
    }

    public static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>"E:" for an app on E:\, for the sidebar's "USB · E:".</summary>
    public static string Drive(string appRoot) => (Path.GetPathRoot(Path.GetFullPath(appRoot)) ?? "").TrimEnd('\\', '/');

    /// <summary>The first start of a portable copy: offline until the user turns things on (web search, the browser, mail, controlling
    /// programs, Telegram, the heartbeat, the cluster network), its own port, and its work folder on the drive. The user's later
    /// choices stay on the drive.</summary>
    public static void FirstStart(Settings s, string dataFolder)
    {
        s.WebSearch = false;
        s.FeatureBrowser = false;
        s.FeatureMail = false;
        s.FeatureComputerUse = false;
        s.FeatureTelegram = false;
        s.Heartbeat = false;
        s.ClusterAllowLan = false;
        s.Port = Port;
        var work = Path.Combine(dataFolder, "Werkmap");
        Directory.CreateDirectory(work);
        s.Workspace = work;
    }
}
