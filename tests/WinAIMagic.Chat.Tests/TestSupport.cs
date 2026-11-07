using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WinAIMagic.Chat;
using WinAIMagic.Core.Support;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinAIMagic.Chat.Tests;

/// <summary>Each test: its own app data, default settings, Dutch, no web, no folder, and no skills or system management.</summary>
public abstract class ChatTest : IDisposable
{
    protected readonly string Root;
    protected static readonly Uri Base = new("http://127.0.0.1:8080");

    protected ChatTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "wam-chat-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Paths.AppData = Path.Combine(Root, "app");
        Settings.Shared = Settings.Load();
        Settings.Shared.Language = "nl";
        Settings.Shared.WebSearch = false;
        // skills and system management are on by default; the older tests describe the requests without them
        Settings.Shared.Skills = false;
        Settings.Shared.SystemManagement = false;
        Settings.Shared.AppTools = false;
        WorkspaceTools.Current = null;
        DeveloperTools.MissingOverride = [];
        CommandRunner.ShellOverride = null;
        WebTools.Http = new HttpClient(new FakeWeb());
        SearchRoute.Shared = new SearchRoute(0);
    }

    protected string NewFolder(string name)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    protected static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("waited too long");
            await Task.Delay(20);
        }
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        WorkspaceTools.Current = null;
        try { Directory.Delete(Root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Plays the engine: stream requests get the next answer of the script, others the Complete text.</summary>
public sealed class FakeEngine : HttpMessageHandler
{
    private readonly Queue<Func<JsonObject, HttpResponseMessage>> script = new();
    public Func<JsonObject, HttpResponseMessage>? Always { get; set; }
    public Func<JsonObject, string?> Complete { get; set; } = _ => null;
    public List<JsonObject> Requests { get; } = [];
    public List<JsonObject> CompleteRequests { get; } = [];
    public List<string?> Authorization { get; } = [];
    public List<Uri> Urls { get; } = [];

    public FakeEngine(params Func<HttpResponseMessage>[] answers)
    {
        foreach (var a in answers) script.Enqueue(_ => a());
    }

    public void Then(Func<JsonObject, HttpResponseMessage> answer) => script.Enqueue(answer);

    public HttpClient Client => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        lock (this)
        {
            Urls.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (body["stream"]?.GetValue<bool>() != true)
            {
                CompleteRequests.Add(body);
                var text = Complete(body);
                if (text == null) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var answer = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = text } }) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString()) };
            }
            Requests.Add(body);
            if (script.TryDequeue(out var next)) return next(body);
            if (Always != null) return Always(body);
        }
        throw new InvalidOperationException("the script ended, request " + Requests.Count);
    }

    // answers

    public static string Chunk(JsonObject delta, string? finish = null) => "data: " + new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta, ["finish_reason"] = finish }),
    }.ToJsonString();

    private static string Ending(string finish, int? total)
    {
        var last = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = finish }),
            ["timings"] = new JsonObject { ["predicted_per_second"] = 21.5, ["prompt_per_second"] = 300.25 },
        };
        if (total != null) last["usage"] = new JsonObject { ["total_tokens"] = total };
        return "data: " + last.ToJsonString();
    }

    public static HttpResponseMessage Sse(params string[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, "text/event-stream"),
    };

    public static HttpResponseMessage Say(string text, string finish = "stop", int? total = null, string reasoning = "")
    {
        var delta = new JsonObject { ["content"] = text };
        if (reasoning.Length > 0) delta["reasoning_content"] = reasoning;
        return Sse(Chunk(delta), "", Ending(finish, total), "", "data: [DONE]");
    }

    public static HttpResponseMessage Call(string name, JsonObject args, string id = "call_1", string finish = "tool_calls", int? total = null)
    {
        var delta = new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args.ToJsonString() },
            }),
        };
        return Sse(Chunk(delta), Ending(finish, total), "data: [DONE]");
    }

    public static HttpResponseMessage Error(int status, string text) => new((HttpStatusCode)status) { Content = new StringContent(text) };

    /// <summary>The chunks come in one by one, with a pause, the way an engine writes.</summary>
    public static HttpResponseMessage Slow(IEnumerable<string> pieces, TimeSpan pause)
    {
        var pipe = new Pipe();
        _ = Task.Run(async () =>
        {
            foreach (var piece in pieces)
            {
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Chunk(new JsonObject { ["content"] = piece }) + "\n\n"));
                await Task.Delay(pause);
            }
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Ending("stop", 10) + "\n\ndata: [DONE]\n\n"));
            await pipe.Writer.CompleteAsync();
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
    }
}

/// <summary>Plays the web, by host; no test goes to the network.</summary>
public sealed class FakeWeb : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => throw new HttpRequestException("no network in tests");
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
        return Answer(request);
    }
}

/// <summary>A single thread with a message queue: the UI thread in the tests.</summary>
public sealed class OwnerThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<Action> queue = [];
    private readonly Thread thread;

    public OwnerThread()
    {
        thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var work in queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true };
        thread.Start();
    }

    public int ThreadId => thread.ManagedThreadId;
    public override void Post(SendOrPostCallback d, object? state) => queue.Add(() => d(state));
    public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("never wait for the owner");
    public void Run(Action work) => queue.Add(work);

    public void Dispose() => queue.CompleteAdding();
}
