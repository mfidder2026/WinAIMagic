using System.Text.RegularExpressions;

namespace WinAIMagic.Chat;

/// <summary>The same error, again and again: then the model's idea of its cause is wrong, and it should stop and look (RepeatedErrors
/// in the Mac's ChatClient). In a C64 run on the Mac (Terminator intro, Qwen3.6, 11 Nov 2026) Kick Assembler said "Syntax error" on
/// `beq !t1-done` in 10 of 13 builds; the model blamed the line above the ^, rewrote the whole file 14 times and never looked at
/// the line itself. After the third time the app now says so in that tool result.</summary>
public sealed class RepeatedErrors
{
    /// <summary>The third time the same error comes back the model hears it, and again every third time after.</summary>
    public const int WarnEvery = 3;
    private readonly Dictionary<string, int> counts = [];

    /// <summary>A line that reports an error, in the output of a build, a script or a command.</summary>
    private static readonly Regex ErrorLine = new(@"error|fout|exception|fatal|failed|mislukt|unknown symbol|invalid|not found|no such file|undefined|cannot find|is not recognized",
        RegexOptions.IgnoreCase);
    /// <summary>Lines that every failure has, whatever its cause: they say nothing about which error it is (build.sh on the Mac,
    /// c64kbuild.py and PowerShell's own lines here).</summary>
    private static readonly Regex Generic = new(@"^(\[build\] error: (assembleren mislukt|syntax checker)|fout: (de syntax checker vond|kick assembler gaf een fout)|traceback|\[exit code|summary: |got \d+ errors|\[build\] verander alleen|\+ categoryinfo|\+ fullyqualifiederrorid)",
        RegexOptions.IgnoreCase);

    /// <summary>What identifies the error a result reports: the source line the ^ points at (Kick Assembler, Python, cc) and the
    /// first specific error line, with numbers and folders left out, so a line that moved is the same error. Null when the result
    /// reports no error.</summary>
    public static string? Signature(string result)
    {
        var lines = result.Replace("\r\n", "\n").Split('\n');
        var parts = new List<string>();
        // the line right above a line that is only a ^
        int caret = Array.FindIndex(lines, l => l.Trim() == "^");
        if (caret > 0) parts.Add(lines[caret - 1]);
        var first = lines.FirstOrDefault(line =>
        {
            var t = line.Trim();
            return t.Length > 0 && ErrorLine.IsMatch(t) && !Generic.IsMatch(t);
        });
        if (first != null) parts.Add(first);
        return parts.Count == 0 ? null : string.Join(" | ", parts.Select(Normalized));
    }

    /// <summary>Lower case, digits as #, a path as its last part (C:\…\, /…/), spaces collapsed.</summary>
    public static string Normalized(string line)
    {
        var s = line.ToLowerInvariant();
        s = Regex.Replace(s, @"(?:[a-z]:)?(?:[\\/][^\s\\/:()'""]+)+[\\/]", "");
        s = Regex.Replace(s, @"\d+", "#");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    /// <summary>Counts the error in a tool result; returns the note for the model when it came back for the 3rd (6th, …) time.</summary>
    public string? Note(string result)
    {
        if (Signature(result) is not { } key) return null;
        int n = counts[key] = counts.GetValueOrDefault(key) + 1;
        if (n % WarnEvery != 0) return null;
        var shown = key.Length > 200 ? key[..200] + "…" : key;
        return $"(Note from the app: the same error has now come back {n} times in this answer: {shown}\nWhat you changed so far did not fix it, so your idea of its cause is probably wrong. Stop changing other things. Read the line the error names, word for word, and the hint if there is one; change only that line. If you are not sure why it fails, try that one construct alone in a small test file, or look it up in the documentation. Do not rewrite the whole file.)";
    }
}

/// <summary>The same file written in full again and again without running it: the model is then guessing on paper, and every rewrite
/// brings new mistakes that nothing shows. On the Mac (Terminator intro, Qwen3.8, 11 Nov 2026) the model wrote terminator.asm in full
/// 9 times and gen.py 5 times in two hours, built nothing, and 54% of the time went to those rewrites. The third time a file is
/// written in full while no command named it since, the app says so with that write's result, and again every third time after.</summary>
public sealed class UntestedRewrites
{
    public const int WarnEvery = 3;
    private readonly Dictionary<string, int> counts = [];

    /// <summary>A write_file to `path` that went through: the note for the model at the 3rd (6th, …) full write since a command last
    /// named the file.</summary>
    public string? Wrote(string path)
    {
        var key = Key(path);
        if (key.Length == 0) return null;
        int n = counts[key] = counts.GetValueOrDefault(key) + 1;
        if (n % WarnEvery != 0) return null;
        return $"(Note from the app: you have now written {path} in full {n} times in this answer, and no command ran it since. Rewriting does not show what is wrong. Build or run it now, then fix only what fails, with edit_file. Make it work first; make it nicer after.)";
    }

    /// <summary>A command: the files it names count as run. A file is named by its own name (intro.asm) or by its name with another
    /// extension (build\intro.prg); a folder of the same name (cd …\Intro) does not count.</summary>
    public void Ran(string command)
    {
        var c = command.ToLowerInvariant();
        foreach (var key in counts.Keys.ToList())
            if (Names(key, c)) counts[key] = 0;
    }

    private static string Key(string path) => path.Replace('\\', '/').Split('/').Last().ToLowerInvariant();

    private static bool Names(string key, string command)
    {
        if (command.Contains(key, StringComparison.Ordinal)) return true;
        var stem = Path.GetFileNameWithoutExtension(key);
        if (stem.Length == 0 || stem == key) return false;
        return Regex.IsMatch(command, @"(^|[^a-z0-9_])" + Regex.Escape(stem) + @"\.[a-z0-9]+");
    }
}
