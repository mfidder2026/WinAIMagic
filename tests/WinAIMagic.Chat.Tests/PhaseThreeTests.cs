using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;
using static WinAIMagic.Chat.Tests.FakeEngine;

namespace WinAIMagic.Chat.Tests;

public class ModelGateTests
{
    [Fact]
    public async Task TurnsInOrder()
    {
        var gate = new ModelGate();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var order = new List<Guid>();
        Assert.True(await gate.AcquireAsync(a, default));
        var tb = Task.Run(async () => { if (await gate.AcquireAsync(b, default)) { lock (order) order.Add(b); gate.Release(); } });
        await Task.Delay(100);
        var tc = Task.Run(async () => { if (await gate.AcquireAsync(c, default)) { lock (order) order.Add(c); gate.Release(); } });
        await Task.Delay(100);
        Assert.Equal([b, c], gate.Waiting);
        gate.Release();
        await Task.WhenAll(tb, tc);
        Assert.Equal([b, c], order);
        Assert.Null(gate.Holder);
    }

    [Fact]
    public async Task AStopWhileWaitingLeavesTheQueue()
    {
        var gate = new ModelGate();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await gate.AcquireAsync(a, default);
        using var stop = new CancellationTokenSource();
        var tb = gate.AcquireAsync(b, stop.Token);
        await Task.Delay(50);
        stop.Cancel();
        Assert.False(await tb);
        Assert.Empty(gate.Waiting);
        gate.Release();
        Assert.Null(gate.Holder);   // nobody left to hand the model to
    }
}

/// <summary>A stand-in for the engine that takes its time, like fake_engine.py on the Mac: the last user message decides.
/// "SCHRIJF" asks for write_file (and after the result says KLAAR), "PLAN" sets a task list, anything else gets
/// "Antwoord op: &lt;text&gt;". Most says how many requests ran at once at most.</summary>
public sealed class SlowEngine : HttpMessageHandler
{
    private int now;
    public int Most { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        lock (this) Most = Math.Max(Most, ++now);
        try
        {
            await Task.Delay(400, ct);   // long enough for another request to come in meanwhile
            if (body["stream"]?.GetValue<bool>() != true)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"GEEN\"}}]}") };
            var msgs = body["messages"]!.AsArray();
            var lastUser = msgs.LastOrDefault(m => m!["role"]!.GetValue<string>() == "user");
            var text = lastUser?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            bool afterTool = msgs[^1]!["role"]!.GetValue<string>() == "tool";
            if (text.Contains("SCHRIJF") && !afterTool) return Call("write_file", new JsonObject { ["path"] = "x.txt", ["content"] = "hoi" }, "call1");
            if (text.Contains("MAILSTUUR") && !afterTool) return Call("mail_send", new JsonObject { ["to"] = "test@example.com", ["subject"] = "Hallo", ["body"] = "Een test." }, "call4");
            if (text.Contains("PLAN") && !afterTool)
                return Call("task_list", new JsonObject
                {
                    ["steps"] = new JsonArray(
                        new JsonObject { ["title"] = "Eerst kijken", ["status"] = "done" },
                        new JsonObject { ["title"] = "Dan bouwen", ["status"] = "doing" },
                        new JsonObject { ["title"] = "Testen", ["status"] = "todo" }),
                }, "call2");
            return Say(afterTool ? "KLAAR: geschreven." : "Antwoord op: " + text);
        }
        finally { lock (this) now--; }
    }
}

/// <summary>Two conversations at once against a stand-in for the engine.</summary>
public class ConcurrentChatTests : ChatTest
{
    private readonly ConversationStore store = new();
    private readonly SlowEngine engine = new();

    public ConcurrentChatTests()
    {
        Settings.Shared.WorkspaceTools = true;
        Settings.Shared.RightWrite = "ask";
    }

