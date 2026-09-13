using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PassiveHudHostLeaseTests
{
    private static readonly DateTime Start = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
    private static JObject Frame(int generation = 1, int publishedAt = 0, bool visible = true) =>
        PassiveHudHostLease.CreateFrame(new JObject {
            ["schema"] = 1, ["visible"] = visible, ["kind"] = "speedometer",
            ["speed"] = 42, ["units"] = "MPH", ["gear"] = "3", ["manual"] = false, ["notice"] = ""
        }, generation, Start.AddMilliseconds(publishedAt));

    [Fact]
    public void RealJsonRoundTripRetainsUtcTimestampAndRenewsLease()
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        var frame = JObject.Parse(Frame(publishedAt: 500).ToString());
        Assert.Equal(JTokenType.Date, frame["hostPublishedUtc"]!.Type);
        Assert.True(lease.Accept(frame, Start.AddMilliseconds(500), 500));
        Assert.False(lease.ShouldHide(1000));
        Assert.True(lease.ShouldHide(1500));
    }

    [Fact]
    public void PausedProducerExpiresNativeLeaseEvenWithNoScriptOrBrowserTick()
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        Assert.True(lease.Accept(Frame(), Start, 0));
        Assert.True(lease.AllowsPresentation(999));
        Assert.False(lease.ShouldHide(999));
        Assert.True(lease.ShouldHide(1000));
        Assert.False(lease.AllowsPresentation(1000));
        Assert.False(lease.ShouldHide(1001));
        Assert.False(lease.Accept(Frame(publishedAt: 1100), Start.AddMilliseconds(1100), 1100));
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 1100);
        Assert.False(lease.AllowsPresentation(1100));
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 2, 1100);
        Assert.True(lease.Accept(Frame(2, 1100), Start.AddMilliseconds(1100), 1100));
    }

    [Fact]
    public void FreshFramesKeepHudAliveWithoutAnyMenuInteraction()
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        for (var now = 0; now <= 30000; now += 100)
        {
            Assert.True(lease.Accept(Frame(publishedAt: now), Start.AddMilliseconds(now), now));
            Assert.False(lease.ShouldHide(now));
        }
        Assert.True(lease.ShouldHide(31000));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(900, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    [InlineData(1001, false)]
    [InlineData(-1, false)]
    public void TransportDelayConsumesLeaseInsteadOfRenewingOldQueuedFrames(int age, bool accepted)
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        Assert.Equal(accepted, lease.Accept(Frame(publishedAt: -age), Start, 0));
        if (accepted) Assert.True(lease.ShouldHide(1000 - age));
    }

    [Fact]
    public void WrongGenerationAndReplaysCannotRenewOrHideCurrentHud()
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 2, 0);
        Assert.True(lease.Accept(Frame(2), Start, 0));
        Assert.False(lease.Accept(Frame(1, 500, false), Start.AddMilliseconds(500), 500));
        Assert.False(lease.Accept(Frame(3, 500), Start.AddMilliseconds(500), 500));
        Assert.False(lease.Accept(Frame(2), Start.AddMilliseconds(900), 900));
        Assert.True(lease.ShouldHide(1000));
    }

    [Theory]
    [InlineData(HostSurfaceMode.None)]
    [InlineData(HostSurfaceMode.About)]
    [InlineData(HostSurfaceMode.Initializing)]
    public void MenuOrStartupPreemptionCannotBeHiddenByOldHudTimer(string mode)
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        lease.Accept(Frame(), Start, 0);
        lease.BeginSurface(mode, 2, 500);
        Assert.False(lease.Accept(Frame(1, 600, false), Start.AddMilliseconds(600), 600));
        Assert.False(lease.ShouldHide(10000));
    }

    [Fact]
    public void ExplicitHideAndDisconnectRevokeLease()
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        Assert.True(lease.Accept(Frame(1, 1, false), Start.AddMilliseconds(1), 1));
        Assert.True(lease.ShouldHide(1));
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 2, 2);
        lease.Clear();
        Assert.False(lease.Accept(Frame(2, 3), Start.AddMilliseconds(3), 3));
        Assert.False(lease.AllowsPresentation(3));
    }

    [Theory]
    [InlineData("hostSurfaceGeneration")]
    [InlineData("hostPublishedUtc")]
    [InlineData("schema")]
    [InlineData("visible")]
    [InlineData("speed")]
    [InlineData("units")]
    public void MissingTransportOrInvalidContentDoesNotRenewLease(string key)
    {
        var lease = new PassiveHudHostLease();
        lease.BeginSurface(HostSurfaceMode.PassiveHud, 1, 0);
        var frame = Frame(publishedAt: 500);
        frame.Remove(key);
        Assert.False(lease.Accept(frame, Start.AddMilliseconds(500), 500));
        Assert.True(lease.ShouldHide(1000));
    }
}
