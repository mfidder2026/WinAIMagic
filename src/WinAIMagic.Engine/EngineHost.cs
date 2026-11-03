using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>Ties the engine's parts together: models, backend, memory plan, engine, live metrics, benchmarks, cluster. The engine
/// half of AppState. Events come on background threads; the UI marshals them itself.</summary>
public sealed class EngineHost : IDisposable
{
    public ModelStore Store { get; }
    public IGpuBackend Backend { get; private set; }
    public BackendChoice Choice { get; private set; }
    public LiveMetrics Metrics { get; }
    public BenchmarkRunner Bench { get; }
    public ClusterController Cluster { get; }
    public SystemCheck Check { get; }

    /// <summary>The engine that runs or last ran; null before the first start.</summary>
    public IEngine? Engine { get; private set; }
    public GGUFIndex? Index { get; private set; }
    public string? IndexError { get; private set; }
    public SystemInfo System { get; private set; }

    /// <summary>The state, the plan or the engine changed.</summary>
    public event Action? Changed;

    /// <summary>For the tests: the app as another PC sees it.</summary>
    public SystemInfo? SimulatedSystem
    {
        get => simulated;
        set { simulated = value; RefreshSystem(); }
    }
    private SystemInfo? simulated;

    /// <summary>Tests: a fake process in place of the engine.</summary>
    internal Action<IEngine>? PrepareEngine { get; set; }

    public EngineHost(BackendChoice? choice = null, ModelStore? store = null, ClusterController? cluster = null)
    {
        Choice = choice ?? GpuDetection.Detect();
        Backend = Choice.Backend;
        System = SystemInfo.Current(Backend);
        Store = store ?? new ModelStore();
        Cluster = cluster ?? new ClusterController(Backend);
        Metrics = new LiveMetrics(() => Engine, Backend.Adapters.FirstOrDefault()?.Luid ?? 0, Backend.VramTotal);
        Bench = new BenchmarkRunner();
        Check = new SystemCheck();
        Store.Changed += () => Changed?.Invoke();
        Cluster.Changed += () => Changed?.Invoke();
        LoadIndex();
    }

    /// <summary>Call once at the app's start: leftovers of a forced quit go, downloads and pending work go on.</summary>
    public void OnAppStart()
    {
        EngineOrphans.Stop();
        Store.ResumeInterrupted();
    }

    public string SelectedModelId
    {
        get
        {
            var id = Settings.Shared.SelectedModelId;
            return Store.Catalog.Model(id) != null ? id : (MemoryPlanner.Recommended(Store.Catalog.Models, System) ?? Store.Catalog.Models.FirstOrDefault())?.Id ?? "";
        }
        set
        {
            Settings.Shared.SelectedModelId = value;
            LoadIndex();
            Changed?.Invoke();
        }
    }

    public ModelInfo? SelectedModel => Store.Catalog.Model(SelectedModelId) ?? Store.Catalog.Models.FirstOrDefault();

    /// <summary>Which engine runs the selected model here; null: none can (`Blocked` says why).</summary>
    public EngineKind? EngineForSelected => SelectedModel is { } m ? MemoryPlanner.EngineFor(m, System) : null;

    /// <summary>Why the selected model cannot start on this PC, in words; there is no "too big".</summary>
    public string? Blocked => SelectedModel is { } m ? MemoryPlanner.Blocked(m, System) : null;

    /// <summary>The second engine can use a cluster, the main engine cannot: the cluster page says so and offers these.</summary>
    public IReadOnlyList<ModelInfo> ClusterModels => Store.Catalog.Models.Where(m => MemoryPlanner.EngineFor(m, System) == EngineKind.Llama).ToList();
    public bool ClusterApplies => EngineForSelected == EngineKind.Llama;

    private IReadOnlyList<ulong> Helpers => ClusterApplies ? Cluster.HelperBudgets : Array.Empty<ulong>();

    public MemoryPlan? Plan
    {
        get
        {
            if (Index == null || EngineForSelected is not EngineKind kind) return null;
            bool vision = UseVision(Index);
            return MemoryPlanner.Plan(Index, System, ContextSize(Index), vision, kind, Helpers);
        }
    }

    /// <summary>The plan's prediction for any model of the catalog before its files are here.</summary>
    public MemoryPlanner.Fit FitOf(ModelInfo model) => MemoryPlanner.FitFor(model, System);
    public ModelInfo? Recommended => MemoryPlanner.Recommended(Store.Catalog.Models, System);

    /// <summary>The chosen context, at most what the model was made for, and on a PC with little memory at most what leaves the
    /// model its room (a cluster gives this PC room, so there the setting decides).</summary>
    public int ContextSize(GGUFIndex index)
    {
        var kind = EngineForSelected ?? EngineKind.Llama;
        return MemoryPlanner.ContextSize(index, System, Settings.Shared.ContextSize, UseVision(index), kind, Helpers.Count > 0);
    }

