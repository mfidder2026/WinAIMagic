using System.Text.Json;
using WinAIMagic.Engine;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>After a comparison the user's own model comes back (the Mac's 2144748), and a model measured while another program kept
/// the GPU busy is marked (3b96e58, 5a0f173).</summary>
public class BenchmarkRestoreTests
{
    [Fact]
    public void TheUsersOwnModelComesBackAfterwards()
    {
        // the user's model ran; the comparison left another running: stop that, start the user's again
        Assert.Equal((true, true), BenchmarkRunner.AfterRun("qwen", wasRunning: "qwen", nowRunning: "ling"));
        // nothing ran before: the last measured model stops, nothing starts
        Assert.Equal((true, false), BenchmarkRunner.AfterRun("qwen", wasRunning: null, nowRunning: "ling"));
        // the last model measured was the user's own: it keeps running
        Assert.Equal((false, false), BenchmarkRunner.AfterRun("qwen", wasRunning: "qwen", nowRunning: "qwen"));
        // the engine stopped during the run (a failed start): the user's own starts again
        Assert.Equal((false, true), BenchmarkRunner.AfterRun("qwen", wasRunning: "qwen", nowRunning: null));
    }

    [Fact]
    public void AMeasurementWithABusyGpuIsMarked()
    {
        var quiet = new BenchResult { ModelId = "a", ModelName = "A", OtherGpu = 12 };
        var busy = quiet with { OtherGpu = 80 };
        Assert.False(quiet.SharedGpu);
        Assert.True(busy.SharedGpu);
        Assert.False((quiet with { OtherGpu = null }).SharedGpu);    // a result of an older version
        Assert.Contains("\"otherGPU\":80", JsonSerializer.Serialize(busy));
    }
}
