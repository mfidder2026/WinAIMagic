using System.Collections.Concurrent;
using System.Net;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>A small download server behind HttpClient.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    public readonly ConcurrentDictionary<string, byte[]> Files = new();
    public readonly ConcurrentBag<(string Url, long? From)> Requests = new();
    /// <summary>The first N requests fail.</summary>
    public int FailFirst;
    public bool IgnoreRange;
    public HttpStatusCode? AlwaysStatus;
    /// <summary>The connection breaks after this many bytes, once.</summary>
    public int BreakAfter;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        long? from = request.Headers.Range?.Ranges.First().From;
        Requests.Add((url, from));
        if (AlwaysStatus is { } st) return Task.FromResult(new HttpResponseMessage(st));
        if (Interlocked.Decrement(ref FailFirst) >= 0) throw new HttpRequestException("broken " + url);
        if (!Files.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        bool partial = from != null && !IgnoreRange;
        var body = partial ? bytes[(int)from!.Value..] : bytes;
        var code = partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
        int cut = Interlocked.Exchange(ref BreakAfter, 0);
        if (cut > 0)
        {
            // a real server names the whole size, and only then does the connection break
            var broken = new StreamContent(new BreakingStream(body[..cut]));
            broken.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(code) { Content = broken });
        }
        return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
    }

    private sealed class BreakingStream : MemoryStream
    {
        public BreakingStream(byte[] b) : base(b) { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            return n > 0 ? n : throw new IOException("connection lost");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = base.Read(buffer.Span);
            return n > 0 ? new ValueTask<int>(n) : throw new IOException("connection lost");
        }
    }
}

/// <summary>A request that stays open until the test lets go of it, as a real download does.</summary>
public sealed class BlockingServer : HttpMessageHandler
{
    private readonly TaskCompletionSource release = new();
    public bool Entered;
    public void Release() => release.TrySetResult();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Entered = true;
        await release.Task.WaitAsync(ct);
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    }
}

