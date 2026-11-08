using System.Text;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>
/// Reads the tensor tables of a split GGUF (headers only) and groups the bytes the way the memory plan needs them:
/// dense weights, routed experts per layer, and the Engram tables that stay on the SSD.
/// </summary>
public sealed class GGUFIndex
{
    public readonly record struct Tensor(string Name, ulong Bytes);

    public List<Tensor> Tensors { get; init; } = new();
    public Dictionary<string, object> Metadata { get; init; } = new();

    public Dictionary<int, ulong> ExpertBytesPerLayer
    {
        get
        {
            var o = new Dictionary<int, ulong>();
            foreach (var t in Tensors)
                if (t.Name.Contains("_exps.") && Layer(t.Name) is int il)
                    o[il] = o.GetValueOrDefault(il) + t.Bytes;
            return o;
        }
    }

    private static bool IsEngram(string name) => name.Contains("engram_embd") || name.StartsWith("per_layer_token_embd");

    public ulong EngramBytes => Tensors.Where(t => IsEngram(t.Name)).Aggregate(0UL, (n, t) => n + t.Bytes);
    public ulong DenseBytes => Tensors.Where(t => !t.Name.Contains("_exps.") && !IsEngram(t.Name)).Aggregate(0UL, (n, t) => n + t.Bytes);
    public ulong ExpertBytes => ExpertBytesPerLayer.Values.Aggregate(0UL, (n, b) => n + b);

    /// <summary>&lt;arch&gt;.block_count, whatever the architecture is called.</summary>
    public int LayerCount
    {
        get
        {
            foreach (var (k, v) in Metadata)
                if (k.EndsWith(".block_count") && AsInt(v) is int n) return n;
            var per = ExpertBytesPerLayer;
            return (per.Count == 0 ? -1 : per.Keys.Max()) + 1;
        }
    }

    public string Architecture => Metadata.TryGetValue("general.architecture", out var v) && v is string s ? s : "";

    /// <summary>&lt;arch&gt;.context_length: the longest context the model was made for (0 = unknown).</summary>
    public int ContextLength
    {
        get
        {
            foreach (var (k, v) in Metadata)
                if (k.EndsWith(".context_length") && !k.Contains("original") && AsInt(v) is int n) return n;
            return 0;
        }
    }

    /// <summary>KV cache per token of context: measured for DeepSeek (compressed attention) and GLM, from the heads for Qwen.
    /// qwen35moe: from the measurement on the 5060 Ti (14.0 GB in all at 64K with 20 layers of experts on the card).
    /// mistral3: 34 layers without compressed attention, measured on the 5060 Ti (8K, 16K and 32K use 7050, 8160 and 10370 MiB).
    /// bailingmoe3 (Ling 3.0): 18 of its 24 layers have linear attention with a fixed state, the other 6 compressed attention; measured on
    /// the 5060 Ti (8K, 16K, 32K and 64K use 6829, 6899, 7039 and 7319 MiB in 16 bits: 70 MiB per 8K).</summary>
    public ulong KvBytesPerToken => Architecture switch
    {
        "deepseek41" or "deepseek4" => 5_600,
        "glm5-next" => 20_000,
        "qwen4exp" => 36_000,
        "qwen35moe" => 16_000,
        "mistral3" => 142_000,
        "bailingmoe3" => 9_000,
        _ => 32_000,
    };

    /// <summary>Main engine: the positions of the KV cache in VRAM (a window of them where it streams), upstream's figures.</summary>
    public ulong TieredKvVram(int context) => Architecture switch
    {
        "deepseek41" or "deepseek4" => 5_600UL * (ulong)context,
        "glm5-next" => 20_000UL * (ulong)Math.Min(context, 32_768),
        _ => 61_000UL * (ulong)Math.Min(context, 32_768),
    };

    /// <summary>Main engine: the KV cache in pinned RAM behind that window (GLM 12 KB per token, MiniMax 15 GB at 128K).</summary>
    public ulong TieredKvRam(int context) => context <= 32_768 ? 0 : Architecture switch
    {
        "deepseek41" or "deepseek4" => 0,
        "glm5-next" => 12_000UL * (ulong)context,
        _ => 117_000UL * (ulong)context,
    };

    private static int? AsInt(object v) => v switch
    {
        uint n => (int)n,
        ulong n => (int)n,
        int n => n,
        long n => (int)n,
        _ => null,
    };

