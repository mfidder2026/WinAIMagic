using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Concentus;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Engine;

/// <summary>Sound as the speech engine takes it: 16 kHz, one channel, 16 bit.</summary>
public static class Audio
{
    public const int Rate = 16000;

    /// <summary>A WAV file of 16 kHz mono samples.</summary>
    public static void WriteWav(string path, ReadOnlySpan<short> samples)
    {
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write(s);
    }

    /// <summary>The loudness of a piece of sound, 0 to 1 (root mean square).</summary>
    public static double Level(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length) / 32768.0;
    }

    /// <summary>The packets of an Ogg stream (RFC 3533), in order; a packet may run over pages. The checksums are not checked:
    /// a damaged page gives a packet the decoder refuses.</summary>
    public static IEnumerable<byte[]> OggPackets(byte[] data)
    {
        var packet = new List<byte>();
        int at = 0;
        while (at + 27 <= data.Length)
        {
            if (data[at] != 'O' || data[at + 1] != 'g' || data[at + 2] != 'g' || data[at + 3] != 'S')
                throw new InvalidDataException(L("Dit is geen Ogg-bestand."));
            int segments = data[at + 26];
            int body = at + 27 + segments;
            if (body > data.Length) yield break;
            int pos = body;
            for (int i = 0; i < segments; i++)
            {
                int len = data[at + 27 + i];
                if (pos + len > data.Length) yield break;
                packet.AddRange(new ArraySegment<byte>(data, pos, len));
                pos += len;
                // a segment shorter than 255 ends the packet
                if (len < 255)
                {
                    yield return packet.ToArray();
                    packet.Clear();
                }
            }
            at = pos;
        }
    }

    /// <summary>An Ogg Opus file (a Telegram voice message) as 16 kHz mono samples. Opus decodes to 16 kHz itself, and a stereo
    /// stream to one channel.</summary>
    public static short[] ReadOggOpus(byte[] data)
    {
        var packets = OggPackets(data).ToList();
        if (packets.Count == 0 || packets[0].Length < 19 || Encoding.ASCII.GetString(packets[0], 0, 8) != "OpusHead")
            throw new InvalidDataException(L("Dit is geen Opus-geluid."));
        // what the encoder put before the sound, counted at 48 kHz
        int preSkip = BitConverter.ToUInt16(packets[0], 10) / 3;
        var decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
        var frame = new short[Rate * 120 / 1000];
        var output = new List<short>();
        // the second packet holds the tags
        foreach (var p in packets.Skip(2))
        {
            if (p.Length == 0) continue;
            int n = decoder.Decode(p, frame, frame.Length);
            output.AddRange(new ArraySegment<short>(frame, 0, n));
        }
        return output.Skip(Math.Min(preSkip, output.Count)).ToArray();
    }
}

/// <summary>When a spoken question is done: a stretch of sound loud enough to be speech, and then two seconds without.
/// The floor follows the room: it falls at once to a quieter piece and rises slowly with what is not speech, so a fan or a hum does not count as speaking.</summary>
public sealed class SpeechPause
{
    /// <summary>Speech is this many times as loud as the room, and never quieter than `Least`.</summary>
    public const double Above = 4;
    public const double Least = 0.01;
    public static readonly TimeSpan Needed = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    private double floor = double.NaN;
    private TimeSpan spoken, quiet;

    public bool HeardSpeech => spoken >= Needed;

    /// <summary>One piece of sound; true when the question is done.</summary>
    public bool Add(double level, TimeSpan length)
    {
        if (double.IsNaN(floor)) floor = level;
        bool speaking = level >= Math.Max(Least, floor * Above);
        if (speaking)
        {
            spoken += length;
            quiet = TimeSpan.Zero;
        }
        else
        {
            // only the quiet moves the floor, so speaking for long does not become the room
            floor = level < floor ? level : floor + (level - floor) * 0.05;
            quiet += length;
        }
        return HeardSpeech && quiet >= Pause;
    }
}

/// <summary>How the speech engine runs on this PC. A pure function of the PC, as ImagePlanner is.
/// Not measured yet on a card beside a loaded model: the card is used only when this much of it is free at that moment.</summary>
public static class SpeechPlanner
{
    /// <summary>What the speech model takes on a card while it works: its weights and the engine's buffers, with room to spare.</summary>
    public const ulong OnCardNeeds = 1_600_000_000;

    public static bool OnCard(SystemInfo s) => s.HasGpu && !s.UnifiedMemory && (s.VramFree ?? s.VramTotal) >= OnCardNeeds + s.VramReserve;

    /// <summary>The model always listens to 30 seconds (1500 steps of 20 ms), also to a short question. On the processor that took 13 s for
    /// a question of 6 s on the test PC (i9-10900K, 10 threads); with the window cut to the recording and 2 s to spare, 4 s, with the same
    /// text. On a card the full window costs little, so it stays there.</summary>
    public static int? Window(double seconds, bool onCard)
    {
        if (onCard || seconds >= 28) return null;
        return (int)Math.Ceiling((seconds + 2) * 50 / 64) * 64;
    }

