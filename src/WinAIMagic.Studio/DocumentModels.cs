using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Documents;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Studio;

/// Which model does what in the documents module: small work (search questions, notes, spelling) goes to a fast model
/// when one fits next to the heavy one, the real writing to the heavy one. The heavy model is the app's own engine.
public enum ModelRole { Fast, Heavy }

public enum EngineRunState { Stopped, Starting, Running, Failed }

/// `Text`: what is being loaded, or why it failed.
public readonly record struct EngineStatus(EngineRunState State, string Text = "")
{
    public bool IsRunning => State == EngineRunState.Running;

    /// What the state means, in the words of the documents module.
    public string StatusText(string idle) => State switch
    {
        EngineRunState.Running => L("Draait"),
        EngineRunState.Starting or EngineRunState.Failed => Text,
        _ => idle,
    };
}

/// A downloaded model. `Memory`: what it takes when loaded, else its size on disk.
public sealed record DocumentModel(string Id, string ShortName, ulong Memory);

/// What the documents module needs of the engines; the app has them, this library does not. Everything on the UI thread.
public interface IDocumentEngines
{
    /// A state changed. Raised on the UI thread.
    event Action? StateChanged;

    /// The model chosen under Modellen, loaded or not.
    DocumentModel? HeavyModel { get; }
    IReadOnlyList<DocumentModel> DownloadedModels { get; }

    Uri HeavyBaseUrl { get; }
    /// The model the heavy engine has loaded, else the one chosen; what requests name.
    string HeavyModelId { get; }
    EngineStatus HeavyState { get; }
    /// False: nothing is downloaded yet.
    bool CanStartHeavy { get; }
    /// Returns at once: HeavyState is Starting by then.
    void StartHeavy();

    /// The second engine: Settings.Port + 1 (DocumentModels.FastPort), only reachable from this PC.
    Uri FastBaseUrl { get; }
    /// What the fast engine has loaded or is loading, if anything.
    string? FastLoadedModelId { get; }
    EngineStatus FastState { get; }
    /// May a second model run beside the heavy one on this PC (WinAIMagic-PROMPT.md §7)? Takes the place of the Mac's
    /// rule resident + small + 2.9e9 + 8e9 <= RAM. False: one model does everything.
    bool CanRunFastBeside();
    /// Returns at once: FastState is Starting by then. May throw when the model cannot be read. Context: DocumentModels.FastContext.
    void StartFast(string modelId);
    /// Also when it is still starting; FastState goes to Stopped.
    void StopFast();
    Task StopFastAndWaitAsync();
}

public static class DocumentModels
{
    /// The context of the fast model: its work is one small piece at a time.
    public const int FastContext = 16_384;

    /// The second engine takes the port after the app's own.
    public static int FastPort => Settings.Shared.Port + 1;

    /// The smallest downloaded model next to `heavy`, when this PC can run both. `chosen`: the model the user chose under Modellen;
    /// it goes first when it is downloaded and is not the heavy one, as long as it fits beside it.
    public static DocumentModel? FastModel(DocumentModel? heavy, IEnumerable<DocumentModel> downloaded, Func<bool> canRunBeside, string chosen = "")
    {
        if (heavy == null) return null;
        var list = downloaded.ToList();
        var small = list.FirstOrDefault(m => m.Id == chosen && m.Id != heavy.Id)
                    ?? list.Where(m => m.Id != heavy.Id && m.Memory < heavy.Memory).MinBy(m => m.Memory);
        return small != null && canRunBeside() ? small : null;
    }
}

