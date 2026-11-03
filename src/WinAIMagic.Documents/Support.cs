global using static WinAIMagic.Documents.Loc;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WinAIMagic.Documents;

/// A range in UTF-16 units, as Foundation counts them.
public readonly record struct NSRange(int Location, int Length);

/// The texts are Dutch keys with %@ for each value ("%@ lagen"); a literal % in a key is %%. The app plugs its L() in here.
public static class Loc
{
    public static Func<string, string> Localize { get; set; } = key => key;

    /// The text in the chosen language. A translation may reorder the values with %1$@, %2$@.
    public static string L(string key, params object[] args)
    {
        var template = Localize(key);
        var values = args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? "").ToArray();
        var output = new StringBuilder();
        int next = 0, i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '%') { output.Append(c); i++; continue; }
            var rest = template.AsSpan(i + 1);
            if (rest.StartsWith("%", StringComparison.Ordinal))
            {
                output.Append('%');
                i += 2;
            }
            else if (rest.StartsWith("@", StringComparison.Ordinal))
            {
                output.Append(next < values.Length ? values[next] : "");
                next++;
                i += 2;
            }
            else if (rest.Length >= 3 && char.IsAsciiDigit(rest[0]) && rest[1..].StartsWith("$@", StringComparison.Ordinal))
            {
                int n = rest[0] - '0';
                output.Append(n >= 1 && n - 1 < values.Length ? values[n - 1] : "");
                i += 4;
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }
}

/// What Swift's String and CharacterSet do differently from .NET's string.
public static class Str
{
    /// CharacterSet.whitespaces, as CoreFoundation defines it.
    public static bool IsWhitespaces(char c) =>
        (int)c is 0x20 or 0x09 or 0xA0 or 0x1680 or 0x202F or 0x205F or 0x3000 or (>= 0x2000 and <= 0x200B);

    /// CharacterSet.newlines.
    public static bool IsNewlines(char c) => (int)c is (>= 0x0A and <= 0x0D) or 0x85 or 0x2028 or 0x2029;

    private static string Trim(string s, Func<char, bool> set)
    {
        int a = 0, b = s.Length;
        while (a < b && set(s[a])) a++;
        while (b > a && set(s[b - 1])) b--;
        return a == 0 && b == s.Length ? s : s[a..b];
    }

    /// trimmingCharacters(in: .whitespaces)
    public static string TrimWS(this string s) => Trim(s, IsWhitespaces);
    /// trimmingCharacters(in: .newlines)
    public static string TrimNL(this string s) => Trim(s, IsNewlines);
    /// trimmingCharacters(in: .whitespacesAndNewlines)
    public static string TrimWSNL(this string s) => Trim(s, c => IsWhitespaces(c) || IsNewlines(c));

    public static bool HasPrefix(this string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);
    public static bool HasSuffix(this string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal);

    public static string Lower(this string s) => s.Replace(((char)0x130).ToString(), "i" + (char)0x307).ToLowerInvariant();

    /// Array(s): what Swift calls Characters.
    public static string[] Graphemes(this string s)
    {
        var output = new List<string>(s.Length);
        for (int i = 0; i < s.Length;)
        {
            int n = StringInfo.GetNextTextElementLength(s.AsSpan(i));
            output.Add(s.Substring(i, n));
            i += n;
        }
        return output.ToArray();
    }

    /// s.count
    public static int GraphemeCount(this string s)
    {
        int count = 0;
        for (int i = 0; i < s.Length; count++) i += StringInfo.GetNextTextElementLength(s.AsSpan(i));
        return count;
    }

    /// String(s.prefix(n)): never splits a surrogate pair or a character with its marks.
    public static string Prefix(this string s, int n)
    {
        int i = 0;
        for (int count = 0; i < s.Length && count < n; count++) i += StringInfo.GetNextTextElementLength(s.AsSpan(i));
        return s[..i];
    }

    private static Rune FirstRune(string character)
    {
        Rune.DecodeFromUtf16(character, out var rune, out _);
        return rune;
    }

    // the properties of a Swift Character are those of its first scalar
    public static bool CharIsWhitespace(string c) => Rune.IsWhiteSpace(FirstRune(c));
    public static bool CharIsPunctuation(string c) => Rune.IsPunctuation(FirstRune(c));
    public static bool CharIsSymbol(string c) => Rune.IsSymbol(FirstRune(c));
    public static bool CharIsNumber(string c) => Rune.IsNumber(FirstRune(c));
    public static bool CharIsLetter(string c)
    {
        var rune = FirstRune(c);
        return Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;
    }

    /// Int(s): only an optional sign and digits.
    public static int? ToInt(string? s) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static long? ToLong(string? s) =>
        long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// localizedStandardCompare: "Rapport 2" before "Rapport 10".
    public static int LocalizedStandardCompare(string a, string b) => StrCmpLogicalW(a, b);
}

/// What NSString does with a path, always with /: the names in a package are no Windows paths.
public static class NSPath
{
    public static string LastPathComponent(string path)
    {
        var p = path.TrimEnd('/');
        if (p.Length == 0) return path.Length == 0 ? "" : "/";
        return p[(p.LastIndexOf('/') + 1)..];
    }

    public static string DeletingLastPathComponent(string path)
    {
        var p = path.TrimEnd('/');
        if (p.Length == 0) return path.Length == 0 ? "" : "/";
        int slash = p.LastIndexOf('/');
        if (slash < 0) return "";
        return slash == 0 ? "/" : p[..slash].TrimEnd('/');
    }

    public static string AppendingPathComponent(string path, string component) =>
        path.Length == 0 ? component : path.TrimEnd('/') + "/" + component;

    public static string PathExtension(string path)
    {
        var last = LastPathComponent(path);
        int dot = last.LastIndexOf('.');
        return dot <= 0 ? "" : last[(dot + 1)..];
    }

    public static string DeletingPathExtension(string path)
    {
        var extension = PathExtension(path);
        return extension.Length == 0 ? path : path.TrimEnd('/')[..^(extension.Length + 1)];
    }
}
