using System.Text.Json;
using System.Text.Json.Serialization;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Engine;

/// <summary>A file of a model. `Url` keeps the name upstream gave it; on disk and on screen it is `Local`.</summary>
public sealed record ModelFile
{
    [JsonPropertyName("local")] public required string Local { get; init; }
    [JsonPropertyName("size")] public required ulong Size { get; init; }
    /// <summary>Every published version counts (upstream re-headered some first shards: the same data, another hash).</summary>
    [JsonPropertyName("sha256"), JsonConverter(typeof(OneOrManyConverter))] public required IReadOnlyList<string> Sha256 { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    /// <summary>[no, count]: a shard published on its own, whose header gets its place in the set after download.</summary>
    [JsonPropertyName("stamp")] public IReadOnlyList<int>? Stamp { get; init; }
}

/// <summary>The index the main engine keeps next to a model: which tool builds it.</summary>
public sealed record PackInfo([property: JsonPropertyName("tool")] string Tool);

/// <summary>The main engine's own data for a model.</summary>
public sealed record TierInfo
{
    [JsonPropertyName("exe")] public required string Exe { get; init; }
    [JsonPropertyName("sampling")] public Dictionary<string, double> Sampling { get; init; } = new();
    /// <summary>The second engine can run it too (the limited mode). False: only the main engine.</summary>
    [JsonPropertyName("llama")] public bool Llama { get; init; } = true;
    /// <summary>Only recommended for cards up to this size: on larger ones the other models make better use of the card.</summary>
    [JsonPropertyName("recommendMaxVramGb")] public double? RecommendMaxVramGb { get; init; }
}

public sealed record ModelInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
    [JsonPropertyName("vision")] public string? Vision { get; init; }
    [JsonPropertyName("files")] public required IReadOnlyList<ModelFile> Files { get; init; }
    /// <summary>Multi-token prediction head for speculative decoding: in a separate file (Qwen) or inside the model (GLM).</summary>
    [JsonPropertyName("draft")] public ModelFile? Draft { get; init; }
    /// <summary>Other files the engine reads beside the shards (MiniMax's indexers).</summary>
    [JsonPropertyName("extras")] public IReadOnlyList<ModelFile>? Extras { get; init; }
    [JsonPropertyName("mtp")] public string? Mtp { get; init; }
    /// <summary>Measured from the GGUF headers: what stays in memory while it runs (dense weights + experts, not DeepSeek's
    /// Engram tables, which come from the SSD), and the dense part of that, which always goes on the GPU.</summary>
    [JsonPropertyName("memory")] public ulong? Memory { get; init; }
    [JsonPropertyName("dense")] public ulong? Dense { get; init; }
    /// <summary>"tiered" or "llama".</summary>
    [JsonPropertyName("engine")] public string Engine { get; init; } = "llama";
    [JsonPropertyName("pack")] public PackInfo? Pack { get; init; }
    [JsonPropertyName("tier")] public TierInfo? Tier { get; init; }
    /// <summary>A licence of its own, to show before the download; null: an open licence that asks nothing first (MIT, Apache 2.0).</summary>
    [JsonPropertyName("license")] public string? License { get; init; }
    /// <summary>Only recommended for cards up to this size, as under `tier`: for a model on the second engine, which has no tier data.</summary>
    [JsonPropertyName("recommendMaxVramGb")] public double? RecommendMaxVramGb { get; init; }
    /// <summary>False: a variant of another model (Qwen3.6 Heretic beside Qwen3.6), never the recommendation, even when it is smaller.</summary>
    [JsonPropertyName("recommend")] public bool Recommend { get; init; } = true;
    /// <summary>True: a small model, recommended only on a PC with little memory, as a model without experts always is. Elsewhere a
    /// larger model that reads a little from the SSD does more (Ling beside Qwen3.6).</summary>
    [JsonPropertyName("smallPc")] public bool SmallPc { get; init; }

