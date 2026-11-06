using System.Text.Json;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>The model files on disk for every model in the catalog: what is there, what is verified, and the downloads.
/// Events and properties change on background threads: the UI marshals them itself.</summary>
public sealed class ModelStore : IDisposable
{
    /// <param name="OkHash">What the .ok beside the file says: null none, "" an empty one (its version unknown, trusted), else the sha256
    /// the file had when it was checked.</param>
    /// <param name="Sha">The catalog's sha256 values for this file; empty: nothing to compare with.</param>
    public sealed record FileStatus(string Name, ulong Expected, ulong Have, string? OkHash = null, IReadOnlyList<string>? Sha = null)
    {
        public double Fraction => Expected == 0 ? 0 : Math.Min(1, (double)Have / Expected);
        /// <summary>Checked, and of the version the catalog names.</summary>
        public bool Verified => OkHash != null && !Stale;
        /// <summary>Another version than the catalog names: checked with another sha256, or a size that no longer matches. It keeps
        /// working until the update.</summary>
        public bool Stale => OkHash != null && (Have != Expected || OkHash.Length > 0 && Sha is { Count: > 0 } s && !s.Contains(OkHash, StringComparer.OrdinalIgnoreCase));
        /// <summary>Put in the folder by hand: the exact size is enough to use it (sha256 is checked by our own download). A file of
        /// another version still runs until the update.</summary>
        public bool Complete => Verified || Stale || Have == Expected;
    }

    public sealed record ModelStatus
    {
        public IReadOnlyList<FileStatus> Files { get; init; } = Array.Empty<FileStatus>();
        /// <summary>The second engine's image file.</summary>
        public FileStatus? Vision { get; init; }
        /// <summary>The main engine's image files.</summary>
        public IReadOnlyList<FileStatus> TieredVision { get; init; } = Array.Empty<FileStatus>();
        public FileStatus? Draft { get; init; }
        public IReadOnlyList<FileStatus> Extras { get; init; } = Array.Empty<FileStatus>();
        public ulong Have => Files.Aggregate(0UL, (n, f) => n + f.Have);
        public ulong Expected => Files.Aggregate(0UL, (n, f) => n + f.Expected);
        public double Fraction => Expected == 0 ? 0 : (double)Have / Expected;
        public bool IsComplete => Files.Count > 0 && Files.All(f => f.Complete) && Extras.All(f => f.Complete);
        public bool AllVerified => Files.Count > 0 && Files.All(f => f.Verified);
        /// <summary>The catalog names another version of some files: Update downloads only those.</summary>
        public bool UpdateAvailable => Mine.Any(f => f.Stale);
        public ulong UpdateBytes => Mine.Where(f => f.Stale).Aggregate(0UL, (n, f) => n + f.Expected);
        /// <summary>The model's own files, not the shared image files.</summary>
        private IEnumerable<FileStatus> Mine => Files.Concat(Extras).Concat(Draft is { } d ? new[] { d } : Array.Empty<FileStatus>());
        /// <summary>The headers sit at the start of every shard, so the plan can be made long before a download ends.</summary>
        public bool HeadersAvailable => Files.Count > 0 && Files.All(f => f.Have >= Math.Min(f.Expected, 32UL << 20));
    }

    public ModelCatalog Catalog { get; }
    public IReadOnlyDictionary<string, ModelStatus> Status { get; private set; } = new Dictionary<string, ModelStatus>();
    public IReadOnlySet<string> Downloading { get { lock (gate) return new HashSet<string>(downloads.Keys); } }
    /// <summary>Bytes per second, all downloads together.</summary>
    public double Speed { get; private set; }
    /// <summary>What the download is doing, per model.</summary>
    public IReadOnlyDictionary<string, string> LastMessage { get { lock (gate) return new Dictionary<string, string>(messages); } }
    /// <summary>What went wrong, for the model card (the Mac showed these nowhere).</summary>
    public IReadOnlyDictionary<string, string> LastError { get { lock (gate) return new Dictionary<string, string>(errors); } }
    /// <summary>The pack being built: model id and how far (0..1, null: no figure known).</summary>
    public IReadOnlyDictionary<string, double?> PackProgress { get { lock (gate) return new Dictionary<string, double?>(packs); } }
    public event Action? Changed;
    /// <summary>Which engine runs a model on this PC, for the image files that engine reads; the host sets it. Null: the second engine's.</summary>
    public Func<ModelInfo, EngineKind?>? EngineOf { get; set; }

