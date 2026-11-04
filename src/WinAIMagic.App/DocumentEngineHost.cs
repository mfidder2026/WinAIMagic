using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using WinAIMagic.Studio;
using static WinAIMagic.Core.Support.Lang;
using StudioStatus = WinAIMagic.Studio.EngineStatus;

namespace WinAIMagic.App;

/// <summary>The engines of the documents module on top of EngineHost. Heavy: the app's own engine. Fast: the smallest downloaded model on
/// port + 1, 16K context, no vision, always local, on the processor (-ngl 0) so that it takes no VRAM from the heavy one (brief 7).</summary>
internal sealed class DocumentEngineHost : IDocumentEngines, IDisposable
{
    /// <summary>What stays free for Windows and the rest beside both models.</summary>
    private const ulong Margin = 8_000_000_000;

    private readonly AppState app;
    private readonly IGpuBackend processor = new CpuBackend(new BackendProbe());
    private LlamaEngine? fast;
    private Task stopping = Task.CompletedTask;
    private bool heavyStarting;

    public event Action? StateChanged;

    public DocumentEngineHost(AppState app)
    {
        this.app = app;
        app.Changed += () => StateChanged?.Invoke();
    }

    private EngineHost Host => app.Host;

    private static DocumentModel Described(ModelInfo m) => new(m.Id, m.ShortName, m.Memory ?? m.TotalSize);

    private static StudioStatus Described(EngineState s) => s.Status switch
    {
        Engine.EngineStatus.Running => new StudioStatus(EngineRunState.Running),
        Engine.EngineStatus.Starting => new StudioStatus(EngineRunState.Starting, s.Message),
        Engine.EngineStatus.Failed => new StudioStatus(EngineRunState.Failed, s.Message),
        _ => new StudioStatus(EngineRunState.Stopped),
    };

    public DocumentModel? HeavyModel => Host.SelectedModel is { } m ? Described(m) : null;

    /// <summary>Only what the second engine can run: this list is where the fast model comes from.</summary>
    public IReadOnlyList<DocumentModel> DownloadedModels =>
        Host.Store.Catalog.Models.Where(m => Host.Store.StatusOf(m.Id).IsComplete && m.Tier?.Llama != false).Select(Described).ToList();

    public Uri HeavyBaseUrl => app.BaseUrl;
    public string HeavyModelId => app.ChatModel?.Id ?? "";
    /// <summary>The engine itself starts on another thread: the wait for it must not read "stopped" before that.</summary>
    public StudioStatus HeavyState => heavyStarting && !app.EngineState.IsActive ? new StudioStatus(EngineRunState.Starting, L("Model laden…")) : Described(app.EngineState);
    public bool CanStartHeavy => Host.CanStart;

    public void StartHeavy()
    {
        heavyStarting = true;
        app.StartEngineTask().ContinueWith(_ => app.Ui.TryEnqueue(() => { heavyStarting = false; app.Notify(); }));
    }

    public Uri FastBaseUrl => new($"http://127.0.0.1:{DocumentModels.FastPort}");
    public string? FastLoadedModelId => fast?.State.IsActive == true ? fast.Model?.Id : null;
    public StudioStatus FastState => fast == null ? new StudioStatus(EngineRunState.Stopped) : Described(fast.State);

    /// <summary>What the heavy engine is going to hold in RAM. Once it runs, that is already out of the free RAM.</summary>
    private ulong HeavyRamToCome
    {
        get
        {
            if (app.EngineState.IsRunning) return 0;
            if (Host.Plan is { } plan) return plan.RamExperts + plan.KvRam + (Host.System.HasGpu ? 0 : plan.Dense + plan.Overhead);
            var size = Host.SelectedModel is { } m ? m.Memory ?? m.TotalSize : 0;
            return size > Host.System.VramTotal ? size - Host.System.VramTotal : 0;
        }
    }

    public bool CanRunFastBeside()
    {
        // it runs: its own memory is no reason to stop it
        if (fast?.State.IsActive == true) return true;
        var heavy = Host.SelectedModelId;
        var small = DownloadedModels.Where(m => m.Id != heavy).MinBy(m => m.Memory);
        if (small == null) return false;
        ulong free = Host.Metrics.Current is { } now ? (ulong)Math.Max((now.RamTotalGB - now.RamUsedGB) * 1e9, 0) : Host.System.RamAvailable;
        return free >= HeavyRamToCome + small.Memory + Margin;
    }

    public void StartFast(string modelId)
    {
        var model = Host.Store.Catalog.Model(modelId) ?? throw new InvalidOperationException(L("Dit model staat niet in de catalogus."));
        var shards = Host.Store.ShardPaths(model);
        var index = GGUFIndex.Load(shards, model.Files.Select(f => f.Size).ToList());
        var plan = MemoryPlanner.Plan(index, SystemInfo.Current(processor), DocumentModels.FastContext, false);
        var engine = new LlamaEngine(DocumentModels.FastPort, Paths.EngineFastLog);
        engine.StateChanged += _ => app.Notify();
        fast?.Dispose();
        fast = engine;
        engine.Start(new EngineStart(model, shards, plan, processor));
    }

    public void StopFast()
    {
        if (fast is { } f) stopping = f.StopAndWait();
    }

    public async Task StopFastAndWaitAsync()
    {
        StopFast();
        await stopping;
    }

    public void Dispose() => fast?.Dispose();
}