public class DownloaderTests : EngineTest
{
    private readonly FakeServer server = new();
    private static byte[] Data(int n, byte seed = 1) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = (byte)(i * seed); return b; }

    private static ModelCatalog OneFile(string id, byte[] content, string? sha = null, IReadOnlyList<int>? stamp = null, string url = "https://example.test/Maya/File-Maya.gguf") =>
        new()
        {
            Models = new[]
            {
                new ModelInfo
                {
                    Id = id, Name = "Test", Family = "qwen", Summary = "s",
                    Files = new[] { new ModelFile { Local = id + ".gguf", Size = (ulong)content.Length, Sha256 = new[] { sha ?? TestModels.Sha(content) }, Url = url, Stamp = stamp } },
                },
            },
        };

    private ModelStore Store(ModelCatalog c) => new(c, new HttpClient(server), timer: false) { RetryDelay = TimeSpan.Zero };

    private async Task<ModelStore> Download(ModelCatalog c, string id)
    {
        var store = Store(c);
        store.StartDownload(id);
        await store.WaitForDownload(id);
        return store;
    }

    private static string Dir(string id) => Paths.ModelDir(id);
    private static string Local => Path.Combine(Dir("m"), "m.gguf");

    [Fact]
    public async Task ADownloadIsVerifiedAndGetsItsOk()
    {
        var content = Data(5000);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        using var store = await Download(c, "m");
        Assert.Equal(content, File.ReadAllBytes(Local));
        Assert.Equal(TestModels.Sha(content), File.ReadAllText(Local + ".ok"));
        var s = store.StatusOf("m");
        Assert.True(s.IsComplete);
        Assert.True(s.AllVerified);
        Assert.Equal(1, s.Fraction);
        Assert.Empty(store.Downloading);
        Assert.False(store.LastError.ContainsKey("m"));
        Assert.Contains("Alle bestanden gecontroleerd", store.LastMessage["m"]);
    }

    [Fact]
    public async Task AnAlternativeHashIsGoodToo()
    {
        var content = Data(100);
        var c = new ModelCatalog
        {
            Models = new[]
            {
                new ModelInfo
                {
                    Id = "m", Name = "T", Family = "glm", Summary = "s",
                    Files = new[] { new ModelFile { Local = "m.gguf", Size = 100, Sha256 = new[] { new string('0', 64), TestModels.Sha(content) }, Url = "https://x.test/a" } },
                },
            },
        };
        server.Files["https://x.test/a"] = content;
        using var store = await Download(c, "m");
        Assert.True(store.StatusOf("m").AllVerified);
    }

    [Fact]
    public async Task AWrongHashIsShownAndTheFileStaysUnverified()
    {
        var content = Data(100);
        var c = OneFile("m", content, new string('a', 64));
        server.Files[c.Models[0].Files[0].Url] = content;
        using var store = await Download(c, "m");
        Assert.False(store.StatusOf("m").AllVerified);
        Assert.Contains("controlesom klopt niet", store.LastError["m"]);
        Assert.Contains("Download onvolledig", store.LastMessage["m"]);
        Assert.False(File.Exists(Local + ".ok"));
    }

    [Fact]
    public async Task ItResumesWithRangeFromWhatIsThere()
    {
        var content = Data(10_000);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, content[..4000]);
        using var store = await Download(c, "m");
        Assert.Contains(server.Requests, r => r.From == 4000);
        Assert.DoesNotContain(server.Requests, r => r.From == null);
        Assert.Equal(content, File.ReadAllBytes(Local));
    }

    [Fact]
    public async Task AServerThatIgnoresRangeStartsTheFileOver()
    {
        var content = Data(10_000);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        server.IgnoreRange = true;
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, content[..4000]);
        using var store = await Download(c, "m");
        Assert.Equal(content, File.ReadAllBytes(Local));
        Assert.True(store.StatusOf("m").AllVerified);
    }

    [Fact]
    public async Task ABrokenConnectionGoesOnWhereItStopped()
    {
        var content = Data(10_000);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        server.BreakAfter = 3000;
        using var store = await Download(c, "m");
        Assert.Equal(content, File.ReadAllBytes(Local));
        Assert.Contains(server.Requests, r => r.From == 3000);
    }

    [Fact]
    public async Task TenAttemptsThenTheErrorIsOnTheCardWithoutTheAddress()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        server.FailFirst = 1000;
        using var store = await Download(c, "m");
        Assert.Equal(Downloader.Attempts, server.Requests.Count);
        var error = store.LastError["m"];
        Assert.Contains("m.gguf", error);
        Assert.DoesNotContain("maya", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", error);
    }

    [Fact]
    public async Task ServerStatusIsDescribed()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        server.AlwaysStatus = HttpStatusCode.Forbidden;
        using var store = await Download(c, "m");
        Assert.Contains("403", store.LastError["m"]);
    }

    [Fact]
    public async Task TheLastAttemptMayStillSucceed()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        server.FailFirst = 9;
        using var store = await Download(c, "m");
        Assert.True(store.StatusOf("m").AllVerified);
    }

    [Fact]
    public async Task AFileOfTheWrongSizeIsReported()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, new byte[200]);
        using var store = await Download(c, "m");
        Assert.Contains("grootte 200", store.LastError["m"]);
    }

    /// <summary>Seen on a real model: the maker replaced its files, and 111 GB came down before the app said so.</summary>
    [Fact]
    public async Task AReplacedFileIsReportedBeforeAByteIsWritten()
    {
        var c = OneFile("m", Data(100));
        server.Files[c.Models[0].Files[0].Url] = Data(200);
        using var store = await Download(c, "m");
        Assert.Contains("vervangen of verwijderd", store.LastError["m"]);
        Assert.Single(server.Requests);
        Assert.Equal(0UL, ModelStore.FileSize(Local));
    }

    [Fact]
    public async Task AReplacedFileLeavesAPartialFileAlone()
    {
        var c = OneFile("m", Data(100));
        server.Files[c.Models[0].Files[0].Url] = Data(200);
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, Data(50));
        using var store = await Download(c, "m");
        Assert.Contains("vervangen of verwijderd", store.LastError["m"]);
        Assert.Equal(50UL, ModelStore.FileSize(Local));
    }

    [Fact]
    public async Task ARemovedFileIsReportedAtOnce()
    {
        var c = OneFile("m", Data(100));
        using var store = await Download(c, "m");
        Assert.Contains("vervangen of verwijderd", store.LastError["m"]);
        Assert.Single(server.Requests);
    }

    /// <summary>The first file is replaced, the second never answers: the download of the model ends anyway.</summary>
    [Fact]
    public async Task AReplacedFileStopsTheRestOfTheModel()
    {
        var first = new ModelFile { Local = "a.gguf", Size = 100, Sha256 = new[] { new string('0', 64) }, Url = "https://x.test/a" };
        var second = new ModelFile { Local = "b.gguf", Size = 100, Sha256 = new[] { new string('0', 64) }, Url = "https://x.test/b" };
        var c = new ModelCatalog { Models = new[] { new ModelInfo { Id = "m", Name = "T", Family = "qwen", Summary = "s", Files = new[] { first, second } } } };
        using var store = new ModelStore(c, new HttpClient(new OneReplacedOneSilent()), timer: false) { RetryDelay = TimeSpan.Zero };
        store.StartDownload("m");
        await store.WaitForDownload("m").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("vervangen of verwijderd", store.LastError["m"]);
    }

    private sealed class OneReplacedOneSilent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.ToString().EndsWith("/a")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[200]) };
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>The hash is of the file as published, the stamp comes after it.</summary>
    [Fact]
    public async Task StampIsPatchedAfterTheHash()
    {
        var body = TestModels.WriteGguf(new (string, uint, object)[]
        {
            ("general.architecture", 8, "deepseek41"),
            ("split.no", 2, (ushort)0),
            ("split.count", 2, (ushort)1),
        }, new (string, ulong)[] { ("token_embd.weight", 0) }).Concat(new byte[64]).ToArray();
        var c = OneFile("m", body, stamp: new[] { 8, 9 });
        server.Files[c.Models[0].Files[0].Url] = body;
        using var store = await Download(c, "m");
        Assert.True(File.Exists(Local + ".ok"));
        Assert.Equal(TestModels.Sha(body), File.ReadAllText(Local + ".ok"));
        Assert.NotEqual(TestModels.Sha(body), TestModels.Sha(File.ReadAllBytes(Local)));
        var idx = GGUFIndex.Load(new[] { Local }, new[] { (ulong)body.Length });
        Assert.Equal((ushort)7, idx.Metadata["split.no"]);
        Assert.Equal((ushort)9, idx.Metadata["split.count"]);
    }

    [Fact]
    public void StampWithoutTheKeysSaysSo()
    {
        var p = Path.Combine(Root, "x.gguf");
        File.WriteAllBytes(p, new byte[100]);
        Assert.Throws<InvalidDataException>(() => Downloader.StampSplit(p, 1, 2));
    }

    [Fact]
    public async Task FilesAreFetchedInParallelAlongWithVisionAndDraft()
    {
        byte[] a = Data(100, 1), b = Data(200, 3), v = Data(50, 5), d = Data(60, 7);
        var c = new ModelCatalog
        {
            Models = new[]
            {
                new ModelInfo
                {
                    Id = "m", Name = "T", Family = "qwen", Summary = "s", Vision = "vv",
                    Files = new[]
                    {
                        new ModelFile { Local = "m-00001-of-00002.gguf", Size = 100, Sha256 = new[] { TestModels.Sha(a) }, Url = "https://x.test/a" },
                        new ModelFile { Local = "m-00002-of-00002.gguf", Size = 200, Sha256 = new[] { TestModels.Sha(b) }, Url = "https://x.test/b" },
                    },
                    Draft = new ModelFile { Local = "m-draft.gguf", Size = 60, Sha256 = new[] { TestModels.Sha(d) }, Url = "https://x.test/d" },
                },
            },
            Vision = new[] { new VisionInfo { Id = "vv", Local = "vision.gguf", Size = 50, Sha256 = new[] { TestModels.Sha(v) }, Url = "https://x.test/v" } },
        };
        server.Files["https://x.test/a"] = a;
        server.Files["https://x.test/b"] = b;
        server.Files["https://x.test/v"] = v;
        server.Files["https://x.test/d"] = d;
        using var store = await Download(c, "m");
        Assert.Equal(4, server.Requests.Count);
        var s = store.StatusOf("m");
        Assert.True(s.AllVerified);
        Assert.True(s.Vision!.Verified);
        Assert.True(s.Draft!.Verified);
        Assert.Equal(Path.Combine(Paths.VisionDir, "vision.gguf"), store.VisionPath(c.Models[0]));
        Assert.Equal(Path.Combine(Dir("m"), "m-draft.gguf"), store.DraftPath(c.Models[0]));
    }

    private static (ModelCatalog Catalog, byte[] Model, byte[] Second, byte[] Encoder, byte[] Vocab) WithTieredVision()
    {
        byte[] a = Data(100, 1), v = Data(50, 5), e = Data(70, 7), w = Data(30, 9);
        return (new ModelCatalog
        {
            Models = new[]
            {
                new ModelInfo
                {
                    Id = "m", Name = "T", Family = "glm", Summary = "s", Vision = "vv", Engine = "tiered",
                    Files = new[] { new ModelFile { Local = "m.gguf", Size = 100, Sha256 = new[] { TestModels.Sha(a) }, Url = "https://x.test/a" } },
                },
            },
            Vision = new[] { new VisionInfo { Id = "vv", Local = "second.gguf", Size = 50, Sha256 = new[] { TestModels.Sha(v) }, Url = "https://x.test/v" } },
            TieredVision = new[]
            {
                new TieredVisionInfo
                {
                    Id = "vv",
                    Files = new[]
                    {
                        new ModelFile { Local = "encoder.gguf", Size = 70, Sha256 = new[] { TestModels.Sha(e) }, Url = "https://x.test/e" },
                        new ModelFile { Local = "vocab.gguf", Size = 30, Sha256 = new[] { TestModels.Sha(w) }, Url = "https://x.test/w" },
                    },
                },
            },
        }, a, v, e, w);
    }

    /// <summary>The two engines read images from different files: a model downloads the ones of the engine that runs it here.</summary>
    [Fact]
    public async Task AModelFetchesTheImageFilesOfItsEngine()
    {
        var (c, a, v, e, w) = WithTieredVision();
        server.Files["https://x.test/a"] = a;
        server.Files["https://x.test/v"] = v;
        server.Files["https://x.test/e"] = e;
        server.Files["https://x.test/w"] = w;
        using var store = Store(c);
        store.EngineOf = _ => EngineKind.Tiered;
        store.StartDownload("m");
        await store.WaitForDownload("m");
        store.Refresh();
        Assert.Equal(3, server.Requests.Count);
        Assert.False(store.StatusOf("m").Vision!.Verified);
        var folder = Path.Combine(Paths.VisionDir, "vv");
        Assert.Equal(new[] { Path.Combine(folder, "encoder.gguf"), Path.Combine(folder, "vocab.gguf") }, store.VisionPaths(c.Models[0]));

        store.EngineOf = _ => EngineKind.Llama;
        Assert.Empty(store.VisionPaths(c.Models[0]));
        Assert.Equal("second.gguf", Assert.Single(store.VisionStatus(c.Models[0])).Name);
    }

    /// <summary>A model that is already here gets only its image files, with the same checks; a wrong file is no image file.</summary>
    [Fact]
    public async Task TheImageFilesCanComeLaterAndAreVerified()
    {
        var (c, a, _, e, w) = WithTieredVision();
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Path.Combine(Dir("m"), "m.gguf"), a);
        server.Files["https://x.test/e"] = e;
        server.Files["https://x.test/w"] = Data(30, 11);
        using var store = Store(c);
        store.EngineOf = _ => EngineKind.Tiered;
        Assert.True(store.StatusOf("m").IsComplete);
        Assert.Empty(store.VisionPaths(c.Models[0]));

        store.StartVisionDownload("m");
        await store.WaitForDownload("m");
        store.Refresh();
        Assert.DoesNotContain(server.Requests, r => r.Url.Contains("x.test/a"));
        Assert.Contains("vocab.gguf", store.LastError["m"]);
        Assert.False(store.StatusOf("m").TieredVision[1].Verified);
        Assert.True(store.StatusOf("m").TieredVision[0].Verified);

        File.Delete(Path.Combine(Paths.VisionDir, "vv", "vocab.gguf"));
        server.Files["https://x.test/w"] = w;
        store.StartVisionDownload("m");
        await store.WaitForDownload("m");
        store.Refresh();
        Assert.Equal(2, store.VisionPaths(c.Models[0]).Count);
        Assert.False(store.LastError.ContainsKey("m"));
    }

    /// <summary>A hand-placed file of the right size counts, so models from a Mac need no new download.</summary>
    [Fact]
    public void HandPlacedFilesOfTheRightSizeCount()
    {
        var c = OneFile("m", Data(100));
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, new byte[100]);
        using var store = Store(c);
        var s = store.StatusOf("m");
        Assert.True(s.IsComplete);
        Assert.False(s.AllVerified);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AVerifiedFileIsNotFetchedAgain()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, content);
        File.WriteAllText(Local + ".ok", TestModels.Sha(content));
        using var store = await Download(c, "m");
        Assert.Empty(server.Requests);
    }

    // MARK: - versions (the Mac's ModelVersionTests)

    private static ModelStore.FileStatus Status(string? ok, ulong have = 10, ulong expected = 10) => new("a", expected, have, ok, new[] { "abc" });

    [Fact]
    public void TheCatalogsVersion()
    {
        var s = Status("abc");
        Assert.True(s.Verified && s.Complete);
        Assert.False(s.Stale);
        Assert.True(Status("ABC").Verified, "a hash is compared without case");
    }

    [Fact]
    public void AnotherVersionIsAnUpdate()
    {
        Assert.True(Status("def").Stale);
        Assert.True(Status("abc", have: 8).Stale, "the size no longer matches");
        var model = new ModelStore.ModelStatus { Files = new[] { Status("def"), Status("abc") } };
        Assert.True(model.UpdateAvailable);
        Assert.Equal(10UL, model.UpdateBytes);
        Assert.True(model.IsComplete, "the old version keeps working until the update");
        Assert.False(model.AllVerified);
    }

    /// <summary>An empty .ok (a model folder from the Mac before its 1.9.12) is trusted; a file without one is no update.</summary>
    [Fact]
    public void AnOldOkIsTrusted()
    {
        var s = Status("");
        Assert.True(s.Verified && s.Complete);
        Assert.False(s.Stale);
        Assert.False(Status(null).Stale, "a file downloaded by hand is no update");
    }

    [Fact]
    public void TheOkIsReadFromDisk()
    {
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, new byte[10]);
        File.WriteAllText(Local + ".ok", "abc\r\n");
        var s = ModelStore.FileStatusOf(Dir("m"), "m.gguf", 10, new[] { "abc" });
        Assert.Equal("abc", s.OkHash);
        Assert.True(s.Verified);
    }

    /// <summary>Update: the file of another version goes, the catalog's comes down and gets its own .ok; the other file stays.</summary>
    [Fact]
    public async Task AnUpdateFetchesOnlyTheFileThatDiffers()
    {
        byte[] oldA = Data(100, 3), newA = Data(120, 5), b = Data(80, 7);
        var c = new ModelCatalog
        {
            Models = new[]
            {
                new ModelInfo
                {
                    Id = "m", Name = "T", Family = "qwen", Summary = "s",
                    Files = new[]
                    {
                        new ModelFile { Local = "a.gguf", Size = 120, Sha256 = new[] { TestModels.Sha(newA) }, Url = "https://x.test/a" },
                        new ModelFile { Local = "b.gguf", Size = 80, Sha256 = new[] { TestModels.Sha(b) }, Url = "https://x.test/b" },
                    },
                },
            },
        };
        server.Files["https://x.test/a"] = newA;
        server.Files["https://x.test/b"] = b;
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Path.Combine(Dir("m"), "a.gguf"), oldA);
        File.WriteAllText(Path.Combine(Dir("m"), "a.gguf.ok"), TestModels.Sha(oldA));
        File.WriteAllBytes(Path.Combine(Dir("m"), "b.gguf"), b);
        File.WriteAllText(Path.Combine(Dir("m"), "b.gguf.ok"), TestModels.Sha(b));

        using var store = Store(c);
        var before = store.StatusOf("m");
        Assert.True(before.UpdateAvailable);
        Assert.True(before.IsComplete);
        Assert.Equal(120UL, before.UpdateBytes);

        store.StartDownload("m");
        await store.WaitForDownload("m");
        Assert.Equal(newA, File.ReadAllBytes(Path.Combine(Dir("m"), "a.gguf")));
        Assert.Equal(TestModels.Sha(newA), File.ReadAllText(Path.Combine(Dir("m"), "a.gguf.ok")));
        Assert.DoesNotContain(server.Requests, r => r.Url.Contains("x.test/b"));
        var after = store.StatusOf("m");
        Assert.False(after.UpdateAvailable);
        Assert.True(after.AllVerified);
    }

    /// <summary>The app was closed in the middle of a download: the next start goes on with it.</summary>
    [Fact]
    public async Task ADownloadResumesAtTheNextStart()
    {
        var content = Data(5000);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        var slow = new BlockingServer();
        var first = new ModelStore(c, new HttpClient(slow), timer: false) { RetryDelay = TimeSpan.Zero };
        first.StartDownload("m");
        Assert.True(WaitUntil(() => slow.Entered));
        Assert.Contains("m", first.Downloading);
        first.Dispose();    // closing the app
        Assert.True(File.Exists(Path.Combine(Root, "downloads.json")));

        using var second = new ModelStore(c, new HttpClient(server), timer: false) { RetryDelay = TimeSpan.Zero };
        second.ResumeInterrupted();
        await second.WaitForDownload("m");
        Assert.True(second.StatusOf("m").AllVerified);
        // done: the next start has nothing to do
        Assert.DoesNotContain("\"m\"", File.ReadAllText(Path.Combine(Root, "downloads.json")));
    }

    [Fact]
    public async Task PauseKeepsThePartialFileAndIsNotResumed()
    {
        var content = Data(5000);
        var c = OneFile("m", content);
        var slow = new BlockingServer();
        using var store = new ModelStore(c, new HttpClient(slow), timer: false) { RetryDelay = TimeSpan.Zero };
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Local, content[..1000]);
        store.StartDownload("m");
        Assert.True(WaitUntil(() => slow.Entered));
        store.StopDownload("m");
        await store.WaitForDownload("m");
        Assert.Equal(1000, new FileInfo(Local).Length);
        Assert.DoesNotContain("\"m\"", File.ReadAllText(Path.Combine(Root, "downloads.json")));
    }

    [Fact]
    public async Task PathsOver260CharactersWork()
    {
        var content = Data(1000);
        var c = OneFile("m", content);
        server.Files[c.Models[0].Files[0].Url] = content;
        string deep = Root;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        Settings.Shared.ModelFolder = deep;
        using var store = await Download(c, "m");
        Assert.True(store.StatusOf("m").AllVerified);
        Assert.True(Local.Length > 260);
    }

    [Fact]
    public void HeadersAreEnoughToChooseAModel()
    {
        var c = new ModelCatalog
        {
            Models = new[]
            {
                new ModelInfo
                {
                    Id = "m", Name = "T", Family = "qwen", Summary = "s",
                    Files = new[]
                    {
                        new ModelFile { Local = "big.gguf", Size = 1UL << 40, Sha256 = new[] { "" }, Url = "https://x.test/a" },
                        new ModelFile { Local = "small.gguf", Size = 1000, Sha256 = new[] { "" }, Url = "https://x.test/b" },
                    },
                },
            },
        };
        Directory.CreateDirectory(Dir("m"));
        using var store = Store(c);
        Assert.False(store.StatusOf("m").HeadersAvailable);
        File.WriteAllBytes(Path.Combine(Dir("m"), "big.gguf"), new byte[32 << 20]);
        File.WriteAllBytes(Path.Combine(Dir("m"), "small.gguf"), new byte[1000]);
        store.Refresh();
        Assert.True(store.StatusOf("m").HeadersAvailable);
        Assert.False(store.StatusOf("m").IsComplete);
    }

    [Fact]
    public void SpeedIsARollingAverage()
    {
        using var store = Store(new ModelCatalog());
        var t = DateTime.UtcNow;
        store.UpdateSpeed(1000, t);
        Assert.Equal(0, store.Speed);
        store.UpdateSpeed(3000, t.AddSeconds(2));
        Assert.Equal(1000, store.Speed);
        store.UpdateSpeed(3000, t.AddSeconds(4));
        Assert.Equal(800, store.Speed);
        store.UpdateSpeed(5000, t.AddSeconds(4.1));   // too soon to count
        Assert.Equal(800, store.Speed);
    }

    /// <summary>Files that came down under upstream's name get their own: this PC's first models were downloaded by a script.</summary>
    [Fact]
    public void UpstreamNamesAreAdopted()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        Directory.CreateDirectory(Dir("m"));
        File.WriteAllBytes(Path.Combine(Dir("m"), "File-Maya.gguf"), content);
        File.WriteAllText(Path.Combine(Dir("m"), "File-Maya.gguf.ok"), TestModels.Sha(content));
        using var store = Store(c);
        Assert.True(File.Exists(Local));
        Assert.True(File.Exists(Local + ".ok"));
        Assert.False(File.Exists(Path.Combine(Dir("m"), "File-Maya.gguf")));
        Assert.True(store.StatusOf("m").AllVerified);
    }

    [Fact]
    public void DeleteRemovesTheModelButNotWhatOthersShare()
    {
        var content = Data(100);
        var c = OneFile("m", content);
        Directory.CreateDirectory(Dir("m"));
        Directory.CreateDirectory(Paths.VisionDir);
        File.WriteAllBytes(Local, content);
        File.WriteAllBytes(Path.Combine(Paths.VisionDir, "v.gguf"), content);
        using var store = Store(c);
        Assert.Null(store.DeleteModel("m"));
        Assert.False(Directory.Exists(Dir("m")));
        Assert.True(File.Exists(Path.Combine(Paths.VisionDir, "v.gguf")));
        Assert.False(store.StatusOf("m").IsComplete);
        Assert.NotNull(store.DeleteModel("unknown"));
    }

    [Fact]
    public async Task NoDeleteWhileItDownloads()
    {
        var c = OneFile("m", Data(100));
        var slow = new BlockingServer();
        using var store = new ModelStore(c, new HttpClient(slow), timer: false) { RetryDelay = TimeSpan.Zero };
        store.StartDownload("m");
        Assert.True(WaitUntil(() => slow.Entered));
        Assert.Contains("gedownload", store.DeleteModel("m"));
        store.StopDownload("m");
        await store.WaitForDownload("m");
    }

    /// <summary>A disconnected drive: the app says so, and deletes nothing and writes nothing.</summary>
    [Fact]
    public void AMissingFolderIsReportedAndNothingHappens()
    {
        var c = OneFile("m", Data(100));
        var gone = Path.Combine(Root, "not-there");
        Settings.Shared.ModelFolder = gone;
        using var store = Store(c);
        Assert.True(ModelStore.FolderMissing);
        Assert.Contains("niet (meer) op deze pc", store.DeleteModel("m"));
        store.StartDownload("m");
        Assert.Contains("niet (meer) op deze pc", store.LastError["m"]);
        Assert.Empty(store.Downloading);
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public async Task APackIsBuiltAfterTheDownload()
    {
        var content = Data(100);
        var m = OneFile("m", content).Models[0] with { Engine = "tiered", Pack = new PackInfo("glm") };
        var c = new ModelCatalog { Models = new[] { m } };
        server.Files[m.Files[0].Url] = content;
        int built = 0;
        using var store = Store(c);
        store.BuildPack = (model, progress, ct) =>
        {
            progress(0.5, "halfway");
            built++;
            return Task.FromResult<string?>(null);
        };
        store.StartDownload("m");
        await store.WaitForDownload("m");
        Assert.Equal(1, built);
        Assert.Contains("Pack klaar", store.LastMessage["m"]);
    }

    [Fact]
    public async Task APackThatFailsIsShown()
    {
        var content = Data(100);
        var m = OneFile("m", content).Models[0] with { Engine = "tiered", Pack = new PackInfo("glm") };
        var c = new ModelCatalog { Models = new[] { m } };
        server.Files[m.Files[0].Url] = content;
        using var store = Store(c);
        store.BuildPack = (_, _, _) => Task.FromResult<string?>("kapot");
        store.StartDownload("m");
        await store.WaitForDownload("m");
        Assert.Equal("kapot", store.LastError["m"]);
        Assert.NotEqual("kapot", store.LastMessage.GetValueOrDefault("m"));
    }
}

