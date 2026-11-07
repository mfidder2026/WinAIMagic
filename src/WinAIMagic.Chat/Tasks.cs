using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinAIMagic.Core.Support;
using static WinAIMagic.Core.Support.Lang;

namespace WinAIMagic.Chat;

/// <summary>When a task runs: once; every day (or Monday to Friday) at a time; every week on a day; every few hours within
/// active hours. Times are clock times in the PC's time zone, so 08:00 stays 08:00 around summer and winter time.</summary>
public sealed record TaskSchedule
{
    /// <summary>"once", "daily", "weekly" or "every".</summary>
    public string Kind { get; init; } = "daily";
    /// <summary>once: the moment.</summary>
    public DateTimeOffset? At { get; init; }
    public int Hour { get; init; }
    public int Minute { get; init; }
    /// <summary>daily: only Monday to Friday.</summary>
    public bool WeekdaysOnly { get; init; }
    /// <summary>weekly: 1 = Sunday … 7 = Saturday (as the Mac counts).</summary>
    public int Weekday { get; init; }
    /// <summary>every: so many hours, on the hour, from From up to (not including) To; From == To is the whole day.</summary>
    public int Hours { get; init; }
    public int From { get; init; }
    public int To { get; init; }

    public static TaskSchedule Once(DateTimeOffset at) => new() { Kind = "once", At = at };
    public static TaskSchedule Daily(int hour, int minute, bool weekdaysOnly) => new() { Kind = "daily", Hour = hour, Minute = minute, WeekdaysOnly = weekdaysOnly };
    public static TaskSchedule Weekly(int weekday, int hour, int minute) => new() { Kind = "weekly", Weekday = weekday, Hour = hour, Minute = minute };
    public static TaskSchedule Every(int hours, int from, int to) => new() { Kind = "every", Hours = hours, From = from, To = to };

