using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinAIMagic.Core.Support;

/// <summary>How long the model thinks first: the four choices of the button under the input field.</summary>
public enum ThinkingLevel { Off, Low, Normal, High }

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
    /// <summary>The size chosen on the image page, "768x768"; empty: what suits this PC.</summary>
    public string ImageSize { get => Get(""); set => Set(value); }
    /// <summary>The model that makes images (Modellen, Kies voor afbeeldingen); empty: the first in the catalog.</summary>
    public string ImageModel { get => Get(""); set => Set(value); }
    /// <summary>The chat model rewrites the description for the image model first, with the web when it needs it.</summary>
    public bool ImageImprove { get => Get(false); set => Set(value); }
    /// <summary>The fast model of the documents module (Modellen, Snel); empty: the smallest that fits beside the heavy one.</summary>
    public string FastModel { get => Get(""); set => Set(value); }
    public bool Thinking { get => Get(true); set => Set(value); }
    public string ReasoningEffort { get => Get("high"); set => Set(value); }
    /// <summary>At most this many tokens of thinking, then the model writes its answer (0: no limit).</summary>
    public int ThinkingBudget { get => Get(16_000); set => Set(value); }

    /// <summary>Thinking as one choice, the button under the input field. It sets <see cref="Thinking"/> and <see cref="ReasoningEffort"/>,
    /// and how many tokens the model may think: only some templates read reasoning_effort (DeepSeek), while the engine stops the
    /// thinking of every model at the budget. High uses the setting "Maximaal nadenken"; "max" from Settings counts as high.</summary>
    public ThinkingLevel ThinkingLevel
    {
        get => !Thinking ? ThinkingLevel.Off : ReasoningEffort switch { "low" => ThinkingLevel.Low, "medium" => ThinkingLevel.Normal, _ => ThinkingLevel.High };
        set
        {
            Thinking = value != ThinkingLevel.Off;
            if (value == ThinkingLevel.Low) ReasoningEffort = "low";
            else if (value == ThinkingLevel.Normal) ReasoningEffort = "medium";
            else if (value == ThinkingLevel.High && ReasoningEffort != "max") ReasoningEffort = "high";
        }
    }

    /// <summary>How many tokens the model may think in an answer's first round now (0: no limit).</summary>
    public int ThinkingBudgetNow => ThinkingLevel switch { ThinkingLevel.Off => 0, ThinkingLevel.Low => 2_000, ThinkingLevel.Normal => 8_000, _ => ThinkingBudget };
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
    /// <summary>The working folder's files beside the chat (a developer view), and how wide that panel is.</summary>
    public bool FilesPanel { get => Get(false); set => Set(value); }
    public double FilesPanelWidth { get => Get(380.0); set => Set(value); }
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
    /// <summary>Instellingen → Systeembeheer, both on by default. Skills: markdown instructions per subject that the model loads
    /// when it needs them. System management: the model reads this PC's settings and changes them through a plan the user approves.</summary>
    public bool Skills { get => Get(true); set => Set(value); }
    /// <summary>The model knows the app: it reads WinAIMagic's help and how the app stands now, and makes skills (app_help, app_status, create_skill).</summary>
    public bool AppTools { get => Get(true); set => Set(value); }
    public bool SystemManagement { get => Get(true); set => Set(value); }
    /// <summary>Reading the system (diagnosis) and changing it: "allow", "ask" or "deny". Admin changes always ask.</summary>
    public string RightSystemRead { get => Get("allow"); set => Set(value); }
    public string RightSystemChange { get => Get("ask"); set => Set(value); }
    /// <summary>Instellingen → Taken: the model a task started stops again when the tasks are done and the user did not use it meanwhile.</summary>
    public bool TasksStopModel { get => Get(true); set => Set(value); }
    /// <summary>Now and then the model goes through HEARTBEAT.md, and only speaks up when there is something (off by default).</summary>
    public bool Heartbeat { get => Get(false); set => Set(value); }
    public int HeartbeatMinutes { get => Get(60); set => Set(value); }
    public int HeartbeatFrom { get => Get(8); set => Set(value); }
    public int HeartbeatTo { get => Get(22); set => Set(value); }
    /// <summary>A heartbeat may load the model; off, it only checks while the model is loaded anyway.</summary>
    public bool HeartbeatMayLoad { get => Get(false); set => Set(value); }
    /// <summary>When the last heartbeat ran (ISO 8601); empty: never.</summary>
    public string HeartbeatLast { get => Get(""); set => Set(value); }
    /// <summary>With the window closed the app stays in the notification area while there are tasks or a heartbeat.</summary>
    public bool StayInTray { get => Get(true); set => Set(value); }
    /// <summary>Closing the window stops the model (unless something is running), so its memory is free again.</summary>
    public bool StopModelOnClose { get => Get(true); set => Set(value); }
    // Instellingen → Agent, all off by default
    /// <summary>The model reads mail through Outlook; drafts and sending after a card.</summary>
    public bool FeatureMail { get => Get(false); set => Set(value); }
    /// <summary>The model browses in a window of its own in WinAIMagic.</summary>
    public bool FeatureBrowser { get => Get(false); set => Set(value); }
    /// <summary>The model controls the programs the user allows (ComputerApps), every action after a card.</summary>
    public bool FeatureComputerUse { get => Get(false); set => Set(value); }
    /// <summary>The programs the model may control, one per line: the path of the .exe.</summary>
    public string ComputerApps { get => Get(""); set => Set(value); }
    /// <summary>Speak in the chat and read answers aloud.</summary>
    public bool FeatureVoice { get => Get(false); set => Set(value); }
    public bool SpeakAnswers { get => Get(false); set => Set(value); }
    public string VoiceId { get => Get(""); set => Set(value); }
    // Instellingen → Telegram
    /// <summary>Talk with the model on this PC through a Telegram bot of your own (off by default).</summary>
    public bool FeatureTelegram { get => Get(false); set => Set(value); }
    /// <summary>Tasks and the heartbeat report in Telegram too, to the first account paired.</summary>
    public bool TelegramNotify { get => Get(true); set => Set(value); }
    /// <summary>Tests: allow what would be asked.</summary>
    public bool AutoApprove { get => Get(false); set => Set(value); }
    /// <summary>Speculative decoding with the model's multi-token prediction head (GLM, Qwen).</summary>
    public bool Mtp { get => Get(true); set => Set(value); }
    /// <summary>ngram-mod beside MTP: what the model writes again from the conversation (code it changes, a file it rewrites) is
    /// proposed in long stretches at once ("Herhaalde tekst sneller schrijven").</summary>
    public bool NgramMod { get => Get(true); set => Set(value); }
    /// <summary>Save a conversation's place in the main engine when another one takes it or the engine stops, and read it back
    /// (SlotKeeper): going on with a long conversation then does not read it all again.</summary>
    public bool KeepEngineMemory { get => Get(true); set => Set(value); }
    /// <summary>Leave out experts missing from VRAM that count little: "off", "light" or "strong". Faster, but the answers change a little.</summary>
    public string SkipExperts { get => Get("off"); set => Set(value); }
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

    public string SystemRight(bool change)
    {
        var value = change ? RightSystemChange : RightSystemRead;
        return value == "ask" && AutoApprove ? "allow" : value;
    }

    public static Settings Load()
    {
        var s = new Settings { loading = true };
        bool resave = false;
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.SettingsFile));
                // (0.1.0 also wrote a copy with the keys unencrypted: the next save leaves it out)
                resave = doc.RootElement.TryGetProperty("Shared", out _);
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
        if (resave) s.Save();
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

    // ThinkingLevel is made of Thinking and ReasoningEffort, which are stored themselves
    private static IEnumerable<PropertyInfo> Stored() =>
        typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(ThinkingLevel));

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
