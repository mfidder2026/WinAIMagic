using System.Runtime.InteropServices;
using System.Text;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>What the file panel beside the chat shows of the working folder: the entries of one folder, and the text of one
/// file. Only reads (the owner asked for a way to look at the source the model writes, a developer view).</summary>
public static class WorkspaceFiles
{
    public sealed record Entry(string Path, string Name, bool IsFolder, long Size);

    /// <summary>At most this many entries of one folder; the rest is counted.</summary>
    public const int MaxEntries = 500;
    /// <summary>A longer file shows its first part.</summary>
    public const int MaxBytes = 1_000_000;

    /// <summary>Folders first, then files, each in Explorer's order (file2 before file10). Hidden and system entries stay
    /// out, as in Explorer, and so does .git. `more` is how many did not fit.</summary>
    public static (IReadOnlyList<Entry> Entries, int More) List(string folder)
    {
        var found = new List<Entry>();
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
            {
                if (info.Name == ".git") continue;
                bool isFolder = info is DirectoryInfo;
                found.Add(new Entry(info.FullName, info.Name, isFolder, isFolder ? 0 : ((FileInfo)info).Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        found.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : Logical(a.Name, b.Name));
        return found.Count > MaxEntries ? (found.Take(MaxEntries).ToList(), found.Count - MaxEntries) : (found, 0);
    }

    public sealed record Content(string? Text, string? Note);

    /// <summary>The text of a file, or why there is none. A file that holds a 0 byte in its first 8 kB is not text.</summary>
    public static Content Read(string path)
    {
        byte[] head;
        long length;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = f.Length;
            head = new byte[(int)Math.Min(length, MaxBytes)];
            int read = 0;
            while (read < head.Length)
            {
                int n = f.Read(head, read, head.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < head.Length) Array.Resize(ref head, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new Content(null, L($"Kan {Path.GetFileName(path)} niet lezen: {e.Message}"));
        }
        if (head.AsSpan(0, Math.Min(head.Length, 8192)).Contains((byte)0))
            return new Content(null, L($"Geen tekstbestand ({Size(length)})."));
        var text = Decode(head);
        if (length > head.Length)
        {
            // never end in the middle of a line
            int cut = text.LastIndexOf('\n');
            if (cut > 0) text = text[..cut];
            return new Content(text, L($"De eerste {Size(head.Length)} van {Size(length)}."));
        }
        return new Content(text, null);
    }

    /// <summary>UTF-8 (with or without its mark) or UTF-16 with its mark; a file that is not valid UTF-8 is read as Windows-1252,
    /// the code page of older Windows text files.</summary>
    private static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(b, skip, b.Length - skip); }
        catch (DecoderFallbackException)
        {
            // a cut through a character at the end is no reason to call the file something else
            if (b.Length >= MaxBytes)
                return new UTF8Encoding(false, false).GetString(b, skip, b.Length - skip).TrimEnd('�');
            return Latin.GetString(b);
        }
    }

    private static readonly Encoding Latin = CodePagesEncoding();

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} kB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string a, string b);

    private static int Logical(string a, string b) => StrCmpLogicalW(a, b);
}