    /// <summary>A clock time on a day in the zone; a time that does not exist (the night summer time starts) moves an hour on.</summary>
    private static DateTimeOffset Clock(DateTime day, int hour, int minute, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The first moment after this one the schedule says to run, or null (a single run that is past).</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(after, zone).DateTime.Date;
        switch (Kind)
        {
            case "once":
                return At > after ? At : null;
            case "daily":
                for (int i = 0; i < 10; i++)
                {
                    var day = today.AddDays(i);
                    if (WeekdaysOnly && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                    var d = Clock(day, Hour, Minute, zone);
                    if (d > after) return d;
                }
                return null;
            case "weekly":
                for (int i = 0; i < 9; i++)
                {
                    var day = today.AddDays(i);
                    if ((int)day.DayOfWeek != Weekday - 1) continue;
                    var d = Clock(day, Hour, Minute, zone);
                    if (d > after) return d;
                }
                return null;
            case "every":
                int step = Math.Clamp(Hours, 1, 24);
                int end = From == To ? From + 24 : To > From ? To : To + 24;
                for (int offset = 0; offset < 3; offset++)
                {
                    for (int hour = From; hour < end; hour += step)
                    {
                        // past midnight (a window such as 22–02) the hour belongs to the next day
                        var d = Clock(today.AddDays(offset + hour / 24), hour % 24, 0, zone);
                        if (d > after) return d;
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private static string Time(int h, int m) => $"{h:00}:{m:00}";

    /// <summary>In ordinary words, for the list.</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            var culture = AppLanguages.Current.Culture();
            switch (Kind)
            {
                case "once":
                    var at = (At ?? DateTimeOffset.Now).ToLocalTime().ToString("g", culture);
                    return L($"Eenmalig, {at}");
                case "daily":
                    var time = Time(Hour, Minute);
                    return WeekdaysOnly ? L($"Elke werkdag om {time}") : L($"Elke dag om {time}");
                case "weekly":
                    var name = Weekday is >= 1 and <= 7 ? culture.DateTimeFormat.DayNames[Weekday - 1] : "?";
                    var clock = Time(Hour, Minute);
                    return L($"Elke {name} om {clock}");
                default:
                    string first = Time(From, 0), last = Time(To % 24, 0);
                    var span = From == To ? L("de hele dag") : L($"van {first} tot {last}");
                    int hours = Hours;
                    return hours == 1 ? L($"Elk uur, {span}") : L($"Elke {hours} uur, {span}");
            }
        }
    }
}

/// <summary>A task the model does on its own: a prompt, when, and in which folder.</summary>
public sealed class ScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public TaskSchedule Schedule { get; set; } = TaskSchedule.Daily(8, 0, false);
    /// <summary>The work folder of its conversations (null: none).</summary>
    public string? Workspace { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRun { get; set; }
    /// <summary>When it runs next; set by the store whenever the task or the schedule changes.</summary>
    public DateTimeOffset? NextRun { get; set; }
    /// <summary>The conversation of its last run.</summary>
    public Guid? LastConversation { get; set; }
    /// <summary>A run that was missed by too much (the PC was off for a day) and was not made up for.</summary>
    public DateTimeOffset? LastSkipped { get; set; }

    public ScheduledTask Copy() => (ScheduledTask)MemberwiseClone();
}

/// <summary>The tasks, saved as one JSON file in the app data folder.</summary>
public sealed class TaskStore
{
    private readonly object sync = new();
    private List<ScheduledTask> tasks = [];
    public string File { get; }

    /// <summary>A run missed by at most this long is made up for, once; an older one is skipped (a morning briefing is no use at night).</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    /// <summary>A task was added, changed or removed.</summary>
    public event Action? Changed;

    private static readonly JsonSerializerOptions Json = new(ChatJson.Options) { WriteIndented = true };

    public TaskStore(string? file = null)
    {
        File = file ?? Paths.Tasks;
        try
        {
            if (System.IO.File.Exists(File))
                tasks = JsonSerializer.Deserialize<List<ScheduledTask>>(System.IO.File.ReadAllBytes(File), Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { tasks = []; }
    }

    public IReadOnlyList<ScheduledTask> Tasks { get { lock (sync) return [.. tasks.Select(t => t.Copy())]; } }

    public ScheduledTask? Task(Guid id) { lock (sync) return tasks.Find(t => t.Id == id)?.Copy(); }

    /// <summary>Adds or replaces it, with its next run worked out.</summary>
    public void Upsert(ScheduledTask task, DateTimeOffset? now = null)
    {
        var t = task.Copy();
        t.NextRun = t.Enabled ? t.Schedule.Next(now ?? DateTimeOffset.Now) : null;
        lock (sync)
        {
            int i = tasks.FindIndex(x => x.Id == t.Id);
            if (i >= 0) tasks[i] = t; else tasks.Add(t);
        }
        Save();
    }

    public void Remove(Guid id)
    {
        lock (sync) tasks.RemoveAll(t => t.Id == id);
        Save();
    }

    /// <summary>The tasks that are due now, in order. Each comes once: its next run moves on at once. One that was missed by
    /// more than the catch-up window is skipped and moves on as well.</summary>
    public List<ScheduledTask> TakeDue(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.Now;
        var due = new List<ScheduledTask>();
        bool changed = false;
        lock (sync)
        {
            foreach (var t in tasks)
            {
                if (!t.Enabled || t.NextRun is not { } next || next > now) continue;
                changed = true;
                if (now - next > CatchUpWindow) t.LastSkipped = next;
                else
                {
                    t.LastRun = now;
                    due.Add(t.Copy());
                }
                t.NextRun = t.Schedule.Next(now);
                if (t.NextRun == null) t.Enabled = false;     // a single run is done
            }
        }
        if (changed) Save();
        return due;
    }

    /// <summary>The run's conversation, for the list.</summary>
    public void Ran(Guid id, Guid conversation)
    {
        lock (sync) { if (tasks.Find(t => t.Id == id) is { } t) t.LastConversation = conversation; else return; }
        Save();
    }

    /// <summary>The task runs now, besides its schedule (Nu uitvoeren).</summary>
    public void MarkRun(Guid id, DateTimeOffset? now = null)
    {
        lock (sync) { if (tasks.Find(t => t.Id == id) is { } t) t.LastRun = now ?? DateTimeOffset.Now; else return; }
        Save();
    }

    public void Save()
    {
        byte[] data;
        lock (sync) data = JsonSerializer.SerializeToUtf8Bytes(tasks, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicFile.Write(File, data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }
}

/// <summary>schedule_task: the model plans a task for the user ("give me my agenda every weekday at 8"). It always goes
/// through the approval card first.</summary>
public static class TaskTool
{
    public const string ToolName = "schedule_task";

    public sealed class Problem(string message) : Exception(message);

    public static JsonObject Definition => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = ToolName,
            ["description"] = "Plan a task that you will do on your own later or repeatedly, when the user asks for that (a reminder, a daily summary, a weekly check). The user approves it first. The prompt is what you will be asked then, so write it as a complete instruction.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "A short name" },
                    ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "The instruction for each run, complete on its own" },
                    ["when"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("once", "daily", "weekdays", "weekly", "every") },
                    ["at"] = new JsonObject { ["type"] = "string", ["description"] = "For once: local date and time, YYYY-MM-DDTHH:MM" },
                    ["time"] = new JsonObject { ["type"] = "string", ["description"] = "For daily, weekdays and weekly: HH:MM" },
                    ["weekday"] = new JsonObject { ["type"] = "string", ["description"] = "For weekly: monday … sunday" },
                    ["hours"] = new JsonObject { ["type"] = "integer", ["description"] = "For every: every so many hours" },
                    ["from"] = new JsonObject { ["type"] = "integer", ["description"] = "For every: first hour of the day (0-23)" },
                    ["to"] = new JsonObject { ["type"] = "integer", ["description"] = "For every: hour to stop (0-23)" },
                },
                ["required"] = new JsonArray("title", "prompt", "when"),
            },
        },
    };

    private static int? Int(JsonObject a, string key) => a[key] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var n) => n,
        _ => null,
    };

