using Xunit;

namespace WinAIMagic.Studio.Tests;

/// <summary>A small model (Ling-3.0-tiny) repeated one line until its token limit in 4 of 12 sections on the test PC. The app stops
/// such a section and keeps one copy of the line.</summary>
public sealed class LoopGuardTests : StudioTest
{
    private const string Loop = "- De werknemer die niet in aanmerking komt, kan dit doorgeven aan de HR-afdeling.\n";

    [Fact]
    public void ALineThatComesThreeTimesIsALoop()
    {
        var text = "Intro van de sectie.\n" + Loop + Loop + Loop + Loop;
        var at = LocalModel.LoopStart(text);
        Assert.NotNull(at);
        Assert.Equal("Intro van de sectie.\n" + Loop, text[..at!.Value]);
    }

    [Fact]
    public void OrdinaryTextIsNoLoop()
    {
        Assert.Null(LocalModel.LoopStart("Een.\nTwee regels die verschillen van elkaar.\nDrie regels die ook weer anders zijn.\n"));
        // short lines repeat in ordinary text (table rules, "Ja", empty list items)
        Assert.Null(LocalModel.LoopStart("| --- | --- |\n| a | b |\n| --- | --- |\n| c | d |\n| --- | --- |\n"));
        // twice is still allowed: a summary may repeat a sentence once
        Assert.Null(LocalModel.LoopStart(Loop + "Iets anders dat lang genoeg is om mee te tellen.\n" + Loop));
    }

    [Fact]
    public Task ARepeatingSectionIsCutAndSaidSo() => UiThread.Run(async () =>
    {
        Answer("TITEL: T\n# Een\n> X.\n# Twee\n> Z.\n", "Begin van de tekst.\n" + Loop + Loop + Loop + Loop + Loop);
        Studio.Make("Maak iets", false);
        await WaitUntilDone();
        Assert.Contains(Studio.Notes, n => n.Contains("herhaalde zichzelf"));
        Assert.DoesNotContain(Studio.Notes, n => n.Contains("te lang werd"));
        // one copy of the line stays in section Een
        var een = Studio.Text[..Studio.Text.IndexOf("# Twee", StringComparison.Ordinal)];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(een, "niet in aanmerking komt"));
        // the second engine gets the DRY sampler for every piece of writing
        Assert.All(Model.Requests, r => Assert.Equal(0.8, r.Body["dry_multiplier"]!.GetValue<double>()));
    });
}
