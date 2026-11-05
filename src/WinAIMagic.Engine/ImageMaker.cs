using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

public readonly record struct ImageSize(int Width, int Height)
{
    public double Megapixels => Width * Height / 1e6;
    public string Key => Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);

    public static ImageSize? Parse(string key)
    {
        var parts = key.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int w) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int h)
            ? new ImageSize(w, h) : null;
    }
}

/// <summary>How one image is made on this PC.</summary>
public sealed record ImagePlan
{
    public ImageSize Size { get; init; }
    public bool OnCard { get; init; }
    /// <summary>What the engine may keep on the card, in GiB; null on the processor.</summary>
    public double? VramBudgetGib { get; init; }
    /// <summary>The last stage works in parts: slower, and much less memory.</summary>
    public bool Tiling { get; init; }
    public int Threads { get; init; }
}

/// <summary>A pure function of the PC, as MemoryPlanner is. The engine runs once per image, so all its memory is free again afterwards.
///
/// Measured on the test PC (RTX 5060 Ti 16 GB, i9-10900K), FLUX.2 Klein 4B at 4 steps, with limits that stand in for a smaller PC:
/// - Processor, 4 threads: 512 in 152 s, 768 in 295 s, 1024 in 533 s; own memory 2.2, 4.4 and 7.5 GB, nearly all in the last stage.
///   In parts, 1024 takes 715 s and 1.4 GB. 512 took the same time when it might hold only 2.5 GB in memory, the rest mapped from the files.
/// - Card, 1024: 8 s and 8.1 GB without a budget; 9 s and 3.9 GB with a budget of 5.2 GiB and the last stage in parts, which costs next to
///   nothing on a card. A budget of 3.2 GiB used more, 5.3 GB, so a smaller budget is no smooth trade. A budget of 1.2 GiB ran 768 on
///   3.4 GB and failed cleanly at 1024.</summary>
public static class ImagePlanner
{
    public static readonly IReadOnlyList<ImageSize> Sizes = [new(512, 512), new(768, 768), new(1024, 1024), new(1024, 768), new(768, 1024)];

    /// <summary>What the second engine may use on the card (MemoryPlanner) from where every size runs: an 8 GB card has 5.6 GB of it.</summary>
    public const ulong LargeCard = 5_400_000_000;
    /// <summary>From where the smaller sizes run on the card, with `SmallCardBudgetGib`: a 6 GB card, not measured on a real one.</summary>
    public const ulong SmallCard = 3_400_000_000;
    public const double SmallCardBudgetGib = 1.2;
    public const double SmallCardMegapixels = 0.6;
    /// <summary>Below this much RAM without a usable card the smallest size is the default: the next one has to work in parts there.</summary>
    public const ulong LittleMemory = 12UL << 30;

    /// <summary>The engine stopped first, so what others hold on the card is only the desktop.</summary>
    private static ulong CardLimit(SystemInfo s) => MemoryPlanner.LlamaVramLimit(s with { VramFree = null });

    private static bool FitsOnCard(ImageSize size, SystemInfo s)
    {
        ulong limit = CardLimit(s);
        return limit >= LargeCard || (limit >= SmallCard && size.Megapixels <= SmallCardMegapixels);
    }

    /// <summary>Own memory of the last stage on the processor, a line through the three measurements.</summary>
    public static ulong DecoderBytes(ImageSize size) => 400_000_000 + (ulong)(size.Megapixels * 6_840_000_000);

    public static ImageSize DefaultSize(SystemInfo s)
    {
        var large = new ImageSize(1024, 1024);
        var middle = new ImageSize(768, 768);
        if (FitsOnCard(large, s)) return large;
        if (FitsOnCard(middle, s)) return middle;
        return s.RamTotal < LittleMemory ? new ImageSize(512, 512) : middle;
    }

    public static ImagePlan For(ImageSize size, SystemInfo s)
    {
        if (!FitsOnCard(size, s)) return ForProcessor(size, s);
        ulong limit = CardLimit(s);
        double budget = limit >= LargeCard ? Math.Floor(limit / (double)(1UL << 30) * 10) / 10 : SmallCardBudgetGib;
        return new ImagePlan { Size = size, OnCard = true, VramBudgetGib = budget, Tiling = true, Threads = Math.Max(4, s.PerformanceCores) };
    }

    /// <summary>The last stage may take half the RAM; beyond that it works in parts.</summary>
    public static ImagePlan ForProcessor(ImageSize size, SystemInfo s) =>
        new() { Size = size, OnCard = false, Tiling = DecoderBytes(size) > s.RamTotal / 2, Threads = Math.Max(4, s.PerformanceCores) };