    /// <summary>A local date, or a date and time ("2026-11-07", "2026-11-07T09:30", with seconds or an offset).</summary>
    public static DateTimeOffset? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (DateTimeOffset.TryParseExact(t, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var withZone)
            && (t.EndsWith('Z') || t.Length > 19 && (t[^6] == '+' || t[^6] == '-')))
            return withZone;
        if (DateTime.TryParseExact(t, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return new DateTimeOffset(local);
        return null;
    }

    /// <summary>The task the model asked for, or what is wrong with the request.</summary>
    public static ScheduledTask Parse(JsonObject a, DateTimeOffset now)
    {
        var title = (WorkspaceTools.Str(a, "title") ?? "").Trim();
        var prompt = (WorkspaceTools.Str(a, "prompt") ?? "").Trim();
        if (title.Length == 0) throw new Problem("title is missing");
        if (prompt.Length == 0) throw new Problem("prompt is missing");
        (int, int) Clock()
        {
            var parts = (WorkspaceTools.Str(a, "time") ?? "").Split(':').Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
            if (parts.Length < 2 || parts[0] is < 0 or > 23 || parts[1] is < 0 or > 59) throw new Problem("time must be HH:MM");
            return (parts[0], parts[1]);
        }
        TaskSchedule schedule;
        switch ((WorkspaceTools.Str(a, "when") ?? "").ToLowerInvariant())
        {
            case "once":
                if (Date(WorkspaceTools.Str(a, "at")) is not { } at || at <= now) throw new Problem("at must be a moment in the future, YYYY-MM-DDTHH:MM");
                schedule = TaskSchedule.Once(at);
                break;
            case "daily":
                { var (h, m) = Clock(); schedule = TaskSchedule.Daily(h, m, false); }
                break;
            case "weekdays":
                { var (h, m) = Clock(); schedule = TaskSchedule.Daily(h, m, true); }
                break;
            case "weekly":
                {
                    var (h, m) = Clock();
                    if (Weekday(a["weekday"]) is not { } wd) throw new Problem("weekday must be monday … sunday");
                    schedule = TaskSchedule.Weekly(wd, h, m);
                }
                break;
            case "every":
                int hours = Int(a, "hours") ?? 1;
                if (hours is < 1 or > 24) throw new Problem("hours must be 1 to 24");
                schedule = TaskSchedule.Every(hours, Math.Clamp(Int(a, "from") ?? 0, 0, 23), Math.Clamp(Int(a, "to") ?? 0, 0, 23));
                break;
            default:
                throw new Problem("when must be once, daily, weekdays, weekly or every");
        }
        return new ScheduledTask { Title = TextLimit.Prefix(title, 80), Prompt = prompt, Schedule = schedule };
    }

    private static readonly string[][] DayNames =
    [
        ["sunday", "zondag", "sonntag", "dimanche"], ["monday", "maandag", "montag", "lundi"],
        ["tuesday", "dinsdag", "dienstag", "mardi"], ["wednesday", "woensdag", "mittwoch", "mercredi"],
        ["thursday", "donderdag", "donnerstag", "jeudi"], ["friday", "vrijdag", "freitag", "vendredi"],
        ["saturday", "zaterdag", "samstag", "samedi"],
    ];

    /// <summary>1 = Sunday … 7 = Saturday, from a number or an English, Dutch, German or French name.</summary>
    public static int? Weekday(JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<int>(out var n)) return n is >= 1 and <= 7 ? n : null;
        var s = (value is JsonValue sv && sv.TryGetValue<string>(out var str) ? str : "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (int.TryParse(s, out var num)) return num is >= 1 and <= 7 ? num : null;
        int i = Array.FindIndex(DayNames, names => names.Contains(s));
        return i < 0 ? null : i + 1;
    }
}

/// <summary>The heartbeat: every so often, within the active hours, the model goes through HEARTBEAT.md and only speaks up
/// when there is something.</summary>
public static class Heartbeat
{
    /// <summary>Marks heartbeat conversations (Conversation.Task) and runs.</summary>
    public static readonly Guid TaskId = Guid.Parse("00000000-0000-0000-0000-0000000000ab");

