using System.Globalization;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>The memory rules of the image page, for any PC. The figures in them were measured on the test PC with limits that stand in
/// for a smaller one, not on such a PC itself.</summary>
public class ImagePlanTests : EngineTest
{
    private static readonly ImageSize Small = new(512, 512), Middle = new(768, 768), Large = new(1024, 1024);

    private static SystemInfo Pc(int vram, int ram) =>
        SystemInfo.Simulated(vram == 0 ? FakeGpuBackend.Cpu() : FakeGpuBackend.Nvidia(vram), ram, 8);

    /// <summary>A card from 8 GB takes every size, with a budget and the last stage in parts: on a card that costs next to nothing.</summary>
    [Theory]
    [InlineData(8, 16, 5.2)]
    [InlineData(8, 8, 5.2)]
    [InlineData(16, 32, 13.2)]
    public void ACardTakesEverySize(int vram, int ram, double budget)
    {
        var s = Pc(vram, ram);
        Assert.Equal(Large, ImagePlanner.DefaultSize(s));
        foreach (var size in ImagePlanner.Sizes)
        {
            var p = ImagePlanner.For(size, s);
            Assert.True(p.OnCard);
            Assert.True(p.Tiling);
            Assert.Equal(budget, p.VramBudgetGib);
        }
    }

    /// <summary>A 6 GB card takes the smaller sizes on a small budget; 1024 failed there, so that goes to the processor at once.</summary>
    [Fact]
    public void ASixGBCardTakesTheSmallerSizes()
    {
        var s = Pc(6, 16);
        Assert.Equal(Middle, ImagePlanner.DefaultSize(s));
        Assert.Equal(ImagePlanner.SmallCardBudgetGib, ImagePlanner.For(Middle, s).VramBudgetGib);
        Assert.True(ImagePlanner.For(Small, s).OnCard);
        var large = ImagePlanner.For(Large, s);
        Assert.False(large.OnCard);
        Assert.Null(large.VramBudgetGib);
        Assert.False(ImagePlanner.For(new ImageSize(1024, 768), s).OnCard);
    }

    [Fact]
    public void AFourGBCardLeavesItToTheProcessor()
    {
        var s = Pc(4, 16);
        Assert.Equal(Middle, ImagePlanner.DefaultSize(s));
        foreach (var size in ImagePlanner.Sizes) Assert.False(ImagePlanner.For(size, s).OnCard);
    }

    /// <summary>16 GB and no card: every size whole, 768 first because 1024 takes minutes more.</summary>
    [Fact]
    public void SixteenGBWithoutACard()
    {
        var s = Pc(0, 16);
        Assert.Equal(Middle, ImagePlanner.DefaultSize(s));
        foreach (var size in ImagePlanner.Sizes)
        {
            var p = ImagePlanner.For(size, s);
            Assert.False(p.OnCard);
            Assert.False(p.Tiling);
        }
    }

    /// <summary>8 GB and no card: 512 whole; larger ones in parts, which measured 1.4 GB at 1024 against 7.5 GB.</summary>
    [Theory]
    [InlineData(8.0)]
    [InlineData(7.8)]
    [InlineData(7.4)]
    public void EightGBWithoutACard(double gib)
    {
        var s = Pc(0, 8) with { RamTotal = (ulong)(gib * (1UL << 30)) };
        Assert.Equal(Small, ImagePlanner.DefaultSize(s));
        Assert.False(ImagePlanner.For(Small, s).Tiling);
        Assert.True(ImagePlanner.For(Middle, s).Tiling);
        Assert.True(ImagePlanner.For(Large, s).Tiling);
    }

    /// <summary>The line through what was measured on the processor.</summary>
    [Theory]
    [InlineData(512, 512, 2.2)]
    [InlineData(768, 768, 4.4)]
    [InlineData(1024, 1024, 7.5)]
    public void TheDecoderFollowsTheMeasurements(int w, int h, double gb) =>
        Assert.Equal(gb, ImagePlanner.DecoderBytes(new ImageSize(w, h)) / 1e9, 0.3);

    [Fact]
    public void TheEngineSeesWhatIsOnTheCardNow()
    {
        // what a chat engine held is free again by the time the image starts
        var s = Pc(8, 16) with { VramFree = 500_000_000 };
        Assert.True(ImagePlanner.For(Large, s).OnCard);
    }

    private static readonly string[] Files = { "image.gguf", "text.gguf", "decoder.safetensors" };

