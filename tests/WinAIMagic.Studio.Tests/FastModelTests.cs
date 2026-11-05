using WinAIMagic.Studio;
using WinAIMagic.Documents;
using Xunit;

namespace WinAIMagic.Studio.Tests;

/// When the fast model does not start, the work goes on and the user can see why.
public sealed class FastModelTests : StudioTest
{
    private sealed class FakeEngines : IDocumentEngines
    {
        public event Action? StateChanged;
        public int Launches;
        public string? Failure;
        public Exception? Throw;
        public Action? OnLaunch;
        public bool Beside = true;
        public DocumentModel? Heavy = new("zwaar", "Zwaar", 100);
        public List<DocumentModel> Downloaded = [new("zwaar", "Zwaar", 100), new("klein", "Klein", 20), new("groter", "Groter", 50)];

        public DocumentModel? HeavyModel => Heavy;
        public IReadOnlyList<DocumentModel> DownloadedModels => Downloaded;
        public Uri HeavyBaseUrl => new("http://heavy.model.test");
        public string HeavyModelId => "zwaar";
        public EngineStatus HeavyState { get; set; } = new(EngineRunState.Running);
        public bool CanStartHeavy { get; set; } = true;
        public int HeavyStarts;
        public void StartHeavy()
        {
            HeavyStarts++;
            HeavyState = new EngineStatus(EngineRunState.Running);
        }

        public Uri FastBaseUrl => new("http://127.0.0.1:8081");
        public string? FastLoadedModelId { get; set; }
        public EngineStatus FastState { get; set; }
        public bool CanRunFastBeside() => Beside;
        public void StartFast(string modelId)
        {
            Launches++;
            OnLaunch?.Invoke();
            if (Throw != null) throw Throw;
            if (OnLaunch != null) return;
            FastLoadedModelId = modelId;
            FastState = Failure == null ? new EngineStatus(EngineRunState.Running) : new EngineStatus(EngineRunState.Failed, Failure);
            StateChanged?.Invoke();
        }
        public void StopFast()
        {
            FastLoadedModelId = null;
            FastState = new EngineStatus(EngineRunState.Stopped);
        }
        public Task StopFastAndWaitAsync()
        {
            StopFast();
            return Task.CompletedTask;
        }
    }

    private readonly FakeEngines fake = new();
    private readonly DocumentEngines engines;

    public FastModelTests() => engines = new DocumentEngines(fake);

    private async Task<string?> FastEndpoint()
    {
        try { return (await engines.EndpointAsync(ModelRole.Fast)).Model; }
        catch (Exception) { return null; }
    }

    [Fact]
    public void FastModelOnlyWhenItFitsNextToTheHeavyOne()
    {
        DocumentModel heavy = new("heavy", "H", 100), small = new("small", "S", 20), tiny = new("tiny", "T", 5);
        Assert.Equal("small", DocumentModels.FastModel(heavy, [heavy, small], () => true)?.Id);
        Assert.Equal("tiny", DocumentModels.FastModel(heavy, [heavy, small, tiny], () => true)?.Id);
        // this PC cannot run two
        Assert.Null(DocumentModels.FastModel(heavy, [heavy, small], () => false));
        // not downloaded, or no smaller one
        Assert.Null(DocumentModels.FastModel(heavy, [heavy], () => true));
        Assert.Null(DocumentModels.FastModel(small, [heavy, small], () => true));
        Assert.Null(DocumentModels.FastModel(null, [heavy, small], () => true));
    }

    /// <summary>The fast model chosen under Modellen goes first, never the heavy one itself, and only where two fit.</summary>
    [Fact]
    public void TheChosenFastModelGoesFirst()
    {
        DocumentModel heavy = new("heavy", "H", 100), small = new("small", "S", 20), tiny = new("tiny", "T", 5);
        Assert.Equal("tiny", DocumentModels.FastModel(heavy, [heavy, small, tiny], () => true)?.Id);
        Assert.Equal("small", DocumentModels.FastModel(heavy, [heavy, small, tiny], () => true, "small")?.Id);
        Assert.Equal("tiny", DocumentModels.FastModel(heavy, [heavy, small, tiny], () => true, "heavy")?.Id);
        Assert.Equal("tiny", DocumentModels.FastModel(heavy, [heavy, small, tiny], () => true, "weg")?.Id);
        Assert.Null(DocumentModels.FastModel(heavy, [heavy, small, tiny], () => false, "small"));
    }

    [Fact]
    public async Task AFailureIsKeptAndNotTriedForEveryPage()
    {
        fake.Failure = "poort bezet";
        await FastEndpoint();
        await FastEndpoint();
        await FastEndpoint();
        Assert.Equal(1, fake.Launches);
        Assert.Equal("poort bezet", engines.FastProblem);
    }