public class PackBuilderTests : EngineTest
{
    private static ModelInfo Model(string tool) => new()
    {
        Id = "m", Name = "M", Family = tool, Summary = "s", Engine = "tiered", Pack = new PackInfo(tool),
        Files = new[]
        {
            new ModelFile { Local = "m-00001-of-00002.gguf", Size = 10, Sha256 = new[] { "" }, Url = "u" },
            new ModelFile { Local = "m-00002-of-00002.gguf", Size = 10, Sha256 = new[] { "" }, Url = "u" },
        },
        Extras = tool == "minimax" ? new[] { new ModelFile { Local = "m-indexers.bin", Size = 1, Sha256 = new[] { "" }, Url = "u" } } : null,
    };

    [Fact]
    public void GlmCommandAsTheBriefSays()
    {
        var c = PackBuilder.Command(Model("glm"));
        Assert.Equal(Path.Combine(Paths.EngineRoot, "python", "python.exe"), c.Exe);
        Assert.Equal(Path.Combine(Paths.EngineRoot, "tiered", "tools", "iq_pack.py"), c.Args[0]);
        Assert.Equal(new[] { "--gguf", Path.Combine(Paths.ModelDir("m"), "m-00001-of-00002.gguf"), "--out", Path.Combine(Paths.ModelDir("m"), "pack"), "--compat-bf16" }, c.Args.Skip(1));
        Assert.Equal(Path.Combine(Paths.EngineRoot, "tiered", "tools", "gguf-py"), c.Env!["STRATA_GGUF_PY"]);
    }