    private static IReadOnlyList<string> Args(ImagePlan plan, string prompt = "een vos") => ImagePlanner.Arguments(plan, Files, prompt, 42, "uit.png");

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void TheArgumentsOnACard()
    {
        var args = Args(ImagePlanner.For(Large, Pc(8, 16)));
        Assert.Equal("image.gguf", After(args, "--diffusion-model"));
        Assert.Equal("text.gguf", After(args, "--llm"));
        Assert.Equal("decoder.safetensors", After(args, "--vae"));
        Assert.Equal("4", After(args, "--steps"));
        Assert.Equal("1.0", After(args, "--cfg-scale"));
        Assert.Equal("1024", After(args, "-W"));
        Assert.Equal("42", After(args, "-s"));
        Assert.Equal("uit.png", After(args, "-o"));
        Assert.Contains("--mmap", args);
        Assert.Contains("--vae-tiling", args);
        Assert.Contains("--disable-image-metadata", args);
        Assert.DoesNotContain("--backend", args);
    }

    [Fact]
    public void TheArgumentsOnTheProcessor()
    {
        var args = Args(ImagePlanner.For(Middle, Pc(0, 16)));
        Assert.Equal("cpu", After(args, "--backend"));
        Assert.DoesNotContain("--max-vram", args);
        Assert.DoesNotContain("--vae-tiling", args);
        Assert.Equal("8", After(args, "-t"));
    }

    [Fact]
    public void ThreadsAreAtLeastFour() =>
        Assert.Equal(4, ImagePlanner.For(Middle, SystemInfo.Simulated(FakeGpuBackend.Cpu(), 16, 2)).Threads);

    /// <summary>A Dutch PC writes 5,2; the engine reads a point.</summary>
    [Fact]
    public void TheBudgetIsWrittenWithAPoint()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try { Assert.Equal("5.2", After(Args(ImagePlanner.For(Large, Pc(8, 16))), "--max-vram")); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void ThePromptStaysWhole()
    {
        const string prompt = "een café met “één” rode fiets";
        Assert.Equal(prompt, After(Args(ImagePlanner.For(Middle, Pc(0, 16)), prompt), "-p"));
    }

    [Fact]
    public void ASizeIsSavedAndReadBack()
    {
        Assert.Equal(new ImageSize(1024, 768), ImageSize.Parse(new ImageSize(1024, 768).Key));
        Assert.Null(ImageSize.Parse(""));
        Assert.Null(ImageSize.Parse("groot"));
    }
}

/// <summary>What the page shows while an image is made, from lines of the real engine's output.</summary>
public class ImageProgressTests : EngineTest
{
    [Fact]
    public void TheStagesFollowTheOutput()
    {
        var p = new ImageProgress();
        Assert.Contains("Tekst lezen", p.Status);
        // reading weights has a bar of its own, with hundreds of parts: not a step
        Assert.False(p.Read("  |##################                                | 107/298 -   2.36GB/s\u001b[K"));
        Assert.False(p.Read("[I] get_learned_condition completed, taking 7.99s --- image.cpp:627"));
        Assert.True(p.Read("[I] sampling using Euler method --- request.cpp:477"));
        Assert.Equal("Tekenen: stap 1 van 4", p.Status);
        double start = p.Fraction;
        Assert.False(p.Read("  |#####                                             | 140/1400 - 463.94MB/s"));
        Assert.True(p.Read("  |============>                                     | 1/4 -   1.02it/s\u001b[K"));
        Assert.Equal("Tekenen: stap 2 van 4", p.Status);
        Assert.True(p.Fraction > start);
        Assert.False(p.Read("  |============>                                     | 1/4 -   1.04it/s"));
        Assert.True(p.Read("  |==================================================| 4/4 -   4.49it/s"));
        Assert.Equal("Tekenen: stap 4 van 4", p.Status);
        Assert.True(p.Read("[I] sampling completed, taking 1.66s --- image.cpp:1000"));
        Assert.Contains("afmaken", p.Status);
        Assert.Equal(0.85, p.Fraction);
    }

    /// <summary>A token is a patch of 16 x 16 pixels; the bar says the seconds per step either way round.</summary>
    [Fact]
    public void ImageTokens()
    {
        Assert.Equal(4096, ImagePlanner.TokensPerStep(new ImageSize(1024, 1024)));
        Assert.Equal(8.62, ImageProgress.StepSeconds("|====|  2/4 -   8.62s/it - ETA 00:17"));
        Assert.Equal(0.5, ImageProgress.StepSeconds("|=| 1/4 - 2.00it/s"));
        var p = new ImageProgress();
        p.Read("[I] sampling using Euler method --- request.cpp:477");
        p.Read("  |============>                                     | 1/4 -   8.49s/it");
        Assert.Equal(8.49, p.SecondsPerStep);
        Assert.True(p.IsDrawing);
    }

