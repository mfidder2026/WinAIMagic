using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WinAIMagic.Core.Support;

/// <summary>The languages of the interface. The texts in the code are Dutch; i18n/*.json has the others.</summary>
public enum AppLanguage { Nl, En, De, Fr }

public static class AppLanguages
{
    public static string Code(this AppLanguage l) => l.ToString().ToLowerInvariant();

    public static string Name(this AppLanguage l) => l switch
    {
        AppLanguage.Nl => "Nederlands",
        AppLanguage.En => "English",
        AppLanguage.De => "Deutsch",
        _ => "Français",
    };

    public static global::System.Globalization.CultureInfo Culture(this AppLanguage l) => new(l.Code());

    /// <summary>The Windows display language if the app has it, else English.</summary>
    public static AppLanguage System => Parse(global::System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? AppLanguage.En;

    public static AppLanguage? Parse(string? code) => code?.ToLowerInvariant() switch
    {
        "nl" => AppLanguage.Nl,
        "en" => AppLanguage.En,
        "de" => AppLanguage.De,
        "fr" => AppLanguage.Fr,
        _ => null,
    };

    public static AppLanguage Current => Parse(Settings.Shared.Language) ?? System;
}

/// <summary>A text with its values: the key is the Dutch text with %@ for each value ($"{n} lagen" -> "%@ lagen").</summary>
[InterpolatedStringHandler]
public ref struct Loc
{
    private readonly StringBuilder key;
    private readonly List<string> args;

    public Loc(int literalLength, int formattedCount)
    {
        key = new StringBuilder(literalLength + formattedCount * 2);
        args = new List<string>(formattedCount);
    }

    public void AppendLiteral(string s) => key.Append(s.Replace("%", "%%"));

    public void AppendFormatted<T>(T value, string? format = null)
    {
        key.Append("%@");
        var text = value is IFormattable f ? f.ToString(format, AppLanguages.Current.Culture()) : value?.ToString();
        args.Add(text ?? "");
    }

    public static implicit operator Loc(string text)
    {
        var l = new Loc(text.Length, 0);
        l.key.Append(text);
        return l;
    }

    internal readonly string Key => key.ToString();
    internal readonly List<string> Args => args;
}

/// <summary>using static WinAIMagic.Core.Support.Lang; then L("Kies map…") or L($"{n} lagen").</summary>
public static class Lang
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Table = new(Load);

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var table = new Dictionary<string, Dictionary<string, string>>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var code in new[] { "en", "de", "fr" })
        {
            using var s = asm.GetManifestResourceStream($"i18n.{code}.json");
            if (s != null)
                table[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        return table;
    }

    /// <summary>Every key of a language's table (tests: each language has each key).</summary>
    public static IReadOnlyDictionary<string, string> TableFor(string code) =>
        Table.Value.TryGetValue(code, out var t) ? t : new Dictionary<string, string>();

    /// <summary>The text in the chosen language. A translation may reorder the values with %1$@, %2$@.</summary>
    public static string L(Loc text) => Expand(Template(text.Key), text.Args);

    /// <summary>A text only known at run time (a model's summary from the catalog, a stored default title).</summary>
    public static string LKey(string key)
    {
        var lang = AppLanguages.Current;
        if (lang == AppLanguage.Nl) return key;
        return TableFor(lang.Code()).TryGetValue(key.Replace("%", "%%"), out var t) ? t.Replace("%%", "%") : key;
    }

    private static string Template(string key)
    {
        var lang = AppLanguages.Current;
        if (lang == AppLanguage.Nl) return key;
        return TableFor(lang.Code()).TryGetValue(key, out var t) ? t : key;
    }

    internal static string Expand(string template, IReadOnlyList<string> args)
    {
        var o = new StringBuilder(template.Length + 16);
        int next = 0;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '%' || i + 1 >= template.Length) { o.Append(c); continue; }
            char d = template[i + 1];
            if (d == '%') { o.Append('%'); i++; }
            else if (d == '@') { if (next < args.Count) o.Append(args[next]); next++; i++; }
            else if (char.IsDigit(d) && i + 3 < template.Length && template[i + 2] == '$' && template[i + 3] == '@')
            {
                int n = d - '0';
                if (n - 1 < args.Count) o.Append(args[n - 1]);
                i += 3;
            }
            else o.Append(c);
        }
        return o.ToString();
    }
}
