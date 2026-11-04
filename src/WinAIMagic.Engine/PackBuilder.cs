using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>
/// The pack: the main engine's index of a model's files (about 1 GB, the experts stay in the GGUF files). Built on this PC after
/// the download with the bundled Python, because its sources.json holds the files' times. The engine finds the GGUF files at
/// &lt;pack&gt;\.., so the pack lies in the model's folder.
/// </summary>
public static class PackBuilder
{
    public static string PackDir(ModelInfo model) => Path.Combine(Paths.ModelDir(model.Id), "pack");
    public static string PythonExe => Path.Combine(Paths.EngineRoot, "python", "python.exe");
    public static string TieredRoot => Path.Combine(Paths.EngineRoot, "tiered");
    private static string Tools => Path.Combine(TieredRoot, "tools");

    /// <summary>The files a whole pack holds, per tool (maya.py's lists).</summary>
    internal static string[] NeededFiles(string tool) => tool switch
    {
        "deepseek" => new[] { "ds4.txt", "tensors.txt", "engram.bin", "tokenizer/vocab.json", "tokenizer/chat_template.jinja" },
        "minimax" => new[] { "mm3.txt", "tensors.txt", "tokenizer/vocab.json", "tokenizer/chat_template.jinja" },
        _ => new[] { "index.txt", "native_experts.txt", "dense.bin", "tokenizer/vocab.json", "tokenizer/merges.txt", "tokenizer/token_type.json", "tokenizer/chat_template.jinja" },
    };

    /// <summary>Why the pack cannot be used as it is, or null. A file that changed after the pack was built leaves its places wrong;
    /// a pack that points at another file name or folder (a renamed shard, a moved model) too.</summary>
    public static string? WhyNotReady(ModelInfo model)
    {
        if (model.Pack == null) return null;
        var pack = PackDir(model);
        foreach (var f in NeededFiles(model.Pack.Tool))
            if (!File.Exists(Path.Combine(pack, f))) return L($"Het pack ontbreekt nog: {f}");
        var shards = model.Files.Select(f => Path.Combine(Paths.ModelDir(model.Id), f.Local)).ToList();
        if (model.Pack.Tool == "glm")
        {
            if (FirstLine(Path.Combine(pack, "native_experts.txt")) is { } first
                && Regex.Match(first, @"absolute offsets in ([^,)]+)") is { Success: true } m
                && m.Groups[1].Value != Path.GetFileName(shards[0]))
                return L("Het pack hoort bij een andere bestandsnaam.");
        }
        else
        {
            var geometry = Path.Combine(pack, model.Pack.Tool == "deepseek" ? "ds4.txt" : "mm3.txt");
            var dirLine = File.ReadLines(geometry).FirstOrDefault(l => l.StartsWith("model_dir "));
            if (dirLine != null && !SamePath(dirLine["model_dir ".Length..].Trim(), Paths.ModelDir(model.Id)))
                return L("Het pack hoort bij een andere map.");
            if (TowerFolder(model) != null && !PackHasTower(model) && !TowerRefused(model))
                return L("Het pack krijgt nog de afbeeldingsbestanden.");
        }
        return WhyStale(pack, shards);
    }

    /// <summary>Set in the pack when the tower could not go in, so that the model starts text only and the build does not repeat at every start.</summary>
    private const string NoTowerMark = "no-vision-tower";

    /// <summary>DeepSeek and MiniMax: the folder of their vision tower once all its files are here, else null.</summary>
    internal static string? TowerFolder(ModelInfo model)
    {
        if (model.Pack?.Tool is not ("deepseek" or "minimax") || ModelCatalog.Shared.TieredVisionFor(model) is not { } v) return null;
        return v.Files.All(f => ModelStore.FileSize(Path.Combine(v.Folder, f.Local)) == f.Size) ? v.Folder : null;
    }

    internal static bool TowerRefused(ModelInfo model) => File.Exists(Path.Combine(PackDir(model), NoTowerMark));