    /// <summary>`language`: a two-letter code; "auto" lets the model hear which language it is.</summary>
    public static IReadOnlyList<string> Arguments(string model, string wav, string output, string language, bool onCard, int threads, double seconds = 30)
    {
        var args = new List<string>
        {
            "-m", model, "-f", wav, "-l", language,
            "-t", threads.ToString(CultureInfo.InvariantCulture),
            // the text into a file of its own, without times: the console speaks another code page
            "-otxt", "-of", output, "-nt",
        };
        if (!onCard) args.Add("-ng");
        if (Window(seconds, onCard) is int window) args.AddRange(new[] { "-ac", window.ToString(CultureInfo.InvariantCulture) });
        return args;
    }

    /// <summary>What the model wrote, as one piece of text. Whisper marks what is not speech in brackets ("[BLANK_AUDIO]", "(muziek)").</summary>
    public static string Clean(string text)
    {
        var lines = text.Split('\n').Select(l => Regex.Replace(l, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", "").Trim()).Where(l => l.Length > 0);
        return Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim();
    }
}

/// <summary>Speech to text on this PC, with the speech engine and the speech model: nothing leaves the PC. One recording at a time.</summary>
public sealed class SpeechEngine
{
    public const string ExeName = "wam-speech.exe";

    /// <summary>Tests: a stand-in for the engine.</summary>
    internal string? ExecutableOverride { get; set; }

    private readonly SemaphoreSlim one = new(1, 1);

    public static string Binary(IGpuBackend backend) => Path.Combine(Paths.EngineRoot, backend.EngineFolder, ExeName);

    /// <summary>The model file when it is downloaded; null: not (yet).</summary>
    public static string? ModelPath(ModelStore store)
    {
        var m = store.Catalog.Speech.FirstOrDefault();
        return m != null && store.StatusOf(m.Id).IsComplete ? store.ShardPaths(m)[0] : null;
    }

    /// <summary>The text of 16 kHz mono samples, or why not. `language`: two letters, or "auto".</summary>
    public async Task<(string? Text, string? Error)> Transcribe(short[] samples, string model, string language, SystemInfo pc, IGpuBackend backend, CancellationToken ct = default)
    {
        if (samples.Length < Audio.Rate / 4) return (null, L("De opname was te kort."));
        var exe = ExecutableOverride ?? Binary(backend);
        if (!File.Exists(exe)) return (null, L("De spraakengine ontbreekt in de app. Installeer WinAIMagic opnieuw."));
        if (!File.Exists(model)) return (null, L("Het spraakmodel is nog niet gedownload (Instellingen → Agent → Spraak)."));
        await one.WaitAsync(ct);
        var dir = Path.Combine(Path.GetTempPath(), "WinAIMagic-speech-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var wav = Path.Combine(dir, "in.wav");
            Audio.WriteWav(wav, samples);
            bool onCard = SpeechPlanner.OnCard(pc);
            double seconds = samples.Length / (double)Audio.Rate;
            var outcome = await Run(exe, model, wav, Path.Combine(dir, "out"), language, onCard, pc.PerformanceCores, seconds, ct);
            // a card can refuse in ways no plan foresees; the processor always works
            if (outcome.Text == null && onCard && !ct.IsCancellationRequested)
                outcome = await Run(exe, model, wav, Path.Combine(dir, "out"), language, false, pc.PerformanceCores, seconds, ct);
            if (outcome.Text == null) return (null, outcome.Error);
            return outcome.Text.Length == 0 ? (null, L("Er werd niets verstaan.")) : (outcome.Text, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return (null, LogFilter.Clean(e.Message));
        }
        finally
        {
            one.Release();
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A Telegram voice message (Ogg Opus).</summary>
    public async Task<(string? Text, string? Error)> TranscribeFile(string oggPath, string model, string language, SystemInfo pc, IGpuBackend backend, CancellationToken ct = default)
    {
        short[] samples;
        try { samples = Audio.ReadOggOpus(await File.ReadAllBytesAsync(oggPath, ct)); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OpusException)
        {
            return (null, L($"Het geluid kon niet worden gelezen: {e.Message}"));
        }
        return await Transcribe(samples, model, language, pc, backend, ct);
    }

    private static async Task<(string? Text, string? Error)> Run(string exe, string model, string wav, string output, string language, bool onCard, int threads, double seconds, CancellationToken ct)
    {
        var tail = new StringBuilder();
        var args = SpeechPlanner.Arguments(model, wav, output, language, onCard, Math.Max(4, threads), seconds);
        using var child = ChildProcess.Start(new ChildStart(exe, args)
        {
            Cwd = Path.GetDirectoryName(exe),
            OnOutput = (b, n) => { lock (tail) { tail.Append(Encoding.UTF8.GetString(b, 0, n)); if (tail.Length > 8000) tail.Remove(0, tail.Length - 8000); } },
        });
        using var reg = ct.Register(child.KillTree);
        int code = await child.Exited;
        var txt = output + ".txt";
        if (code == 0 && File.Exists(txt)) return (SpeechPlanner.Clean(await File.ReadAllTextAsync(txt, Encoding.UTF8, CancellationToken.None)), null);
        string log;
        lock (tail) log = tail.ToString();
        try { File.WriteAllText(Paths.SpeechLog, LogFilter.Clean("$ " + Path.GetFileName(exe) + " " + string.Join(" ", args) + "\n" + log + $"\nexit {code}\n")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var last = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase));
        return (null, L("De spraakherkenning lukte niet.") + (last is { Length: > 0 } ? " " + LogFilter.Clean(last) : ""));
    }
}