/// Starts the models for a piece of work and says where to reach them.
public sealed class DocumentEngines : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IDocumentEngines engines;
    private string? fastProblem;
    /// The model that failed: it is not tried again for every page, only when the user asks or another model is the choice.
    private string? failedModel;
    private bool stoppedByUser;

    public DocumentEngines(IDocumentEngines engines)
    {
        this.engines = engines;
        engines.StateChanged += () => Raise("");
    }

    private void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Why the fast model did not start. The heavy model does its work then; the user must be able to see that.
    public string? FastProblem
    {
        get => fastProblem;
        private set
        {
            if (fastProblem == value) return;
            fastProblem = value;
            Raise("");
        }
    }

    public DocumentModel? FastChoice => DocumentModels.FastModel(engines.HeavyModel, engines.DownloadedModels, engines.CanRunFastBeside, Settings.Shared.FastModel);

    // The status lines and the button of the panel.

    public bool HeavyIsRunning => engines.HeavyState.IsRunning;
    public string HeavyStatusLine => engines.HeavyState.StatusText(L("Start vanzelf als er geschreven wordt."));
    /// There is no fast model: said in place of its row.
    public string OneModelLine => AppLanguages.Current switch
    {
        // the table has this with "this Mac"
        AppLanguage.Nl => "Eén model voor alles: er past geen tweede model naast op deze pc, of er is er geen gedownload.",
        AppLanguage.De => "Ein Modell für alles: Auf diesem PC passt kein zweites Modell daneben, oder es ist keines heruntergeladen.",
        AppLanguage.Fr => "Un seul modèle pour tout : aucun second modèle ne tient à côté sur ce PC, ou aucun n'est téléchargé.",
        _ => "One model for everything: no second model fits next to it on this PC, or none is downloaded.",
    };
    public bool FastIsRunning => engines.FastState.IsRunning;
    public string FastStatusLine => FastProblem is { } problem
        ? L($"Start niet; het zware model doet dit werk nu. {problem}")
        : engines.FastState.StatusText(L("Start vanzelf bij de eerste snelle actie."));
    /// The button stops (disable it while the studio is busy); else it calls StartFastNow.
    public bool FastButtonStops => engines.FastState.IsRunning || engines.FastState.State == EngineRunState.Starting;
    public string FastButtonTitle => FastButtonStops ? L("Stop") : FastProblem == null ? L("Start nu") : L("Probeer opnieuw");

    public async Task<(Uri Url, string Model)> EndpointAsync(ModelRole role, CancellationToken ct = default)
    {
        if (role == ModelRole.Fast && FastChoice is { } small && failedModel != small.Id)
        {
            if (await StartingFast(small, ct)) return (engines.FastBaseUrl, small.Id);
        }
        return await Heavy(ct);
    }

    /// True when it runs. Stopping the work is not a failure of the model; anything else is, and is kept for the user.
    private async Task<bool> StartingFast(DocumentModel small, CancellationToken ct)
    {
        stoppedByUser = false;
        try
        {
            await StartFast(small, ct);
            failedModel = null;
            FastProblem = null;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            engines.StopFast();
            // stopped with the button: it starts again by itself at the next quick action
            if (stoppedByUser) return false;
            failedModel = small.Id;
            FastProblem = Readable(e.Message, engines.FastBaseUrl.Port);
            return false;
        }
    }

    /// The engine's own words, unless the cause is known.
    public static string Readable(string problem, int port) =>
        problem.Contains("HTTP server error", StringComparison.Ordinal) || problem.Contains("bind", StringComparison.Ordinal)
            ? L($"Poort {port} is al in gebruik door een ander programma.") : problem;

    /// The button: also after a failure. Nothing to wait for; the state and FastProblem say how it went.
    public async Task StartFastNow()
    {
        if (FastChoice is not { } small) return;
        failedModel = null;
        FastProblem = null;
        try { await StartingFast(small, CancellationToken.None); }
        catch (OperationCanceledException) { }
    }

    public void StopFast()
    {
        stoppedByUser = true;
        failedModel = null;
        FastProblem = null;
        engines.StopFast();
    }

    private async Task<(Uri Url, string Model)> Heavy(CancellationToken ct)
    {
        if (!engines.HeavyState.IsRunning)
        {
            if (engines.HeavyState.State != EngineRunState.Starting)
            {
                if (!engines.CanStartHeavy) throw new Zip.Failure(L("Download eerst een model onder Modellen."));
                engines.StartHeavy();
            }
            await Wait(() => engines.HeavyState, ct);
        }
        return (engines.HeavyBaseUrl, engines.HeavyModelId);
    }

    private async Task StartFast(DocumentModel small, CancellationToken ct)
    {
        if (engines.FastState.IsRunning && engines.FastLoadedModelId == small.Id) return;
        if (engines.FastLoadedModelId != small.Id) await engines.StopFastAndWaitAsync();
        if (engines.FastState.State != EngineRunState.Starting) engines.StartFast(small.Id);
        await Wait(() => engines.FastState, ct);
    }

    private static async Task Wait(Func<EngineStatus> state, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = state();
            switch (now.State)
            {
                case EngineRunState.Running: return;
                case EngineRunState.Failed: throw new Zip.Failure(now.Text);
                case EngineRunState.Stopped: throw new Zip.Failure(L("De engine is gestopt."));
                default: await Task.Delay(300, ct); break;
            }
        }
    }
}

