using System.Net;
using System.Net.Sockets;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>A start with helpers must not wait for minutes on a helper that cannot be reached, and loading must show that it moves.</summary>
public class ClusterCheckTests : EngineTest
{
    private static ClusterController Cluster(Func<string, int, bool> reach)
    {
        var c = new ClusterController(FakeGpuBackend.Nvidia(16), browse: false) { ReachOverride = reach, ProbeOverride = _ => 8192 };
        return c;
    }

    [Fact]
    public async Task AHelperThatDoesNotAnswerStaysOutAndTheReasonIsSaid()
    {
        using var c = Cluster((host, _) => host == "10.0.0.2");
        c.AddManual("10.0.0.2");
        c.AddManual("10.0.0.3:50060");
        await Task.Delay(300);   // the probe of a helper added by hand runs in the background
        Assert.Equal(2, c.Active.Count);
        var left = c.CheckBeforeStart();
        Assert.Equal(["10.0.0.2:50052"], left.Select(h => h.Id));
        Assert.Equal("10.0.0.2:50052", c.RpcArgument);
        Assert.Contains("10.0.0.3:50060", c.Problem);
        Assert.Contains("firewall", c.Problem);
        Assert.True(c.Helpers.Single(h => h.Id == "10.0.0.3:50060").Unreachable);

        // it answers again: it takes part again
        c.ReachOverride = (_, _) => true;
        Assert.Equal(2, c.CheckBeforeStart().Count);
        Assert.Null(c.Problem);
        Assert.False(c.Helpers.Single(h => h.Id == "10.0.0.3:50060").Unreachable);

        // all answer: no problem left
        using var ok = Cluster((_, _) => true);
        ok.AddManual("10.0.0.4");
        await Task.Delay(300);
        ok.CheckBeforeStart();
        Assert.Null(ok.Problem);
    }

    [Fact]
    public void ReachableAsksThePortForReal()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(ClusterController.Reachable("127.0.0.1", port, TimeSpan.FromSeconds(2)));
        listener.Stop();
        Assert.False(ClusterController.Reachable("127.0.0.1", port, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TheTransferShowsAndAStallIsSaid()
    {
        using var c = Cluster((_, _) => true);
        ulong sent = 0;
        int ticks = 0;
        c.SentOverride = () => sent;
        var seen = new List<ulong>();
        c.Changed += () => { if (c.Transfer is { } t) seen.Add(t.Sent); };
        // 1 MB per tick for five ticks, then nothing: a stall after 0.2 s
        var watch = c.WatchTransfer(() => ticks < 15, expected: 50_000_000, tick: TimeSpan.FromMilliseconds(20), stallAfter: TimeSpan.FromMilliseconds(200));
        while (!watch.IsCompleted)
        {
            await Task.Delay(20);
            if (ticks < 5) sent += 1_000_000;
            ticks++;
        }
        await watch;
        Assert.Contains(seen, s => s >= 3_000_000);
        Assert.Null(c.Transfer);   // gone when loading ends
        Assert.Contains("niets meer naar de helpers", c.Problem);
    }

    [Fact]
    public void WhatGoesToTheHelpers()
    {
        // a 20 GB model, helpers of 6 and 10 GB beside a main PC that holds 16: half of it, 10 GB
        Assert.Equal(10_000_000_000UL, ClusterController.ExpectedTransfer(20_000_000_000, [6_000_000_000, 10_000_000_000], 16_000_000_000));
        // never more than they can hold
        Assert.Equal(4_000_000_000UL, ClusterController.ExpectedTransfer(100_000_000_000, [4_000_000_000], 1));
        Assert.Equal(0UL, ClusterController.ExpectedTransfer(20_000_000_000, [], 16_000_000_000));
    }
}