    /// <summary>Vision when it is on and downloaded, unless it would leave the model too little room: then the model goes first.</summary>
    public bool UseVision(GGUFIndex index)
    {
        var kind = EngineForSelected ?? EngineKind.Llama;
        // the main engine's own vision path is not wired up yet: its capabilities say so
        if (kind == EngineKind.Tiered) return false;
        bool wanted = Settings.Shared.Vision && SelectedModel is { } m && Store.VisionPath(m) != null;
        return MemoryPlanner.UseVision(index, System, wanted, kind, Helpers.Count > 0);
    }

    public bool CanStart
    {
        get
        {
            var m = SelectedModel;
            if (m == null || !Store.StatusOf(m.Id).IsComplete || Plan == null || Blocked != null) return false;
            if (EngineForSelected == EngineKind.Tiered && PackBuilder.WhyNotReady(m) != null) return false;
            return true;
        }
    }

    /// <summary>Why the selected model cannot start yet, in words; null when it can.</summary>
    public string? WhyNotStartable
    {
        get
        {
            var m = SelectedModel;
            if (m == null) return L("Dit model staat niet in de catalogus.");
            if (ModelStore.FolderMissing) return ModelStore.FolderMissingMessage;
            if (Blocked is { } blocked) return blocked;
            if (!Store.StatusOf(m.Id).IsComplete) return L($"{m.Name} is nog niet helemaal gedownload.");
            if (Index == null) return IndexError ?? L($"De headers van {m.Name} zijn nog niet gelezen.");
            return EngineForSelected == EngineKind.Tiered ? PackBuilder.WhyNotReady(m) : null;
        }
    }

    /// <summary>A selected model whose pack is missing or out of date (a moved or renamed file): build it, with progress in the store.</summary>
    public Task<string?> BuildPack(CancellationToken ct = default) =>
        SelectedModel is { } m && EngineForSelected == EngineKind.Tiered ? Store.BuildPackIfNeeded(m, ct) : Task.FromResult<string?>(null);

    public void LoadIndex()
    {
        Store.Refresh();
        var m = SelectedModel;
        if (m == null || !Store.StatusOf(m.Id).HeadersAvailable)
        {
            Index = null;
            return;
        }
        try
        {
            Index = GGUFIndex.Load(Store.ShardPaths(m), m.Files.Select(f => f.Size).ToList());
            IndexError = null;
        }
        catch (Exception e) when (e is GGUFException or IOException or UnauthorizedAccessException)
        {
            Index = null;
            IndexError = e.Message;
        }
    }

    public void RefreshSystem()
    {
        // while an engine runs its own use counts as "others": keep the reading from before it started
        if (simulated != null) System = simulated;
        else if (Engine?.State.IsActive == true) System = SystemInfo.Current(Backend) with { VramFree = System.VramFree };
        else System = SystemInfo.Current(Backend);
        Changed?.Invoke();
    }

    public void StartEngine()
    {
        if (Index == null) LoadIndex();
        if (Engine?.State.IsActive == true) return;
        if (simulated == null) RefreshSystem();
        var m = SelectedModel;
        var plan = Plan;
        if (m == null || Index == null || plan == null || EngineForSelected is not EngineKind kind) return;

        var previous = Engine;
        IEngine engine = kind == EngineKind.Tiered ? new TieredEngine() : new LlamaEngine();
        previous?.Dispose();
        PrepareEngine?.Invoke(engine);
        engine.StateChanged += state =>
        {
            Metrics.Fast = state.IsActive;
            Changed?.Invoke();
        };
        engine.Changed += () => Changed?.Invoke();
        Engine = engine;
        engine.Start(new EngineStart(m, Store.ShardPaths(m), plan, Backend)
        {
            Vision = plan.Vision ? Store.VisionPath(m) : null,
            Draft = Store.DraftPath(m),
            Rpc = ClusterApplies ? Cluster.RpcArgument : null,
        });
    }

    public void StopEngine() => Engine?.Stop();

    /// <summary>Switch to another model: stops the engine first when it runs a different one.</summary>
    public async Task Select(string id)
    {
        bool wasActive = Engine?.State.IsActive == true;
        bool other = Engine?.Model?.Id != id;
        if (wasActive && other && Engine != null) await Engine.StopAndWait();
        SelectedModelId = id;
        if (wasActive && other && CanStart) StartEngine();
    }

    /// <summary>The model folder changes: only with the engine stopped; see ModelFolderMover. On return null, or what went wrong.</summary>
    public async Task<string?> MoveModels(string to, Action<ulong, ulong>? progress = null, CancellationToken ct = default)
    {
        if (Store.Downloading.Count > 0) return L("Er loopt nog een download. Pauzeer die eerst.");
        var error = await ModelFolderMover.Run(to, Engine?.State.IsActive == true, progress, ct);
        LoadIndex();
        RefreshSystem();
        return error;
    }

    public void Dispose()
    {
        Engine?.Dispose();
        Metrics.Dispose();
        Cluster.Dispose();
        Store.Dispose();
    }
}