    public static DateTimeOffset? LastRun
    {
        get => DateTimeOffset.TryParse(Settings.Shared.HeartbeatLast, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        set => Settings.Shared.HeartbeatLast = value?.ToString("o", CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>On, inside the active hours, and the interval has passed since the last one.</summary>
    public static bool IsDue(DateTimeOffset now)
    {
        var s = Settings.Shared;
        return s.Heartbeat && IsDue(now, LastRun, s.HeartbeatMinutes, s.HeartbeatFrom, s.HeartbeatTo);
    }

    public static bool IsDue(DateTimeOffset now, DateTimeOffset? last, int minutes, int from, int to)
    {
        int hour = now.ToLocalTime().Hour;
        bool inside = from == to || (from < to ? hour >= from && hour < to : hour >= from || hour < to);
        if (!inside) return false;
        if (last is not { } l) return true;
        return (now - l).TotalSeconds >= Math.Max(minutes, 5) * 60 - 1;
    }

    /// <summary>The checklist, made with an example the first time; null when there is nothing on it but headings and comments.</summary>
    public static string? Checklist(string? file = null)
    {
        file ??= Paths.Heartbeat;
        try
        {
            if (!System.IO.File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                System.IO.File.WriteAllText(file, Example, AtomicFile.Utf8);
            }
            var text = System.IO.File.ReadAllText(file);
            bool any = text.Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("<!--", StringComparison.Ordinal) && !l.StartsWith('>'));
            return any ? TextLimit.Prefix(text, 6000) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string Prompt(string checklist) =>
        L("Dit is een geplande controle (heartbeat), de gebruiker kijkt nu niet mee. Loop de checklist hieronder na met je gereedschap. Is er niets dat de gebruiker nu moet weten, antwoord dan alleen met het woord NIETS. Is er wel iets, zeg dan in een paar zinnen wat en waarom; begin met het belangrijkste.")
        + "\n\n" + checklist;

    private static readonly string[] Nothing = ["NIETS", "NOTHING", "NICHTS", "RIEN", "HEARTBEAT_OK", "KLAAR: NIETS", "KLAAR NIETS"];

    /// <summary>The model had nothing to report.</summary>
    public static bool IsNothing(string answer)
    {
        var word = answer.Trim().Trim('*', '_', '.', '!', '"', '\'', '`').ToUpperInvariant();
        return Nothing.Contains(word);
    }

    /// <summary>The first checklist, in Dutch or English (the file is the user's to edit, in any language).</summary>
    public static string Example => AppLanguages.Current.Code() == "nl"
        ? """
          # Checklist voor de heartbeat

          > Het model loopt deze lijst na, elke keer dat de heartbeat komt. Het meldt zich alleen als er iets is.
          > Regels die beginnen met # of > zijn uitleg; zet je eigen punten eronder, één per regel.

          > Voorbeelden (haal het > weg om er een te gebruiken):
          > - Staat er in het komende uur een afspraak in mijn agenda? Noem hem dan.
          > - Is er in de map Downloads een bestand van meer dan 10 GB bijgekomen?
          > - Is de schijf C: van deze pc voor meer dan 90% vol?
          """
        : """
          # Heartbeat checklist

          > The model goes through this list at every heartbeat. It only speaks up when something needs you.
          > Lines starting with # or > are explanation; put your own points below, one per line.

          > Examples (remove the > to use one):
          > - Is there an appointment in my calendar in the coming hour? Then name it.
          > - Has a file of more than 10 GB appeared in the Downloads folder?
          > - Is this PC's drive C: more than 90% full?
          """;
}