    [Fact]
    public void OtherToolsGetTheirArguments()
    {
        var ds = PackBuilder.Command(Model("deepseek"));
        Assert.EndsWith(Path.Combine("ds4", "pack_gguf.py"), ds.Args[0]);
        Assert.Contains("--shards", ds.Args);
        var mm = PackBuilder.Command(Model("minimax"));
        Assert.EndsWith(Path.Combine("mm3", "pack_gguf.py"), mm.Args[0]);
        Assert.Contains("--text-only", mm.Args);
        Assert.Equal(Path.Combine(Paths.ModelDir("m"), "m-indexers.bin"), mm.Args[mm.Args.ToList().IndexOf("--indexers") + 1]);
    }

    private static ModelInfo WithTower(string tool, string vision) => Model(tool) with { Vision = vision };

    private static string PlaceTower(string vision)
    {
        var v = ModelCatalog.Shared.TieredVision.First(t => t.Id == vision);
        Directory.CreateDirectory(v.Folder);
        foreach (var f in v.Files)
        {
            using var file = File.Create(Path.Combine(v.Folder, f.Local));
            file.SetLength((long)f.Size);
        }
        return v.Folder;
    }

    /// <summary>MiniMax's pack takes the tower once its files are here; without them it stays text only.</summary>
    [Fact]
    public void TheMiniMaxPackTakesItsTower()
    {
        var m = WithTower("minimax", "minimax-m3.1");
        Assert.Contains("--text-only", PackBuilder.Command(m).Args);
        var folder = PlaceTower("minimax-m3.1");
        var args = PackBuilder.Command(m).Args.ToList();
        Assert.DoesNotContain("--text-only", args);
        Assert.Equal(folder, args[args.IndexOf("--companions") + 1]);
        Assert.True(args.IndexOf("--companions") < args.IndexOf("--shards"));
        Assert.Contains("--text-only", PackBuilder.Command(m, textOnly: true).Args);
    }