    /// <summary>`files`: the image model, the text model and the decoder, in the catalog's order.</summary>
    public static IReadOnlyList<string> Arguments(ImagePlan plan, IReadOnlyList<string> files, string prompt, long seed, string output)
    {
        string I(long n) => n.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "--diffusion-model", files[0], "--llm", files[1], "--vae", files[2],
            "-p", prompt,
            // what this model was made for: 4 steps, and no second pass for what the image should not show
            "--steps", "4", "--cfg-scale", "1.0", "--sampling-method", "euler",
            "-s", I(seed), "-W", I(plan.Size.Width), "-H", I(plan.Size.Height), "-t", I(plan.Threads),
            "-o", output,
            // the weights stay mapped from the files, so what is not in use can leave memory
            "--mmap",
            // the prompt stays out of an image that is passed on
            "--disable-image-metadata",
        };
        if (plan.Tiling) args.Add("--vae-tiling");
        if (plan.VramBudgetGib is double budget) args.AddRange(new[] { "--max-vram", budget.ToString("0.0", CultureInfo.InvariantCulture) });
        if (!plan.OnCard) args.AddRange(new[] { "--backend", "cpu" });
        return args;
    }
}

/// <summary>The stages of one image, read from the engine's output.</summary>
public sealed class ImageProgress
{
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Bar = new(@"\|[^|]*\|\s*(\d+)/(\d+)\s*-", RegexOptions.Compiled);
    private const int Steps = 4;

    private enum Stage { Reading, Drawing, Finishing }
    private Stage stage = Stage.Reading;
    private int step;

    public string? LastError { get; private set; }

    public static string Clean(string line) => Ansi.Replace(line, "").Trim();

    /// <summary>One line of output; true when what the page shows changed.</summary>
    public bool Read(string raw)
    {
        var line = Clean(raw);
        if (line.StartsWith("[E]", StringComparison.Ordinal))
        {
            // "[E] generate failed --- main.cpp:926": the words, not the place in the source
            int end = line.IndexOf(" --- ", StringComparison.Ordinal);
            LastError = (end > 0 ? line[3..end] : line[3..]).Trim();
            return false;
        }
        if (stage == Stage.Reading && line.Contains("sampling using", StringComparison.Ordinal)) { stage = Stage.Drawing; return true; }
        if (stage == Stage.Drawing)
        {
            if (line.Contains("sampling completed", StringComparison.Ordinal)) { stage = Stage.Finishing; return true; }
            var m = Bar.Match(line);
            // the same kind of bar goes with reading weights, with hundreds of parts
            if (m.Success && m.Groups[2].Value == Steps.ToString(CultureInfo.InvariantCulture) && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var n && n != step)
            {
                step = n;
                return true;
            }
        }
        return false;
    }

    public double Fraction => stage switch
    {
        Stage.Reading => 0.03,
        Stage.Drawing => 0.05 + 0.75 * step / Steps,
        _ => 0.85,
    };

    public string Status => stage switch
    {
        Stage.Reading => L("Tekst lezen…"),
        Stage.Drawing => L($"Tekenen: stap {Math.Min(step + 1, Steps)} van {Steps}"),
        _ => L("Afbeelding afmaken…"),
    };
}

