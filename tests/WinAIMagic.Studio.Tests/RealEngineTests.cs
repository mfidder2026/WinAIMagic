using WinAIMagic.Chat;
using WinAIMagic.Studio;
using Xunit;
using Xunit.Abstractions;

namespace WinAIMagic.Studio.Tests;

/// <summary>The whole pipeline against a running engine, only with WAM_REAL_URL set: a real draft and .docx in the house style.
/// WAM_REAL_OUT receives a copy of the result for opening in Word.</summary>
public class RealEngineTests(ITestOutputHelper log) : StudioTest
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("WAM_REAL_URL");

    [Fact]
    public Task ADocumentInTheHouseStyle() => UiThread.Run(async () =>
    {
        if (Url == null) return;
        LocalModel.Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Studio.Endpoint = (_, _) => Task.FromResult((new Uri(Url), "real"));
        Studio.ContextLimit = () => 65_536;
        Studio.UseHouseStyle(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sjabloon.docx"));
        var started = DateTime.UtcNow;
        Studio.Make("Schrijf een korte notitie over het vervangen van een verouderd zaaksysteem bij een gemeente: aanleiding, aanpak en risico's. Houd het kort.", false);
        while (Studio.Busy && DateTime.UtcNow - started < TimeSpan.FromMinutes(50)) await Task.Delay(1000);
        foreach (var s in Studio.Steps) log.WriteLine($"step: {s.Text} [{s.State}]");
        log.WriteLine($"minutes {(DateTime.UtcNow - started).TotalMinutes:F1}, problem: {Studio.Problem}");
        Assert.Null(Studio.Problem);
        Assert.True(Studio.Text.Length > 200, Studio.Text);
        log.WriteLine(Studio.Text[..Math.Min(500, Studio.Text.Length)]);

        Studio.Deliver();
        Assert.Null(Studio.Problem);
        Assert.NotNull(Studio.Report);
        log.WriteLine("validator: " + string.Join(" | ", Studio.Report!.Problems));
        var docx = Directory.GetFiles(Folder, "*.docx").Single();
        log.WriteLine("output: " + docx);
        if (Environment.GetEnvironmentVariable("WAM_REAL_OUT") is { } copy) File.Copy(docx, copy, true);
    });
}
