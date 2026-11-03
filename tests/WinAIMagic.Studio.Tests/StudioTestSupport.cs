using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using WinAIMagic.Studio;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinAIMagic.Studio.Tests;

/// Answers like a local model, by what the request asks. It also remembers the requests.
public sealed class FakeModel : HttpMessageHandler
{
    public sealed record Request(string Host, JsonObject Body)
    {
        public string System => Body["messages"]![0]!["content"]!.GetValue<string>();
        public string User => Body["messages"]![1]!["content"]!.GetValue<string>();
    }

    public List<Request> Requests { get; } = [];
    public Func<string, string, string> Answer { get; set; } = (_, _) => "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "";
        if (!host.EndsWith(".model.test", StringComparison.Ordinal)) throw new HttpRequestException("connection refused");
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        var r = new Request(host, body);
        Requests.Add(r);
        var text = Answer(r.System, r.User);
        var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text }, ["finish_reason"] = "stop" }) };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + chunk.ToJsonString() + "\n\ndata: [DONE]\n\n", Encoding.UTF8) };
    }
}

/// The one thread a screen has: what the studio does after an await must come back here.
public static class UiThread
{
    private sealed class Pump : SynchronizationContext
    {
        public readonly BlockingCollection<Action> Queue = [];
        public override void Post(SendOrPostCallback d, object? state) => Queue.Add(() => d(state));
    }

    private static readonly Pump Context = Start();

    public static int Id { get; private set; }

    private static Pump Start()
    {
        var pump = new Pump();
        var thread = new Thread(() =>
        {
            Id = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(pump);
            foreach (var work in pump.Queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = "ui" };
        thread.Start();
        return pump;
    }

    public static Task Run(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Context.Post(async _ =>
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception e) { done.SetException(e); }
        }, null);
        return done.Task;
    }
}

/// Each test: its own app data, default settings, Dutch (the fake model recognizes the requests by their Dutch text), no network.
public abstract class StudioTest : IDisposable
{
    protected readonly string Root;
    protected readonly string Folder;
    protected readonly FakeModel Model = new();
    protected readonly DocumentStudio Studio;
    protected readonly List<string> Searched = [];

    /// What a model that reads a requirements attachment answers when it plans.
    protected const string Plan = "TITEL: AQS Citrix\nOPBOUW: eisen.txt\n# Inleiding\n> Waarom deze scan.\n# Bevindingen\n> Per thema.\n## Beveiliging\n> MFA.\nZOEK: eisen BIO aan MFA\nZOEK: citrix beveiliging Gemeente Voorbeeld\n";
    protected const string PlanWithoutSearch = "TITEL: AQS Citrix\nOPBOUW: eisen.txt\n# Inleiding\n> Waarom deze scan.\n# Bevindingen\n";

    protected StudioTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "wam-studio-" + Guid.NewGuid().ToString("N")[..12]);
        Folder = Path.Combine(Root, "project");
        Directory.CreateDirectory(Folder);
        Paths.AppData = Path.Combine(Root, "app");
        Settings.Shared = Settings.Load();
        Settings.Shared.Language = "nl";
        LocalModel.Http = new HttpClient(Model) { Timeout = Timeout.InfiniteTimeSpan };
        Studio = NewStudio(Folder);
        Studio.Endpoint = (role, _) => Task.FromResult((new Uri(role == ModelRole.Fast ? "http://fast.model.test" : "http://heavy.model.test"), role == ModelRole.Fast ? "snel" : "zwaar"));
        Studio.SearchWeb = (q, _) =>
        {
            Searched.Add(q);
            return Task.FromResult(new List<WebTools.Hit>
            {
                new("Bron A", "https://a.example/1", ""),
                new("Bron B", "https://b.example/2", ""),
                new("Kapot", "https://kapot.example", ""),
                new("Ruis", "https://ruis.example", ""),
            });
        };
        Studio.FetchPage = (url, _) =>
        {
            if (url.Contains("kapot")) throw new HttpRequestException("cannot find host");
            if (url.Contains("ruis")) return Task.FromResult(("Ruis", "Recept voor appeltaart."));
            return Task.FromResult((url.Contains("a.example") ? "Bron A" : "Bron B", $"Inhoud van {url}: MFA is verplicht."));
        };
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Settings.Shared = Settings.Load();
        try { Directory.Delete(Root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Without the Recycle Bin of whoever runs the tests.
    protected static DocumentStudio NewStudio(string? folder = null, string? root = null) => new(folder, root) { Trash = File.Delete };

    protected static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sjabloon.docx");

    protected async Task WaitUntilDone()
    {
        var finished = await Task.WhenAny(Studio.Completion, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(Studio.Completion, finished);
        await finished;
        Assert.False(Studio.Busy);
    }

    protected void Attach(string name, string text)
    {
        var dir = Path.Combine(Root, "bron-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var note = Path.Combine(dir, name);
        File.WriteAllText(note, text);
        Studio.AddAttachment(note);
    }

    protected string Named(string name)
    {
        var dir = Path.Combine(Root, "kopie-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.Copy(Fixture, path);
        return path;
    }

    /// A model that plans with `plan`, reads pages, and writes.
    protected void Answer(string plan, string section = "Tekst [1].") => Model.Answer = (system, user) =>
    {
        if (system.Contains("hoe een document eruit moet zien")) return plan;
        if (system.Contains("webpagina")) return user.Contains("appeltaart") ? "NIETS" : "MFA is verplicht.";
        if (system.Contains("voorblad")) return "";
        return section;
    };
}