    /// <summary>The words, not the place in the source.</summary>
    [Fact]
    public void AnErrorIsKept()
    {
        var p = new ImageProgress();
        p.Read("[E] flux segment 3/27 (flux.double_blocks.1) failed during weight preparation --- ggml_runner.cpp:933");
        p.Read("[E] generate failed --- main.cpp:926");
        Assert.Equal("generate failed", p.LastError);
    }
}

/// <summary>One image from start to end, with a stand-in for the engine.</summary>
public class ImageMakerTests : EngineTest
{
    private static readonly ImageSize Size = new(512, 512);
    private static readonly string[] Files = { "a", "b", "c" };
    private static readonly IGpuBackend Card = FakeGpuBackend.Nvidia(16), None = FakeGpuBackend.Cpu();
    private static readonly SystemInfo OnCard = SystemInfo.Simulated(Card, 64, 8), OnProcessor = SystemInfo.Simulated(None, 16, 8);

    private string Folder => Path.Combine(Root, "Afbeeldingen");
    private ImageMaker Maker() => new() { Root = Folder, ExecutableOverride = FakeExe };

    [Fact]
    public async Task AnImageComesWithItsDescription()
    {
        var m = Maker();
        var r = await m.Make("een vos", Size, OnCard, Card, Files, seed: 7);
        Assert.NotNull(r);
        Assert.True(File.Exists(r!.Path));
        Assert.EndsWith("-7.png", r.Path);
        Assert.True(r.OnCard);
        Assert.Equal(r, m.Last);
        Assert.False(m.Busy);
        Assert.Null(m.Error);
        var again = Assert.Single(m.History());
        Assert.Equal("een vos", again.Prompt);
        Assert.Equal(7, again.Seed);
        Assert.Equal(512, again.Width);
        Assert.Equal(r.Path, again.Path);
    }

    [Fact]
    public async Task ThePageSeesTheStages()
    {
        var m = Maker();
        var seen = new List<string>();
        m.Changed += () => { lock (seen) seen.Add(m.Status); };
        await m.Make("een vos", Size, OnCard, Card, Files);
        lock (seen)
        {
            Assert.Contains("Tekst lezen…", seen);
            Assert.Contains("Tekenen: stap 4 van 4", seen);
            Assert.Contains("Afbeelding afmaken…", seen);
        }
    }

    /// <summary>A card that refuses costs seconds; the processor always works, and the page says what happened.</summary>
    /// <summary>The meters count image tokens while the model draws, and let go when it is done: 1024 tokens a step at 512, 4.48 steps a second.</summary>
    [Fact]
    public async Task TheMetersFollowTheImageTokens()
    {
        var m = Maker();
        var rates = new List<double?>();
        m.OnRate = r => { lock (rates) rates.Add(r); };
        var made = await m.Make("een vos", Size, OnCard, Card, Files);
        lock (rates)
        {
            Assert.Equal(0, rates[0]);
            Assert.Contains(rates, r => r is double v && Math.Abs(v - 1024 * 4.48) < 0.01);
            Assert.Null(rates[^1]);
        }
        Assert.Equal(1024 * 4.48, made!.TokensPerSecond!.Value, 2);
        Assert.Equal(1024 * 4.48, Assert.Single(m.History()).TokensPerSecond!.Value, 2);
    }

    [Fact]
    public async Task ACardThatRefusesLeavesItToTheProcessor()
    {
        var m = Maker();
        var r = await m.Make("kaart stuk", Size, OnCard, Card, Files);
        Assert.NotNull(r);
        Assert.False(r!.OnCard);
        Assert.Contains("op de processor", m.Note);
        Assert.Null(m.Error);
    }

    [Fact]
    public async Task AFailureOnTheProcessorIsReported()
    {
        var m = Maker();
        Assert.Null(await m.Make("kapot", Size, OnProcessor, None, Files));
        Assert.Contains("niet gelukt", m.Error);
        Assert.Contains("sampling for image", m.Error);
        Assert.DoesNotContain("image.cpp", m.Error);
        Assert.Null(m.Note);
        Assert.Empty(m.History());
        Assert.True(File.Exists(Paths.ImageLog));
    }

    /// <summary>The first try stays in the log beside the second.</summary>
    [Fact]
    public async Task TheLogKeepsBothTries()
    {
        var m = Maker();
        await m.Make("kaart stuk", Size, OnCard, Card, Files);
        var log = File.ReadAllText(Paths.ImageLog);
        Assert.Contains("failed", log);
        Assert.Contains("--backend cpu", log);
    }

