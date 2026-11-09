using System.Diagnostics;
using System.Text.Json.Nodes;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>A conversation's place in the engine is saved when another one takes the model and after its answer, and read
/// back when it returns (SlotKeeper), against the Mac's stand-in for the engine's /slots endpoints (fake_engine.py).</summary>
public sealed class SlotKeeperTests : ChatTest
{
    private readonly Process server;
    private readonly int port;
    private readonly string dir;
    private static readonly HttpClient Http = new();

    public SlotKeeperTests()
    {
        dir = NewFolder("slots");
        port = McpFixtures.FreePort();
        var psi = new ProcessStartInfo(McpFixtures.Python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-I", McpFixtures.Script("fake_engine.py"), port.ToString(), dir }) psi.ArgumentList.Add(a);
        server = Process.Start(psi)!;
        Assert.Contains("READY", server.StandardOutput.ReadLine());
        Settings.Shared.KeepEngineMemory = true;
    }

    public override void Dispose()
    {
        try { server.Kill(true); } catch (InvalidOperationException) { }
        server.Dispose();
        base.Dispose();
    }

    private Uri Url => new($"http://127.0.0.1:{port}");

    private async Task<List<string>> SlotCalls()
    {
        var json = JsonNode.Parse(await Http.GetStringAsync(new Uri(Url, "stats")))!;
        return json["slots"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    private static string Id(Guid g) => g.ToString("D").ToUpperInvariant();

    private static (ConversationStore Store, Conversation A, Conversation B) TwoConversations(string? workspace = null)
    {
        var store = new ConversationStore();
        var a = new Conversation { Workspace = workspace };
        var b = new Conversation { Workspace = workspace };
        store.Insert(b);
        store.Insert(a);
        return (store, a, b);
    }

    private static async Task Answer(ChatClient chat, string text, Guid id, ConversationStore store, Uri url)
    {
        chat.Send(text, [], id, store, url);
        await Until(() => !chat.IsBusy(id));
    }

    [Fact]
    public async Task AConversationThatComesBackIsRestored()
    {
        var (store, a, b) = TwoConversations();
        var keeper = new SlotKeeper(dir);
        keeper.Started(Url, "model-c65536-v1-s0");
        var chat = new ChatClient();

        await Answer(chat, "hallo", a.Id, store, Url);
        Assert.Equal(["save " + Id(a.Id)], await SlotCalls());     // an answer is saved when it is done

        await Answer(chat, "hoi", b.Id, store, Url);
        Assert.Equal(["save " + Id(a.Id), "save " + Id(b.Id)], await SlotCalls());     // A was saved already: B's turn does not save it again

        await Answer(chat, "verder", a.Id, store, Url);
        Assert.Equal(["save " + Id(a.Id), "save " + Id(b.Id), "restore " + Id(a.Id), "save " + Id(a.Id)], await SlotCalls());
        Assert.True(File.Exists(Path.Combine(dir, SlotKeeper.FileName("model-c65536-v1-s0", b.Id))));

        // the same conversation again: no restore, only its save
        await Answer(chat, "en nog iets", a.Id, store, Url);
        Assert.Equal(5, (await SlotCalls()).Count);
        Assert.False(keeper.HasUnsaved);    // saved after its answer: a quit has nothing left to do
        GC.KeepAlive(keeper);
    }

    /// <summary>The notes in WINAIMAGIC.md change while a conversation goes on (the model writes lessons there): its system
    /// prompt stays as it began, so the engine reuses what it read and a restored state still fits. A new conversation sees them.</summary>
    [Fact]
    public async Task AConversationKeepsTheNotesItBeganWith()
    {
        Settings.Shared.WorkspaceTools = true;
        Settings.Shared.LearnFromChats = false;
        var ws = NewFolder("werkmap");
        var notes = Path.Combine(ws, "WINAIMAGIC.md");
        File.WriteAllText(notes, "Gebruik VICE.");
        var (store, a, b) = TwoConversations(ws);
        var chat = new ChatClient();

        await Answer(chat, "hallo", a.Id, store, Url);
        File.WriteAllText(notes, "Gebruik VICE.\n\n## Geleerd\n- iets nieuws");
        await Answer(chat, "verder", a.Id, store, Url);
        await Answer(chat, "nieuw", b.Id, store, Url);

        var systems = JsonNode.Parse(await Http.GetStringAsync(new Uri(Url, "systems")))!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(3, systems.Count);
        Assert.Equal(systems[0], systems[1]);                 // the same conversation keeps its start
        Assert.DoesNotContain("iets nieuws", systems[1]);
        Assert.Contains("iets nieuws", systems[2]);           // a new conversation reads the notes as they are now
    }

    [Fact]
    public async Task AnotherEngineSetupDoesNotRestoreAndOffSavesNothing()
    {
        var keeper = new SlotKeeper(dir);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        keeper.Started(Url, "model-c65536-v1-s0");
        await keeper.EnterAsync(a);
        await keeper.EnterAsync(b);        // saves a
        // the engine restarts with another context: a's state does not fit it
        keeper.Started(Url, "model-c32768-v1-s0");
        await keeper.EnterAsync(a);
        Assert.Equal(["save " + Id(a)], await SlotCalls());
        // switched off: no saving or restoring at all
        Settings.Shared.KeepEngineMemory = false;
        await keeper.EnterAsync(b);
        Assert.False(keeper.HasUnsaved);
        Assert.Single(await SlotCalls());
    }

    [Fact]
    public async Task QuittingSavesTheConversationInTheEngine()
    {
        var keeper = new SlotKeeper(dir);
        var a = Guid.NewGuid();
        keeper.Started(Url, "k");
        await keeper.EnterAsync(a);
        var lines = new List<string>();
        keeper.Note = lines.Add;
        keeper.SaveBlocking();
        Assert.Single(lines);
        Assert.Contains("save (afsluiten)", lines[0]);
        Assert.Equal(["save " + Id(a)], await SlotCalls());
        Assert.False(keeper.HasUnsaved);
    }

    [Fact]
    public async Task DocumentsSaveTheChatsConversationFirst()
    {
        var keeper = new SlotKeeper(dir);
        var a = Guid.NewGuid();
        keeper.Started(Url, "k");
        await keeper.EnterAsync(a);
        await SlotKeeper.YieldTo(new Uri($"http://localhost:{port}"));
        Assert.Equal(["save " + Id(a)], await SlotCalls());
        Assert.Null(keeper.Current);
        await SlotKeeper.YieldTo(new Uri("http://127.0.0.1:1"));
        Assert.Single(await SlotCalls());   // another engine leaves it alone
    }

    [Fact]
    public void OldStatesGoFirstAndADeletedConversationTakesItsOwn()
    {
        var keeper = new SlotKeeper(dir);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        for (int n = 0; n < ids.Count; n++)
        {
            var f = Path.Combine(dir, SlotKeeper.FileName("m", ids[n]));
            File.WriteAllBytes(f, new byte[1000]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddSeconds(n - 10));
        }
        keeper.Prune(2500);
        Assert.False(File.Exists(Path.Combine(dir, SlotKeeper.FileName("m", ids[0]))));    // the oldest went
        Assert.Equal(2000, keeper.Used);
        keeper.Forget(ids[2]);
        Assert.Equal(1000, keeper.Used);
    }

    [Fact]
    public void TheLogSaysWhatWasSavedHowLargeAndHowFast()
    {
        var id = Guid.Parse("80b56923-0000-0000-0000-000000000000");
        Assert.Equal("WinAIMagic: gesprek 80B56923 save: 50406 tokens, 210 MB in 0.07 s",
            SlotKeeper.Describe("save", id, true, """{"id_slot":0,"n_saved":50406,"n_written":210000000}""", 0.07));
        Assert.Equal("WinAIMagic: gesprek 80B56923 restore mislukt: file not found",
            SlotKeeper.Describe("restore", id, false, """{"error":{"message":"file not found"}}""", 0.01));
    }
}