    /// <summary>The tower's geometry in the pack, the lines the setup looks for too.</summary>
    internal static bool PackHasTower(ModelInfo model)
    {
        var (file, key) = model.Pack?.Tool == "deepseek" ? ("ds4.txt", "vision.num_hidden_layers ") : ("mm3.txt", "vision.hidden_size");
        try { return File.ReadLines(Path.Combine(PackDir(model), file)).Any(l => l.StartsWith(key, StringComparison.Ordinal)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string? FirstLine(string path)
    {
        try { return File.ReadLines(path).FirstOrDefault(); }
        catch (IOException) { return null; }
    }

    internal static string? WhyStale(string pack, IReadOnlyList<string> shards)
    {
        var file = Path.Combine(pack, "sources.json");
        if (!File.Exists(file)) return null;   // from before the record: as upstream, no change counts
        try
        {
            var list = JsonNode.Parse(File.ReadAllText(file))?["files"]?.AsArray();
            if (list == null) return null;
            var old = list.Where(n => n != null).ToDictionary(n => n!["name"]!.GetValue<string>(), n => n!);
            foreach (var s in shards)
            {
                var name = Path.GetFileName(s);
                if (!old.TryGetValue(name, out var r)) return L($"{name} veranderde nadat het pack is gebouwd.");
                if (r["size"]?.GetValue<long>() != new FileInfo(s).Length || r["mtime_ns"]?.GetValue<long>() != MtimeNs(s))
                    return L($"{name} veranderde nadat het pack is gebouwd.");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or FormatException) { return null; }
        return null;
    }

    /// <summary>Python's st_mtime_ns.</summary>
    internal static long MtimeNs(string path) =>
        (File.GetLastWriteTimeUtc(path).Ticks - DateTime.UnixEpoch.Ticks) * 100;

    /// <summary>The model files' sizes and times into the pack (sources.json), as upstream writes them.</summary>
    internal static void WriteSources(string pack, IReadOnlyList<string> shards)
    {
        var files = shards.Select(s => new { name = Path.GetFileName(s), size = new FileInfo(s).Length, mtime_ns = MtimeNs(s) });
        File.WriteAllText(Path.Combine(pack, "sources.json"), JsonSerializer.Serialize(new { files }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The command per tool: the Python, its arguments and its environment.</summary>
    public static ChildStart Command(ModelInfo model, Action<byte[], int>? onOutput = null, bool textOnly = false)
    {
        var dir = Paths.ModelDir(model.Id);
        var pack = PackDir(model);
        var first = Path.Combine(dir, model.Files[0].Local);
        var names = model.Files.Select(f => f.Local);
        var tower = textOnly ? null : TowerFolder(model);
        IEnumerable<string> args = model.Pack?.Tool switch
        {
            "deepseek" => new[] { Path.Combine(Tools, "ds4", "pack_gguf.py"), "--model", dir, "--out", pack }
                .Concat(tower != null && File.Exists(Path.Combine(tower, "vision.json")) ? new[] { "--vision", tower } : Array.Empty<string>())
                .Append("--shards").Concat(names),
            "minimax" => new[] { Path.Combine(Tools, "mm3", "pack_gguf.py"), "--model", dir, "--out", pack }
                .Concat(tower != null ? new[] { "--companions", tower } : new[] { "--text-only" })
                .Concat(new[] { "--indexers", Path.Combine(dir, model.Extras![0].Local), "--shards" }).Concat(names),
            _ => new[] { Path.Combine(Tools, "iq_pack.py"), "--gguf", first, "--out", pack, "--compat-bf16" },
        };
        return Python(args.ToList(), onOutput);
    }

    /// <summary>DeepSeek's tower as the one file its pack points at (vision.bin, vision.json), made from the downloaded mmproj.</summary>
    internal static ChildStart TowerCommand(string tower, Action<byte[], int>? onOutput = null) =>
        Python(new[] { Path.Combine(Tools, "ds4", "vision_pack.py"), "--dir", tower, "--mmproj", Path.Combine(tower, "mmproj-BF16.gguf") }, onOutput);

    private static ChildStart Python(IReadOnlyList<string> args, Action<byte[], int>? onOutput) => new(PythonExe, args)
    {
        Cwd = TieredRoot,
        Env = new Dictionary<string, string?>
        {
            ["STRATA_GGUF_PY"] = Path.Combine(Tools, "gguf-py"),
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
            ["PYTHONDONTWRITEBYTECODE"] = "1",
        },
        OnOutput = onOutput,
    };

    /// <summary>Builds the pack; on return null, or what went wrong. `progress`: how far (null: no figure) and the last line.</summary>
    public static async Task<string?> Build(ModelInfo model, Action<double?, string> progress, CancellationToken ct)
    {
        if (model.Pack == null) return null;
        if (!File.Exists(PythonExe)) return L("De engine is niet compleet: Python ontbreekt. Installeer WinAIMagic opnieuw.");
        var pack = PackDir(model);
        Directory.CreateDirectory(pack);
        int layers = 0;
        try
        {
            var index = GGUFIndex.Load(new[] { Path.Combine(Paths.ModelDir(model.Id), model.Files[0].Local) }, new[] { model.Files[0].Size });
            layers = index.LayerCount;
        }
        catch (Exception e) when (e is GGUFException or IOException) { }

        var parser = new PackProgress(layers);
        var lines = new LineSplitter();
        string last = "";
        void Output(byte[] b, int n)
        {
            foreach (var line in lines.Add(b, n))
            {
                last = LogFilter.Clean(line);
                progress(parser.Consume(line), last);
            }
        }
        async Task<int> Run(ChildStart command)
        {
            using var child = ChildProcess.Start(command);
            using var reg = ct.Register(child.KillTree);
            int exit = await child.Exited;
            ct.ThrowIfCancellationRequested();
            return exit;
        }
        progress(0, L($"Pack bouwen: {model.Name}"));
        File.Delete(Path.Combine(pack, NoTowerMark));
        // (a tower that cannot be made leaves the pack text only: images are extra, the model still starts)
        if (model.Pack.Tool == "deepseek" && TowerFolder(model) is { } tower && !File.Exists(Path.Combine(tower, "vision.json")))
            await Run(TowerCommand(tower, Output));
        int code = await Run(Command(model, Output));
        if (code != 0 && TowerFolder(model) != null) code = await Run(Command(model, Output, textOnly: true));
        if (code != 0) return L($"Het pack kon niet worden gebouwd (code {code}): {last}");
        if (TowerFolder(model) != null && !PackHasTower(model)) File.WriteAllText(Path.Combine(pack, NoTowerMark), last);
        foreach (var f in NeededFiles(model.Pack.Tool))
            if (!File.Exists(Path.Combine(pack, f)) && f != "tokenizer/chat_template.jinja")
                return L($"Het pack is niet compleet: {f} ontbreekt. {last}");
        // a GLM pack whose GGUF carried no chat template gets GLM-5's, as upstream's setup does
        if (model.Pack.Tool == "glm")
        {
            var template = Path.Combine(pack, "tokenizer", "chat_template.jinja");
            var base_ = Path.Combine(TieredRoot, "serve", "glm5_chat_template.jinja");
            if (!File.Exists(template) && File.Exists(base_)) File.Copy(base_, template);
        }
        WriteSources(pack, model.Files.Select(f => Path.Combine(Paths.ModelDir(model.Id), f.Local)).ToList());
        return null;
    }
}

/// <summary>iq_pack prints "layer N ..." per layer of experts; the other tools print no figures.</summary>
internal sealed class PackProgress
{
    private readonly int layers;
    private double? fraction = 0;

    public PackProgress(int layers) { this.layers = layers; }

    public double? Consume(string line)
    {
        var m = Regex.Match(line, @"^\s*layer\s+(\d+)\b");
        if (m.Success && layers > 0 && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            fraction = Math.Min(0.99, (n + 1) / (double)layers);
        return fraction;
    }
}

/// <summary>Cuts a stream of bytes into lines; a line cut in two by a chunk comes out whole.</summary>
internal sealed class LineSplitter
{
    private readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder pending = new();

    public List<string> Add(byte[] bytes, int count)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        int n = decoder.GetChars(bytes, 0, count, chars, 0);
        var lines = new List<string>();
        for (int i = 0; i < n; i++)
        {
            char c = chars[i];
            if (c == '\n')
            {
                if (pending.Length > 0 && pending[^1] == '\r') pending.Length--;
                lines.Add(pending.ToString());
                pending.Clear();
            }
            else pending.Append(c);
        }
        return lines;
    }
}

/// <summary>The name of the upstream project never reaches the user: not on screen and not in a log file (brief 2.2, section 11).</summary>
public static partial class LogFilter
{
    [GeneratedRegex("maya", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    public static string Clean(string text) => Name().Replace(text, "engine");
}
