using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>WinAIMagic Portable (the Mac's 7702cf5): everything in WinAIMagic-data beside the app, offline at its first start, its own
/// port, and a fallback only when the drive cannot be written. Tests use folders in %TEMP%, never a real USB drive.</summary>
public sealed class PortableTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wam-portable-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string app, local;

    public PortableTests()
    {
        app = Path.Combine(root, "E", "WinAIMagic Portable");
        local = Path.Combine(root, "LocalAppData");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(local);
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) new DirectoryInfo(d).Attributes = FileAttributes.Directory;
        Directory.Delete(root, true);
    }

    [Fact]
    public void ThePortableAppKeepsItsDataBesideIt()
    {
        Assert.False(Portable.IsOn(app));
        File.WriteAllText(Path.Combine(app, Portable.Marker), "");
        Assert.True(Portable.IsOn(app));
        var (folder, onDrive) = Portable.DataFolder(app, local);
        Assert.Equal(Path.Combine(app, "WinAIMagic-data"), folder);
        Assert.True(onDrive);
        Assert.Empty(Directory.EnumerateFileSystemEntries(local));     // nothing on the pc itself
        Assert.Empty(Directory.EnumerateFiles(folder));                // the write test leaves nothing behind
    }

    [Fact]
    public void ACopyOfBeforeKeepsItsDataFolder()
    {
        Directory.CreateDirectory(Path.Combine(app, "data"));
        Assert.Equal(Path.Combine(app, "data"), Portable.DataFolder(app, local).Folder);
    }

    /// <summary>A drive that cannot be written: the data goes to %LOCALAPPDATA%\WinAIMagic Portable, never the installed app's folder.</summary>
    [Fact]
    public void AReadOnlyDriveFallsBackToItsOwnFolder()
    {
        // a data folder that is a file cannot be written to: the simplest read-only stand-in that works without a real drive
        var blocked = Path.Combine(root, "F", "WinAIMagic Portable");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "WinAIMagic-data"), "a file, not a folder");
        var (folder, onDrive) = Portable.DataFolder(blocked, local);
        Assert.False(onDrive);
        Assert.Equal(Path.Combine(local, "WinAIMagic Portable"), folder);
        Assert.NotEqual(Path.Combine(local, "WinAIMagic"), folder);
    }

    [Fact]
    public void TheFirstStartIsOfflineOnItsOwnPort()
    {
        var s = Settings.Detached();
        s.WebSearch = s.FeatureBrowser = s.FeatureMail = s.FeatureComputerUse = s.FeatureTelegram = s.Heartbeat = s.ClusterAllowLan = true;
        var data = Path.Combine(app, "WinAIMagic-data");
        Portable.FirstStart(s, data);
        Assert.False(s.WebSearch || s.FeatureBrowser || s.FeatureMail || s.FeatureComputerUse || s.FeatureTelegram || s.Heartbeat || s.ClusterAllowLan);
        Assert.Equal(8090, s.Port);
        Assert.Equal(Path.Combine(data, "Werkmap"), s.Workspace);
        Assert.True(Directory.Exists(s.Workspace));
    }

    [Fact]
    public void TheDriveIsNamedInTheSidebar()
    {
        Assert.Equal("E:", Portable.Drive(@"E:\WinAIMagic Portable\"));
        Assert.Equal("D:", Portable.Drive(@"D:\"));
    }
}
