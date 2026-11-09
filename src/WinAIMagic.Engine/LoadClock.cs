using System.Text.Json;
using WinAIMagic.Core.Support;

namespace WinAIMagic.Engine;

/// <summary>How far loading or unloading a model is, for the gauge. The main engine says little while it reads a model of
/// 20 GB (a dot now and then; on this PC 31 s of silence for Qwen3.6-35B), so the time it took last time counts too: the
/// gauge runs on the clock, and on what the engine reports when that is further. The first time the size gives a guess.</summary>
public static class LoadClock
{
    /// <summary>Reading speed for the first guess: Qwen3.6-35B (20.7 GB) took 31 s from the SSD on the owner's PC.</summary>
    public const double BytesPerSecond = 0.7e9;
    public static readonly TimeSpan FirstUnload = TimeSpan.FromSeconds(3);

    /// <summary>0..0.99: never full before the engine answers. Past the expected time it creeps on instead of standing still.</summary>
    public static double Fraction(double reported, TimeSpan elapsed, TimeSpan expected)
    {
        double byTime = expected.TotalSeconds <= 0 ? 0 : Math.Max(0, elapsed.TotalSeconds) / expected.TotalSeconds;
        if (byTime > 0.9) byTime = 0.9 + 0.09 * (1 - Math.Exp(-(byTime - 0.9) * 3));
        return Math.Clamp(Math.Max(reported, byTime), 0, 0.99);
    }

    /// <summary>Seconds still to go, or null when it is past the expected time (then the gauge only says it is busy).</summary>
    public static int? Remaining(TimeSpan elapsed, TimeSpan expected)
    {
        var left = expected - elapsed;
        return left.TotalSeconds >= 1 ? (int)Math.Ceiling(left.TotalSeconds) : null;
    }

    public static TimeSpan FirstLoad(ulong bytes) => TimeSpan.FromSeconds(3 + bytes / BytesPerSecond);

    private static readonly object gate = new();
    private static Dictionary<string, double>? times;
    private static string FilePath => Path.Combine(Paths.AppData, "load-times.json");

    /// <summary>The time remembered for this key (as "load:model-id"), or the guess.</summary>
    public static TimeSpan Expected(string key, TimeSpan guess)
    {
        lock (gate) return Load().TryGetValue(key, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : guess;
    }

    /// <summary>Remembers how long it took; the newest time weighs most (the first load after a restart reads from the SSD,
    /// a later one partly from the cache).</summary>
    public static void Record(string key, TimeSpan took)
    {
        if (took <= TimeSpan.Zero || took.TotalHours > 2) return;
        lock (gate)
        {
            var all = Load();
            all[key] = all.TryGetValue(key, out var old) && old > 0 ? 0.6 * took.TotalSeconds + 0.4 * old : took.TotalSeconds;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(all)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? readFrom;

    private static Dictionary<string, double> Load()
    {
        // tests point the app at a folder of their own: read again when the folder changed
        if (times != null && readFrom == FilePath) return times;
        times = null;
        readFrom = FilePath;
        try { times = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return times ??= [];
    }
}
