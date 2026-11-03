using System.Diagnostics;

namespace WinAIMagic.Chat;

/// <summary>Are Python and git here? Without them the model should not try them first. Asked once per app session.</summary>
public static class DeveloperTools
{
    private static readonly Lazy<IReadOnlyList<string>> Found = new(() => new[] { "python", "git" }.Where(n => !Present(n)).ToList());

    /// <summary>Tests decide what is missing.</summary>
    public static IReadOnlyList<string>? MissingOverride { get; set; }

    public static IReadOnlyList<string> Missing => MissingOverride ?? Found.Value;
    public static bool HasDeveloperTools => Missing.Count == 0;

    private static bool Present(string name)
    {
        var lines = Run("where.exe", name);
        if (lines == null) return false;
        foreach (var path in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Windows ships a python.exe in WindowsApps that only points at the Store: it fails to run
            if (!path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return true;
            if (Run(path, "--version") != null) return true;
        }
        return false;
    }

    /// <summary>The output when the program exits with 0, else null.</summary>
    private static string? Run(string program, string argument)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, argument)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { p.Kill(true); return null; }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
