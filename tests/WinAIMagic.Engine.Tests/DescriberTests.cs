using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>Who writes the description for an image: the fast model, the heavy one only when there is no other. Names as on the Mac.</summary>
public class DescriberTests : EngineTest
{
    private static ModelInfo M(string id) => ModelCatalog.Shared.Model(id)!;
    private static readonly ModelInfo Heavy = M("glm-5.3-flash-compact-24"), Small = M("ministral-3-8b-q4"), Qwen = M("qwen3.6-35b-a3b-q4");

    [Fact]
    public void TheFastModelWritesIt()
    {
        var d = ImageDescriber.Choose(Heavy, new[] { Heavy, Qwen, Small }, "", heavyRunning: false, fastRunning: null, fits: true);
        Assert.Equal(Small.Id, d?.Model.Id);
        Assert.True(d?.OnFast);
        Assert.Equal("Snel", d?.Role);
    }

    [Fact]
    public void TheChosenFastModelWritesIt()
    {
        var d = ImageDescriber.Choose(Heavy, new[] { Heavy, Qwen, Small }, "qwen3.6-35b-a3b-q4", false, null, true);
        Assert.Equal("qwen3.6-35b-a3b-q4", d?.Model.Id);
    }

    [Fact]
    public void OnlyOneModelMeansTheHeavyOne()
    {
        var d = ImageDescriber.Choose(Heavy, new[] { Heavy }, "", false, null, true);
        Assert.Equal(Heavy.Id, d?.Model.Id);
        Assert.False(d?.OnFast);
        Assert.Equal("Zwaar", d?.Role);
    }

    [Fact]
    public void ARunningHeavyModelWithoutRoomWritesIt()
    {
        var d = ImageDescriber.Choose(Heavy, new[] { Heavy, Small }, "", heavyRunning: true, fastRunning: null, fits: false);
        Assert.Equal(Heavy.Id, d?.Model.Id);
        // with room the fast one still does it
        Assert.True(ImageDescriber.Choose(Heavy, new[] { Heavy, Small }, "", true, null, true)?.OnFast);
    }

    [Fact]
    public void AFastModelThatRunsWritesIt()
    {
        var d = ImageDescriber.Choose(Heavy, new[] { Heavy, Small }, "", true, fastRunning: Qwen, fits: false);
        Assert.Equal(Qwen.Id, d?.Model.Id);
        Assert.True(d?.OnFast);
    }

    [Fact]
    public void NoModelNoDescriber()
    {
        Assert.Null(ImageDescriber.Choose(null, Array.Empty<ModelInfo>(), "", false, null, true));
        // no heavy one chosen, but a small one downloaded: that one
        Assert.Equal(Small.Id, ImageDescriber.Choose(null, new[] { Small }, "", false, null, true)?.Model.Id);
    }
}
