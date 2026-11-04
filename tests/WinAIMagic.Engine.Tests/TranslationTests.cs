using System.Text;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>Brief §4: settings.json atomically written, API keys encrypted, and each setting there once.</summary>
public class SettingsFileTests : EngineTest
{
    [Fact]
    public void KeysAreEncryptedAndNothingIsStoredTwice()
    {
        Settings.Shared.ApiKey = "geheime-sleutel-123";
        Settings.Shared.BraveKey = "brave-sleutel-456";
        var text = File.ReadAllText(Paths.SettingsFile);
        Assert.DoesNotContain("geheime-sleutel-123", text);
        Assert.DoesNotContain("brave-sleutel-456", text);
        Assert.DoesNotContain("\"Shared\"", text);
        Assert.Equal(1, Regex.Matches(text, "\"Port\"").Count);
        var again = Settings.Load();
        Assert.Equal("geheime-sleutel-123", again.ApiKey);
        Assert.Equal("brave-sleutel-456", again.BraveKey);
    }
}

/// <summary>Every text in the code has its translation in every language, with the same values (brief §10).</summary>
public class TranslationTests : EngineTest
{
    private static readonly Regex Literal = new(@"\bL\((\$?)""((?:[^""\\]|\\.)*)""");

    private static string KeyOf(bool interpolated, string body)
    {
        var text = body.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        if (!interpolated) return text;
        var o = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { o.Append('{'); i++; }
            else if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { o.Append('}'); i++; }
            else if (c == '{') { if (depth++ == 0) o.Append("%@"); }
            else if (c == '}') depth--;
            else if (depth == 0) o.Append(c == '%' ? "%%" : c.ToString());
        }
        return o.ToString();
    }

    private static HashSet<string> KeysInCode()
    {
        var keys = new HashSet<string>();
        char sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            foreach (Match m in Literal.Matches(File.ReadAllText(file)))
                keys.Add(KeyOf(m.Groups[1].Value == "$", m.Groups[2].Value));
        }
        return keys;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    public void EveryLanguageHasEveryKey(string code)
    {
        var table = Lang.TableFor(code);
        var missing = KeysInCode().Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    public void TranslationsKeepTheirValues(string code)
    {
        static int Values(string s) => Regex.Matches(s, @"%(\d\$)?@").Count;
        var wrong = Lang.TableFor(code).Where(p => Values(p.Key) != Values(p.Value)).Select(p => p.Key).ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