    private string? LastAnswer(Guid id) => store.Find(id)?.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Length > 0)?.Text;

    [Fact]
    public async Task ATaskWaitingForApprovalDoesNotHoldUpAnotherChat()
    {
        string dirA = NewFolder("a"), dirB = NewFolder("b");
        var a = new Conversation { Workspace = dirA };
        var b = new Conversation { Workspace = dirB };
        store.Conversations = [a, b];
        var chat = new ChatClient(new HttpClient(engine) { Timeout = Timeout.InfiniteTimeSpan });

        chat.Send("SCHRIJF een bestand", [], a.Id, store, Base);
        await Until(() => chat.ApprovalFor(a.Id) != null);
        // A waits for the user; B gets the model and its answer
        chat.Send("hallo", [], b.Id, store, Base);
        Assert.True(chat.IsBusy(a.Id) && chat.IsBusy(b.Id));
        await Until(() => !chat.IsBusy(b.Id));
        Assert.Equal("Antwoord op: hallo", LastAnswer(b.Id));
        Assert.True(chat.IsBusy(a.Id), "A still waits for its approval");

        chat.Answer(chat.ApprovalFor(a.Id)!, true);
        await Until(() => !chat.IsBusy(a.Id));
        Assert.Equal("Geschreven.", LastAnswer(a.Id));   // the done-word is not shown
        Assert.True(File.Exists(Path.Combine(dirA, "x.txt")), "A writes in its own folder: " + store.Find(a.Id)!.Messages.First(m => m.Role == ChatRole.Tool).Text);
        Assert.False(File.Exists(Path.Combine(dirB, "x.txt")));
        Assert.Equal(1, engine.Most);   // the model wrote for one conversation at a time
    }

    [Fact]
    public async Task StoppingOneConversationLeavesTheOther()
    {
        var a = new Conversation { Workspace = NewFolder("a") };
        var b = new Conversation();
        store.Conversations = [a, b];
        var chat = new ChatClient(new HttpClient(engine) { Timeout = Timeout.InfiniteTimeSpan });
        chat.Send("SCHRIJF", [], a.Id, store, Base);
        await Until(() => chat.ApprovalFor(a.Id) != null);
        chat.Send("nog een vraag", [], b.Id, store, Base);
        chat.Stop(a.Id);
        await Until(() => !chat.IsBusy(a.Id));
        await Until(() => !chat.IsBusy(b.Id));
        Assert.Null(chat.ApprovalFor(a.Id));
        Assert.Equal("Antwoord op: nog een vraag", LastAnswer(b.Id));
    }

    [Fact]
    public async Task TheModelsStepListIsKept()
    {
        var a = new Conversation { Workspace = NewFolder("a") };
        store.Conversations = [a];
        var chat = new ChatClient(new HttpClient(engine) { Timeout = Timeout.InfiniteTimeSpan });
        chat.Send("PLAN iets groots", [], a.Id, store, Base);
        await Until(() => !chat.IsBusy(a.Id));
        var steps = store.Find(a.Id)!.Steps!;
        Assert.Equal(["Eerst kijken", "Dan bouwen", "Testen"], steps.Select(s => s.Title));
        Assert.Equal(["done", "doing", "todo"], steps.Select(s => s.Status));
        var result = store.Find(a.Id)!.Messages.First(m => m.Role == ChatRole.Tool).Text;
        Assert.Contains("1 of 3 done", result);
        // a list that is not finished stays with the next question; a finished one goes
        chat.Send("hallo", [], a.Id, store, Base);
        await chat.Completion;
        Assert.NotNull(store.Find(a.Id)!.Steps);
        store.Update(a.Id, c => c.Steps = [new WorkStep("klaar", "done")]);
        chat.Send("en nu", [], a.Id, store, Base);
        await chat.Completion;
        Assert.Null(store.Find(a.Id)!.Steps);
    }

    /// <summary>schedule_task asks first, the task gets the conversation's folder, and a wrong time goes back to the model.</summary>
    [Fact]
    public async Task TheModelPlansATaskThroughTheCard()
    {
        var work = NewFolder("w");
        var a = new Conversation { Workspace = work };
        store.Conversations = [a];
        var tasks = new TaskStore(Path.Combine(Root, "tasks.json"));
        var fake = new FakeEngine(
            () => Call("schedule_task", new JsonObject { ["title"] = "Agenda", ["prompt"] = "Wat staat er vandaag?", ["when"] = "daily", ["time"] = "25:00" }),
            () => Call("schedule_task", new JsonObject { ["title"] = "Agenda", ["prompt"] = "Wat staat er vandaag?", ["when"] = "weekdays", ["time"] = "08:00" }, "c2"),
            () => Say("Gepland."));
        var asked = new List<Approval>();
        var chat = new ChatClient(fake.Client) { Tasks = tasks, AskUser = (ap, _) => { asked.Add(ap); return Task.FromResult(new ApprovalAnswer(true)); } };
        Assert.Contains(chat.RunTools(), t => t!["function"]!["name"]!.GetValue<string>() == "schedule_task");
        chat.Send("Geef me elke werkdag om 8 uur mijn agenda", [], a.Id, store, Base);
        await chat.Completion;
        var tool = store.Find(a.Id)!.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Text).ToList();
        Assert.Contains("time must be HH:MM", tool[0]);
        Assert.Single(asked);
        Assert.Equal(L("Taak plannen: Agenda"), asked[0].Title);
        Assert.Contains(L("Elke werkdag om 08:00"), asked[0].Detail);
        var t = Assert.Single(tasks.Tasks);
        Assert.Equal(work, t.Workspace);
        Assert.Equal(TaskSchedule.Daily(8, 0, true), t.Schedule);
        Assert.NotNull(t.NextRun);
    }

    private static string L(string key) => Lang.LKey(key);
}

