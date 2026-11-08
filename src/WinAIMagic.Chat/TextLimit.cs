using System.Text;

namespace WinAIMagic.Chat;

/// <summary>Limits count UTF-16 units here; a surrogate pair never goes in half.</summary>
internal static class TextLimit
{
    public static string Prefix(string s, int n)
    {
        if (s.Length <= n) return s;
        if (n <= 0) return "";
        return s[..(char.IsHighSurrogate(s[n - 1]) ? n - 1 : n)];
    }

    public static string Suffix(string s, int n)
    {
        if (s.Length <= n) return s;
        if (n <= 0) return "";
        int i = s.Length - n;
        return s[(char.IsLowSurrogate(s[i]) ? i + 1 : i)..];
    }
}

/// <summary>How the chat shows a very long message: a monologue of 100,000 characters made the Mac's chat go blank, and is slow
/// to lay out here too.</summary>
public static class LongText
{
    /// <summary>A very long text as its start and its end, cut at line ends; null when it is short enough to show whole.</summary>
    public static (string Head, int Hidden, string Tail)? Folded(string text, int above = 12_000, int keep = 4000)
    {
        if (text.Length <= above) return null;
        var head = TextLimit.Prefix(text, keep);
        if (head.LastIndexOf('\n') is var nl and > 0) head = head[..nl];
        var tail = TextLimit.Suffix(text, keep);
        if (tail.IndexOf('\n') is var first and >= 0) tail = tail[(first + 1)..];
        return (head, text.Length - head.Length - tail.Length, tail);
    }

    /// <summary>A very long thinking text shows its end.</summary>
    public static string ThinkingEnd(string text) => text.Length > 12_000 ? "…\n" + TextLimit.Suffix(text, 8000) : text;
}

/// <summary>Write beside the file, then rename. Antivirus can hold a file for a moment: then try again.</summary>
internal static class AtomicFile
{
    public static readonly UTF8Encoding Utf8 = new(false);
    /// <summary>Fails on invalid UTF-8, like String(contentsOf:encoding:) on the Mac.</summary>
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Write(string path, byte[] data)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(tmp, data);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                    throw;
                }
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public static void Write(string path, string text) => Write(path, Utf8.GetBytes(text));

    /// <summary>The file as UTF-8 text without its byte order mark, or null when it cannot be read.</summary>
    public static string? ReadUtf8(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return StrictUtf8.GetString(bytes, bom, bytes.Length - bom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { return null; }
    }
}
