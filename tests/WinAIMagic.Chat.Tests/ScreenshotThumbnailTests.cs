using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace WinAIMagic.Chat.Tests;

/// <summary>The owner wanted to watch along when the model takes a screenshot of a program or the C64 emulator: on the Mac the model
/// saved 37 screenshots with commands in one C64 conversation and looked at 9, and those 9 showed only while it worked.</summary>
public class ScreenshotThumbnailTests : ChatTest
{
    /// <summary>A 384 x 272 PNG, the size of a C64 screen in VICE.</summary>
    internal static byte[] Png(int width = 384, int height = 272)
    {
        var pixels = new byte[4 * width * height];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 0xCC; pixels[i + 1] = 0x4C; pixels[i + 2] = 0x4C; pixels[i + 3] = 255; }
        using var output = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        var bytes = new byte[output.Size];
        output.Seek(0);
        output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return bytes;
    }

    [Fact]
    public void TheImagesACommandMadeAreFound()
    {
        var folder = NewFolder("work");
        Directory.CreateDirectory(Path.Combine(folder, "a", "b", "c"));
        Directory.CreateDirectory(Path.Combine(folder, "met spatie"));
        WorkspaceTools.Current = folder;
        var old = Path.Combine(folder, "oud.png");
        File.WriteAllBytes(old, Png());
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        var start = DateTime.UtcNow;
        File.WriteAllBytes(Path.Combine(folder, "intro.png"), Png());
        File.WriteAllBytes(Path.Combine(folder, "a", "b", "kasteel.png"), Png());
        File.WriteAllBytes(Path.Combine(folder, "a", "b", "c", "te-diep.png"), Png());
        File.WriteAllText(Path.Combine(folder, "notes.png.txt"), "geen plaatje");
        var outside = Path.Combine(NewFolder("elders met spatie"), "shot.png");
        File.WriteAllBytes(outside, Png());
        var deep = Path.Combine(folder, "a", "b", "c", "genoemd.png");
        File.WriteAllBytes(deep, Png());

        var found = WorkspaceTools.NewImages(start, $"python c64run.py test.prg --shot \"{outside}\"\nscreenshot: a\\b\\c\\genoemd.png", 10);
        var names = found.Select(Path.GetFileName).ToHashSet();
        Assert.Equal(["genoemd.png", "intro.png", "kasteel.png", "shot.png"], names.Order());
        Assert.Contains(outside, found, StringComparer.OrdinalIgnoreCase);
        // at most `limit`, the newest
        Assert.Single(WorkspaceTools.NewImages(start, "", 1));
    }

    [Fact]
    public void APathInTempIsFoundThroughItsVariable()
    {
        WorkspaceTools.Current = NewFolder("work");
        var name = "wam-shot-" + Guid.NewGuid().ToString("N")[..8] + ".png";
        var file = Path.Combine(Path.GetTempPath(), name);
        var start = DateTime.UtcNow;
        File.WriteAllBytes(file, Png());
        try
        {
            Assert.Equal([file], WorkspaceTools.NewImages(start, $"python c64run.py x.prg --shot $env:TEMP\\{name}"), StringComparer.OrdinalIgnoreCase);
            Assert.Equal([file], WorkspaceTools.NewImages(start, $"--shot %TEMP%\\{name}"), StringComparer.OrdinalIgnoreCase);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task TheChatCopyIsSmall()
    {
        var file = Path.Combine(NewFolder("work"), "intro.png");
        File.WriteAllBytes(file, Png());
        var a = await ImageScaler.ThumbnailAsync(file);
        Assert.NotNull(a);
        Assert.Equal("intro.png", a.Filename);
        Assert.True(a.Jpeg.Length < 40_000, $"{a.Jpeg.Length} bytes");
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(a.Jpeg.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        Assert.Equal(360u, Math.Max(decoder.PixelWidth, decoder.PixelHeight));
    }

    [Fact]
    public void EachPictureShowsOnceWithItsNewestCopy()
    {
        static Attachment Shot(string name, byte b) => new() { Filename = name, Jpeg = [b] };
        var step1 = new ToolActivity { Label = "Uitvoeren: c64run.py", Done = true, Images = [Shot("intro.png", 1)] };
        var step2 = new ToolActivity { Label = "Uitvoeren: c64run.py", Done = true, Images = [Shot("intro.png", 2), Shot("kasteel.png", 3)] };
        List<AnswerPart> parts =
        [
            new(PartKind.Step, "1", Step: step1), new(PartKind.Looked, "l", Attachments: [Shot("geel.png", 4)]),
            new(PartKind.Text, "t", "tussendoor"), new(PartKind.Step, "2", Step: step2),
        ];
        var pictures = AnswerPart.Pictures(parts);
        Assert.Equal(["geel.png", "intro.png", "kasteel.png"], pictures.Select(p => p.Filename));
        Assert.Equal([2], pictures.First(p => p.Filename == "intro.png").Jpeg);
    }

    [Fact]
    public void AnOldStepWithoutImagesStillLoads()
    {
        var a = JsonSerializer.Deserialize<ToolActivity>("""{"Id":"4f9c8b1e-2d3a-4b5c-9d8e-7f6a5b4c3d2e","Label":"Uitvoeren: dir","Done":true,"Output":"a"}""");
        Assert.NotNull(a);
        Assert.Null(a.Images);
    }
}
