using System.Net;
using System.Net.Sockets;
using System.Text;
using WinAIMagic.Core.Support;
using WinAIMagic.Engine;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinAIMagic.Engine.Tests;

/// <summary>Every test gets an app data folder and settings of its own.</summary>
public abstract class EngineTest : IDisposable
{
    protected readonly string Root;

    protected EngineTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "wam-tests", Guid.NewGuid().ToString("N"));
        Paths.AppData = Root;
        Settings.Shared = Settings.Load();
        Settings.Shared.Language = "nl";
    }

    public virtual void Dispose()
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    public static string FakeExe => Path.Combine(AppContext.BaseDirectory, "FakeEngine.exe");

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static bool WaitUntil(Func<bool> condition, double seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "WinAIMagic.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo not found");
        }
    }
}

/// <summary>Writes small GGUF files the way the converter does, and indexes that describe models without files.</summary>
public static class TestModels
{
    public static byte[] WriteGguf(IEnumerable<(string Key, uint Type, object Value)> kv, IReadOnlyList<(string Name, ulong Offset)> tensors, int padding = 32)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        var pairs = kv.ToList();
        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(3u);
        w.Write((ulong)tensors.Count);
        w.Write((ulong)pairs.Count);
        foreach (var (key, type, value) in pairs)
        {
            Str(key);
            w.Write(type);
            switch (value)
            {
                case uint u: w.Write(u); break;
                case ulong u: w.Write(u); break;
                case ushort u: w.Write(u); break;
                case string s: Str(s); break;
                case string[] many:
                    w.Write(8u);
                    w.Write((ulong)many.Length);
                    foreach (var s in many) Str(s);
                    break;
                default: throw new ArgumentException(value.GetType().Name);
            }
        }
        foreach (var (name, offset) in tensors)
        {
            Str(name);
            w.Write(1u);
            w.Write(1UL);
            w.Write(0u);
            w.Write(offset);
        }
        w.Flush();
        while (ms.Length % padding != 0) w.Write((byte)0);
        return ms.ToArray();
    }

    /// <summary>A tiny real model file: one dense tensor, and experts in `layers` layers.</summary>
    public static byte[] TinyModel(string arch = "qwen35moe", int layers = 2)
    {
        var tensors = new List<(string, ulong)> { ("token_embd.weight", 0) };
        for (int il = 0; il < layers; il++) tensors.Add(($"blk.{il}.ffn_up_exps.weight", (ulong)(32 * (il + 1))));
        var header = WriteGguf(new (string, uint, object)[]
        {
            ("general.architecture", 8, arch),
            (arch + ".block_count", 4, (uint)layers),
            (arch + ".context_length", 4, 262144u),
        }, tensors);
        return header.Concat(new byte[32 * (layers + 1)]).ToArray();
    }

    /// <summary>An index as the headers of a model of this size would give it, without any file.</summary>
    public static GGUFIndex Index(string arch, int layers, ulong dense, ulong expertsPerLayer, int firstExpertLayer = 0, ulong engram = 0, int contextLength = 0)
    {
        var idx = new GGUFIndex();
        idx.Metadata["general.architecture"] = arch;
        idx.Metadata[arch + ".block_count"] = (uint)layers;
        if (contextLength > 0) idx.Metadata[arch + ".context_length"] = (uint)contextLength;
        idx.Tensors.Add(new GGUFIndex.Tensor("token_embd.weight", dense));
        for (int il = firstExpertLayer; il < layers; il++)
            idx.Tensors.Add(new GGUFIndex.Tensor($"blk.{il}.ffn_up_exps.weight", expertsPerLayer));
        if (engram > 0) idx.Tensors.Add(new GGUFIndex.Tensor("blk.1.engram_embd.weight", engram));
        return idx;
    }

    /// <summary>The catalog's model as an index: layers per family, from its measured memory and dense weights.</summary>
    public static GGUFIndex IndexOf(ModelInfo m)
    {
        ulong memory = m.Memory ?? m.TotalSize;
        ulong dense = m.Dense ?? memory / 10;
        return m.Family switch
        {
            "deepseek" => Index("deepseek41", 40, dense, (memory - dense) / 40, engram: m.TotalSize - memory, contextLength: 1 << 20),
            "glm" => Index("glm5-next", 46, dense, (memory - dense) / 43, firstExpertLayer: 3, contextLength: 1 << 20),
            "minimax" => Index("minimax-m3", 60, dense, (memory - dense) / 57, firstExpertLayer: 3, contextLength: 1 << 20),
            "mistral" => Index("mistral3", 34, dense, 0, contextLength: 262144),
            "ling" => Index("bailingmoe3", 24, dense, (memory - dense) / 23, firstExpertLayer: 1, contextLength: 131072),
            _ => Index(m.Id.StartsWith("qwen3.8") ? "qwen4exp" : "qwen35moe", 40, dense, (memory - dense) / 40, contextLength: 262144),
        };
    }

    public static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
}