/// One call to a local model, with the answer as it comes. Without tools: a page from the web can only be read here.
public static class LocalModel
{
    public sealed class Result
    {
        public string Text { get; set; } = "";
        public bool CutOff { get; set; }
        /// <summary>The model began to repeat itself; the stream was stopped and the repetition taken off.</summary>
        public bool Looped { get; set; }
        public double? TokensPerSecond { get; set; }
    }

    /// <summary>Where a text starts to go round in circles: the second copy of a line of some length once that line has come three
    /// times. Null when it does not. Ling-3.0-tiny wrote "maar de HR-afdeling heeft de bevoegdheid om dit te bevestigen" until its
    /// token limit in 4 of 12 sections on the test PC; that kept the machine at full load for nothing.</summary>
    public static int? LoopStart(string text, int times = 3, int minLength = 25)
    {
        var seen = new Dictionary<string, List<int>>();
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text[start..end].Trim().TrimStart('-', '*', ' ').Trim();
            if (line.Length >= minLength)
            {
                if (!seen.TryGetValue(line, out var at)) seen[line] = at = [];
                at.Add(start);
                if (at.Count >= times) return at[1];
            }
            start = end + 1;
        }
        return null;
    }

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3600);

    /// All traffic to the engines; tests put a fake behind it.
    public static HttpClient Http { get; set; } = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// The speed gauge counts this writing too. On the caller's thread.
    public static event Action<int>? TokensStreamed;

    /// `thinking`: how many tokens the model may think first; 0 is none. Thinking takes most of the time of a piece of
    /// writing (measured on Qwen3.8: 1,400 tokens for an answer of 150), so only what needs weighing gets it.
    ///
    /// `onText` runs on the caller's thread (its SynchronizationContext), and at most ten times a second. This function
    /// does not: a screen changed from another thread locks up the whole app.
    public static Task<Result> CompleteAsync(Uri url, string model, string system, string user, int thinking = 0, int maxTokens = 4096,
                                             double temperature = 0.3, Action<string>? onText = null, CancellationToken ct = default)
    {
        var context = SynchronizationContext.Current;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["stream"] = true,
            ["temperature"] = temperature,
            // the thinking counts too
            ["max_tokens"] = maxTokens + thinking,
            // a small model repeats a line until its limit (Ling: 4 of 12 sections on the test PC, 1 of 12 with this); the
            // second engine's DRY sampler makes a repeated stretch less likely, others ignore the field
            ["dry_multiplier"] = 0.8,
            ["dry_allowed_length"] = 3,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = thinking > 0 },
            // the budget too when it is off: GLM's template ignores enable_thinking and always opens with <think>
            ["thinking_budget_tokens"] = thinking,
        };
        if (thinking > 0) body["reasoning_budget_message"] = "\n\n" + L("Genoeg nagedacht; nu schrijf ik het antwoord.") + "\n";
        var key = Settings.Shared.ApiKey;
        return Task.Run(() => Stream(url, body, key, context, onText, ct), ct);
    }

    private static async Task<Result> Stream(Uri url, JsonObject body, string key, SynchronizationContext? context, Action<string>? onText, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url.ToString().TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200)
            {
                var message = (await resp.Content.ReadAsStringAsync(idle.Token).ConfigureAwait(false)).Replace("\r", "").Replace("\n", "");
                throw new Zip.Failure(L($"Engine gaf {(int)resp.StatusCode}: {message.Prefix(300)}"));
            }
            var result = new Result();
            var text = new StringBuilder();
            var shown = DateTime.MinValue;
            int unseenTokens = 0;
            await using var stream = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
            {
                idle.CancelAfter(RequestTimeout);
                ct.ThrowIfCancellationRequested();
                if (!line.HasPrefix("data: ")) continue;
                var payload = line[6..];
                if (payload == "[DONE]") break;
                if (!ReadChunk(payload, result, text, ref unseenTokens)) continue;
                // a model that goes round in circles is stopped here: closing the stream stops the engine too
                if (text.Length > 0 && text[^1] == '\n' && LoopStart(text.ToString()) is int loop)
                {
                    text.Length = loop;
                    result.Looped = true;
                    result.CutOff = false;
                    break;
                }
                if (DateTime.UtcNow - shown >= TimeSpan.FromSeconds(0.1))
                {
                    shown = DateTime.UtcNow;
                    await Show(context, text, unseenTokens, onText).ConfigureAwait(false);
                    unseenTokens = 0;
                }
            }
            await Show(context, text, unseenTokens, onText).ConfigureAwait(false);
            result.Text = text.ToString();
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The engine did not answer for {RequestTimeout.TotalSeconds:0} s.");
        }
    }

    private static bool ReadChunk(string payload, Result result, StringBuilder text, ref int unseenTokens)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(payload); }
        catch (JsonException) { return false; }
        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("predicted_per_second", out var speed) && speed.ValueKind == JsonValueKind.Number)
                result.TokensPerSecond = speed.GetDouble();
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return false;
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) return false;
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length") result.CutOff = true;
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                bool content = delta.TryGetProperty("content", out var piece);
                if (content && piece.ValueKind == JsonValueKind.String) text.Append(piece.GetString());
                if (content || delta.TryGetProperty("reasoning_content", out _)) unseenTokens++;
            }
            return true;
        }
    }

    /// Waits for the caller's thread: what is shown after the answer returned would overwrite what the caller does with it.
    private static Task Show(SynchronizationContext? context, StringBuilder text, int tokens, Action<string>? onText)
    {
        var current = text.ToString();
        void Run()
        {
            TokensStreamed?.Invoke(tokens);
            if (current.Length > 0) onText?.Invoke(current);
        }
        if (context == null)
        {
            Run();
            return Task.CompletedTask;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                Run();
                done.SetResult();
            }
            catch (Exception e) { done.SetException(e); }
        }, null);
        return done.Task;
    }
}