    /// <summary>DeepSeek's: the mmproj becomes vision.bin first, and the pack points at that.</summary>
    [Fact]
    public void TheDeepSeekPackTakesItsTower()
    {
        var m = WithTower("deepseek", "deepseek-v4.1");
        var folder = PlaceTower("deepseek-v4.1");
        Assert.DoesNotContain("--vision", PackBuilder.Command(m).Args);
        var made = PackBuilder.TowerCommand(folder);
        Assert.EndsWith(Path.Combine("ds4", "vision_pack.py"), made.Args[0]);
        Assert.Equal(Path.Combine(folder, "mmproj-BF16.gguf"), made.Args[made.Args.ToList().IndexOf("--mmproj") + 1]);
        File.WriteAllText(Path.Combine(folder, "vision.json"), "{}");
        var args = PackBuilder.Command(m).Args.ToList();
        Assert.Equal(folder, args[args.IndexOf("--vision") + 1]);
        Assert.True(args.IndexOf("--vision") < args.IndexOf("--shards"));
    }

    /// <summary>A pack without the tower is built again when the files arrive; when they cannot go in it stays usable, text only, without a build at every start.</summary>
    [Fact]
    public void APackIsBuiltAgainForItsTowerOnce()
    {
        var m = WithTower("deepseek", "deepseek-v4.1");
        WholePack("deepseek");
        var geometry = Path.Combine(PackBuilder.PackDir(m), "ds4.txt");
        File.WriteAllText(geometry, "model_dir " + Paths.ModelDir("m") + "\n");
        Assert.Null(PackBuilder.WhyNotReady(m));
        PlaceTower("deepseek-v4.1");
        Assert.Contains("afbeeldingsbestanden", PackBuilder.WhyNotReady(m));

        File.WriteAllText(Path.Combine(PackBuilder.PackDir(m), "no-vision-tower"), "reason");
        Assert.True(PackBuilder.TowerRefused(m));
        Assert.Null(PackBuilder.WhyNotReady(m));
        File.Delete(Path.Combine(PackBuilder.PackDir(m), "no-vision-tower"));

        File.AppendAllText(geometry, "vision.num_hidden_layers 24\n");
        Assert.True(PackBuilder.PackHasTower(m));
        Assert.Null(PackBuilder.WhyNotReady(m));
    }