public class WorkStepsTests
{
    [Fact]
    public void Parse()
    {
        var steps = WorkSteps.Parse(new JsonObject
        {
            ["steps"] = new JsonArray(
                new JsonObject { ["title"] = " Kijken ", ["status"] = "DONE" },
                new JsonObject { ["title"] = "Bouwen", ["status"] = "bezig" },
                new JsonObject { ["title"] = "", ["status"] = "todo" }),
        });
        Assert.Equal(["Kijken", "Bouwen"], steps!.Select(s => s.Title));
        Assert.Equal(["done", "todo"], steps!.Select(s => s.Status));   // an unknown status counts as todo; an empty step is left out
        Assert.Null(WorkSteps.Parse(new JsonObject { ["steps"] = "nee" }));
        Assert.Null(WorkSteps.Parse(new JsonObject { ["steps"] = new JsonArray() }));
        Assert.Equal("Task list updated: 1 of 2 done; now: b.", WorkSteps.Summary([new WorkStep("a", "done"), new WorkStep("b", "doing")]));
    }
}

public class DoneWordTests
{
    /// <summary>The done-word is for the app; the user sees the answer without it.</summary>
    [Fact]
    public void DoneWordIsNotShown()
    {
        Assert.Equal("Morgen (zondag 8 november 2026) staat er niets in je agenda.", ChatClient.WithoutDoneWord("KLAAR — morgen (zondag 8 november 2026) staat er niets in je agenda."));
        Assert.Equal("Het spel draait in VICE.", ChatClient.WithoutDoneWord("KLAAR: het spel draait in VICE."));
        Assert.Equal("Het staat in uit.txt.", ChatClient.WithoutDoneWord("**KLAAR**\n\nHet staat in uit.txt."));
        Assert.Equal("The file is written.", ChatClient.WithoutDoneWord("DONE. The file is written."));
        Assert.Equal("Klaar! Het bestand is er.", ChatClient.WithoutDoneWord("Klaar! Het bestand is er."));   // ordinary language stays
        Assert.Equal("KLAARZETTEN gaat zo.", ChatClient.WithoutDoneWord("KLAARZETTEN gaat zo."));
        Assert.Equal("KLAAR", ChatClient.WithoutDoneWord("KLAAR"));   // nothing after it: leave it
        // at the end too (seen with Qwen3.6: "… verder zoek. KLAAR")
        Assert.Equal("Laat maar weten als je verder wilt zoeken.", ChatClient.WithoutDoneWord("Laat maar weten als je verder wilt zoeken. KLAAR"));
        Assert.Equal("Het is af.", ChatClient.WithoutDoneWord("Het is af.\n\n**KLAAR**"));
        Assert.Equal("Ik ben klaar", ChatClient.WithoutDoneWord("Ik ben klaar"));
    }
}

