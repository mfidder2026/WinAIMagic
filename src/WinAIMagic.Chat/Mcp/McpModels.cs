using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WinAIMagic.Chat;

/// <summary>A tool a connector (MCP server) offers.</summary>
public sealed record McpTool(string Name, string? Title, string Description, string Schema, bool? ReadOnly = null, bool? Destructive = null)
{
    public static McpTool Make(string name, string description, JsonObject? schema = null, string? title = null, bool? readOnly = null, bool? destructive = null) =>
        new(name, title, description, (schema ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }).ToJsonString(), readOnly, destructive);

    public static McpTool? FromJson(JsonObject json)
    {
        if (json["name"] is not JsonValue n || !n.TryGetValue<string>(out var name)) return null;
        var annotations = json["annotations"] as JsonObject;
        return Make(name, Str(json, "description") ?? "", json["inputSchema"] as JsonObject is { } s ? (JsonObject)s.DeepClone() : null,
            Str(json, "title") ?? (annotations == null ? null : Str(annotations, "title")),
            annotations == null ? null : Bool(annotations, "readOnlyHint"), annotations == null ? null : Bool(annotations, "destructiveHint"));
    }

    public JsonObject SchemaObject => JsonNode.Parse(Schema) as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

    internal static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    internal static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}

/// <summary>What went wrong with a connector, said so a user can act on it.</summary>
public sealed class McpException(string message, int? code = null, string? challenge = null) : Exception(message)
{
    /// <summary>The JSON-RPC or HTTP error code, when the server gave one.</summary>
    public int? Code { get; } = code;
    /// <summary>The WWW-Authenticate header of a 401.</summary>
    public string? Challenge { get; } = challenge;
    public bool Unauthorized => Challenge != null || Code == 401;

    public static McpException NotRunning(string why) => new($"The connector is not running: {why}");
    public static McpException Timeout(string method) => new($"The connector did not answer {method} in time.");
    public static McpException Server(int code, string message) => new($"The connector answered with an error ({code}): {message}", code);
    public static McpException BadResponse() => new("The connector sent something that is not a valid answer.");
    public static McpException Login(string? challenge) => new("The connector asks to log in.", 401, challenge ?? "");
}

/// <summary>One connector as it stands in mcp.json: the format of Claude Desktop (command/args/env for a program on this PC,
/// url/headers for an address on the internet), plus what WinAIMagic adds. A value "keychain:&lt;name&gt;" in env or headers is
/// read from the Windows Credential Manager when the connector starts, so tokens do not stand in the file (the word is the
/// Mac's, so one mcp.json reads the same on both).</summary>
public sealed record McpServerConfig
{
    [JsonIgnore] public string Name { get; init; } = "";
    public string? Command { get; init; }
    public List<string>? Args { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public string? Url { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>Off connectors are not offered to the model.</summary>
    public bool? Enabled { get; init; }
    /// <summary>"allow", "ask" or "deny" for tools that change something; read-only tools are allowed unless "deny".</summary>
    public string? Right { get; init; }
    /// <summary>Its tools are always in the request, without use_connector.</summary>
    public bool? AlwaysLoaded { get; init; }
    /// <summary>One line for the model: what this connector is for.</summary>
    public string? Description { get; init; }
    /// <summary>A connector built into the app instead of a program or address.</summary>
    public string? Builtin { get; init; }
    /// <summary>Log in with OAuth when the server asks for it.</summary>
    public bool? Oauth { get; init; }

    [JsonIgnore] public bool IsEnabled => Enabled ?? true;
    [JsonIgnore] public bool IsRemote => Url != null;
    [JsonIgnore] public string RightValue => Right ?? "ask";

    public bool Same(McpServerConfig o) =>
        Name == o.Name && Command == o.Command && Url == o.Url && Enabled == o.Enabled && Right == o.Right && AlwaysLoaded == o.AlwaysLoaded
        && Description == o.Description && Builtin == o.Builtin && Oauth == o.Oauth
        && (Args ?? []).SequenceEqual(o.Args ?? []) && Dict(Env) == Dict(o.Env) && Dict(Headers) == Dict(o.Headers);

    private static string Dict(Dictionary<string, string>? d) => d == null ? "" : string.Join("\n", d.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
}

/// <summary>mcp.json: {"mcpServers": {name: config}}.</summary>
public static class McpConfigFile
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static List<McpServerConfig> Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["mcpServers"] is not JsonObject servers) return [];
            var list = new List<McpServerConfig>();
            foreach (var (name, node) in servers)
            {
                if (node is not JsonObject o) continue;
                var c = o.Deserialize<McpServerConfig>(Read);
                if (c != null) list.Add(c with { Name = name });
            }
            return [.. list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException) { return []; }
    }