    [Fact]
    public async Task AnEngineThatMakesNoFileHasNotSucceeded()
    {
        var m = Maker();
        Assert.Null(await m.Make("geen bestand", Size, OnProcessor, None, Files));
        Assert.Contains("niet gelukt", m.Error);
    }

    [Fact]
    public async Task StopEndsTheEngineAndLeavesNoError()
    {
        var m = Maker();
        var making = m.Make("slaap", Size, OnCard, Card, Files);
        Assert.True(WaitUntil(() => m.Status.StartsWith("Tekenen", StringComparison.Ordinal)));
        Assert.True(m.Busy);
        m.Stop();
        Assert.Null(await making);
        Assert.False(m.Busy);
        Assert.Null(m.Error);
        Assert.Null(m.Note);     // a stop is no reason to try the processor
        Assert.Empty(m.History());
    }

    [Fact]
    public async Task OneImageAtATime()
    {
        var m = Maker();
        var first = m.Make("slaap", Size, OnCard, Card, Files);
        Assert.True(WaitUntil(() => m.Busy));
        Assert.Null(await m.Make("een vos", Size, OnCard, Card, Files));
        m.Stop();
        await first;
        Assert.Empty(m.History());
    }

    [Fact]
    public async Task AMissingEngineSaysWhatToDo()
    {
        var m = new ImageMaker { Root = Folder, ExecutableOverride = Path.Combine(Root, "weg.exe") };
        Assert.Null(await m.Make("een vos", Size, OnCard, Card, Files));
        Assert.Contains("Installeer WinAIMagic opnieuw", m.Error);
    }

    [Fact]
    public async Task WhatComesFirstCanRefuse()
    {
        var m = Maker();
        Assert.Null(await m.Make("een vos", Size, OnCard, Card, Files, prepare: () => Task.FromResult<string?>("eerst iets anders")));
        Assert.Equal("eerst iets anders", m.Error);
        Assert.False(m.Busy);
    }

    /// <summary>A PNG that lost the file beside it still shows.</summary>
    [Fact]
    public void AnImageWithoutItsFileStillShows()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "los.png"), new byte[] { 1 });
        var item = Assert.Single(Maker().History());
        Assert.Equal("", item.Prompt);
        Assert.Equal(0, item.Seconds);
    }

    /// <summary>Looking creates nothing: the folder comes with the first image.</summary>
    [Fact]
    public void LookingCreatesNoFolder()
    {
        Assert.Empty(Maker().History());
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void TheEngineIsInTheFolderOfTheBackend() =>
        Assert.EndsWith(Path.Combine("engine", "cpu", "wam-image.exe"), ImageMaker.Binary(None));
}

/// <summary>Images and the chat engine share one PC.</summary>
public class ImageHostTests : EngineTest
{
    private static readonly ImageSize Size = new(512, 512);

    private EngineHost Host(ulong imageFileSize = 8)
    {
        var bytes = TestModels.TinyModel();
        var chat = new ModelInfo
        {
            Id = "tiny", Name = "Tiny", Family = "qwen", Summary = "s",
            Files = new[] { new ModelFile { Local = "tiny.gguf", Size = (ulong)bytes.Length, Sha256 = new[] { TestModels.Sha(bytes) }, Url = "https://x.test/a" } },
            Memory = (ulong)bytes.Length, Dense = (ulong)bytes.Length / 3,
        };
        Directory.CreateDirectory(Paths.ModelDir("tiny"));
        File.WriteAllBytes(Path.Combine(Paths.ModelDir("tiny"), "tiny.gguf"), bytes);
        var image = new ModelInfo
        {
            Id = "flux", Name = "Flux", Family = "flux", Summary = "s",
            Files = new[] { "flux-image.gguf", "flux-text.gguf", "flux-decoder.safetensors" }
                .Select(n => new ModelFile { Local = n, Size = imageFileSize, Sha256 = new[] { new string('0', 64) }, Url = "https://x.test/" + n }).ToArray(),
        };
        Directory.CreateDirectory(Paths.ModelDir("flux"));
        foreach (var f in image.Files) File.WriteAllBytes(Path.Combine(Paths.ModelDir("flux"), f.Local), new byte[8]);
        var catalog = new ModelCatalog { Models = new[] { chat }, Images = new[] { image } };
        var store = new ModelStore(catalog, new HttpClient(new FakeServer()), timer: false);
        var b = FakeGpuBackend.Nvidia(16);
        var host = new EngineHost(new BackendChoice(b, "reden", Array.Empty<string>()), store, new ClusterController(b, browse: false))
        {
            SimulatedSystem = SystemInfo.Simulated(b, 128, 10),
        };
        host.PrepareEngine = e => HostTests.ToFake(e, Settings.Shared.Port);
        host.Images.Root = Path.Combine(Root, "Afbeeldingen");
        host.Images.ExecutableOverride = FakeExe;
        return host;
    }