    private readonly object gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> downloads = new();
    private readonly Dictionary<string, string> messages = new(), errors = new();
    private readonly Dictionary<string, double?> packs = new();
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Timer? timer;
    private ulong lastTotal;
    private DateTime lastTick = DateTime.UtcNow;

    /// <summary>After a download: builds the pack. Tests replace it.</summary>
    internal Func<ModelInfo, Action<double?, string>, CancellationToken, Task<string?>> BuildPack { get; set; } = PackBuilder.Build;
    /// <summary>Tests: no waiting between attempts.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <param name="catalog">Tests give a small catalog of their own.</param>
    /// <param name="http">Tests give a client with a fake server behind it.</param>
    /// <param name="timer">The 2 s status; tests call Refresh themselves.</param>
    public ModelStore(ModelCatalog? catalog = null, HttpClient? http = null, bool timer = true)
    {
        Catalog = catalog ?? ModelCatalog.Shared;
        ownsHttp = http == null;
        this.http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        AdoptUpstreamNames();
        Refresh();
        if (timer) this.timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        timer?.Dispose();
        List<(CancellationTokenSource Cts, Task Task)> running;
        lock (gate) running = downloads.Values.ToList();
        foreach (var d in running) d.Cts.Cancel();
        // not removed from the list of downloads: closing the app is not a pause, the next start resumes them
        try { Task.WaitAll(running.Select(d => d.Task).ToArray(), TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        if (ownsHttp) http.Dispose();
    }

    public ModelStatus StatusOf(string id) => Status.TryGetValue(id, out var s) ? s : new ModelStatus();

    public double? EtaSeconds(string id)
    {
        var s = StatusOf(id);
        return Speed > 1 && Downloading.Contains(id) ? (s.Expected - Math.Min(s.Have, s.Expected)) / Speed : null;
    }

    /// <summary>The chosen folder is gone (a disconnected drive): the app says so and deletes nothing.</summary>
    public static bool FolderMissing
    {
        get
        {
            var root = Paths.ModelsRoot;
            return root != Paths.DefaultModelsRoot && !Directory.Exists(root);
        }
    }

    public static string FolderMissingMessage => L($"De modelmap {Paths.ModelsRoot} staat niet (meer) op deze pc. Sluit de schijf aan of kies een andere map; er is niets verwijderd.");

    public void Refresh()
    {
        var next = new Dictionary<string, ModelStatus>();
        foreach (var m in Catalog.All)
        {
            var dir = Paths.ModelDir(m.Id);
            FileStatus F(string d, string name, ulong size, IReadOnlyList<string> sha) => FileStatusOf(d, name, size, sha);
            var vision = Catalog.VisionFor(m);
            var tiered = Catalog.TieredVisionFor(m);
            next[m.Id] = new ModelStatus
            {
                Files = m.Files.Select(f => F(dir, f.Local, f.Size, f.Sha256)).ToList(),
                Extras = (m.Extras ?? Array.Empty<ModelFile>()).Select(f => F(dir, f.Local, f.Size, f.Sha256)).ToList(),
                Vision = vision == null ? null : F(Paths.VisionDir, vision.Local, vision.Size, vision.Sha256),
                TieredVision = tiered == null ? Array.Empty<FileStatus>() : tiered.Files.Select(f => F(tiered.Folder, f.Local, f.Size, f.Sha256)).ToList(),
                Draft = m.Draft == null ? null : F(dir, m.Draft.Local, m.Draft.Size, m.Draft.Sha256),
            };
        }
        Status = next;
        ulong total = next.Values.Aggregate(0UL, (n, s) => n + s.Have + (s.Vision?.Have ?? 0) + s.TieredVision.Aggregate(0UL, (t, f) => t + f.Have));
        UpdateSpeed(total, DateTime.UtcNow);
        Changed?.Invoke();
    }

    /// <summary>The rolling speed: 0.8 of what it was, 0.2 of the last interval; nothing to compare with at first.</summary>
    internal void UpdateSpeed(ulong total, DateTime now)
    {
        if (lastTotal == 0)
        {
            lastTotal = total;
            lastTick = now;
        }
        else if ((now - lastTick).TotalSeconds > 0.5)
        {
            double inst = total > lastTotal ? (total - lastTotal) / (now - lastTick).TotalSeconds : 0;
            Speed = Speed == 0 ? inst : 0.8 * Speed + 0.2 * inst;
            lastTotal = total;
            lastTick = now;
        }
    }

    /// <summary>One file as it is on disk, and what its .ok says.</summary>
    public static FileStatus FileStatusOf(string dir, string name, ulong size, IReadOnlyList<string> sha)
    {
        var path = Path.Combine(dir, name);
        string? ok = null;
        try { if (File.Exists(path + ".ok")) ok = File.ReadAllText(path + ".ok").Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = ""; }
        return new FileStatus(name, size, FileSize(path), ok, sha);
    }

    public static ulong FileSize(string path)
    {
        try { return (ulong)new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    public IReadOnlyList<string> ShardPaths(ModelInfo model) => model.Files.Select(f => Path.Combine(Paths.ModelDir(model.Id), f.Local)).ToList();

    public string? DraftPath(ModelInfo model) =>
        model.Draft == null || StatusOf(model.Id).Draft?.Complete != true ? null : Path.Combine(Paths.ModelDir(model.Id), model.Draft.Local);

    public string? VisionPath(ModelInfo model)
    {
        var v = Catalog.VisionFor(model);
        return v == null || StatusOf(model.Id).Vision?.Complete != true ? null : Path.Combine(Paths.VisionDir, v.Local);
    }

    private bool OnMainEngine(ModelInfo model) => EngineOf?.Invoke(model) == EngineKind.Tiered;

    /// <summary>The image files of the engine that runs this model here; empty: that engine has none for it.</summary>
    public IReadOnlyList<FileStatus> VisionStatus(ModelInfo model)
    {
        var st = StatusOf(model.Id);
        return OnMainEngine(model) ? st.TieredVision : st.Vision is { } v ? new[] { v } : Array.Empty<FileStatus>();
    }

    /// <summary>Their paths, in the catalog's order, once all of them are here; else empty.</summary>
    public IReadOnlyList<string> VisionPaths(ModelInfo model)
    {
        var status = VisionStatus(model);
        if (status.Count == 0 || status.Any(f => !f.Complete)) return Array.Empty<string>();
        var folder = OnMainEngine(model) ? Catalog.TieredVisionFor(model)!.Folder : Paths.VisionDir;
        return status.Select(f => Path.Combine(folder, f.Name)).ToList();
    }

    private IEnumerable<DownloadItem> VisionItems(ModelInfo model)
    {
        if (!OnMainEngine(model))
            return Catalog.VisionFor(model) is { } v ? new[] { new DownloadItem(Paths.VisionDir, v.Local, v.Size, v.Sha256, v.Url, null) } : Array.Empty<DownloadItem>();
        return Catalog.TieredVisionFor(model) is { } t
            ? t.Files.Select(f => new DownloadItem(t.Folder, f.Local, f.Size, f.Sha256, f.Url, null))
            : Array.Empty<DownloadItem>();
    }

    /// <summary>Files that came down under the name upstream gave them get their own name: never in a file name for the user
    /// (brief 2.2). Nothing is lost: a pack that named them is built again, its staleness check sees that.</summary>
    private void AdoptUpstreamNames()
    {
        foreach (var m in Catalog.All)
        {
            var dir = Paths.ModelDir(m.Id);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in m.AllFiles)
            {
                var name = Uri.UnescapeDataString(f.Url[(f.Url.LastIndexOf('/') + 1)..]);
                var from = Path.Combine(dir, name);
                var to = Path.Combine(dir, f.Local);
                try
                {
                    if (name == f.Local || !File.Exists(from) || File.Exists(to)) continue;
                    File.Move(from, to);
                    if (File.Exists(from + ".ok")) File.Move(from + ".ok", to + ".ok");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // MARK: - downloads

    private static string ListFile => Path.Combine(Paths.AppData, "downloads.json");

    private void SaveList()
    {
        try
        {
            var tmp = ListFile + ".tmp";
            lock (gate) File.WriteAllText(tmp, JsonSerializer.Serialize(wanted));
            File.Move(tmp, ListFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private List<string> wanted = new();

    /// <summary>Downloads the app was closed in the middle of go on; call at start.</summary>
    public void ResumeInterrupted()
    {
        try
        {
            if (!File.Exists(ListFile)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ListFile)) ?? new();
            foreach (var id in list) if (Catalog.Model(id) != null) StartDownload(id);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public void StartDownload(string id) => StartDownload(id, false);

    /// <summary>Only the image files of a model that is already here, with the same pinned checks.</summary>
    public void StartVisionDownload(string id) => StartDownload(id, true);

    private void StartDownload(string id, bool visionOnly)
    {
        var model = Catalog.Model(id);
        if (model == null) return;
        if (FolderMissing)
        {
            lock (gate) errors[id] = FolderMissingMessage;
            Changed?.Invoke();
            return;
        }
        var cts = new CancellationTokenSource();
        lock (gate)
        {
            if (downloads.ContainsKey(id)) return;
            errors.Remove(id);
            if (!wanted.Contains(id)) wanted.Add(id);
            downloads[id] = (cts, Task.Run(() => Run(model, visionOnly, cts.Token)));
        }
        SaveList();
        Changed?.Invoke();
    }

    /// <summary>Pause: the partial files stay and the next start of this download resumes them.</summary>
    public void StopDownload(string id)
    {
        (CancellationTokenSource Cts, Task Task) d;
        lock (gate)
        {
            wanted.Remove(id);
            if (!downloads.TryGetValue(id, out d)) { d = default; }
        }
        SaveList();
        d.Cts?.Cancel();
    }

    /// <summary>Waits for a running download to end (tests, and before a move of the folder).</summary>
    public Task WaitForDownload(string id)
    {
        lock (gate) return downloads.TryGetValue(id, out var d) ? d.Task : Task.CompletedTask;
    }

    private void Say(string id, string text)
    {
        lock (gate) messages[id] = text;
        Changed?.Invoke();
    }

    /// <summary>Only the error: the card shows it once, in its own colour, and not again as the progress line.</summary>
    private void Fail(string id, string text)
    {
        lock (gate) errors[id] = text;
        Changed?.Invoke();
    }

    private async Task Run(ModelInfo model, bool visionOnly, CancellationToken ct)
    {
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var d = new Downloader(http) { RetryDelay = RetryDelay, Replaced = stop.Cancel };
            var dir = Paths.ModelDir(model.Id);
            var items = new List<DownloadItem>();
            if (!visionOnly) foreach (var f in model.AllFiles) items.Add(new DownloadItem(dir, f.Local, f.Size, f.Sha256, f.Url, f.Stamp));
            items.AddRange(VisionItems(model));
            var results = await Task.WhenAll(items.Select(item => d.Fetch(item, s => Say(model.Id, s), e => Fail(model.Id, e), stop.Token)));
            if (ct.IsCancellationRequested) return;
            if (results.All(ok => ok))
            {
                Say(model.Id, L($"Alle bestanden gecontroleerd: {model.Name}"));
                lock (gate) wanted.Remove(model.Id);
                SaveList();
                Refresh();
                await BuildPackIfNeeded(model, ct);
            }
            else
            {
                Say(model.Id, L($"Download onvolledig: {model.Name}"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Fail(model.Id, LogFilter.Clean(e.Message));
        }
        finally
        {
            lock (gate) downloads.Remove(model.Id);
            Refresh();
        }
    }

    /// <summary>The pack, when this model has one and it is missing or out of date. On return: null, or what went wrong.</summary>
    public async Task<string?> BuildPackIfNeeded(ModelInfo model, CancellationToken ct = default)
    {
        if (model.Pack == null || PackBuilder.WhyNotReady(model) == null) return null;
        lock (gate) packs[model.Id] = 0;
        Changed?.Invoke();
        try
        {
            var error = await BuildPack(model, (fraction, text) =>
            {
                lock (gate) packs[model.Id] = fraction;
                Say(model.Id, text);
            }, ct);
            if (error != null) Fail(model.Id, error);
            else Say(model.Id, L($"Pack klaar: {model.Name}"));
            return error;
        }
        finally
        {
            lock (gate) packs.Remove(model.Id);
            Changed?.Invoke();
        }
    }

    /// <summary>Verwijder model. Never while it downloads; a missing folder stays a message, not a deletion. The vision file is
    /// shared between models and stays. On return: null, or why not.</summary>
    public string? DeleteModel(string id)
    {
        if (Catalog.Model(id) == null) return L("Dit model staat niet in de catalogus.");
        if (FolderMissing) return FolderMissingMessage;
        if (Downloading.Contains(id)) return L("Dit model wordt nog gedownload. Pauzeer de download eerst.");
        lock (gate) { wanted.Remove(id); errors.Remove(id); messages.Remove(id); }
        SaveList();
        try
        {
            var dir = Paths.ModelDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return L($"Kon het model niet verwijderen: {e.Message}");
        }
        Refresh();
        return null;
    }
}
