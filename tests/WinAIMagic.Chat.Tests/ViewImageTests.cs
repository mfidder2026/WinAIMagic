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

    [Fact]
    public async Task OnlyTheLastPicturesTheModelLookedAtAreSentAgain()
    {
        await Screenshot("shot.png");
        var a = await WorkspaceTools.ImageAsync(Call("shot.png"));
        var history = Enumerable.Range(1, 5).Select(i => new ChatMessage { Role = ChatRole.User, Text = $"beeld {i}", Attachments = [a], Automatic = true }).ToList();
        var o = new ChatClient().RequestMessages(history);
        Assert.Equal(1 + 5, o.Count);
        Assert.Equal(ChatClient.ImagesKept, o.Count(m => m!["content"] is JsonArray));
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