    [Fact]
    public async Task TheModelHasToBeHereFirst()
    {
        using var host = Host(imageFileSize: 100);
        Assert.Null(await host.MakeImage("een vos", Size));
        Assert.Contains("nog niet helemaal", host.Images.Error);
    }

    [Fact]
    public async Task ARunningEngineStopsFirst()
    {
        Settings.Shared.Port = FreePort();
        using var host = Host();
        host.PrepareEngine = e =>
        {
            HostTests.ToFake(e, Settings.Shared.Port);
            ((EngineBase)e).ArgumentsOverride = new[] { "--port", Settings.Shared.Port.ToString(), "--idle" };
        };
        host.StartEngine();
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning), host.Engine!.State.ToString());
        var r = await host.MakeImage("een vos", Size);
        Assert.NotNull(r);
        Assert.Equal(EngineState.Stopped, host.Engine!.State);
    }

    /// <summary>An answer being written is not cut off for an image.</summary>
    [Fact]
    public async Task AnEngineAtWorkGoesFirst()
    {
        Settings.Shared.Port = FreePort();
        using var host = Host();
        host.StartEngine();
        Assert.True(WaitUntil(() => host.Engine!.State.IsRunning), host.Engine!.State.ToString());
        Assert.Null(await host.MakeImage("een vos", Size));
        Assert.Contains("werkt aan een antwoord", host.Images.Error);
        Assert.True(host.Engine!.State.IsRunning);
        await host.Engine.StopAndWait();
    }

    /// <summary>Nothing starts beside an image, and the start button says why.</summary>
    [Fact]
    public async Task NoEngineStartsWhileAnImageIsMade()
    {
        Settings.Shared.Port = FreePort();
        using var host = Host();
        Assert.True(host.CanStart);
        var making = host.MakeImage("slaap", Size);
        Assert.True(WaitUntil(() => host.Images.Status.StartsWith("Tekenen", StringComparison.Ordinal)));
        Assert.False(host.CanStart);
        Assert.Contains("afbeelding", host.WhyNotStartable);
        host.StartEngine();
        Assert.Null(host.Engine);
        host.Images.Stop();
        await making;
        Assert.True(host.CanStart);
    }

    [Fact]
    public async Task WhatKeepsTheMemorySaysSo()
    {
        using var host = Host();
        Assert.Null(await host.MakeImage("een vos", Size, () => Task.FromResult<string?>("de documenten")));
        Assert.Equal("de documenten", host.Images.Error);
    }

    /// <summary>Compare measures the image model by making the same image, and keeps it apart from the text figures.</summary>
    [Fact]
    public async Task CompareMeasuresTheImageModel()
    {
        using var host = Host();
        await host.Bench.Run(new[] { "flux" }, host);
        var r = Assert.Single(host.Bench.Results);
        Assert.True(r.MakesImage);
        Assert.Equal("1024x1024", r.ImageSize);
        Assert.True(r.ImageSeconds > 0);
        Assert.True(r.DecodeTps > 0);
        Assert.Empty(host.Images.History());       // the measured image does not stay among the user's images
        Assert.Equal("", Settings.Shared.ImageModel);
        Assert.Contains(r, host.Bench.Latest(host.Store.Catalog));
    }

    /// <summary>The model chosen under Modellen makes the images; an unknown choice falls back to the first.</summary>
    [Fact]
    public void TheChosenImageModel()
    {
        using var host = Host();
        Settings.Shared.ImageModel = "weg";
        Assert.Equal("flux", host.ImageModel!.Id);
        Settings.Shared.ImageModel = "flux";
        Assert.Equal("flux", host.ImageModel!.Id);
        Assert.Equal("tiny", host.SelectedModelId);
    }

    /// <summary>The image model is not one the chat can choose, and it still has a status and a download.</summary>
    [Fact]
    public void TheImageModelStaysApartFromTheChat()
    {
        using var host = Host();
        Assert.Equal("flux", host.ImageModel!.Id);
        Assert.DoesNotContain(host.Store.Catalog.Models, m => m.Id == "flux");
        Assert.True(host.Store.StatusOf("flux").IsComplete);
        Assert.Equal("tiny", host.SelectedModelId);
        Assert.Null(host.Store.DeleteModel("flux"));
        Assert.False(host.Store.StatusOf("flux").IsComplete);
    }
}
