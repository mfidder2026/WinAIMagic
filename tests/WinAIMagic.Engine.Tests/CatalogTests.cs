using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

public class GGUFIndexTests : EngineTest
{
    private string Write(byte[] content, string name = "m.gguf")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void ReadsMetadataAndTensorSizesFromOffsets()
    {
        var body = TestModels.WriteGguf(new (string, uint, object)[]
        {
            ("general.architecture", 8, "deepseek41"),
            ("deepseek41.block_count", 4, 40u),
            ("deepseek41.context_length", 10, 1048576UL),
            ("deepseek41.context_length_original", 4, 1u),
            ("tokenizer.ggml.tokens", 9, Enumerable.Range(0, 5000).Select(i => "t" + i).ToArray()),
        }, new (string, ulong)[]
        {
            ("blk.1.ffn_up_exps.weight", 100),
            ("token_embd.weight", 0),
            ("blk.1.engram_embd.weight", 300),
            ("per_layer_token_embd.weight", 400),
            ("blk.2.ffn_down_exps.weight", 500),
        });
        var path = Write(body);
        var index = GGUFIndex.Load(new[] { path }, new[] { (ulong)body.Length + 1000 });
        Assert.Equal(40, index.LayerCount);
        Assert.Equal(1048576, index.ContextLength);
        Assert.Equal("deepseek41", index.Architecture);
        Assert.Equal(5600UL, index.KvBytesPerToken);
        Assert.Equal(100UL, index.DenseBytes);
        Assert.Equal(200UL, index.EngramBytes);
        Assert.Equal(200UL, index.ExpertBytesPerLayer[1]);
        // the last tensor's true size comes from the shard's full size, also while the shard is still downloading
        Assert.Equal(500UL, index.ExpertBytesPerLayer[2]);
        Assert.Equal(5000UL, index.Metadata["tokenizer.ggml.tokens"]);
    }

    [Fact]
    public void ManyShardsAddUpAndTheFirstMetadataWins()
    {
        var a = TestModels.TinyModel("glm5-next", 2);
        var b = TestModels.TinyModel("glm5-next", 3);
        var pa = Write(a, "a.gguf");
        var pb = Write(b, "b.gguf");
        var index = GGUFIndex.Load(new[] { pa, pb }, new[] { (ulong)a.Length, (ulong)b.Length });
        Assert.Equal(2, index.LayerCount);
        Assert.Equal(20_000UL, index.KvBytesPerToken);
        Assert.Equal(3 + 4, index.Tensors.Count);
    }

    [Fact]
    public void NotAGgufAndTruncatedSaySo()
    {
        var bad = Write(new byte[100], "bad.gguf");
        var e = Assert.Throws<GGUFException>(() => GGUFIndex.Load(new[] { bad }, new[] { 100UL }));
        Assert.Contains("bad.gguf is geen GGUF-bestand", e.Message);
        var whole = TestModels.TinyModel();
        var cut = Write(whole[..20], "cut.gguf");
        Assert.Throws<GGUFException>(() => GGUFIndex.Load(new[] { cut }, new[] { 100UL }));
    }

    [Theory]
    [InlineData("blk.12.ffn_up_exps.weight", 12)]
    [InlineData("token_embd.weight", null)]
    [InlineData("blk.x.y", null)]
    [InlineData("v.blk.1", null)]
    public void LayerOfATensor(string name, int? layer) => Assert.Equal(layer, GGUFIndex.Layer(name));

    [Theory]
    [InlineData("deepseek4", 5600)]
    [InlineData("deepseek41", 5600)]
    [InlineData("glm5-next", 20000)]
    [InlineData("qwen4exp", 36000)]
    [InlineData("qwen35moe", 16000)]
    [InlineData("other", 32000)]
    public void KvCostPerArchitecture(string arch, int bytes) =>
        Assert.Equal((ulong)bytes, TestModels.Index(arch, 1, 1, 1).KvBytesPerToken);

    /// <summary>Indexes the real headers when they are on this PC (read-only; under the name the file has there).</summary>
    [Fact]
    public void RealHeadersIfPresent()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAIMagic", "models");
        var qwen = ModelCatalog.Shared.Model("qwen3.6-35b-a3b-q4")!;
        var file = Path.Combine(root, qwen.Id, qwen.Files[0].Local);
        if (File.Exists(file))
        {
            var idx = GGUFIndex.Load(new[] { file }, new[] { qwen.Files[0].Size });
            Assert.Equal(40, idx.LayerCount);
            Assert.Equal(40, idx.ExpertBytesPerLayer.Count);
            Assert.Equal("qwen35moe", idx.Architecture);
            Assert.Equal(qwen.Dense, idx.DenseBytes);
        }
        var glm = ModelCatalog.Shared.Model("glm-5.3-flash-compact-24")!;
        var dir = Path.Combine(root, glm.Id);
        var names = glm.Files.Select(f => Path.Combine(dir, f.Local)).ToList();
        if (!names.All(File.Exists)) names = glm.Files.Select(f => Path.Combine(dir, f.Url[(f.Url.LastIndexOf('/') + 1)..])).ToList();
        if (names.All(File.Exists))
        {
            var idx = GGUFIndex.Load(names, glm.Files.Select(f => f.Size).ToList());
            Assert.Equal(46, idx.LayerCount);
            Assert.Equal(43, idx.ExpertBytesPerLayer.Count);
            Assert.Equal(glm.Dense, idx.DenseBytes);
            Assert.Equal(glm.Memory, idx.DenseBytes + idx.ExpertBytes);
        }
    }
}

