using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinAIMagic.Core.Support;

/// <summary>Keeps a conversation's place in the main engine across a switch and a restart (the Mac's SlotKeeper).
///
/// The engine has one slot: what it read of a conversation (its KV cache) is lost when another conversation, a task, the
/// heartbeat or Documents uses it, and when the engine stops. Reading it again costs minutes on a large model. The engine
/// can write its slot to a file and read it back (<c>--slot-save-path</c>, <c>POST /slots/0?action=save|restore</c>);
/// measured on the Mac with DeepSeek-V4.1-Flash: a conversation of 19,913 tokens read in 145 s, saved in 0.03 s (81 MB),
/// restored after a restart in 0.05 s, and the next question answered in 1.4 s.
///
/// So: when another conversation gets the engine, the one that had it is saved; a conversation that comes back is restored
/// first. After an answer, and before the engine stops, the conversation in it is saved. The engine compares the restored
/// tokens with the new prompt itself, so a conversation that changed meanwhile only reuses what is still the same.</summary>
public sealed class SlotKeeper
{
    private readonly object sync = new();
    /// <summary>One save or restore at a time: two would race for the one slot.</summary>
    private readonly SemaphoreSlim busy = new(1, 1);
    private Uri? baseUrl;
    private string? key;
    private Guid? current;
    private bool unsaved;
    private readonly List<string> log = [];

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Folder { get; }
    /// <summary>A line for the engine's log: what was saved or restored, how large and how fast, or why not.</summary>
    public Action<string>? Note { get; set; }

    public static string DefaultFolder => Path.Combine(Paths.AppData, "EngineMemory");

    public SlotKeeper(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
        lock (All) All.Add(new WeakReference<SlotKeeper>(this));
    }

    public Uri? BaseUrl { get { lock (sync) return baseUrl; } }
    public string? Key { get { lock (sync) return key; } }
    /// <summary>The conversation whose state is in the engine now.</summary>
    public Guid? Current { get { lock (sync) return current; } }
    /// <summary>The conversation in the engine changed since it was saved.</summary>
    public bool HasUnsaved { get { lock (sync) return current != null && unsaved && Settings.Shared.KeepEngineMemory; } }
    /// <summary>Saves and restores, in order, for the tests.</summary>
    public IReadOnlyList<string> Log { get { lock (sync) return [.. log]; } }