public class TaskScheduleTests : ChatTest
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static DateTimeOffset Date(string s)
    {
        var local = DateTime.ParseExact(s, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(local, Amsterdam.GetUtcOffset(local));
    }

    private static string? Text(DateTimeOffset? d) =>
        d is { } x ? TimeZoneInfo.ConvertTime(x, Amsterdam).ToString("yyyy-MM-dd HH:mm ddd", System.Globalization.CultureInfo.InvariantCulture) : null;

    [Fact]
    public void Once()
    {
        var at = Date("2026-11-08 10:00");
        Assert.Equal(at, TaskSchedule.Once(at).Next(Date("2026-11-07 12:00"), Amsterdam));
        Assert.Null(TaskSchedule.Once(at).Next(at, Amsterdam));   // a single run that is past
    }

    [Fact]
    public void DailyAndWorkdays()
    {
        var sat = Date("2026-11-07 12:00");   // Saturday 7 November 2026
        Assert.Equal("2026-11-08 08:00 Sun", Text(TaskSchedule.Daily(8, 0, false).Next(sat, Amsterdam)));
        Assert.Equal("2026-11-09 08:00 Mon", Text(TaskSchedule.Daily(8, 0, true).Next(sat, Amsterdam)));
        Assert.Equal("2026-11-07 13:30 Sat", Text(TaskSchedule.Daily(13, 30, false).Next(sat, Amsterdam)));   // later today
    }

    [Fact]
    public void Weekly()
    {
        // 6 = Friday
        Assert.Equal("2026-11-13 17:00 Fri", Text(TaskSchedule.Weekly(6, 17, 0).Next(Date("2026-11-07 12:00"), Amsterdam)));
    }

    [Fact]
    public void EveryFewHoursWithinActiveHours()
    {
        var s = TaskSchedule.Every(2, 8, 22);
        Assert.Equal("2026-11-07 14:00 Sat", Text(s.Next(Date("2026-11-07 12:00"), Amsterdam)));
        Assert.Equal("2026-11-08 08:00 Sun", Text(s.Next(Date("2026-11-07 21:00"), Amsterdam)));   // after the active hours: the next morning
        Assert.Equal("2026-11-07 08:00 Sat", Text(s.Next(Date("2026-11-07 05:00"), Amsterdam)));
        Assert.Equal("2026-11-08 00:00 Sun", Text(TaskSchedule.Every(1, 0, 0).Next(Date("2026-11-07 23:10"), Amsterdam)));   // the whole day
        Assert.Equal("2026-11-08 00:00 Sun", Text(TaskSchedule.Every(1, 22, 2).Next(Date("2026-11-07 23:10"), Amsterdam)));   // a window past midnight
    }

    /// <summary>Summer time starts on 29 March 2026 (02:00 → 03:00) and ends on 25 October 2026 (03:00 → 02:00).</summary>
    [Fact]
    public void SummerAndWinterTime()
    {
        var spring = TaskSchedule.Daily(2, 30, false).Next(Date("2026-03-28 12:00"), Amsterdam);
        Assert.Equal("2026-03-29", Text(spring)![..10]);   // 02:30 does not exist that night: it runs that night all the same
        Assert.Equal("2026-10-25 08:00 Sun", Text(TaskSchedule.Daily(8, 0, false).Next(Date("2026-10-24 12:00"), Amsterdam)));
        // the night the clock goes back is an hour longer: 25 hours between the two morning runs
        var a = TaskSchedule.Daily(8, 0, false).Next(Date("2026-10-24 07:00"), Amsterdam)!.Value;
        var b = TaskSchedule.Daily(8, 0, false).Next(a, Amsterdam)!.Value;
        Assert.Equal(TimeSpan.FromHours(25), b - a);   // 08:00 stays 08:00 on the clock
    }

    [Fact]
    public void Descriptions()
    {
        Assert.Equal("Elke werkdag om 08:05", TaskSchedule.Daily(8, 5, true).Description);
        Assert.Equal("Elk uur, van 08:00 tot 22:00", TaskSchedule.Every(1, 8, 22).Description);
        Assert.Equal("Elke vrijdag om 17:00", TaskSchedule.Weekly(6, 17, 0).Description);
        Settings.Shared.Language = "en";
        Assert.Equal("Every weekday at 08:05", TaskSchedule.Daily(8, 5, true).Description);
    }
}