    public static string Encode(IEnumerable<McpServerConfig> servers)
    {
        var o = new JsonObject();
        foreach (var c in servers.OrderBy(c => c.Name, StringComparer.Ordinal))
            if (!o.ContainsKey(c.Name)) o[c.Name] = JsonSerializer.SerializeToNode(c, Write);
        return new JsonObject { ["mcpServers"] = o }.ToJsonString(Write);
    }
}

/// <summary>A connector and one of its tools, behind a name the model used.</summary>
public sealed record McpRoute(McpServerConfig Server, McpTool Tool);

/// <summary>Secrets for connectors (tokens, OAuth logins) in the Windows Credential Manager, under "WinAIMagic connectors/…".
/// A value longer than one credential holds is split over numbered parts. Tests replace Store with memory.</summary>
public static class McpSecrets
{
    public const string Service = "WinAIMagic connectors";

    public interface IStore
    {
        string? Get(string account);
        void Set(string account, string value);
        void Delete(string account);
    }

    /// <summary>The Windows Credential Manager.</summary>
    public static IStore Windows { get; } = new CredentialManager();
    public static IStore Store { get; set; } = Windows;

    public static string? Get(string account) => Store.Get(account);
    public static void Set(string account, string value) => Store.Set(account, value);
    public static void Delete(string account) => Store.Delete(account);

    /// <summary>"keychain:&lt;name&gt;" becomes the stored value; anything else stays as it is.</summary>
    public static string Resolve(string value) =>
        value.StartsWith("keychain:", StringComparison.Ordinal) ? Get(value[9..]) ?? "" : value;

    public sealed class Memory : IStore
    {
        private readonly Dictionary<string, string> items = [];
        public string? Get(string account) { lock (items) return items.GetValueOrDefault(account); }
        public void Set(string account, string value) { lock (items) items[account] = value; }
        public void Delete(string account) { lock (items) items.Remove(account); }
    }

    private sealed class CredentialManager : IStore
    {
        private const int Part = 2400;   // a generic credential holds at most 2,560 bytes

        private static string Target(string account, int part) => $"{Service}/{account}" + (part == 0 ? "" : "#" + part);

        public string? Get(string account)
        {
            var sb = new StringBuilder();
            for (int part = 0; part < 64; part++)
            {
                if (Read(Target(account, part)) is not { } chunk) return part == 0 ? null : sb.ToString();
                sb.Append(chunk);
                if (chunk.Length * 2 < Part) break;
            }
            return sb.ToString();
        }

        public void Set(string account, string value)
        {
            Delete(account);
            int chars = Part / 2;
            int part = 0;
            for (int i = 0; i == 0 || i < value.Length; i += chars, part++)
                WriteOne(Target(account, part), value.Substring(i, Math.Min(chars, value.Length - i)));
            // a value of exactly a whole number of parts ends with an empty one, so reading knows where it stops
            if (value.Length > 0 && value.Length % chars == 0) WriteOne(Target(account, part), "");
        }

        public void Delete(string account)
        {
            for (int part = 0; part < 64; part++)
                if (!CredDelete(Target(account, part), 1, 0) && part > 0) break;
        }

        private static string? Read(string target)
        {
            if (!CredRead(target, 1, 0, out var p)) return null;
            try
            {
                var c = Marshal.PtrToStructure<Credential>(p);
                return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
            }
            finally { CredFree(p); }
        }

        private static void WriteOne(string target, string value)
        {
            var blob = Marshal.StringToCoTaskMemUni(value);
            try
            {
                var c = new Credential
                {
                    Type = 1,   // CRED_TYPE_GENERIC
                    TargetName = target,
                    CredentialBlobSize = (uint)(value.Length * 2),
                    CredentialBlob = blob,
                    Persist = 2,   // CRED_PERSIST_LOCAL_MACHINE: this user, this PC
                    UserName = Environment.UserName,
                };
                CredWrite(ref c, 0);
            }
            finally { Marshal.FreeCoTaskMem(blob); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);
    }
}