    /// <summary>No routed experts: every token reads all the weights, so nothing can stay behind on the SSD for long.</summary>
    [JsonIgnore] public bool IsPlain => Dense != null && Dense == Memory;
    [JsonIgnore] public bool IsForSmallPc => IsPlain || SmallPc;
    [JsonIgnore] public ulong TotalSize => Files.Aggregate(0UL, (n, f) => n + f.Size);
    [JsonIgnore] public bool HasMtp => Mtp is "builtin" or "file";
    [JsonIgnore] public string ShortName => Name.Split(' ')[0];
    [JsonIgnore] public IEnumerable<ModelFile> AllFiles => Files.Concat(Extras ?? Array.Empty<ModelFile>()).Concat(Draft == null ? Array.Empty<ModelFile>() : new[] { Draft });
}

public sealed record VisionInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
    [JsonPropertyName("size")] public required ulong Size { get; init; }
    [JsonPropertyName("sha256"), JsonConverter(typeof(OneOrManyConverter))] public required IReadOnlyList<string> Sha256 { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
}

/// <summary>What images need on the main engine, in a folder of their own under Paths.VisionDir; not the second engine's file, whose
/// projector the encoder cannot read. GLM: the encoder's mmproj and then its vocabulary. DeepSeek and MiniMax: the tower their pack indexes.</summary>
public sealed record TieredVisionInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("files")] public required IReadOnlyList<ModelFile> Files { get; init; }
    [JsonIgnore] public string Folder => Path.Combine(Paths.VisionDir, Id);
    [JsonIgnore] public ulong TotalSize => Files.Aggregate(0UL, (n, f) => n + f.Size);
}

/// <summary>The models WinAIMagic knows, read from the embedded models.json. The order is the order of recommendation.</summary>
public sealed record ModelCatalog
{
    [JsonPropertyName("models")] public IReadOnlyList<ModelInfo> Models { get; init; } = Array.Empty<ModelInfo>();
    [JsonPropertyName("vision")] public IReadOnlyList<VisionInfo> Vision { get; init; } = Array.Empty<VisionInfo>();
    [JsonPropertyName("tieredVision")] public IReadOnlyList<TieredVisionInfo> TieredVision { get; init; } = Array.Empty<TieredVisionInfo>();
    /// <summary>The models that make images, on a page of their own: their files in the order the image engine takes them (the image model, the
    /// text model, the decoder). `Models` is what can chat.</summary>
    [JsonPropertyName("images")] public IReadOnlyList<ModelInfo> Images { get; init; } = Array.Empty<ModelInfo>();
    /// <summary>The speech model: speaking a question and Telegram voice messages, with the speech engine. Not a model to chat with or compare.</summary>
    [JsonPropertyName("speech")] public IReadOnlyList<ModelInfo> Speech { get; init; } = Array.Empty<ModelInfo>();
    [JsonIgnore] public IEnumerable<ModelInfo> All => Models.Concat(Images);
    /// <summary>Everything the model folder can hold: what the store downloads, checks and deletes.</summary>
    [JsonIgnore] public IEnumerable<ModelInfo> Stored => All.Concat(Speech);

    public static ModelCatalog Shared { get; } = Load();

    private static ModelCatalog Load()
    {
        using var s = typeof(Paths).Assembly.GetManifestResourceStream("Resources.models.json");
        return s == null ? new ModelCatalog() : JsonSerializer.Deserialize<ModelCatalog>(s) ?? new ModelCatalog();
    }

    public ModelInfo? Model(string id) => All.FirstOrDefault(m => m.Id == id);
    /// <summary>Also the speech model.</summary>
    public ModelInfo? Find(string id) => Stored.FirstOrDefault(m => m.Id == id);
    public VisionInfo? VisionFor(ModelInfo model) => Vision.FirstOrDefault(v => v.Id == model.Vision);
    public TieredVisionInfo? TieredVisionFor(ModelInfo model) => TieredVision.FirstOrDefault(v => v.Id == model.Vision);
}

internal sealed class OneOrManyConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new[] { r.GetString()! };
        return JsonSerializer.Deserialize<List<string>>(ref r, o) ?? new List<string>();
    }

    public override void Write(Utf8JsonWriter w, IReadOnlyList<string> v, JsonSerializerOptions o) =>
        JsonSerializer.Serialize(w, v, o);
}
