using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PassiveHudGenerationMapTests
{
    private static JObject Request(int generation) => new() { ["mode"] = HostSurfaceMode.PassiveHud, ["generation"] = generation };
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 14)]
    [InlineData(20, 30)]
    public void DifferentScriptAndHostCountersAreCorrelatedWithoutRewritingTimestamp(int provider, int host)
    {
        var map = new PassiveHudGenerationMap();
        Assert.True(map.BeginRequest(Request(provider)));
        map.BindSurface(HostSurfaceMode.PassiveHud, host);
        var original = PassiveHudHostLease.CreateFrame(new JObject { ["schema"] = 1, ["visible"] = false }, provider, DateTime.UtcNow);
        var wire = JObject.Parse(original.ToString());
        var mapped = map.ToHostFrame(wire);
        Assert.NotNull(mapped);
        Assert.Equal(host, mapped.Value<int>("hostSurfaceGeneration"));
        Assert.Equal(provider, original.Value<int>("hostSurfaceGeneration"));
        Assert.True(JToken.DeepEquals(wire["hostPublishedUtc"], mapped["hostPublishedUtc"]));
        Assert.True(map.ToProviderReceipt(new HostSurfacePresentation(HostSurfaceMode.PassiveHud, host))!.Matches(HostSurfaceMode.PassiveHud, provider));
        Assert.Null(map.ToProviderReceipt(new HostSurfacePresentation(HostSurfaceMode.PassiveHud, host - 1)));
    }
    [Fact]
    public void ReplacementRetirementAndReloadCannotReuseOldMapping()
    {
        var map = new PassiveHudGenerationMap();
        map.BeginRequest(Request(1)); map.BindSurface(HostSurfaceMode.PassiveHud, 2);
        map.BeginRequest(Request(2));
        Assert.Null(map.ToProviderReceipt(new HostSurfacePresentation(HostSurfaceMode.PassiveHud, 2)));
        map.BindSurface(HostSurfaceMode.PassiveHud, 3);
        var old = new JObject { ["hostSurfaceGeneration"] = 1 };
        Assert.Null(map.ToHostFrame(old));
        map.BindSurface(HostSurfaceMode.None, 4);
        Assert.Null(map.ToProviderReceipt(new HostSurfacePresentation(HostSurfaceMode.PassiveHud, 3)));
        map.BeginRequest(Request(1)); map.BindSurface(HostSurfaceMode.PassiveHud, 5);
        Assert.True(map.ToProviderReceipt(new HostSurfacePresentation(HostSurfaceMode.PassiveHud, 5))!.Matches(HostSurfaceMode.PassiveHud, 1));
        map.Clear(); Assert.Null(map.ToHostFrame(old));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidRequestCannotAcquireHostGeneration(int generation)
    {
        var map = new PassiveHudGenerationMap();
        Assert.False(map.BeginRequest(Request(generation)));
        map.BindSurface(HostSurfaceMode.PassiveHud, 2);
        Assert.Null(map.ToProviderReceipt(new HostSurfacePresentation(HostSurfaceMode.PassiveHud, 2)));
    }
}