    private static void WholePack(string tool)
    {
        var pack = PackBuilder.PackDir(Model(tool));
        foreach (var f in PackBuilder.NeededFiles(tool))
        {
            var path = Path.Combine(pack, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    [Fact]
    public void AMissingPackAndAChangedFileNeedABuild()
    {
        var m = Model("glm");
        Assert.NotNull(PackBuilder.WhyNotReady(m));
        WholePack("glm");
        var shards = new[] { "m-00001-of-00002.gguf", "m-00002-of-00002.gguf" }.Select(n => Path.Combine(Paths.ModelDir("m"), n)).ToList();
        foreach (var s in shards) File.WriteAllBytes(s, new byte[10]);
        File.WriteAllText(Path.Combine(PackBuilder.PackDir(m), "native_experts.txt"), "# absolute offsets in m-00001-of-00002.gguf, bytes\n");
        Assert.Null(PackBuilder.WhyNotReady(m));

        PackBuilder.WriteSources(PackBuilder.PackDir(m), shards);
        Assert.Null(PackBuilder.WhyNotReady(m));
        File.SetLastWriteTimeUtc(shards[1], DateTime.UtcNow.AddHours(1));
        Assert.Contains("veranderde", PackBuilder.WhyNotReady(m));
    }

    [Fact]
    public void APackThatNamesOtherFilesNeedsABuild()
    {
        var m = Model("glm");
        WholePack("glm");
        File.WriteAllText(Path.Combine(PackBuilder.PackDir(m), "native_experts.txt"), "# absolute offsets in GLM-Other-00001-of-00003.gguf, bytes\n");
        Assert.Contains("andere bestandsnaam", PackBuilder.WhyNotReady(m));
    }

    [Fact]
    public void APackThatPointsAtAnotherFolderNeedsABuild()
    {
        var m = Model("deepseek");
        WholePack("deepseek");
        foreach (var f in m.Files) File.WriteAllBytes(Path.Combine(Paths.ModelDir("m"), f.Local), new byte[10]);
        File.WriteAllText(Path.Combine(PackBuilder.PackDir(m), "ds4.txt"), "model_dir C:\\elsewhere\\m\nformat gguf\n");
        Assert.Contains("andere map", PackBuilder.WhyNotReady(m));
        File.WriteAllText(Path.Combine(PackBuilder.PackDir(m), "ds4.txt"), "model_dir " + Paths.ModelDir("m") + "\nformat gguf\n");
        Assert.Null(PackBuilder.WhyNotReady(m));
    }

    [Fact]
    public void WrittenTimesAreTheOnesPythonReads()
    {
        var p = Path.Combine(Root, "f");
        File.WriteAllText(p, "x");
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(p, when);
        Assert.Equal(new DateTimeOffset(when).ToUnixTimeSeconds() * 1_000_000_000, PackBuilder.MtimeNs(p));
    }

    [Fact]
    public void ProgressFollowsTheLayers()
    {
        var pr = new PackProgress(46);
        Assert.Equal(0, pr.Consume("glm5-next: block_count 46, nextn 1 -> trunk layers 0..44"));
        Assert.Equal(11 / 46.0, pr.Consume("  layer 10  IQ2_XXS /IQ3_XXS blob 123  at 1.00 GiB")!.Value, 3);
        Assert.Equal(11 / 46.0, pr.Consume("index.txt: 10 tensors")!.Value, 3);
        Assert.Equal(0.99, pr.Consume("  layer 45  x")!.Value, 3);
    }
}

public class ModelFolderMoverTests : EngineTest
{
    /// <summary>A folder on the repo's drive, to get a real copy between drives when the temp folder is on another one.</summary>
    private readonly string target = Path.Combine(AppContext.BaseDirectory, "wam-move-" + Guid.NewGuid().ToString("N"));

    public override void Dispose()
    {
        try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (IOException) { }
        base.Dispose();
    }

    private static (string Path, byte[] Bytes) Model(string name = "m.gguf", int size = 3000)
    {
        var dir = Paths.ModelDir("m");
        Directory.CreateDirectory(dir);
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes);
        File.WriteAllText(p + ".ok", "hash");
        File.SetLastWriteTimeUtc(p, new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        return (p, bytes);
    }

    [Fact]
    public async Task EverythingMovesAndTheFolderChanges()
    {
        var (source, bytes) = Model();
        Directory.CreateDirectory(Paths.VisionDir);
        File.WriteAllText(Path.Combine(Paths.VisionDir, "v.gguf"), "vision");
        ulong lastDone = 0, lastTotal = 0;
        var error = await ModelFolderMover.Run(target, false, (d, t) => { lastDone = d; lastTotal = t; });
        Assert.Null(error);
        Assert.Equal(target, Paths.ModelsRoot);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, "m", "m.gguf")));
        Assert.Equal("hash", File.ReadAllText(Path.Combine(target, "m", "m.gguf.ok")));
        Assert.True(File.Exists(Path.Combine(target, "vision", "v.gguf")));
        // the pack checks the time of the file
        Assert.Equal(new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(target, "m", "m.gguf")));
        Assert.False(File.Exists(source));
        Assert.Equal(lastTotal, lastDone);
        Assert.Null(ModelFolderMover.Pending());
    }

    [Fact]
    public async Task AnInterruptedMoveGoesOnFromWhatIsThere()
    {
        var (source, bytes) = Model();
        Directory.CreateDirectory(Path.Combine(target, "m"));
        File.WriteAllBytes(Path.Combine(target, "m", "m.gguf"), bytes[..1000]);
        var error = await ModelFolderMover.Run(target, false);
        Assert.Null(error);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, "m", "m.gguf")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task AStoppedMoveKeepsEveryOriginalAndCanGoOn()
    {
        var (source, bytes) = Model(size: 5_000_000);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = await ModelFolderMover.Run(target, false, null, cts.Token);
        Assert.Contains("originelen staan er nog", error);
        Assert.True(File.Exists(source));
        Assert.Equal(bytes.Length, new FileInfo(source).Length);
        Assert.Equal(Paths.DefaultModelsRoot, Paths.ModelsRoot);
        Assert.NotNull(ModelFolderMover.Pending());
        Assert.Null(await ModelFolderMover.Run(target, false));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, "m", "m.gguf")));
    }

    [Fact]
    public async Task NotWhileTheEngineRuns()
    {
        var (source, _) = Model();
        var error = await ModelFolderMover.Run(target, true);
        Assert.Contains("Stop eerst de engine", error);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(target));
    }

    /// <summary>The folder is gone (a disconnected drive): the app says so, nothing is deleted, the setting stays.</summary>
    [Fact]
    public async Task AMissingSourceIsReportedAndNothingIsDeleted()
    {
        var gone = Path.Combine(Root, "gone");
        Settings.Shared.ModelFolder = gone;
        var error = await ModelFolderMover.Run(target, false);
        Assert.Contains("niet (meer) op deze pc", error);
        Assert.Equal(gone, Paths.ModelsRoot);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task ALargerFileAtTheTargetStopsTheMoveBeforeAnythingHappens()
    {
        var (source, bytes) = Model();
        Directory.CreateDirectory(Path.Combine(target, "m"));
        File.WriteAllBytes(Path.Combine(target, "m", "m.gguf"), new byte[bytes.Length + 10]);
        var error = await ModelFolderMover.Run(target, false);
        Assert.Contains("groter dan het origineel", error);
        Assert.True(File.Exists(source));
        Assert.Equal(Paths.DefaultModelsRoot, Paths.ModelsRoot);
    }

    [Fact]
    public void TooLittleRoomIsAnsweredWithTheFigures()
    {
        Model();
        var noRoom = new DriveReport("Z:\\", 10, 1000, true, false, null);
        var check = ModelFolderMover.Prepare(Paths.ModelsRoot, "Z:\\models", false, _ => noRoom);
        Assert.Contains("vrij", check.Problem);
        Assert.Contains("Er is niets verplaatst", check.Problem);
        Assert.False(check.SameDrive);
    }

    [Fact]
    public void ADriveThatIsNotThereIsNamed()
    {
        Model();
        var absent = new DriveReport("Z:\\", 0, 0, false, false, null);
        Assert.Contains("niet beschikbaar", ModelFolderMover.Prepare(Paths.ModelsRoot, "Z:\\models", false, _ => absent).Problem);
    }

    [Fact]
    public void TheSameFolderOrOneInsideItIsRefused()
    {
        Model();
        Assert.NotNull(ModelFolderMover.Prepare(Paths.ModelsRoot, Paths.ModelsRoot, false).Problem);
        Assert.Contains("ligt in de huidige", ModelFolderMover.Prepare(Paths.ModelsRoot, Path.Combine(Paths.ModelsRoot, "sub"), false).Problem);
    }

    [Theory]
    [InlineData(17u, "", "ADATA", true)]
    [InlineData(8u, "NVMe", "ADATA LEGEND 710", true)]
    [InlineData(8u, "Samsung", "SSD 870 QVO 4TB", false)]
    [InlineData(11u, "", "WDC WD40", false)]
    public void NvmeAlsoBehindRaidDrivers(uint bus, string vendor, string product, bool nvme) =>
        Assert.Equal(nvme, DriveReport.IsNvmeDevice(bus, vendor, product));

    [Fact]
    public void DriveReportOfThisPc()
    {
        var d = DriveReport.For(Root);
        Assert.True(d.Ready);
        Assert.True(d.TotalBytes > 0);
        Assert.True(d.FreeBytes > 0);
        Assert.NotEmpty(DriveReport.All());
        Assert.Null(DriveReport.BetterDrive(Root, 1));
    }
}