public class TaskStoreTests : ChatTest
{
    private TaskStore Store() => new(Path.Combine(Root, Guid.NewGuid() + ".json"));

    [Fact]
    public void DueOnceAndThenTheNextRun()
    {
        var s = Store();
        var now = DateTimeOffset.Now;
        s.Upsert(new ScheduledTask { Title = "Agenda", Prompt = "Wat staat er vandaag?", Schedule = TaskSchedule.Every(1, 0, 0) }, now.AddSeconds(-3700));   // planned an hour ago: due
        Assert.True(s.Tasks[0].NextRun < now);
        Assert.Equal(["Agenda"], s.TakeDue(now).Select(t => t.Title));
        Assert.Empty(s.TakeDue(now));   // it comes once
        Assert.True(s.Tasks[0].NextRun > now);
        Assert.Equal(now, s.Tasks[0].LastRun);
        // and it is kept
        Assert.Equal(["Agenda"], new TaskStore(s.File).Tasks.Select(t => t.Title));
    }

    [Fact]
    public void MissedByTooMuchIsSkipped()
    {
        var s = Store();
        var now = DateTimeOffset.Now;
        s.Upsert(new ScheduledTask { Title = "Ochtend", Prompt = "x", Schedule = TaskSchedule.Daily(8, 0, false) }, now.AddDays(-3));
        Assert.Empty(s.TakeDue(now));   // three days late: not made up for
        Assert.NotNull(s.Tasks[0].LastSkipped);
        Assert.True(s.Tasks[0].NextRun > now);
    }

    [Fact]
    public void ASingleRunSwitchesItselfOff()
    {
        var s = Store();
        var now = DateTimeOffset.Now;
        s.Upsert(new ScheduledTask { Title = "Eén keer", Prompt = "x", Schedule = TaskSchedule.Once(now.AddSeconds(60)) }, now);
        Assert.Single(s.TakeDue(now.AddSeconds(61)));
        Assert.False(s.Tasks[0].Enabled);
        Assert.Null(s.Tasks[0].NextRun);
    }

    [Fact]
    public void OffMeansNoRun()
    {
        var s = Store();
        s.Upsert(new ScheduledTask { Title = "Uit", Prompt = "x", Schedule = TaskSchedule.Every(1, 0, 0), Enabled = false }, DateTimeOffset.Now.AddHours(-2));
        Assert.Null(s.Tasks[0].NextRun);
        Assert.Empty(s.TakeDue());
    }
}

public class TaskToolTests
{
    private static JsonObject A(params (string Key, JsonNode? Value)[] items) => new(items.Select(i => KeyValuePair.Create(i.Key, i.Value)));