/// What is taken out of a question before it goes to a search engine.
public static class SearchQuery
{
    private const string Money = "(?:euro|eur|mln|miljoen|mld|miljard|k€|dollar|usd)";
    private static readonly Rx[] Patterns =
    [
        new("\\b[\\w.+-]+@[\\w-]+(?:\\.[\\w-]+)+\\b"),                                            // e-mail
        new("\\b[A-Z]{2}\\d{2}\\s?(?:[A-Z0-9]{4}\\s?){2,7}[A-Z0-9]{1,4}\\b"),                      // IBAN
        new("(?:[€$£]\\s?\\d[\\d.,]*(?:\\s?" + Money + ")?|\\b\\d[\\d.,]*\\s?" + Money + "\\b(?:\\s?" + Money + "\\b)?)", RegexOptions.IgnoreCase),
        new("(?<!\\w)(?:\\+|00)\\d{2}[\\s-]?(?:\\(0\\))?[\\s-]?\\d(?:[\\s-]?\\d){7,9}\\b|\\b0\\d{1,3}[\\s-]?\\d(?:[\\s-]?\\d){5,7}\\b"),   // phone
        new("\\b\\d{9}\\b"),                                                                        // BSN
        new("\\b\\d[\\d.,]{5,}\\d\\b"),                                                             // other long numbers
    ];

    /// The question without e-mail addresses, bank and phone numbers, amounts, long numbers and the `sensitive` terms
    /// (the client, the author). Years and article numbers stay.
    public static string Clean(string query, IEnumerable<string>? sensitive = null)
    {
        var output = query;
        foreach (var p in Patterns) output = p.Replace(output, _ => " ");
        foreach (var term in (sensitive ?? []).OrderByDescending(t => t.GraphemeCount()))
        {
            var escaped = Regex.Escape(term.TrimWSNL());
            if (escaped.Length == 0) continue;
            output = new Rx("(?<!\\w)" + escaped + "(?!\\w)", RegexOptions.IgnoreCase).Replace(output, _ => " ");
        }
        output = new Rx("\\s+([,.;:])").Replace(output, m => m.First ?? "");
        return new Rx("\\s{2,}").Replace(output, _ => " ").Trim(' ', ',', ';', ':', '-', '\n', '\t');
    }
}

/// The source references of a researched text: the code, not the model, makes the list of sources.
public static class Citations
{
    public sealed record Source(string Title, string Url);

    /// `Unknown`: the numbers the model made up.
    public sealed record Finished(string Markdown, List<int> Unknown, int Cited);

    private static readonly Rx Reference = new("\\[(\\d{1,3}(?:\\s*,\\s*\\d{1,3})*)\\](?!\\()");

    private static IEnumerable<int> Numbers(Rx.Match m) =>
        (m.First ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => Str.ToInt(n.TrimWS())).OfType<int>();

