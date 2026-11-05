using Xunit;

namespace WinAIMagic.Engine.Tests;

public class MeterStopTests : EngineTest
{
    /// <summary>The gauge read 67 tok/s with nothing running: the last speed of an engine that stopped stayed. It goes to 0.</summary>
    [Fact]
    public void AStoppedEngineLeavesNoSpeedOnTheGauge()
    {
        using var m = new LiveMetrics(() => null);
        var rate = typeof(LiveMetrics).GetField("engineRate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        rate.SetValue(m, 67.0);
        for (int i = 0; i < 4; i++) m.TakeSample();
        Assert.Equal(0, m.Current!.TokensPerSec);
        Assert.False(m.EngineBusy);
    }
}
