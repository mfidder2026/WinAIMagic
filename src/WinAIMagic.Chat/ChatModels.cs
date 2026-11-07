using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

public sealed record Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Filename { get; set; } = "";
    /// <summary>JPEG, already scaled down for the vision tower.</summary>
    public byte[] Jpeg { get; set; } = [];
}

public sealed record ToolCall(string Id, string Name, string Arguments);

public sealed record Source(string Title, string Url);

/// <summary>One step of the agent loop that the user sees: "Zoeken: …", "Pagina lezen: …".</summary>
public sealed record ToolActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public bool Done { get; set; }
    /// <summary>What the tool returned (shortened), shown when the user opens the step.</summary>
    public string? Output { get; set; }
    public bool? Denied { get; set; }
}

public enum ChatRole { System, User, Assistant, Tool }

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChatRole Role { get; set; }
    public string Text { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<Attachment> Attachments { get; set; } = [];
    public List<ToolCall> ToolCalls { get; set; } = [];
    public string? ToolCallId { get; set; }
    public List<ToolActivity> Activities { get; set; } = [];
    public List<Source> Sources { get; set; } = [];
    public double? TokensPerSecond { get; set; }
    public double? PromptTokensPerSecond { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Sent by the app, not the user: the model stopped before its work was done and is told to go on.</summary>
    public bool? Automatic { get; set; }
    /// <summary>While the model writes a tool call: what it prepares ("Schrijft bestand demo.bas · 1.240 tekens"). Not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Preparing { get; set; }

    /// <summary>Assistant turns that only carry tool calls, and the tool results, are kept for the model but folded in the UI.</summary>
    [JsonIgnore]
    public bool IsHidden => Role == ChatRole.Tool || Role == ChatRole.System
        || (Role == ChatRole.Assistant && Text.Length == 0 && ToolCalls.Count > 0 && Activities.Count == 0);
}

public sealed class Conversation
{
    public const string DefaultTitle = "Nieuw gesprek";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = DefaultTitle;
    public List<ChatMessage> Messages { get; set; } = [];
    /// <summary>The folder the model works in for this conversation (files and commands); null = none.</summary>
    public string? Workspace { get; set; }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Tokens the conversation took in the model's context at the last answer (prompt and answer).</summary>
    public int? ContextTokens { get; set; }
    /// <summary>Compacted: a summary of the messages up to and including SummaryUpTo, sent instead of them.</summary>
    public string? Summary { get; set; }
    public Guid? SummaryUpTo { get; set; }
    /// <summary>Connectors whose tools the model loaded in this conversation (use_connector); they stay in its requests.</summary>
    public List<string>? Connectors { get; set; }
    /// <summary>The model's list of steps for a larger piece of work (task_list), shown above the composer.</summary>
    public List<WorkStep>? Steps { get; set; }
    /// <summary>The scheduled task this conversation is a run of (Taken), if any.</summary>
    public Guid? Task { get; set; }
    /// <summary>Not in the sidebar: a heartbeat check that had nothing to report is removed, one that has stays under Taken.</summary>
    public bool? Hidden { get; set; }
    /// <summary>The Telegram account this conversation talks with (Instellingen → Telegram), if any.</summary>
    public long? Telegram { get; set; }

    /// <summary>The title in the chosen language while it is still the default.</summary>
    [JsonIgnore]
    public string DisplayTitle => Title == DefaultTitle ? LKey(DefaultTitle) : Title;

    /// <summary>The messages that still go to the model (after the summary).</summary>
    [JsonIgnore]
    public List<ChatMessage> Unsummarized
    {
        get
        {
            int i = SummaryUpTo is { } last ? Messages.FindIndex(m => m.Id == last) : -1;
            return i < 0 ? [.. Messages] : Messages.GetRange(i + 1, Messages.Count - i - 1);
        }
    }
}

/// <summary>JSON of the conversations file and of the requests to the engine.</summary>
public static class ChatJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Conversations saved as one JSON file in the app data folder.</summary>
public sealed class ConversationStore
{
    private readonly object gate = new();
    private bool saveBlocked;

    public List<Conversation> Conversations { get; set; } = [];
    public Guid? Selection { get; set; }
    /// <summary>The file could not be read: it is kept as .bad and this says so to the user.</summary>
    public string? LoadProblem { get; private set; }
    /// <summary>A conversation changed (its id), or the list did (null). On the thread that made the change.</summary>
    public event Action<Guid?>? Changed;

    public ConversationStore()
    {
        Load();
        if (Conversations.Count == 0) Conversations = [new Conversation()];
        StartInLastFolder();
        Selection = Conversations[0].Id;
    }

    private void Load()
    {
        var file = Paths.ConversationsFile;
        if (!File.Exists(file)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Conversation>>(File.ReadAllBytes(file), ChatJson.Options)
                ?? throw new JsonException("null");
            Conversations = [.. list.OrderByDescending(c => c.Updated)];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // never overwrite what could not be read: it may be repaired by hand
            var bad = file + ".bad";
            try { File.Move(file, bad, true); }
            catch (Exception m) when (m is IOException or UnauthorizedAccessException) { saveBlocked = true; }
            LoadProblem = ChatTexts.ConversationsUnreadable(bad);
        }
    }

    public Conversation? Current => Selection is { } id ? Find(id) : null;

    public Conversation? Find(Guid id)
    {
        lock (gate) return Conversations.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>A new chat starts in the folder chosen last; it can be changed per chat.</summary>
    private void StartInLastFolder()
    {
        if (Conversations.Count == 0) return;
        var first = Conversations[0];
        if (first.Messages.Count > 0 || first.Workspace != null || Settings.Shared.Workspace.Length == 0) return;
        first.Workspace = Settings.Shared.Workspace;
    }

    public void NewConversation()
    {
        lock (gate)
        {
            if (Conversations.Count == 0 || Conversations[0].Messages.Count > 0) Conversations.Insert(0, new Conversation());
            StartInLastFolder();
            Selection = Conversations[0].Id;
        }
        Changed?.Invoke(null);
    }

    /// <summary>A conversation the app makes itself (a task's run, a heartbeat check), at the top of the list.</summary>
    public void Insert(Conversation c)
    {
        lock (gate) Conversations.Insert(0, c);
        Changed?.Invoke(null);
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            Conversations.RemoveAll(c => c.Id == id);
            if (Conversations.Count == 0) Conversations = [new Conversation()];
            StartInLastFolder();
            if (Selection == id) Selection = Conversations[0].Id;
        }
        Save();
        Changed?.Invoke(null);
    }

    public void Update(Guid id, Action<Conversation> change)
    {
        lock (gate)
        {
            var c = Conversations.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            change(c);
            c.Updated = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke(id);
    }

    public void Save()
    {
        if (saveBlocked) return;
        byte[] data;
        lock (gate) data = JsonSerializer.SerializeToUtf8Bytes(Conversations, ChatJson.Options);
        try { AtomicFile.Write(Paths.ConversationsFile, data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