    public static int? Layer(string name)
    {
        var parts = name.Split('.');
        return parts.Length > 2 && parts[0] == "blk" && int.TryParse(parts[1], out var n) ? n : null;
    }

    /// <summary>Index every shard that is on disk; a shard still downloading is read as far as its header goes.
    /// `sizes` are the shards' full sizes, so a partly downloaded shard still gives its last tensor's true size.</summary>
    public static GGUFIndex Load(IReadOnlyList<string> shards, IReadOnlyList<ulong> sizes)
    {
        var index = new GGUFIndex();
        for (int i = 0; i < shards.Count && i < sizes.Count; i++)
        {
            var shard = ReadShard(shards[i], sizes[i]);
            index.Tensors.AddRange(shard.Tensors);
            foreach (var (k, v) in shard.Metadata) index.Metadata.TryAdd(k, v);
        }
        return index;
    }

    private static GGUFIndex ReadShard(string path, ulong declared)
    {
        // FileShare: a shard still being written by the download
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4 << 20);
        var r = new Reader(fs);
        if (!r.Bytes(4).SequenceEqual("GGUF"u8.ToArray())) throw new GGUFException(L($"{Path.GetFileName(path)} is geen GGUF-bestand"));
        r.U32();
        ulong nTensors = r.U64(), nKV = r.U64();
        var meta = new Dictionary<string, object>();
        for (ulong i = 0; i < nKV; i++)
        {
            var key = r.Str();
            meta[key] = r.Value(r.U32());
        }
        var infos = new List<(string Name, ulong Offset)>((int)nTensors);
        for (ulong i = 0; i < nTensors; i++)
        {
            var name = r.Str();
            uint nd = r.U32();
            for (uint d = 0; d < nd; d++) r.U64();
            r.U32();    // type
            infos.Add((name, r.U64()));
        }
        // tensor sizes from consecutive offsets: no per-type block table to keep in sync with ggml
        ulong align = meta.TryGetValue("general.alignment", out var a) && a is uint au ? au : 32;
        ulong dataStart = (r.Offset + align - 1) / align * align;
        infos.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        var o = new GGUFIndex { Metadata = meta };
        for (int i = 0; i < infos.Count; i++)
        {
            ulong end = i + 1 < infos.Count ? infos[i + 1].Offset : declared > dataStart ? declared - dataStart : infos[i].Offset;
            o.Tensors.Add(new Tensor(infos[i].Name, end - infos[i].Offset));
        }
        return o;
    }

    private sealed class Reader
    {
        private readonly FileStream fs;
        private readonly byte[] small = new byte[8];
        public ulong Offset { get; private set; }

        public Reader(FileStream fs) { this.fs = fs; }

        private void Fill(Span<byte> into)
        {
            int got = 0;
            while (got < into.Length)
            {
                int n = fs.Read(into[got..]);
                if (n <= 0) throw new GGUFException("GGUF-header is onvolledig");
                got += n;
            }
            Offset += (ulong)into.Length;
        }

        public byte[] Bytes(int n) { var b = new byte[n]; Fill(b); return b; }
        private Span<byte> Small(int n) { var s = small.AsSpan(0, n); Fill(s); return s; }
        public uint U32() => BitConverter.ToUInt32(Small(4));
        public ulong U64() => BitConverter.ToUInt64(Small(8));
        public string Str() => Encoding.UTF8.GetString(Bytes((int)U64()));

        public object Value(uint type)
        {
            switch (type)
            {
                case 0: case 1: case 7: return Small(1)[0];
                case 2: case 3: return BitConverter.ToUInt16(Small(2));
                case 4: return U32();
                case 5: return BitConverter.ToInt32(Small(4));
                case 6: return BitConverter.ToSingle(Small(4));
                case 8: return Str();
                case 9:
                    uint et = U32();
                    ulong n = U64();
                    // big arrays (vocab, token maps) are skipped, not kept
                    if (n > 4096)
                    {
                        for (ulong i = 0; i < n; i++) Value(et);
                        return n;
                    }
                    var list = new List<object>((int)n);
                    for (ulong i = 0; i < n; i++) list.Add(Value(et));
                    return list;
                case 10: return U64();
                case 11: return BitConverter.ToInt64(Small(8));
                case 12: return BitConverter.ToDouble(Small(8));
                default: throw new GGUFException("GGUF-header is onvolledig");
            }
        }
    }
}

public sealed class GGUFException : Exception
{
    public GGUFException(string message) : base(message) { }
}