/// <summary>An image that was made; beside the PNG in a file of the same name.</summary>
public sealed record ImageResult
{
    public string Prompt { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public long Seed { get; init; }
    public double Seconds { get; init; }
    public bool OnCard { get; init; }
    public DateTime Created { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string Path { get; init; } = "";
}

/// <summary>Makes one image at a time with the image engine. Events and properties change on background threads: the UI marshals them itself.</summary>
public sealed class ImageMaker
{
    public const string ExeName = "wam-image.exe";

    public event Action? Changed;

    /// <summary>A test copy of the app stays inside its data folder; null: Pictures\WinAIMagic.</summary>
    public string? Root { get; set; }
    /// <summary>Tests: a stand-in for the engine.</summary>
    internal string? ExecutableOverride { get; set; }

    public bool Busy { get; private set; }
    public string Status { get; private set; } = "";
    public double Fraction { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Something the user should know about the image that came out.</summary>
    public string? Note { get; private set; }
    public ImageResult? Last { get; private set; }
    public DateTime Started { get; private set; }

    private readonly object gate = new();
    private ChildProcess? running;
    private bool stopped;

    public static string Binary(IGpuBackend backend) => Path.Combine(Paths.EngineRoot, backend.EngineFolder, ExeName);

    private string Folder => Root ?? Paths.PicturesFolder;

    private void Notify() => Changed?.Invoke();

    /// <summary>Makes the image. `prepare` runs first with this maker already busy, so nothing starts beside it; what it returns is why not to go on.
    /// On return: the image, or null with the reason in `Error` (a stop leaves that empty).</summary>
    public async Task<ImageResult?> Make(string prompt, ImageSize size, SystemInfo pc, IGpuBackend backend, IReadOnlyList<string> files, long? seed = null, Func<Task<string?>>? prepare = null)
    {
        lock (gate)
        {
            if (Busy) return null;
            Busy = true;
            stopped = false;
        }
        Error = Note = null;
        Fraction = 0;
        Status = L("Voorbereiden…");
        Started = DateTime.Now;
        Notify();
        using var awake = SleepGuard.Hold();
        try
        {
            if (prepare != null && await prepare() is { } why)
            {
                Error = why;
                return null;
            }
            var exe = ExecutableOverride ?? Binary(backend);
            if (!File.Exists(exe))
            {
                Error = L("De beeldengine ontbreekt in de app. Installeer WinAIMagic opnieuw.");
                return null;
            }
            long chosen = seed ?? Random.Shared.NextInt64(1, int.MaxValue);
            var clock = Stopwatch.StartNew();
            Directory.CreateDirectory(Folder);
            var output = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{chosen.ToString(CultureInfo.InvariantCulture)}.png");

            var plan = ImagePlanner.For(size, pc);
            var outcome = await Run(exe, plan, files, prompt, chosen, output);
            // a card can refuse in ways no plan foresees: an older model, a driver, other programs on it. The processor always works
            if (outcome.Failed && plan.OnCard && !stopped)
            {
                Note = L("De videokaart lukte niet, dus deze afbeelding is op de processor gemaakt. Dat duurt langer.");
                Fraction = 0;
                Notify();
                plan = ImagePlanner.ForProcessor(size, pc);
                outcome = await Run(exe, plan, files, prompt, chosen, output);
            }
            if (stopped) return null;
            if (outcome.Failed)
            {
                Error = L("De afbeelding is niet gelukt.") + (outcome.Reason is { } r ? " " + LogFilter.Clean(r) : "");
                return null;
            }
            var result = new ImageResult
            {
                Prompt = prompt, Width = size.Width, Height = size.Height, Seed = chosen, Seconds = clock.Elapsed.TotalSeconds,
                OnCard = plan.OnCard, Created = DateTime.Now, Path = output,
            };
            try { File.WriteAllText(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(result)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the image itself is there
            Last = result;
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Error = L("De afbeelding is niet gelukt.") + " " + LogFilter.Clean(e.Message);
            return null;
        }
        finally
        {
            lock (gate) Busy = false;
            Status = "";
            Notify();
        }
    }

    private async Task<(bool Failed, string? Reason)> Run(string exe, ImagePlan plan, IReadOnlyList<string> files, string prompt, long seed, string output)
    {
        var progress = new ImageProgress();
        var tail = new StringBuilder();
        using var log = new StreamWriter(Paths.ImageLog, append: Note != null, new UTF8Encoding(false));
        var args = ImagePlanner.Arguments(plan, files, prompt, seed, output);
        log.WriteLine(LogFilter.Clean("$ " + Path.GetFileName(exe) + " " + string.Join(" ", args)));
        void Consume(byte[] buffer, int n)
        {
            lock (tail)
            {
                tail.Append(Encoding.UTF8.GetString(buffer, 0, n));
                // a progress bar rewrites its line with \r
                for (int at; (at = tail.ToString().IndexOfAny(['\r', '\n'])) >= 0;)
                {
                    var line = tail.ToString(0, at);
                    tail.Remove(0, at + 1);
                    if (ImageProgress.Clean(line).Length == 0) continue;
                    if (!line.Contains("it/s", StringComparison.Ordinal) && !line.Contains("/it", StringComparison.Ordinal) && !line.Contains("B/s", StringComparison.Ordinal))
                        log.WriteLine(LogFilter.Clean(ImageProgress.Clean(line)));
                    if (progress.Read(line))
                    {
                        Fraction = progress.Fraction;
                        Status = progress.Status;
                        Notify();
                    }
                }
            }
        }
        Status = progress.Status;
        Notify();
        var child = ChildProcess.Start(new ChildStart(exe, args) { Cwd = Path.GetDirectoryName(exe), OnOutput = Consume });
        lock (gate) running = child;
        if (stopped) child.KillTree();
        int code = await child.Exited;
        lock (gate) running = null;
        child.Dispose();
        lock (tail) log.Flush();
        bool made = code == 0 && File.Exists(output);
        if (!made && !stopped)
            log.WriteLine($"exit {code}");
        return (!made, progress.LastError);
    }

    /// <summary>Stops the image being made; what the engine had done is gone.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (!Busy) return;
            stopped = true;
            running?.KillTree();
        }
        Status = L("Stoppen…");
        Notify();
    }

    /// <summary>The images in the folder, newest first; nothing is created for this.</summary>
    public IReadOnlyList<ImageResult> History(int most = 30)
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<ImageResult>();
            return new DirectoryInfo(Folder).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(most).Select(Read).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<ImageResult>(); }
    }

    private static ImageResult Read(FileInfo file)
    {
        var side = Path.ChangeExtension(file.FullName, ".json");
        try
        {
            if (File.Exists(side) && JsonSerializer.Deserialize<ImageResult>(File.ReadAllText(side)) is { } r) return r with { Path = file.FullName };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new ImageResult { Path = file.FullName, Created = file.LastWriteTime };
    }

    /// <summary>Forget the image on screen when it is the one removed.</summary>
    public void Forget(string path)
    {
        if (Last?.Path == path) Last = null;
        Notify();
    }
}