public class CatalogTests : EngineTest
{
    private static ModelCatalog C => ModelCatalog.Shared;

    [Fact]
    public void CatalogLoads()
    {
        Assert.Equal(8, C.Models.Count);
        foreach (var m in C.Models)
        {
            Assert.NotEmpty(m.Files);
            Assert.NotNull(m.Memory);
            if (m.Family != "minimax")
            {
                Assert.NotNull(C.VisionFor(m));
                Assert.NotNull(m.Dense);
            }
            // llama.cpp finds the other shards from the first one's name: <prefix>-0000N-of-0000M.gguf
            if (m.Files.Count > 1)
                for (int i = 0; i < m.Files.Count; i++)
                {
                    Assert.StartsWith(m.Id, m.Files[i].Local);
                    Assert.EndsWith($"-{i + 1:00000}-of-{m.Files.Count:00000}.gguf", m.Files[i].Local);
                }
            foreach (var f in m.AllFiles)
            {
                Assert.All(f.Sha256, s => Assert.Matches("^[0-9a-f]{64}$", s));
                Assert.NotEmpty(f.Sha256);
                Assert.StartsWith("https://huggingface.co/", f.Url);
                Assert.True(f.Size > 0);
            }
        }
    }

    /// <summary>The names the user sees are our own: the model name starts with the base model, files are named after the id.</summary>
    [Fact]
    public void NamesAreOurOwn()
    {
        foreach (var m in C.Models)
        {
            Assert.True(new[] { "DeepSeek-V4.1-Flash", "GLM-5.3-Flash", "MiniMax-M3.1-Flash", "Qwen3.8-Flash-Next", "Qwen3.6-35B-A3B" }.Any(m.Name.StartsWith), m.Name);
            Assert.All(m.Files, f => Assert.StartsWith(m.Id, f.Local));
        }
    }

    [Fact]
    public void TheUpstreamNameOnlyLivesInDownloadAddresses()
    {
        foreach (var m in C.Models)
        {
            foreach (var text in new[] { m.Id, m.Name, m.Summary })
                Assert.DoesNotContain("maya", text, StringComparison.OrdinalIgnoreCase);
            foreach (var f in m.AllFiles)
                Assert.DoesNotContain("maya", f.Local, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var v in C.Vision) Assert.DoesNotContain("maya", v.Local, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoSpeedEstimatesInTheCatalog()
    {
        foreach (var m in C.Models)
        {
            Assert.DoesNotContain("tok/s", m.Summary);
            Assert.DoesNotContain(" Mac", m.Summary);
        }
        using var s = typeof(Paths).Assembly.GetManifestResourceStream("Resources.models.json")!;
        Assert.DoesNotContain("speedOn32GB", new StreamReader(s).ReadToEnd());
    }

    [Fact]
    public void TheTwoNewEntries()
    {
        var glm = C.Model("glm-5.3-flash-compact-24")!;
        Assert.Equal("GLM-5.3-Flash Compact 24", glm.Name);
        Assert.Equal(3, glm.Files.Count);
        Assert.Equal(94.7, glm.TotalSize / 1e9, 1);
        Assert.Equal(new[] { "8ebd5c74bcf65ece2a8699b7b6fd2b8d177f350ed252cee252fdd99b86a0138c", "3dc347757686c1435eae36c4872f5c151191cc5063bc188ce699b97700aae076" }, glm.Files[0].Sha256);
        Assert.Contains("/peasantsmith/GLM-5.3-Flash-Maya-GGUF/", glm.Files[0].Url);
        Assert.EndsWith("/Maya-S24/GLM-5.3-Flash-Maya-S24-IQ2_XXS_S-00002-of-00003.gguf", glm.Files[1].Url);
        Assert.Equal("wam-glm.exe", glm.Tier!.Exe);
        Assert.Equal("tiered", glm.Engine);
        Assert.Equal("glm", glm.Pack!.Tool);

        var mm = C.Model("minimax-m3.1-flash-compact")!;
        Assert.Equal(93.0, mm.TotalSize / 1e9, 1);
        Assert.Equal("wam-mm3.exe", mm.Tier!.Exe);
        Assert.False(mm.Tier.Llama);
        Assert.NotNull(mm.License);
        Assert.Single(mm.Extras!);
    }

    [Fact]
    public void EnginesPerModel()
    {
        Assert.Equal("wam-ds4.exe", C.Model("deepseek-v4.1-flash-compact")!.Tier!.Exe);
        Assert.Equal("deepseek", C.Model("deepseek-v4.1-flash-q4")!.Pack!.Tool);
        Assert.Equal("llama", C.Model("qwen3.6-35b-a3b-q4")!.Engine);
        Assert.Null(C.Model("qwen3.6-35b-a3b-q4")!.Pack);
        // a stamped shard keeps its stamp
        Assert.Equal(new[] { 8, 9 }, C.Model("deepseek-v4.1-flash-q4")!.Files[7].Stamp);
    }
}
