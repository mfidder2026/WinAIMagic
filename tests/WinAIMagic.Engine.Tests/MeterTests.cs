using Xunit;

namespace WinAIMagic.Engine.Tests;

public class MeterScaleTests : EngineTest
{
    /// <summary>Text keeps its scale; image tokens make it grow, and the meters know an image is being made.</summary>
    [Fact]
    public void TheScaleGrowsWithImageTokens()
    {
        using var m = new LiveMetrics(() => null);
        Assert.Equal(30, m.Scale(30));
        Assert.False(m.MakingImage);
        m.ImageRate = 0;
        Assert.True(m.MakingImage);
        m.ImageRate = 450;
        m.TakeSample();
        m.TakeSample();
        Assert.Equal(500, m.Scale(30));
        Assert.True(m.Current!.TokensPerSec > 100);
        m.ImageRate = null;
        Assert.False(m.MakingImage);
    }
}