    /// <summary>The engine started with <c>--slot-save-path Folder</c>: states fit this key from now on.</summary>
    public void Started(Uri url, string stateKey)
    {
        lock (sync)
        {
            baseUrl = url;
            key = stateKey;
            current = null;
            unsaved = false;
        }
        try { Directory.CreateDirectory(Folder); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The engine stopped: nothing is in it any more.</summary>
    public void Stopped()
    {
        lock (sync)
        {
            baseUrl = null;
            key = null;
            current = null;
            unsaved = false;
        }
    }

    /// <summary>What a saved state belongs to: the same model, context, vision and draft head.</summary>
    public static string StateKey(string modelId, int context, bool vision, bool speculative) =>
        $"{modelId}-c{context.ToString(CultureInfo.InvariantCulture)}-v{(vision ? 1 : 0)}-s{(speculative ? 1 : 0)}";

    /// <summary>The engine takes a plain file name: no slashes, no spaces.</summary>
    public static string FileName(string stateKey, Guid conversation)
    {
        var safe = new string(stateKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return safe + "--" + conversation.ToString("D").ToUpperInvariant() + ".bin";
    }

    /// <summary><paramref name="owner"/> gets the engine for an answer: the conversation that had it is saved, and the owner's state comes back.</summary>
    public async Task EnterAsync(Guid owner)
    {
        if (!Settings.Shared.KeepEngineMemory) return;
        await busy.WaitAsync().ConfigureAwait(false);
        try
        {
            string? k;
            lock (sync)
            {
                k = key;
                if (k == null || baseUrl == null) return;
                if (current == owner) { unsaved = true; return; }
            }
            await SaveCurrentLocked().ConfigureAwait(false);
            lock (sync)
            {
                current = owner;
                unsaved = true;
            }
            var name = FileName(k, owner);
            if (!File.Exists(Path.Combine(Folder, name))) return;
            if (await Call("restore", name, owner).ConfigureAwait(false))
            {
                lock (sync) log.Add("restore " + owner.ToString("D").ToUpperInvariant());
                Touch(name);
            }
        }
        finally { busy.Release(); }
    }

    /// <summary>Something else uses the engine (Documents, a run without a conversation): the conversation in it is saved first.</summary>
    public async Task YieldAsync()
    {
        await busy.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveCurrentLocked().ConfigureAwait(false);
            lock (sync) { current = null; unsaved = false; }
        }
        finally { busy.Release(); }
    }

    /// <summary>Writes the state of the conversation in the engine to its file, when it changed.</summary>
    public async Task SaveCurrentAsync()
    {
        await busy.WaitAsync().ConfigureAwait(false);
        try { await SaveCurrentLocked().ConfigureAwait(false); }
        finally { busy.Release(); }
    }

    private async Task SaveCurrentLocked()
    {
        string? k;
        Guid owner;
        lock (sync)
        {
            if (key == null || current == null || !unsaved || !Settings.Shared.KeepEngineMemory) return;
            k = key;
            owner = current.Value;
            unsaved = false;
        }
        var name = FileName(k, owner);
        if (await Call("save", name, owner).ConfigureAwait(false))
        {
            lock (sync) log.Add("save " + owner.ToString("D").ToUpperInvariant());
            Prune();
        }
    }

    private HttpRequestMessage? Request(string action, string name)
    {
        Uri? url;
        lock (sync) url = baseUrl;
        if (url == null) return null;
        var req = new HttpRequestMessage(HttpMethod.Post, url.ToString().TrimEnd('/') + "/slots/0?action=" + action)
        {
            Content = new StringContent(new JsonObject { ["filename"] = name }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var apiKey = Settings.Shared.ApiKey;
        if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        return req;
    }

    private async Task<bool> Call(string action, string name, Guid owner, TimeSpan? limit = null, string? label = null)
    {
        using var req = Request(action, name);
        if (req == null) return false;
        var watch = Stopwatch.StartNew();
        // a state of several GB: a few seconds on the SSD
        using var cts = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(60));
        try
        {
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            bool ok = (int)resp.StatusCode == 200;
            Note?.Invoke(Describe(label ?? action, owner, ok, body, watch.Elapsed.TotalSeconds));
            return ok;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            Note?.Invoke($"WinAIMagic: gesprek {Short(owner)} {label ?? action} mislukt: {(e is OperationCanceledException ? "duurde te lang" : e.Message)}");
            return false;
        }
    }

    private static string Short(Guid id) => id.ToString("N")[..8].ToUpperInvariant();

    /// <summary>"WinAIMagic: gesprek 1A2B3C4D save: 19917 tokens, 81 MB in 0.03 s", or the engine's error.</summary>
    public static string Describe(string action, Guid owner, bool ok, string body, double seconds)
    {
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (JsonException) { }
        var head = $"WinAIMagic: gesprek {Short(owner)} {action}";
        if (!ok)
        {
            var err = json?["error"]?["message"]?.ToString() ?? json?["error"]?.ToString() ?? body[..Math.Min(body.Length, 200)];
            return head + " mislukt: " + err;
        }
        var tokens = (json?["n_saved"] ?? json?["n_restored"])?.ToString() ?? "?";
        long bytes = 0;
        if ((json?["n_written"] ?? json?["n_read"]) is JsonValue v && v.TryGetValue<long>(out var b)) bytes = b;
        return head + string.Create(CultureInfo.InvariantCulture, $": {tokens} tokens, {bytes / 1e6:0} MB in {seconds:0.00} s");
    }

    /// <summary>For quitting the app: saves within <paramref name="timeout"/>, blocking, so it is done before the engine goes.</summary>
    public void SaveBlocking(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        if (!busy.Wait(limit)) { Note?.Invoke("WinAIMagic: afsluiten, de engine was nog bezig met bewaren: niets bewaard"); return; }
        try
        {
            string? k;
            Guid owner;
            lock (sync)
            {
                if (key == null || current == null || !unsaved || !Settings.Shared.KeepEngineMemory) return;
                k = key;
                owner = current.Value;
            }
            var saved = Task.Run(() => Call("save", FileName(k, owner), owner, limit, "save (afsluiten)")).GetAwaiter().GetResult();
            if (!saved) return;
            lock (sync)
            {
                unsaved = false;
                log.Add("save " + owner.ToString("D").ToUpperInvariant());
            }
        }
        finally { busy.Release(); }
    }

    // MARK: - files

    private void Touch(string name)
    {
        try { File.SetLastWriteTimeUtc(Path.Combine(Folder, name), DateTime.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The most the saved states may take: 40 GB, never more than a tenth of the disk that is free for them.</summary>
    public long Limit
    {
        get
        {
            long free = 0;
            try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder))!).AvailableFreeSpace; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
            return Math.Min(40_000_000_000, Math.Max(2_000_000_000, (free + Used) / 10));
        }
    }

    private List<FileInfo> Files
    {
        get
        {
            try { return [.. new DirectoryInfo(Folder).EnumerateFiles("*.bin")]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    public long Used => Files.Sum(f => f.Length);

    /// <summary>Deletes the oldest states until they fit in the limit (the newest always stays).</summary>
    public void Prune(long? limit = null)
    {
        long cap = limit ?? Limit;
        var all = Files.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = all.Sum(f => f.Length);
        while (total > cap && all.Count > 1)
        {
            var oldest = all[^1];
            all.RemoveAt(all.Count - 1);
            long size = oldest.Length;
            try { oldest.Delete(); total -= size; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A conversation was deleted: its states go too.</summary>
    public void Forget(Guid conversation)
    {
        var tail = "--" + conversation.ToString("D").ToUpperInvariant() + ".bin";
        foreach (var f in Files.Where(f => f.Name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)))
        {
            try { f.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        lock (sync) if (current == conversation) { current = null; unsaved = false; }
    }

    /// <summary>Everything saved, for Settings → Engine.</summary>
    public void Clear()
    {
        foreach (var f in Files)
        {
            try { f.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        lock (sync) { current = null; unsaved = false; }
    }

    // MARK: - every keeper

    private static readonly List<WeakReference<SlotKeeper>> All = [];

    private static List<SlotKeeper> Keepers()
    {
        lock (All)
        {
            All.RemoveAll(w => !w.TryGetTarget(out _));
            return [.. All.Select(w => w.TryGetTarget(out var k) ? k : null).OfType<SlotKeeper>()];
        }
    }

    /// <summary>The keeper of the engine at this address, when it keeps states now.</summary>
    public static SlotKeeper? At(Uri url) => Keepers().FirstOrDefault(k => k.BaseUrl is { } b && SameEngine(b, url));

    /// <summary>A request outside the chat goes to <paramref name="url"/>: the keeper of the engine there saves its conversation first.</summary>
    public static async Task YieldTo(Uri url)
    {
        if (At(url) is { } k) await k.YieldAsync().ConfigureAwait(false);
    }

    /// <summary>A conversation was deleted: its states go, in every keeper's folder.</summary>
    public static void ForgetAll(Guid conversation)
    {
        var keepers = Keepers();
        if (!keepers.Exists(k => SamePath(k.Folder, DefaultFolder))) keepers.Add(new SlotKeeper());
        foreach (var k in keepers) k.Forget(conversation);
    }

    /// <summary>Everything saved, from Settings.</summary>
    public static void ClearAll()
    {
        var keepers = Keepers();
        if (!keepers.Exists(k => SamePath(k.Folder, DefaultFolder))) keepers.Add(new SlotKeeper());
        foreach (var k in keepers) k.Clear();
    }

    /// <summary>What the saved states take in the app's own folder.</summary>
    public static long UsedBytes => (Keepers().FirstOrDefault(k => SamePath(k.Folder, DefaultFolder)) ?? new SlotKeeper()).Used;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool SameEngine(Uri a, Uri b)
    {
        string[] local = ["127.0.0.1", "localhost", "0.0.0.0", "::1", "[::1]"];
        return a.Port == b.Port && (string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) || (local.Contains(a.Host) && local.Contains(b.Host)));
    }
}
