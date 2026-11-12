using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>The model checks a program with a window by looking at a screenshot of it (view_image): a 6-hour C64 run on the Mac said
/// "I cannot read the screenshot myself" and never saw the ?OUT OF DATA ERROR on the emulator's screen.</summary>
public class ViewImageTests : ChatTest
{
    private readonly string dir;

    public ViewImageTests()
    {
        dir = Path.Combine(Root, "werk");
        Directory.CreateDirectory(dir);
        Settings.Shared.WorkspaceTools = true;
        Settings.Shared.Workspace = dir;
        WorkspaceTools.Current = dir;
    }

    /// <summary>A PNG the size of a VICE screenshot (384 × 272).</summary>
    private async Task Screenshot(string name)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = new byte[384 * 272 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 80; pixels[i + 2] = 60; pixels[i + 3] = 255; }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 384, 272, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes);
    }

    private static ToolCall Call(string path) => new("v", WorkspaceTools.ImageTool, new JsonObject { ["path"] = path }.ToJsonString());

    private static async Task<(uint W, uint H)> Size(byte[] jpeg)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var d = await BitmapDecoder.CreateAsync(stream);
        return (d.PixelWidth, d.PixelHeight);
    }

    [Fact]
    public async Task ASmallScreenshotIsEnlargedWithSharpPixels()
    {
        await Screenshot("shot.png");
        var a = await WorkspaceTools.ImageAsync(Call("shot.png"));
        Assert.Equal((768u, 544u), await Size(a.Jpeg));
        Assert.Equal("shot.png", a.Filename);
    }

    [Fact]
    public async Task OnlyImagesInsideTheWorkspace()
    {
        await Assert.ThrowsAsync<ToolError>(() => WorkspaceTools.ImageAsync(Call(@"C:\Windows\win.ini")));
        await Assert.ThrowsAsync<ToolError>(() => WorkspaceTools.ImageAsync(Call("missing.png")));
        await File.WriteAllTextAsync(Path.Combine(dir, "tekst.png"), "geen plaatje");
        await Assert.ThrowsAsync<ToolError>(() => WorkspaceTools.ImageAsync(Call("tekst.png")));
    }

    [Fact]
    public void TheToolIsOfferedOnlyWithVision()
    {
        List<string> Names() => ChatClient.CurrentTools().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        ChatClient.SeesImages = () => false;
        Assert.DoesNotContain("view_image", Names());
        ChatClient.SeesImages = () => true;
        Assert.Contains("view_image", Names());
    }

    /// <summary>Each look as the run makes it: a call of view_image, its result, and the picture as a message of the app.</summary>
    private static List<ChatMessage> Look(int n, Attachment a) =>
    [
        new() { Role = ChatRole.Assistant, ToolCalls = [new ToolCall($"v{n}", WorkspaceTools.ImageTool, "{\"path\":\"shot.png\"}")] },
        new() { Role = ChatRole.Tool, Text = "shot.png bekeken", ToolCallId = $"v{n}" },
        new() { Role = ChatRole.User, Text = "Hier is de afbeelding die je opvroeg: shot.png.", Attachments = [a], Automatic = true },
    ];

    private static int Pictures(JsonArray o) => o.Count(m => m!["content"] is JsonArray);

    /// <summary>Only the last 3 looks went as pictures. A new look turned an old one into text far back in the conversation, and
    /// Qwen3.6 read the whole conversation again each time on the Mac: 82K, 110K and 144K tokens (3, 5 and 11 minutes) in one C64 run.</summary>
    [Fact]
    public async Task APictureTheModelLookedAtStaysAndANewLookChangesNothingBefore()
    {
        await Screenshot("shot.png");
        var a = await WorkspaceTools.ImageAsync(Call("shot.png"));
        ChatClient.SeesImages = () => true;
        var chat = new ChatClient();
        var history = new List<ChatMessage> { new() { Role = ChatRole.User, Text = "Maak een C64-spel en kijk of het werkt." } };
        for (int n = 1; n <= 5; n++) history.AddRange(Look(n, a));
        var before = chat.RequestMessages(history);
        Assert.Equal(5, Pictures(before));
        Assert.Equal(1 + 1 + 5 * 3, before.Count);

        history.AddRange(Look(6, a));
        var after = chat.RequestMessages(history);
        Assert.Equal(6, Pictures(after));
        // what the engine already read is sent the same, character for character: it reads only the new look
        Assert.Equal(new JsonArray([.. before.Select(m => m!.DeepClone())]).ToJsonString(),
            new JsonArray([.. after.Take(before.Count).Select(m => m!.DeepClone())]).ToJsonString());
    }

    /// <summary>A conversation saved by 1.14.12 sent its looks but the last 3 as text. After the update it sends exactly that again,
    /// so the engine's saved state still fits; a new look stays a picture and changes nothing before it.</summary>
    [Fact]
    public async Task AConversationOfTheOlderVersionSendsWhatItSentBefore()
    {
        await Screenshot("shot.png");
        var a = await WorkspaceTools.ImageAsync(Call("shot.png"));
        ChatClient.SeesImages = () => true;
        var c = new Conversation { Messages = [new ChatMessage { Role = ChatRole.User, Text = "Maak een C64-spel en kijk of het werkt." }] };
        for (int n = 1; n <= 5; n++) c.Messages.AddRange(Look(n, a));
        // as 1.14.12 saved it: without the field
        var saved = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new List<Conversation> { c }, ChatJson.Options))!.AsArray();
        Assert.True(saved[0]!.AsObject().Remove("looksKept"));
        File.WriteAllText(Paths.ConversationsFile, saved.ToJsonString());

        var store = new ConversationStore();
        var loaded = store.Find(c.Id)!;
        Assert.True(loaded.LooksKept);
        var chat = new ChatClient();
        var before = chat.RequestMessages(loaded.Messages);
        Assert.Equal(3, Pictures(before));
        Assert.Equal(2, before.Count(m => m!["content"] is JsonValue v && v.GetValue<string>() is var t && t.StartsWith('(') && t.Contains("shot.png")));

        var after = chat.RequestMessages([.. loaded.Messages, .. Look(6, a)]);
        Assert.Equal(4, Pictures(after));
        Assert.Equal(new JsonArray([.. before.Select(m => m!.DeepClone())]).ToJsonString(),
            new JsonArray([.. after.Take(before.Count).Select(m => m!.DeepClone())]).ToJsonString());

        // loading it again (saved by this version) changes nothing more
        store.Update(c.Id, x => x.Messages.AddRange(Look(6, a)));
        store.Save();
        var again = new ConversationStore().Find(c.Id)!;
        Assert.Equal(4, Pictures(chat.RequestMessages(again.Messages)));
    }

    [Fact]
    public void ANewConversationKeepsItsLooks()
    {
        Assert.True(new Conversation().LooksKept);
        var c = new Conversation();
        c.KeepLooksAsSent();
        Assert.True(c.LooksKept);
    }

    /// <summary>The engine runs without vision: a picture in the conversation goes as a line of text, or the engine refuses this
    /// and every later request of the conversation (a Telegram photo, a picture dropped on the chat).</summary>
    [Fact]
    public async Task WithoutVisionAPictureGoesAsText()
    {
        await Screenshot("shot.png");
        var a = await WorkspaceTools.ImageAsync(Call("shot.png"));
        ChatClient.SeesImages = () => false;
        var o = new ChatClient().RequestMessages([new ChatMessage { Role = ChatRole.User, Text = "wat staat hierop?", Attachments = [a] }]);
        Assert.All(o, m => Assert.IsAssignableFrom<JsonValue>(m!["content"]));
        var text = o[^1]!["content"]!.GetValue<string>();
        Assert.Contains("shot.png", text);
        Assert.EndsWith("wat staat hierop?", text);
    }

    /// <summary>In the loop: the picture follows the tool results as a message of the app, and the tool result says so.</summary>
    [Fact]
    public async Task ThePictureFollowsTheToolResults()
    {
        await Screenshot("shot.png");
        ChatClient.SeesImages = () => true;
        Settings.Shared.AutoApprove = true;
        var engine = new FakeEngine(
            () => FakeEngine.Call("view_image", new JsonObject { ["path"] = "shot.png" }),
            () => FakeEngine.Say("KLAAR: ik zie een rood scherm."));
        var store = new ConversationStore();
        var c = new Conversation { Workspace = dir };
        store.Conversations = [c];
        var chat = new ChatClient(engine.Client);
        chat.Send("Kijk naar shot.png", [], c.Id, store, Base);
        await chat.CompletionOf(c.Id);
        var result = c.Messages.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal("The image follows in the next message.", result.Text);
        var look = c.Messages[c.Messages.IndexOf(result) + 1];
        Assert.Equal(ChatRole.User, look.Role);
        Assert.True(look.Automatic);
        Assert.Equal("shot.png", look.Attachments.Single().Filename);
        Assert.IsType<JsonArray>(engine.Requests[1]["messages"]!.AsArray().Last()!["content"]);
    }
}

/// <summary>The user follows the work in the step list above the input field; a model that forgets to tick steps off is reminded.</summary>
public class StepReminderTests
{
    private static readonly List<WorkStep> Open = [new("Code schrijven", "done"), new("Testen in VICE", "todo")];

    [Fact]
    public void AFrozenListIsRemindedOf()
    {
        Assert.Null(WorkSteps.Reminder(Open, WorkSteps.RemindAfter - 1));
        var text = WorkSteps.Reminder(Open, WorkSteps.RemindAfter);
        Assert.NotNull(text);
        Assert.Contains("task_list", text);
        Assert.Contains("[todo] Testen in VICE", text);
    }

    [Fact]
    public void NoReminderWithoutOpenSteps()
    {
        Assert.Null(WorkSteps.Reminder(null, 20));
        Assert.Null(WorkSteps.Reminder(Open.Select(s => s with { Status = "done" }).ToList(), 20));
    }
}
