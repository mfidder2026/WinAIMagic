using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>What the file tools report to the model.</summary>
public sealed class ToolError(string message) : Exception(message)
{
    public static ToolError NoWorkspace() => new("No workspace folder is set. Ask the user to choose one (Werkmap in the chat).");
    public static ToolError Outside(string p) => new($"{p} is outside the workspace; this tool only works inside it. Elsewhere on this PC, use run_command (Get-ChildItem, Get-Content).");
    public static ToolError NotFound(string p) => new($"{p} does not exist.");
    public static ToolError NotUnique(int n) => new($"old_text occurs {n} times; it must occur exactly once. Include more surrounding text.");
    public static ToolError TooBig(string p) => new($"{p} is larger than 2 MB; read it with offset/limit or tail.");
    public static ToolError BadBase64() => new("content is not valid base64.");
    public static ToolError BadName(string p) => new($"{p} is not a usable file name on Windows: alternate data streams (name:stream) and device names (CON, NUL, COM1, ...) are not allowed.");
}

/// <summary>Tools that let the model work in one folder the user chose: list, read, write and edit files, and run commands.
/// Every path is resolved inside the workspace; anything that points outside it is refused.</summary>
public static class WorkspaceTools
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string> { "list_dir", "read_file", "write_file", "edit_file", "run_command" };

    // the kinds of action the user gives rights for (allow, ask, deny)
    public static string Letter(this WorkspaceRight r) => r == WorkspaceRight.Read ? L("L") : r == WorkspaceRight.Write ? L("S") : L("U");
    public static string Title(this WorkspaceRight r) => r == WorkspaceRight.Read ? L("Lezen") : r == WorkspaceRight.Write ? L("Schrijven") : L("Uitvoeren");
    public static string Detail(this WorkspaceRight r) => r switch
    {
        WorkspaceRight.Read => L("Mappen bekijken en bestanden lezen"),
        WorkspaceRight.Write => L("Bestanden maken en aanpassen"),
        _ => L("Commando's, scripts en apps starten"),
    };

    public static WorkspaceRight RightFor(string name) => name switch
    {
        "write_file" or "edit_file" => WorkspaceRight.Write,
        "run_command" => WorkspaceRight.Run,
        _ => WorkspaceRight.Read,
    };

    public static JsonArray Definitions =>
    [
        Function("list_dir", "List the files and folders in a folder of the workspace, with sizes. Use \".\" for the workspace itself.",
            new JsonObject { ["path"] = Prop("string", "Folder, relative to the workspace") }, "path"),
        Function("read_file", "Read a text file from the workspace. For long files or logs, use tail to read only the last lines, or offset/limit for a range of lines.",
            new JsonObject
            {
                ["path"] = Prop("string", "File, relative to the workspace"),
                ["offset"] = Prop("integer", "First line to read (1-based), optional"),
                ["limit"] = Prop("integer", "Number of lines, optional"),
                ["tail"] = Prop("integer", "Read only the last N lines (for log files), optional"),
            }, "path"),
        Function("write_file", "Create or overwrite a file in the workspace. Folders are created as needed. For binary files (for example a C64 .prg, an image or a zip), pass the bytes as base64 and set encoding to \"base64\".",
            new JsonObject
            {
                ["path"] = Prop("string", "File, relative to the workspace"),
                ["content"] = Prop("string", "The full file content (text, or base64 when encoding is base64)"),
                ["encoding"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("text", "base64"), ["description"] = "text (default) or base64" },
            }, "path", "content"),
        Function("edit_file", "Replace one exact piece of text in a file of the workspace. old_text must occur exactly once.",
            new JsonObject { ["path"] = Prop("string"), ["old_text"] = Prop("string"), ["new_text"] = Prop("string") }, "path", "old_text", "new_text"),
        Function("run_command", "Run a shell command (PowerShell) in the workspace folder and return its output and exit code. Use it to run scripts, build or test code, start programs (Start-Process file.ext), or inspect the system. Commands that never end (servers) are stopped after the timeout.",
            new JsonObject
            {
                ["command"] = Prop("string", "The command line"),
                ["timeout"] = Prop("integer", "Seconds before the command is stopped (default 120, max 1800)"),
            }, "command"),
    ];

    private static JsonObject Prop(string type, string? description = null)
    {
        var o = new JsonObject { ["type"] = type };
        if (description != null) o["description"] = description;
        return o;
    }

    private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => (JsonNode?)r)]),
            },
        },
    };

    // MARK: - paths

    /// <summary>The conversation's own folder while the chat client runs its tools; falls back to the last chosen folder.
    /// Each run carries its own (an AsyncLocal, the Mac's TaskLocal), so two runs at once never use each other's folder.</summary>
    public static string? Current { get => current.Value; set => current.Value = value; }
    private static readonly AsyncLocal<string?> current = new();

    public static string? Workspace
    {
        get
        {
            var p = Current ?? Settings.Shared.Workspace;
            return string.IsNullOrEmpty(p) ? null : FinalOfExisting(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))) ?? p;
        }
    }

    /// <summary>A path inside the workspace, or an error. Links are resolved first, so a link cannot lead outside; a path the
    /// file system would read differently than it looks (other drive, device, stream, short name) ends up the same check.</summary>
    public static string Resolve(string path)
    {
        var root = Workspace ?? throw ToolError.NoWorkspace();
        if (path.Contains('\0')) throw ToolError.Outside(path);
        var p = path.Replace('/', '\\');
        if (p == "~" || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];

        // \\?\, \\.\ and \??\ pass by every rule of the file system
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
            throw ToolError.Outside(path);
        bool hasDrive = p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':';
        // C:file leans on the current folder of that drive
        if (hasDrive && (p.Length == 2 || p[2] != '\\')) throw ToolError.Outside(path);
        // UNC: only look at it when it could be inside: touching \\server already sends this user's credentials there
        if (p.StartsWith(@"\\", StringComparison.Ordinal) && !IsInside(Path.GetFullPath(p), root)) throw ToolError.Outside(path);

        foreach (var part in (hasDrive ? p[3..] : p).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains(':') || IsDeviceName(part)) throw ToolError.BadName(path);
            // ".. " and "...": Windows reads these otherwise than they look
            if (part.Trim(' ', '.').Length == 0 && part is not ("." or "..")) throw ToolError.Outside(path);
        }

        var resolved = FinalOfExisting(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Length == 0 ? "." : p, root))) ?? throw ToolError.Outside(path);
        if (!IsInside(resolved, root)) throw ToolError.Outside(path);
        return resolved;
    }

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>A device name also counts with an extension and with trailing spaces or dots: nul.txt, CON .</summary>
    private static bool IsDeviceName(string part)
    {
        int dot = part.IndexOf('.');
        return DeviceNames.Contains((dot >= 0 ? part[..dot] : part).TrimEnd(' ', '.'));
    }

    private static bool IsInside(string path, string root)
    {
        var folder = root.EndsWith('\\') ? root : root + "\\";
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The real path of what exists (links, junctions and short names resolved) plus the part still to be made;
    /// a new file's parent decides. Null when a link in it leads nowhere: writing through it could end up anywhere.</summary>
    private static string? FinalOfExisting(string full)
    {
        var missing = new Stack<string>();
        var cur = full;
        while (GetFileAttributes(cur) == InvalidFileAttributes)
        {
            var parent = Path.GetDirectoryName(cur);
            if (parent == null) return full;   // no such drive
            missing.Push(Path.GetFileName(cur));
            cur = parent;
        }
        var final = FinalPath(cur);
        if (final == null) return null;
        foreach (var name in missing) final = Path.Combine(final, name);
        return final;
    }

    private static string? FinalPath(string path)
    {
        using var handle = CreateFile(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer.Capacity = (int)length + 1;
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0) return null;
        var s = buffer.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    public static string Relative(string path)
    {
        var root = Workspace;
        if (root == null) return path;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return ".";
        var folder = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path[folder.Length..] : path;
    }

    // MARK: - run

    public static JsonObject Args(ToolCall call)
    {
        try { return JsonNode.Parse(call.Arguments) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    internal static string? Str(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static int? Int(JsonObject a, string key)
    {
        if (a[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        var d = v.GetValue<double>();
        return d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? (int)d : null;
    }

    public static string Label(ToolCall call)
    {
        var a = Args(call);
        var path = Str(a, "path") ?? "";
        return call.Name switch
        {
            "list_dir" => L($"Map bekijken: {path}"),
            "read_file" => L($"Lezen: {path}"),
            "write_file" => L($"Schrijven: {path}"),
            "edit_file" => L($"Aanpassen: {path}"),
            "run_command" => L($"Uitvoeren: {Str(a, "command") ?? ""}"),
            _ => call.Name,
        };
    }

    /// <summary>What the user is asked to approve, in plain words.</summary>
    public static (string Title, string Detail) ApprovalText(ToolCall call)
    {
        var a = Args(call);
        switch (call.Name)
        {
            case "write_file":
                var content = Str(a, "content") ?? "";
                var binary = Str(a, "encoding") == "base64";
                return (L($"Bestand schrijven: {Str(a, "path") ?? ""}"),
                    binary ? L($"Binair bestand, {DecodeBase64(content)?.Length ?? 0} bytes") : TextLimit.Prefix(content, 1500));
            case "edit_file":
                return (L($"Bestand aanpassen: {Str(a, "path") ?? ""}"),
                    $"− {TextLimit.Prefix(Str(a, "old_text") ?? "", 600)}\n+ {TextLimit.Prefix(Str(a, "new_text") ?? "", 600)}");
            case "run_command":
                return (L("Commando uitvoeren in de werkmap"), Str(a, "command") ?? "");
            default:
                return (call.Name, call.Arguments);
        }
    }

    public static async Task<string> RunAsync(ToolCall call, CancellationToken ct = default)
    {
        var a = Args(call);
        try
        {
            switch (call.Name)
            {
                case "list_dir": return await Task.Run(() => ListDir(Str(a, "path") ?? "."), ct).ConfigureAwait(false);
                case "read_file": return await Task.Run(() => ReadFile(Str(a, "path") ?? "", Int(a, "offset"), Int(a, "limit"), Int(a, "tail")), ct).ConfigureAwait(false);
                case "write_file": return await Task.Run(() => WriteFile(Str(a, "path") ?? "", Str(a, "content") ?? "", Str(a, "encoding") == "base64"), ct).ConfigureAwait(false);
                case "edit_file": return await Task.Run(() => EditFile(Str(a, "path") ?? "", Str(a, "old_text") ?? "", Str(a, "new_text") ?? ""), ct).ConfigureAwait(false);
                case "run_command": return await RunCommand(Str(a, "command") ?? "", Math.Min(Math.Max(Int(a, "timeout") ?? 120, 1), 1800), ct).ConfigureAwait(false);
                default: return $"Unknown tool {call.Name}";
            }
        }
        catch (OperationCanceledException) { return "Error: stopped by the user."; }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    public static string ListDir(string path)
    {
        var dir = Resolve(path);
        List<FileSystemInfo> items;
        try { items = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos()]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw ToolError.NotFound(path); }
        var lines = items.OrderBy(i => i.Name, StringComparer.Ordinal).Take(500).Select(i =>
        {
            var date = i.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            return i is DirectoryInfo ? $"{i.Name}/  {date}" : $"{i.Name}  {((FileInfo)i).Length} bytes  {date}";
        }).ToList();
        return lines.Count == 0 ? $"(empty folder) {Relative(dir)}" : $"{Relative(dir)}:\n" + string.Join("\n", lines);
    }

    public static string ReadFile(string path, int? offset, int? limit, int? tail)
    {
        var file = Resolve(path);
        if (!File.Exists(file) && !Directory.Exists(file)) throw ToolError.NotFound(path);
        long size = new FileInfo(file).Length;
        byte[] data;
        if (size > 2_000_000)
        {
            if (tail == null && offset == null) throw ToolError.TooBig(path);
            // big file: read the end for tail, else the start
            using var fh = File.OpenRead(file);
            if (tail != null) fh.Seek(Math.Max(0, size - 2_000_000), SeekOrigin.Begin);
            data = new byte[2_000_000];
            data = data[..fh.ReadAtLeast(data, data.Length, false)];
        }
        else data = File.ReadAllBytes(file);

        // Windows PowerShell writes UTF-16 with a byte order mark: that is text, though every other byte is 0
        var text = DecodeBom(data);
        if (text == null)
        {
            if (Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8000)) >= 0)
                return $"{path} is a binary file ({size} bytes). Its first bytes as hex: " + string.Join(" ", data.Take(64).Select(b => b.ToString("x2")));
            text = AtomicFile.Utf8.GetString(data);
        }
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        int first = 1;
        if (tail is { } t)
        {
            t = Math.Max(0, t);
            first = Math.Max(1, lines.Count - t + 1);
            lines = lines.GetRange(Math.Max(0, lines.Count - t), Math.Min(t, lines.Count));
        }
        else if (offset != null || limit != null)
        {
            first = Math.Max(1, offset ?? 1);
            lines = [.. lines.Skip(first - 1).Take(Math.Max(0, limit ?? 400))];
        }
        var o = string.Join("\n", lines.Select((l, i) => $"{first + i}\t{l}"));
        if (o.Length > 40_000) o = TextLimit.Prefix(o, 40_000) + "\n… (truncated; use offset/limit)";
        return o;
    }

    private static string? DecodeBom(byte[] d)
    {
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return Encoding.Unicode.GetString(d, 2, d.Length - 2);
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return Encoding.UTF8.GetString(d, 3, d.Length - 3);
        return null;
    }

    /// <summary>Unknown characters are ignored, as on the Mac: models wrap base64 over lines.</summary>
    private static byte[]? DecodeBase64(string content)
    {
        var clean = new string(content.Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=').ToArray());
        try { return Convert.FromBase64String(clean); }
        catch (FormatException) { return null; }
    }

    public static string WriteFile(string path, string content, bool base64)
    {
        var file = Resolve(path);
        var data = base64 ? DecodeBase64(content) ?? throw ToolError.BadBase64() : AtomicFile.Utf8.GetBytes(content);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.Write(file, data);
        return $"Wrote {data.Length} bytes to {Relative(file)}";
    }

    public static string EditFile(string path, string old, string @new)
    {
        var file = Resolve(path);
        string text;
        try { text = AtomicFile.StrictUtf8.GetString(File.ReadAllBytes(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { throw ToolError.NotFound(path); }
        int count = Occurrences(text, old);
        // read_file shows lines without their \r, so a model quotes a Windows file with \n
        if (count == 0 && old.Contains('\n') && !old.Contains('\r') && text.Contains("\r\n", StringComparison.Ordinal))
        {
            old = old.Replace("\n", "\r\n");
            @new = @new.Replace("\r\n", "\n").Replace("\n", "\r\n");
            count = Occurrences(text, old);
        }
        if (count != 1) throw ToolError.NotUnique(count);
        AtomicFile.Write(file, text.Replace(old, @new, StringComparison.Ordinal));
        return $"Edited {Relative(file)}";
    }

    private static int Occurrences(string text, string part)
    {
        if (part.Length == 0) return 0;
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Runs in the workspace; output is capped so it fits the context. Not sandboxed, like on the Mac.</summary>
    public static async Task<string> RunCommand(string command, int timeout, CancellationToken ct = default)
    {
        var root = Workspace ?? throw ToolError.NoWorkspace();
        return await CommandRunner.RunAsync(command, root, timeout, ct).ConfigureAwait(false);
    }

    private const uint InvalidFileAttributes = 0xFFFFFFFF, FileShareAll = 7, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFileAttributesW")]
    private static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder buffer, uint size, uint flags);
}
