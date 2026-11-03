using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinAIMagic.Core.Support;

/// <summary>User settings: settings.json in the app data folder, written atomically; API keys encrypted with DPAPI.</summary>
public sealed class Settings : INotifyPropertyChanged
{
    private static Settings? shared;
    private static readonly object Gate = new();

    public static Settings Shared
    {
        get { lock (Gate) return shared ??= Load(); }
        set { lock (Gate) shared = value; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Dictionary<string, object?> v = new();
    private bool loading;

    private T Get<T>(T fallback, [CallerMemberName] string key = "") => v.TryGetValue(key, out var x) && x is T t ? t : fallback;

    private void Set<T>(T value, [CallerMemberName] string key = "")
    {
        if (v.TryGetValue(key, out var old) && Equals(old, value)) return;
        v[key] = value;
        if (loading) return;
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
    }

    /// <summary>The language of the interface and of the instructions to the model; at first Windows' own.</summary>
    public string Language { get => Get(AppLanguages.System.Code()); set => Set(value); }
    public string Host { get => Get("127.0.0.1"); set => Set(value); }
    public int Port { get => Get(8080); set => Set(value); }
    [Secret] public string ApiKey { get => Get(""); set => Set(value); }
    public int ContextSize { get => Get(65_536); set => Set(value); }
    public bool Vision { get => Get(true); set => Set(value); }
    public bool Thinking { get => Get(true); set => Set(value); }
    public string ReasoningEffort { get => Get("high"); set => Set(value); }
    /// <summary>At most this many tokens of thinking, then the model writes its answer (0: no limit).</summary>
    public int ThinkingBudget { get => Get(16_000); set => Set(value); }
    public bool WebSearch { get => Get(true); set => Set(value); }
    [Secret] public string BraveKey { get => Get(""); set => Set(value); }
    public double Temperature { get => Get(1.0); set => Set(value); }
    public string SystemPrompt { get => Get(""); set => Set(value); }
    public bool AutoStart { get => Get(false); set => Set(value); }
    /// <summary>The system check was shown once (the first start opens it).</summary>
    public bool SystemCheckSeen { get => Get(false); set => Set(value); }
    public string ModelFolder { get => Get(""); set => Set(value); }
    /// <summary>The folder the model may work in (files and commands), and whether that is on.</summary>
    public string Workspace { get => Get(""); set => Set(value); }
    public bool WorkspaceTools { get => Get(true); set => Set(value); }
    /// <summary>The project open in the documents module.</summary>
    public string DocumentFolder { get => Get(""); set => Set(value); }
    /// <summary>The folders of the projects, one per line.</summary>
    public string DocumentProjects { get => Get(""); set => Set(value); }
    /// <summary>WINAIMAGIC.md in the work folder keeps what the model learned.</summary>
    public bool LearnFromChats { get => Get(true); set => Set(value); }
    /// <summary>What the model may do in the work folder, per kind of action: "allow", "ask" or "deny".</summary>
    public string RightRead { get => Get("allow"); set => Set(value); }
    public string RightWrite { get => Get("ask"); set => Set(value); }
    public string RightRun { get => Get("ask"); set => Set(value); }
    /// <summary>Tests: allow what would be asked.</summary>
    public bool AutoApprove { get => Get(false); set => Set(value); }
    /// <summary>Speculative decoding with the model's multi-token prediction head (GLM, Qwen).</summary>
    public bool Mtp { get => Get(true); set => Set(value); }
    /// <summary>Drifting background colours (off: a still background, calmer).</summary>
    public bool AnimatedBackground { get => Get(false); set => Set(value); }
    /// <summary>Cluster: "main" (this PC runs the engine, helpers add memory) or "helper" (this PC lends its GPU).</summary>
    public string ClusterRole { get => Get("main"); set => Set(value); }
    public bool ClusterAllowLan { get => Get(false); set => Set(value); }
    public string SelectedModelId { get => Get(""); set => Set(value); }
    /// <summary>MiB of VRAM kept free beside the engine; -1: the value from the GPU profile.</summary>
    public int VramReserveMib { get => Get(-1); set => Set(value); }
    /// <summary>The GPU backend the user chose (cuda, hip, vulkan, cpu); empty: the app chooses.</summary>
    public string GpuBackendOverride { get => Get(""); set => Set(value); }

    [AttributeUsage(AttributeTargets.Property)]
    private sealed class SecretAttribute : Attribute { }

    public string Right(WorkspaceRight kind)
    {
        var value = kind == WorkspaceRight.Read ? RightRead : kind == WorkspaceRight.Write ? RightWrite : RightRun;
        return value == "ask" && AutoApprove ? "allow" : value;
    }

    public static Settings Load()
    {
        var s = new Settings { loading = true };
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.SettingsFile));
                foreach (var p in Stored())
                {
                    if (!doc.RootElement.TryGetProperty(p.Name, out var e)) continue;
                    object? value = p.PropertyType == typeof(string) ? e.GetString()
                        : p.PropertyType == typeof(int) ? e.GetInt32()
                        : p.PropertyType == typeof(bool) ? e.GetBoolean()
                        : p.PropertyType == typeof(double) ? e.GetDouble() : null;
                    if (value is string text && Attribute.IsDefined(p, typeof(SecretAttribute))) value = Unprotect(text);
                    if (value != null) p.SetValue(s, value);
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException)
        {
            try { File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bad", true); } catch (IOException) { }
        }
        s.loading = false;
        return s;
    }

    public void Save()
    {
        var o = new Dictionary<string, object?>();
        foreach (var p in Stored())
        {
            var value = p.GetValue(this);
            if (value is string text && Attribute.IsDefined(p, typeof(SecretAttribute))) value = Protect(text);
            o[p.Name] = value;
        }
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Paths.SettingsFile, true);
    }

    private static IEnumerable<System.Reflection.PropertyInfo> Stored() =>
        typeof(Settings).GetProperties().Where(p => p.CanWrite && p.CanRead && p.GetIndexParameters().Length == 0);

    private static string Protect(string text) =>
        text.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string text)
    {
        if (text.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is FormatException or CryptographicException) { return ""; }
    }
}

public enum WorkspaceRight { Read, Write, Run }