    [Fact]
    public void WhatTheModelAsks()
    {
        var now = DateTimeOffset.Now;
        var t = TaskTool.Parse(A(("title", "Agenda"), ("prompt", "Wat staat er vandaag in mijn agenda?"), ("when", "weekdays"), ("time", "08:00")), now);
        Assert.Equal(TaskSchedule.Daily(8, 0, true), t.Schedule);
        Assert.Equal(TaskSchedule.Weekly(6, 17, 30), TaskTool.Parse(A(("title", "a"), ("prompt", "b"), ("when", "weekly"), ("weekday", "Vrijdag"), ("time", "17:30")), now).Schedule);
        Assert.Equal(TaskSchedule.Every(2, 8, 22), TaskTool.Parse(A(("title", "a"), ("prompt", "b"), ("when", "every"), ("hours", "2"), ("from", 8), ("to", 22)), now).Schedule);
        var once = TaskTool.Parse(A(("title", "a"), ("prompt", "b"), ("when", "once"), ("at", "2099-01-01T09:00")), now);
        Assert.Equal("once", once.Schedule.Kind);
        Assert.Equal(9, once.Schedule.At!.Value.ToLocalTime().Hour);
    }

    [Fact]
    public void WhatIsWrongIsSaid()
    {
        var now = DateTimeOffset.Now;
        Assert.Throws<TaskTool.Problem>(() => TaskTool.Parse(A(("title", "a"), ("prompt", "b"), ("when", "daily"), ("time", "25:00")), now));
        Assert.Throws<TaskTool.Problem>(() => TaskTool.Parse(A(("title", "a"), ("prompt", "b"), ("when", "once"), ("at", "2001-01-01T09:00")), now));   // the past
        Assert.Throws<TaskTool.Problem>(() => TaskTool.Parse(A(("title", ""), ("prompt", "b"), ("when", "daily"), ("time", "08:00")), now));
        Assert.Throws<TaskTool.Problem>(() => TaskTool.Parse(A(("title", "a"), ("prompt", "b"), ("when", "soms")), now));
        Assert.Null(TaskTool.Weekday(JsonValue.Create("someday")));
        Assert.Equal(1, TaskTool.Weekday(JsonValue.Create(1)));
    }
}

public class HeartbeatTests : ChatTest
{
    private static DateTimeOffset At(int hour, int minute = 0) => new(DateTime.Today.AddHours(hour).AddMinutes(minute));

    [Fact]
    public void OnlyInsideTheActiveHoursAndAfterTheInterval()
    {
        Assert.True(Heartbeat.IsDue(At(10), null, 60, 8, 22));
        Assert.False(Heartbeat.IsDue(At(23), null, 60, 8, 22));   // outside the active hours
        Assert.False(Heartbeat.IsDue(At(10, 30), At(10), 60, 8, 22));   // too soon
        Assert.True(Heartbeat.IsDue(At(11), At(10), 60, 8, 22));
        Assert.True(Heartbeat.IsDue(At(23), null, 30, 22, 6));   // a window past midnight
        Assert.True(Heartbeat.IsDue(At(3), null, 30, 0, 0));   // the whole day
        // off by default
        Assert.False(Heartbeat.IsDue(At(10)));
    }

    [Fact]
    public void NothingToReport()
    {
        Assert.True(Heartbeat.IsNothing("NIETS"));
        Assert.True(Heartbeat.IsNothing(" **Niets.** \n"));
        Assert.True(Heartbeat.IsNothing("NOTHING"));
        Assert.False(Heartbeat.IsNothing("Om 14:00 heb je een afspraak met de tandarts."));
        Assert.False(Heartbeat.IsNothing("Niets bijzonders, behalve dat de schijf vol is."));
    }

    [Fact]
    public void TheExampleChecklistCountsAsEmpty()
    {
        var file = Path.Combine(Root, "HEARTBEAT.md");
        Assert.Null(Heartbeat.Checklist(file));   // the example has only explanation: nothing to check yet
        Assert.True(File.Exists(file));           // it was written for the user to fill in
        File.WriteAllText(file, Heartbeat.Example + "\n- Staat er in het komende uur een afspraak?\n");
        Assert.NotNull(Heartbeat.Checklist(file));
    }
}