    /// Numbers the sources in the order the text uses them, drops references to what does not exist, and adds the list.
    public static Finished Finish(string text, IReadOnlyList<Source> sources, string heading, string consulted)
    {
        var order = new List<int>();
        var unknown = new List<int>();
        foreach (var m in Reference.Matches(text))
        {
            foreach (var n in Numbers(m))
            {
                if (n >= 1 && n <= sources.Count)
                {
                    if (!order.Contains(n)) order.Add(n);
                }
                else if (!unknown.Contains(n)) unknown.Add(n);
            }
        }
        var renumbered = Reference.Replace(text, m =>
        {
            var known = Numbers(m).Where(order.Contains).Select(n => order.IndexOf(n) + 1).ToList();
            return known.Count == 0 ? "" : "[" + string.Join(", ", known) + "]";
        });
        var listed = order.Count == 0 ? Enumerable.Range(1, sources.Count).ToList() : order;
        var list = listed.Select((n, i) => $"{i + 1}. [{sources[n - 1].Title.Replace("]", ")")}]({sources[n - 1].Url}) ({consulted})");
        var clean = new Rx("[ \\t]+([.,;:])").Replace(renumbered, m => m.First ?? "").TrimWSNL();
        return new Finished(clean + "\n\n# " + heading + "\n\n" + string.Join("\n", list) + "\n", unknown, order.Count);
    }
}

/// The sections of a Markdown draft, to work on one at a time.
public static class DraftSections
{
    /// `Range`: the whole section with its heading, up to the next heading. `BodyRange`: what follows the heading.
    /// `HasChildren`: a lower heading follows directly, so only a short introduction belongs here.
    public sealed record Section(string Title, int Level, NSRange Range, NSRange BodyRange, bool HasChildren);

    private static readonly Rx Heading = new("^(#{1,6})[ \\t]+(.+?)[ \\t]*#*[ \\t]*$", RegexOptions.Multiline);
    private static readonly Rx Fence = new("^ {0,3}(```|~~~)", RegexOptions.Multiline);

    public static List<Section> List(string text)
    {
        var fences = Fence.Matches(text).Select(m => m.Range.Location).ToList();
        // a line that starts with # inside a code block is no heading
        bool InCode(int at) => fences.Count(f => f < at) % 2 == 1;
        var heads = Heading.Matches(text).Where(m => !InCode(m.Range.Location)).ToList();
        var output = new List<Section>();
        for (int i = 0; i < heads.Count; i++)
        {
            var m = heads[i];
            int end = i + 1 < heads.Count ? heads[i + 1].Range.Location : text.Length;
            int bodyStart = Math.Min(m.Range.Location + m.Range.Length, end);
            int level = m.Groups[0]?.Length ?? 1;
            output.Add(new Section(m.Groups[1] ?? "", level,
                                   new NSRange(m.Range.Location, end - m.Range.Location),
                                   new NSRange(bodyStart, end - bodyStart),
                                   i + 1 < heads.Count && (heads[i + 1].Groups[0]?.Length ?? 1) > level));
        }
        return output;
    }

    /// What an action works on: the selection, else the section around the cursor, else everything after the fields.
    public static NSRange Scope(string text, NSRange selection)
    {
        if (selection.Length > 0 && selection.Location + selection.Length <= text.Length) return selection;
        if (List(text).LastOrDefault(s => s.Range.Location <= selection.Location) is { } section) return section.Range;
        int start = text.Length - new DocumentDraft(text).Body.Length;
        return new NSRange(Math.Max(0, start), text.Length - Math.Max(0, start));
    }

    /// The model's answer without a Markdown fence around it or a heading it was told not to write.
    public static string Cleaned(string answer, string? removingHeading = null)
    {
        var t = answer.TrimWSNL();
        if (t.HasPrefix("```"))
        {
            var lines = t.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].TrimWS().HasPrefix("```")) lines.RemoveAt(lines.Count - 1);
            t = string.Join("\n", lines).TrimWSNL();
        }
        if (removingHeading != null)
        {
            var lines = t.Split('\n');
            if (lines[0].HasPrefix("#") && DocxFields.Normalized(lines[0]).HasSuffix(DocxFields.Normalized(removingHeading)))
                t = string.Join("\n", lines.Skip(1)).TrimWSNL();
        }
        return t;
    }

    /// A section's text must bring no headings along: the sections are counted, and the draft's structure is the template's.
    public static string WithoutHeadings(string text) => Heading.Replace(text, m => "**" + (m.Groups[1] ?? "") + "**");

    internal static string Substring(string text, NSRange range) => text.Substring(range.Location, range.Length);

    internal static string Replacing(string text, NSRange range, string with) =>
        string.Concat(text.AsSpan(0, range.Location), with, text.AsSpan(range.Location + range.Length));
}
