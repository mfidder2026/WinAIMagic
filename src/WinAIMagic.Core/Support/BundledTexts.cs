namespace WinAIMagic.Core.Support;

/// <summary>Texts that come inside the app: the built-in skills (skills/&lt;name&gt;/SKILL.md) and the help (help/&lt;language&gt;/&lt;NN-id&gt;.md).</summary>
public static class BundledTexts
{
    /// <summary>Every embedded text under this folder, by its path below it ("uiterlijk/SKILL.md", "01-start.md"), sorted.</summary>
    public static IReadOnlyList<(string Path, string Text)> Under(string folder)
    {
        var asm = typeof(BundledTexts).Assembly;
        // MSBuild writes the folder separator of RecursiveDir as it found it
        var prefix = folder.TrimEnd('/') + "/";
        var list = new List<(string, string)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) continue;
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            list.Add((path[prefix.Length..], r.ReadToEnd()));
        }
        return list.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }
}