    [Fact]
    public async Task TheButtonTriesAgainAndClearsTheProblem()
    {
        fake.Failure = "poort bezet";
        await FastEndpoint();
        Assert.Equal("Probeer opnieuw", engines.FastButtonTitle);
        fake.Failure = null;
        var starting = engines.StartFastNow();
        Assert.Null(engines.FastProblem);
        await starting;
        Assert.Equal(2, fake.Launches);
        Assert.Null(engines.FastProblem);
        Assert.Equal("klein", await FastEndpoint());
    }

    [Fact]
    public async Task AWorkingModelIsUsed()
    {
        var (url, model) = await engines.EndpointAsync(ModelRole.Fast);
        Assert.Equal("klein", model);
        Assert.Equal(new Uri("http://127.0.0.1:8081"), url);
        Assert.Null(engines.FastProblem);
    }

    [Fact]
    public async Task AFailureFallsBackToTheHeavyModel()
    {
        fake.Failure = "poort bezet";
        Assert.Equal("zwaar", await FastEndpoint());
    }

    [Fact]
    public async Task WhenTheFastModelCannotRunBesideItTheHeavyOneDoesEverything()
    {
        fake.Beside = false;
        Assert.Null(engines.FastChoice);
        Assert.Equal("zwaar", await FastEndpoint());
        Assert.Equal(0, fake.Launches);
    }

    [Fact]
    public async Task AnotherChoiceIsTriedAfterAFailure()
    {
        fake.Failure = "poort bezet";
        await FastEndpoint();
        fake.Downloaded = [new("zwaar", "Zwaar", 100), new("ander", "Ander", 10)];
        await FastEndpoint();
        Assert.Equal(2, fake.Launches);
    }

    [Fact]
    public async Task StoppingTheWorkIsNoFailureOfTheModel()
    {
        fake.Throw = new OperationCanceledException();
        await FastEndpoint();
        Assert.Null(engines.FastProblem);
        await FastEndpoint();
        Assert.Equal(2, fake.Launches);
    }

    [Fact]
    public async Task StoppingItYourselfIsNoFailure()
    {
        fake.OnLaunch = engines.StopFast;
        await FastEndpoint();       // the engine says it stopped, which is what was asked
        Assert.Null(engines.FastProblem);
    }

    [Fact]
    public async Task TheHeavyModelStartsWhenNeededAndSaysWhenItCannot()
    {
        fake.Beside = false;
        fake.HeavyState = new EngineStatus(EngineRunState.Stopped);
        Assert.Equal("zwaar", await FastEndpoint());
        Assert.Equal(1, fake.HeavyStarts);

        fake.HeavyState = new EngineStatus(EngineRunState.Stopped);
        fake.CanStartHeavy = false;
        var e = await Assert.ThrowsAsync<Zip.Failure>(() => engines.EndpointAsync(ModelRole.Heavy));
        Assert.Equal("Download eerst een model onder Modellen.", e.Message);
    }

    [Fact]
    public void APortInUseIsSaidInPlainWords()
    {
        Assert.Equal("Poort 8081 is al in gebruik door een ander programma.", DocumentEngines.Readable("Engine gestopt (code 1): 0.00.048 E srv exiting due to HTTP server error", 8081));
        Assert.Equal("Geen geheugen", DocumentEngines.Readable("Geen geheugen", 8081));
    }

    [Fact]
    public void StatusSaysWhatTheModelIsDoing()
    {
        Assert.Equal("vanzelf", new EngineStatus(EngineRunState.Stopped).StatusText("vanzelf"));
        Assert.Equal("Laden", new EngineStatus(EngineRunState.Starting, "Laden").StatusText("x"));
        Assert.Equal("kapot", new EngineStatus(EngineRunState.Failed, "kapot").StatusText("x"));
        Assert.Equal("Draait", new EngineStatus(EngineRunState.Running).StatusText("x"));
    }

    [Fact]
    public async Task ThePanelSaysWhatIsGoingOn()
    {
        Assert.Equal("Start vanzelf als er geschreven wordt.", new DocumentEngines(new FakeEngines { HeavyState = new EngineStatus(EngineRunState.Stopped) }).HeavyStatusLine);
        Assert.Equal("Start vanzelf bij de eerste snelle actie.", engines.FastStatusLine);
        Assert.Equal("Start nu", engines.FastButtonTitle);
        Assert.False(engines.FastButtonStops);

        await FastEndpoint();
        Assert.Equal("Draait", engines.FastStatusLine);
        Assert.Equal("Stop", engines.FastButtonTitle);
        Assert.True(engines.FastButtonStops);

        engines.StopFast();
        fake.Failure = "poort bezet";
        await FastEndpoint();
        Assert.Equal("Start niet; het zware model doet dit werk nu. poort bezet", engines.FastStatusLine);
        Assert.Equal("Probeer opnieuw", engines.FastButtonTitle);
    }
}
