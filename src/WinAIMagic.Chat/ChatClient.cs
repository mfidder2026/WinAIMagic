using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>A change the model wants to make (write a file, run a command), waiting for the user's yes or no.</summary>
public sealed record Approval(Guid Conversation, ToolCall Call, string Title, string Detail)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>Allow, and whether that goes for the rest of this chat in this app session.</summary>
public readonly record struct ApprovalAnswer(bool Allow, bool Always = false);

/// <summary>What the engine answered with instead of a stream.</summary>
public sealed class ChatEngineException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Talks to the local engine's OpenAI-compatible endpoint: streams answers and thinking, and runs the tool loop
/// (web search, page reading, work in a folder) until the model answers without calling a tool. Started on the UI thread,
/// the loop and every store change stay there, as on the Mac's main actor; network and tools run elsewhere.</summary>
public sealed class ChatClient(HttpClient? http = null)
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http = http ?? SharedHttp;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3600);
    /// <summary>At this share of the context the conversation is summarized before the next request.</summary>
    public const double CompactAt = 0.8;
    /// <summary>How many times in a row the model is told to go on without it doing anything in between.</summary>
    public const int MaxNudges = 3;

    public bool Busy { get; private set; }
    /// <summary>Summarizing the start of a conversation to free context.</summary>
    public bool Compacting { get; private set; }
    /// <summary>Writing down what this task taught the model, for WINAIMAGIC.md.</summary>
    public bool Learning { get; private set; }
    /// <summary>The engine's context in tokens (set when it starts).</summary>
    public int ContextLimit { get; set; } = 65_536;
    /// <summary>The model the engine runs, for the system prompt.</summary>
    public string ModelName { get; set; } = "DeepSeek-V4.1-Flash";
    public Approval? PendingApproval { get; private set; }

    /// <summary>The loop that Send started; done when Busy goes false.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Busy, Compacting, Learning or PendingApproval changed.</summary>
    public event Action? StateChanged;
    /// <summary>New text in a message (conversation, message, content, reasoning): at most every 0.1 s, never synchronous
    /// with the stream, on the thread Send was called on. The store has the text already.</summary>
    public event Action<Guid, Guid, string, string>? Delta;
    /// <summary>A token arrived, for the live speed chart. On a network thread: do not touch the UI from it.</summary>
    public event Action<int>? TokensStreamed;
    /// <summary>Something the user should read: a search engine failed and another took over.</summary>
    public event Action<string>? Notice;
    /// <summary>Asks the user. Without it the question waits in PendingApproval until Answer. Stop cancels the token and
    /// counts as no.</summary>
    public Func<Approval, CancellationToken, Task<ApprovalAnswer>>? AskUser { get; set; }

    private CancellationTokenSource? cts;
    private TaskCompletionSource<bool>? approvalAnswer;
    /// <summary>Conversations where the user chose "always allow", for this app session.</summary>
    private readonly HashSet<Guid> trusted = [];
    /// <summary>The notes as they were before this conversation's lessons went in: the model knows those already, and a changed
    /// system prompt would make the engine read the whole conversation again. An edit by the user counts at once.</summary>
    private (Guid Conversation, DateTime? Modified, string? Text)? frozenNotes;
    private Guid? currentConversation;

    /// <summary>Web questions need a few rounds; work in a folder (read, write, run, read the log, fix) needs many more.</summary>
    private int MaxToolRounds => WorkspaceActive ? 200 : 20;
    private static bool WorkspaceActive => Settings.Shared.WorkspaceTools && WorkspaceTools.Workspace != null;

    public void Answer(Approval approval, bool allow, bool always = false)
    {
        if (always) trusted.Add(approval.Conversation);
        var waiting = approvalAnswer;
        approvalAnswer = null;
        PendingApproval = null;
        waiting?.TrySetResult(allow);
        StateChanged?.Invoke();
    }

    private async Task<bool> AskApproval(ToolCall call, Guid conversation, CancellationToken ct)
    {
        if (trusted.Contains(conversation)) return true;
        var (title, detail) = WorkspaceTools.ApprovalText(call);
        var approval = new Approval(conversation, call, title, detail);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        approvalAnswer = answer;
        PendingApproval = approval;
        StateChanged?.Invoke();
        // Stop is a no, also when it comes in just before the question stands
        using var stop = ct.Register(() => { if (PendingApproval == approval) Answer(approval, false); });
        if (ct.IsCancellationRequested) answer.TrySetResult(false);
        if (AskUser is { } ask) _ = Bridge(ask, approval, ct);
        return await answer.Task;
    }

    private async Task Bridge(Func<Approval, CancellationToken, Task<ApprovalAnswer>> ask, Approval approval, CancellationToken ct)
    {
        ApprovalAnswer given;
        try { given = await ask(approval, ct); }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException) { given = new ApprovalAnswer(false); }
        if (PendingApproval == approval) Answer(approval, given.Allow, given.Always);
    }

    public void Stop()
    {
        cts?.Cancel();
        if (PendingApproval is { } a) Answer(a, false);
    }

    public void Send(string text, IReadOnlyList<Attachment> attachments, Guid conversation, ConversationStore store, Uri baseUrl)
    {
        if (Busy) return;
        FillStopped(conversation, store);   // a conversation that ended in a crash: its question must be answered first
        store.Update(conversation, c =>
        {
            c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text, Attachments = [.. attachments] });
            if (c.Title == Conversation.DefaultTitle)
            {
                var t = text.Length == 0 ? (attachments.FirstOrDefault()?.Filename ?? "Afbeelding") : text;
                c.Title = TextLimit.Prefix(t, 48).Replace("\n", " ");
            }
        });
        cts = new CancellationTokenSource();
        SetBusy(true);
        Completion = RunAndLearn(conversation, store, baseUrl, SynchronizationContext.Current, cts.Token);
    }

    private async Task RunAndLearn(Guid id, ConversationStore store, Uri baseUrl, SynchronizationContext? owner, CancellationToken ct)
    {
        try
        {
            await Run(id, store, baseUrl, owner, ct);
            await Learn(id, store, baseUrl, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            store.Update(id, c => { if (c.Messages.Count > 0) c.Messages[^1].Error = e.Message; });
        }
        finally
        {
            SetBusy(false);
            store.Save();
        }
    }

    private void SetBusy(bool value) { Busy = value; StateChanged?.Invoke(); }

    private async Task Run(Guid id, ConversationStore store, Uri baseUrl, SynchronizationContext? owner, CancellationToken ct)
    {
        // this conversation's folder for the tools and the system prompt
        WorkspaceTools.Current = store.Find(id)?.Workspace ?? "";
        currentConversation = id;
        if (KeepsNotes && WorkspaceTools.Workspace is { } folder) WorkspaceNotes.CreateIfNeeded(folder);
        var activities = new List<ToolActivity>();
        var sources = new List<Source>();
        int nudges = 0;
        int rounds = MaxToolRounds;
        try
        {
            for (int round = 0; round < rounds; round++)
            {
                if (store.Find(id)?.ContextTokens is { } used && used > ContextLimit * CompactAt)
                    await CompactAsync(id, store, baseUrl, null, ct);
                if (ct.IsCancellationRequested) return;
                var reply = new ChatMessage { Role = ChatRole.Assistant, Activities = Copy(activities), Sources = [.. sources] };
                store.Update(id, c => c.Messages.Add(reply));

                var throttle = new StreamThrottle((content, reasoning) =>
                {
                    store.Update(id, _ =>
                    {
                        reply.Text += content;
                        reply.Reasoning += reasoning;
                    });
                    Delta?.Invoke(id, reply.Id, content, reasoning);
                }, context: owner);

                StreamResult result;
                bool retried = false;
                while (true)
                {
                    var c = store.Find(id);
                    if (c == null) return;
                    var history = c.Unsummarized;
                    history.RemoveAt(history.Count - 1);
                    try
                    {
                        result = await Task.Run(() => StreamAsync(history, c.Summary, baseUrl, throttle, ct), CancellationToken.None);
                        await throttle.DrainAsync();
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await throttle.DrainAsync();
                        return;
                    }
                    catch (ChatEngineException e) when (!retried && e.Status == 400 && e.Message.Contains("context", StringComparison.OrdinalIgnoreCase))
                    {
                        // the conversation no longer fits: summarize its start and try again once
                        retried = true;
                        await CompactAsync(id, store, baseUrl, reply.Id, ct);
                    }
                    catch (Exception e)
                    {
                        await throttle.DrainAsync();
                        store.Update(id, _ => reply.Error = e.Message);
                        return;
                    }
                }
                if (result.ContextTokens is { } tokens) store.Update(id, c => c.ContextTokens = tokens);

                store.Update(id, _ =>
                {
                    reply.ToolCalls = result.ToolCalls;
                    reply.TokensPerSecond = result.TokensPerSecond;
                    reply.PromptTokensPerSecond = result.PromptTokensPerSecond;
                });
                if (ct.IsCancellationRequested) return;
                if (result.ToolCalls.Count == 0)
                {
                    bool cutOff = result.FinishReason == "length";
                    if (!LooksUnfinished(reply.Text, reply.Reasoning, cutOff, WorkspaceActive || activities.Count > 0, activities.Count > 0)) return;
                    if (nudges >= MaxNudges)
                    {
                        // it keeps stopping: say so, instead of just falling silent
                        store.Update(id, _ => reply.Error = L($"Het model stopte {MaxNudges} keer zonder verder te werken of te zeggen dat de taak af is. Zeg \"ga door\", of geef het een aanwijzing."));
                        return;
                    }
                    nudges++;
                    store.Update(id, _ => reply.Activities = []);
                    var nudge = cutOff ? L("Je antwoord werd afgekapt omdat je context vol was. Ga verder waar je gebleven was.")
                        : L("Je stopte zonder gereedschap te gebruiken. Is de taak nog niet af, roep dan nu meteen het volgende gereedschap aan, zonder het eerst aan te kondigen; werkt iets niet, probeer dan een andere aanpak. Is de taak wel af en gecontroleerd, begin je bericht dan met het woord KLAAR en zeg kort wat het resultaat is.");
                    store.Update(id, c => c.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = nudge, Automatic = true }));
                    continue;
                }
                nudges = 0;

                // run the tools; the next round's message carries the activity list, this one folds away
                foreach (var call in result.ToolCalls)
                {
                    bool isWorkspace = WorkspaceTools.Names.Contains(call.Name);
                    var step = new ToolActivity { Label = isWorkspace ? WorkspaceTools.Label(call) : WebTools.Label(call) };
                    activities.Add(step);
                    store.Update(id, _ => reply.Activities = Copy(activities));
                    string text;
                    if (isWorkspace)
                    {
                        var kind = WorkspaceTools.RightFor(call.Name);
                        var right = Settings.Shared.Right(kind);
                        if (right == "deny")
                        {
                            text = $"The user's settings do not allow this kind of action ({kind.Title()}). Do not try it again; tell the user what you wanted to do, or continue without it.";
                            step.Denied = true;
                        }
                        else if (right == "ask" && !await AskApproval(call, id, ct))
                        {
                            text = "The user did not allow this. Ask what they want instead, or continue without it.";
                            step.Denied = true;
                        }
                        else text = await WorkspaceTools.RunAsync(call, ct);
                    }
                    else
                    {
                        var found = await WebTools.RunAsync(call, ct);
                        text = found.Text;
                        foreach (var s in found.Sources)
                            if (!sources.Exists(x => x.Url == s.Url)) sources.Add(s);
                        if (found.Notice is { } notice) Notice?.Invoke(notice);
                    }
                    if (ct.IsCancellationRequested) return;
                    // one result must not fill a small context: the start and the end, which usually matter most
                    int room = Math.Max(8_000, ContextLimit * 3 / 4);
                    if (text.Length > room)
                        text = TextLimit.Prefix(text, room * 3 / 4) + $"\n… (shortened, the output was {text.Length} characters) …\n" + TextLimit.Suffix(text, room / 4);
                    step.Done = true;
                    step.Output = TextLimit.Prefix(text, 4000);
                    store.Update(id, c =>
                    {
                        reply.Activities = [];
                        reply.Sources = [];
                        c.Messages.Add(new ChatMessage { Role = ChatRole.Tool, Text = text, ToolCallId = call.Id });
                    });
                }
            }
            // the steps ran out: say so, in a message the user sees (a tool result is folded away)
            store.Update(id, c => c.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Activities = Copy(activities),
                Sources = [.. sources],
                Error = L($"Het model heeft {rounds} stappen gezet en pauzeert. Zeg \"ga door\" om verder te gaan."),
            }));
        }
        finally
        {
            if (ct.IsCancellationRequested) FillStopped(id, store);
        }
    }

    private static List<ToolActivity> Copy(List<ToolActivity> activities) => [.. activities.Select(a => a with { })];

    /// <summary>A tool call without a result breaks the next request, and its step would keep spinning: stopping answers it.</summary>
    private static void FillStopped(Guid id, ConversationStore store)
    {
        var c = store.Find(id);
        if (c == null) return;
        int at = c.Messages.FindLastIndex(m => m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0);
        var answered = at < 0 ? [] : c.Messages.Skip(at + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToHashSet();
        var open = at < 0 ? [] : c.Messages[at].ToolCalls.Where(call => !answered.Contains(call.Id)).ToList();
        bool spinning = c.Messages.Exists(m => m.Activities.Exists(a => !a.Done));
        if (open.Count == 0 && !spinning) return;
        store.Update(id, conversation =>
        {
            foreach (var m in conversation.Messages)
                foreach (var a in m.Activities)
                    if (!a.Done) { a.Done = true; a.Output ??= StoppedResult; }
            int place = at + 1;
            while (place < conversation.Messages.Count && conversation.Messages[place].Role == ChatRole.Tool) place++;
            foreach (var call in open)
                conversation.Messages.Insert(place++, new ChatMessage { Role = ChatRole.Tool, Text = StoppedResult, ToolCallId = call.Id });
        });
    }

    internal const string StoppedResult = "gestopt door de gebruiker";

    // MARK: - learning

    /// <summary>The app keeps WINAIMAGIC.md for this work folder: the setting is on, and the user allows writing there.</summary>
    private static bool KeepsNotes => WorkspaceActive && Settings.Shared.LearnFromChats && Settings.Shared.Right(WorkspaceRight.Write) != "deny";

    /// <summary>After a task of some steps the model says what it learned, and that goes in WINAIMAGIC.md. It answers after the
    /// conversation itself, so the engine reuses everything it already has in memory, whatever the model's speed.
    /// Not after a task that read the web: a page must not be able to put instructions in the notes.</summary>
    private async Task Learn(Guid id, ConversationStore store, Uri baseUrl, CancellationToken ct)
    {
        if (!KeepsNotes || ct.IsCancellationRequested || WorkspaceTools.Workspace is not { } ws) return;
        var c = store.Find(id);
        if (c == null || c.Messages.Count == 0 || c.Messages[^1].Error != null) return;
        int start = c.Messages.FindLastIndex(m => m.Role == ChatRole.User && m.Automatic != true);
        if (start < 0) return;
        var calls = c.Messages.Skip(start).SelectMany(m => m.ToolCalls).ToList();
        if (calls.Count < 3 || !calls.TrueForAll(x => WorkspaceTools.Names.Contains(x.Name))) return;

        Learning = true;
        StateChanged?.Invoke();
        try
        {
            var messages = RequestMessages(c.Unsummarized, c.Summary);
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = ChatTexts.LearnRequest });
            var body = new JsonObject
            {
                ["model"] = ModelName,
                ["messages"] = messages,
                ["stream"] = false,
                ["temperature"] = 0.2,
                ["max_tokens"] = 400,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false, ["reasoning_effort"] = Settings.Shared.ReasoningEffort },
                ["thinking_budget_tokens"] = 0,    // GLM's template ignores enable_thinking and always opens with <think>
            };
            // the same tools as the conversation, so its start stays the same for the engine; it must only write here
            var tools = CurrentTools();
            if (tools.Count > 0) { body["tools"] = tools; body["tool_choice"] = "none"; }
            var answer = await CompleteAsync(body, baseUrl, 900, ct);
            if (answer == null || ct.IsCancellationRequested) return;
            var before = NotesForPrompt(ws);
            var added = WorkspaceNotes.Add(WorkspaceNotes.Lessons(answer), ws);
            if (added.Count == 0) return;
            frozenNotes = (id, WorkspaceNotes.Modified(ws), before);
            store.Update(id, conversation =>
            {
                if (conversation.Messages.Count == 0) return;
                conversation.Messages[^1].Activities.Add(new ToolActivity { Label = ChatTexts.NotesUpdated(added.Count), Done = true, Output = string.Join("\n", added) });
            });
        }
        finally
        {
            Learning = false;
            StateChanged?.Invoke();
        }
    }

    private string? NotesForPrompt(string ws)
    {
        if (frozenNotes is { } f && f.Conversation == currentConversation && f.Modified == WorkspaceNotes.Modified(ws)) return f.Text;
        return WorkspaceNotes.ForPrompt(ws);
    }

    // MARK: - stopping too early

    private static readonly string[] CallMarks = ["<tool_call>", "</tool_call>", "<function=", "</function>", "</parameter>"];
    private static readonly string[] EndWords = ["klaar", "done", "fertig", "terminé", "termine"];
    private static readonly string[] Invitations = ["let me know", "laat me weten", "laat het me weten", "lass es mich wissen", "faites-moi savoir", "dites-moi"];
    private static readonly string[] Announcements =
    [
        " laat me ", " laten we ", " ik ga ", " ik maak nu", " ik zal ", " nu ga ik", " nu maak ik", " ga ik nu", " ik probeer ",
        " let me ", " let's ", " i will ", " i'll ", " i am going to", " i'm going to", " now i ", " next, i ",
        " lass mich ", " lassen sie mich ", " ich werde ", " jetzt werde ", " ich versuche ",
        " je vais ", " laisse-moi ", " laissez-moi ", " je vais maintenant",
    ];
    private static readonly Regex SentenceEnd = new(@"[.!?]\s+", RegexOptions.Compiled);

    /// <summary>A local model often ends its turn while the work is not done: it says what it will do and stops, thinks without
    /// answering, or its tool call stays behind in the thinking. Then the turn has no tool calls, and without this the
    /// conversation would just stop. working: tools are in use, so an announcement is meant to be followed by a step.
    /// tookSteps: this task used tools, and then only the model's own word ends it (KLAAR, or a question to the user):
    /// a remark like "dit werkt niet lekker" is no ending.</summary>
    public static bool LooksUnfinished(string text, string reasoning, bool cutOff, bool working, bool tookSteps = false)
    {
        if (cutOff) return true;
        var answer = text.Trim();
        if (answer.Length == 0) return true;
        if (!working) return false;
        var thought = TextLimit.Suffix(reasoning, 300);
        if (CallMarks.Any(m => answer.Contains(m, StringComparison.Ordinal) || thought.Contains(m, StringComparison.Ordinal))) return true;
        if (answer.EndsWith('?')) return false;
        if (answer.EndsWith(':')) return true;
        if (tookSteps)
        {
            int first = 0;
            while (first < answer.Length && !char.IsLetter(answer[first])) first++;
            int end = first;
            while (end < answer.Length && char.IsLetter(answer[end])) end++;
            return !EndWords.Contains(answer[first..end].ToLowerInvariant());
        }
        // the last sentence: does it announce a next step?
        var parts = SentenceEnd.Replace(answer, "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length == 0 ? "" : " " + parts[^1].ToLowerInvariant();
        if (Invitations.Any(i => last.Contains(i, StringComparison.Ordinal))) return false;
        return Announcements.Any(a => last.Contains(a, StringComparison.Ordinal));
    }

    // MARK: - compacting

    /// <summary>Summarize the start of a conversation, so it fits in the context again: everything before the last two questions
    /// becomes a summary that goes with the system prompt; the messages stay visible in the chat.</summary>
    public async Task CompactAsync(Guid id, ConversationStore store, Uri baseUrl, Guid? excluding = null, CancellationToken ct = default)
    {
        var c = store.Find(id);
        if (c == null) return;
        var open = c.Unsummarized.Where(m => m.Id != excluding).ToList();
        var users = Enumerable.Range(0, open.Count).Where(i => open[i].Role == ChatRole.User && open[i].Automatic != true).ToList();
        int cut;
        if (users.Count > 2)
        {
            cut = users[^2];   // keep the last two questions and what came after them
        }
        else
        {
            // one long task: keep its latest steps, and the summary carries the question (a tool result stays with its call)
            if (open.Count <= 8) return;
            cut = open.Count - 6;
            while (cut < open.Count && open[cut].Role == ChatRole.Tool) cut++;
            if (cut >= open.Count) return;
        }
        var old = open.GetRange(0, cut);
        if (old.Count == 0) return;
        var last = old[^1];

        Compacting = true;
        StateChanged?.Invoke();
        try
        {
            // a transcript without images and with long tool results shortened, so the request itself fits
            int budget = (int)(ContextLimit * 2.5);   // characters, about 0.7 of the context in tokens
            var lines = new List<string>();
            if (c.Summary is { } previous) lines.Add(L("Eerdere samenvatting:") + "\n" + previous);
            foreach (var m in old)
            {
                switch (m.Role)
                {
                    case ChatRole.User:
                        if (m.Automatic == true) continue;
                        lines.Add(L("Gebruiker:") + " " + m.Text + (m.Attachments.Count == 0 ? "" : " [" + L("afbeelding") + "]"));
                        break;
                    case ChatRole.Assistant:
                        if (m.Text.Length > 0) lines.Add(L("Assistent:") + " " + m.Text);
                        foreach (var call in m.ToolCalls) lines.Add(L($"Assistent gebruikt {call.Name}:") + " " + TextLimit.Prefix(call.Arguments, 600));
                        break;
                    case ChatRole.Tool:
                        lines.Add(L("Resultaat:") + " " + TextLimit.Prefix(m.Text, 1500));
                        break;
                }
            }
            var transcript = string.Join("\n\n", lines);
            if (transcript.Length > budget) transcript = "[" + L("begin weggelaten") + "]\n" + TextLimit.Suffix(transcript, budget);

            var body = new JsonObject
            {
                ["model"] = ModelName,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = L("Je vat een gesprek samen zodat het verder kan zonder de oorspronkelijke berichten. Houd vast: het doel van de gebruiker, beslissingen, namen en paden van bestanden, commando's die werkten of faalden, de stand van het werk en open vragen of taken. Schrijf beknopt, in het Nederlands, als lijst met kopjes.") },
                    new JsonObject { ["role"] = "user", ["content"] = L("Vat dit gesprek samen:") + "\n\n" + transcript }),
                ["stream"] = false,
                ["temperature"] = 0.3,
                ["max_tokens"] = 2000,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
                ["thinking_budget_tokens"] = 0,    // GLM's template ignores enable_thinking and always opens with <think>
            };
            var summary = await CompleteAsync(body, baseUrl, 1800, ct);
            if (string.IsNullOrEmpty(summary)) return;
            store.Update(id, conversation =>
            {
                conversation.Summary = summary;
                conversation.SummaryUpTo = last.Id;
                conversation.ContextTokens = null;
            });
            store.Save();
        }
        finally
        {
            Compacting = false;
            StateChanged?.Invoke();
        }
    }

    // MARK: - request

    /// <summary>The tools for the next request, from the settings and the folder alone.</summary>
    public static JsonArray CurrentTools()
    {
        var s = Settings.Shared;
        var tools = new JsonArray();
        if (s.WebSearch) foreach (var t in WebTools.Definitions) tools.Add(t!.DeepClone());
        if (s.WorkspaceTools && WorkspaceTools.Workspace != null) foreach (var t in WorkspaceTools.Definitions) tools.Add(t!.DeepClone());
        return tools;
    }

    internal JsonArray RequestMessages(IEnumerable<ChatMessage> history, string? summary = null)
    {
        var s = Settings.Shared;
        var date = DateTime.Now.ToString("D", AppLanguages.Current.Culture());
        var system = ChatTexts.Identity(ModelName, date);
        if (s.WebSearch)
            system += " " + L("Je kunt het web doorzoeken met web_search en pagina's lezen met fetch_url. Doe dat voor actuele informatie en noem je bronnen met hun URL.");
        // the user's general notes, for every conversation (the folder's notes come after them)
        if (AtomicFile.ReadUtf8(Paths.GeneralNotes) is { Length: > 0 } general)
            system += "\n\n" + ChatTexts.GeneralNotes + "\n" + TextLimit.Prefix(general, 8000);
        if (s.WorkspaceTools && WorkspaceTools.Workspace is { } ws)
        {
            system += "\n\n" + ChatTexts.Workspace(ws);
            system += " " + ChatTexts.Outside;
            // the user's own notes for this folder and what was learned here, like CLAUDE.md for Claude Code
            if (NotesForPrompt(ws) is { } text) system += "\n\n" + ChatTexts.FolderNotes + "\n" + text;
            var noTools = DeveloperTools.HasDeveloperTools ? "" : " " + ChatTexts.NoDeveloperTools(DeveloperTools.Missing);
            system += "\n\n" + ChatTexts.KeepGoing + noTools;
        }
        if (s.SystemPrompt.Length > 0) system += "\n\n" + s.SystemPrompt;
        if (summary != null)
            system += "\n\n" + L("Samenvatting van het begin van dit gesprek (die berichten zijn weggelaten om ruimte te maken in je context):") + "\n" + summary;

        var o = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    if (m.Attachments.Count == 0)
                    {
                        o.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    }
                    else
                    {
                        var parts = new JsonArray();
                        foreach (var a in m.Attachments)
                            parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(a.Jpeg) } });
                        if (m.Text.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                        o.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    }
                    break;
                case ChatRole.Assistant:
                    if (m.Text.Length == 0 && m.ToolCalls.Count == 0) continue;
                    // the thinking stays here: reasoning never goes back to the model
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
                    if (m.ToolCalls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var call in m.ToolCalls)
                            calls.Add(new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
                            });
                        msg["tool_calls"] = calls;
                    }
                    o.Add(msg);
                    break;
                case ChatRole.Tool:
                    o.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId ?? "", ["content"] = m.Text });
                    break;
            }
        }
        // after summarizing one long task the rest starts with a step of the model; every template wants a question first
        if (o.Count > 1 && o[1]?["role"]?.GetValue<string>() != "user")
            o.Insert(1, new JsonObject { ["role"] = "user", ["content"] = L("Ga verder met de taak uit de samenvatting.") });
        return o;
    }

    internal JsonObject StreamBody(IEnumerable<ChatMessage> history, string? summary)
    {
        var s = Settings.Shared;
        var body = new JsonObject
        {
            ["model"] = ModelName,
            ["messages"] = RequestMessages(history, summary),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = s.Temperature,
            ["top_p"] = 1.0,
            // enable_thinking is understood by both templates (DeepSeek maps it to thinking_mode)
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = s.Thinking, ["reasoning_effort"] = s.ReasoningEffort },
        };
        // without a limit a model can keep doubting on a hard question and never get to its answer
        // off means off: GLM's template ignores enable_thinking and always opens with <think>
        if (!s.Thinking) body["thinking_budget_tokens"] = 0;
        else if (s.ThinkingBudget > 0)
        {
            body["thinking_budget_tokens"] = s.ThinkingBudget;
            body["reasoning_budget_message"] = "\n\n" + L("Genoeg nagedacht; nu schrijf ik het antwoord.") + "\n";
        }
        // no limit asked: said out loud, because the tiered server otherwise applies its own default
        else body["thinking_budget_tokens"] = -1;
        var tools = CurrentTools();
        if (tools.Count > 0) body["tools"] = tools;
        return body;
    }

    private static HttpRequestMessage Post(JsonObject body, Uri baseUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.ToString().TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(ChatJson.Options), Encoding.UTF8, "application/json"),
        };
        var key = Settings.Shared.ApiKey;
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        return req;
    }

    /// <summary>A request without streaming; the answer's text, or null when anything went wrong: the callers go on without it.</summary>
    private async Task<string?> CompleteAsync(JsonObject body, Uri baseUrl, int timeoutSeconds, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var req = Post(body, baseUrl);
            using var resp = await http.SendAsync(req, limit.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200) return null;
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            return json?["choices"]?[0]?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException) { return null; }
    }

    internal sealed class StreamResult
    {
        public List<ToolCall> ToolCalls = [];
        /// <summary>"length": the context was full, the answer is cut off.</summary>
        public string? FinishReason;
        public double? TokensPerSecond, PromptTokensPerSecond;
        /// <summary>Prompt and answer together: what the conversation takes in the context now.</summary>
        public int? ContextTokens;
    }

    /// <summary>The JSON of an event-stream line; null for what is not data: comments and keep-alives during a long prompt,
    /// blank lines, event names.</summary>
    internal static string? DataOf(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line[5..].TrimStart(' ');
        return payload.Length == 0 ? null : payload;
    }

    private async Task<StreamResult> StreamAsync(List<ChatMessage> history, string? summary, Uri baseUrl, StreamThrottle throttle, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(RequestTimeout);
        try
        {
            using var req = Post(StreamBody(history, summary), baseUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200)
            {
                var msg = (await resp.Content.ReadAsStringAsync(idle.Token).ConfigureAwait(false)).Replace("\r", "").Replace("\n", "");
                throw new ChatEngineException((int)resp.StatusCode, $"Engine gaf {(int)resp.StatusCode}: {TextLimit.Prefix(msg, 400)}");
            }

            var result = new StreamResult();
            var calls = new SortedDictionary<int, (string Id, StringBuilder Name, StringBuilder Arguments)>();
            await using var stream = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, AtomicFile.Utf8);
            while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
            {
                idle.CancelAfter(RequestTimeout);
                ct.ThrowIfCancellationRequested();
                if (DataOf(line) is not { } payload) continue;
                if (payload == "[DONE]") break;
                JsonDocument json;
                try { json = JsonDocument.Parse(payload); }
                catch (JsonException) { continue; }
                using (json)
                {
                    if (json.RootElement.ValueKind == JsonValueKind.Object) ReadChunk(json.RootElement, result, calls, throttle);
                }
            }
            result.ToolCalls = [.. calls.Select(kv =>
            {
                var id = kv.Value.Id.Length > 0 ? kv.Value.Id : $"call_{kv.Key}_{Guid.NewGuid().ToString("N")[..8]}";
                return new ToolCall(id, kv.Value.Name.ToString(), kv.Value.Arguments.ToString());
            })];
            // a tool call cut off halfway has broken arguments: the model writes it again after the context has room
            if (result.FinishReason == "length") result.ToolCalls = [];
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The engine did not answer for {RequestTimeout.TotalSeconds:0} s.");
        }
    }

    private void ReadChunk(JsonElement chunk, StreamResult result, SortedDictionary<int, (string Id, StringBuilder Name, StringBuilder Arguments)> calls, StreamThrottle throttle)
    {
        if (chunk.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var count) && count > 0)
            result.ContextTokens = count;
        if (chunk.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            result.TokensPerSecond = Number(t, "predicted_per_second");
            result.PromptTokensPerSecond = Number(t, "prompt_per_second");
        }
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object) return;
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) result.FinishReason = reason.GetString();
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return;
        var content = Text(delta, "content");
        var reasoning = Text(delta, "reasoning_content");
        if (content.Length > 0 || reasoning.Length > 0)
        {
            throttle.Add(content, reasoning);
            TokensStreamed?.Invoke(1);   // the server streams one token per chunk
        }
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array) return;
        foreach (var tc in toolCalls.EnumerateArray())
        {
            int index = tc.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
            if (!calls.TryGetValue(index, out var call)) call = ("", new StringBuilder(), new StringBuilder());
            if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } newId) call.Id = newId;
            if (tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                call.Name.Append(Text(f, "name"));
                call.Arguments.Append(Text(f, "arguments"));
            }
            calls[index] = call;
        }
    }

    private static string Text(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Number(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
