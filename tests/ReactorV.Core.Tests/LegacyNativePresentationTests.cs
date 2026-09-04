using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class LegacyNativePresentationTests
{
    [Theory]
    [InlineData("about")]
    [InlineData("initializing")]
    [InlineData("verifying")]
    [InlineData("setup-status")]
    public void Known_bootstrap_requires_fresh_native_proof(string surface)
    {
        var waiting = ExclusiveBrowserPresentationPolicy.Resolve(true, false, surface,
            true, false, true, false, requireNativePresenter: true);
        Assert.False(waiting.IsVisible);
        var ready = ExclusiveBrowserPresentationPolicy.Resolve(true, false, surface,
            true, true, true, true, requireNativePresenter: true);
        Assert.Equal(BrowserPresentationOwner.ExternalGpuBootstrap, ready.Owner);
        Assert.False(ready.WebViewVisible);
        Assert.True(ready.ExternalGpuVisible);
    }

    [Theory]
    [InlineData("about", false)]
    [InlineData("initializing", false)]
    [InlineData("none", true)]
    public void Failed_native_route_never_promotes_invisible_webview(string mode, bool provider)
    {
        var failed = ExclusiveBrowserPresentationPolicy.Resolve(true, provider, mode,
            false, false, requireNativePresenter: true);
        Assert.False(failed.IsVisible);
        Assert.Equal(BrowserPresentationOwner.None, failed.Owner);
    }

    [Fact]
    public void Default_interactive_bootstrap_route_is_unchanged()
    {
        Assert.Equal(BrowserPresentationOwner.WebViewBootstrap,
            ExclusiveBrowserPresentationPolicy.Resolve(true, false, "about", true, true, true, true).Owner);
        Assert.False(ExternalBootstrapPresentationGate.IsReady("about", 7, 7, 7, 7, 7, true, true));
    }

    [Theory]
    [InlineData(7, 7, 7, 7, true, true, true)]
    [InlineData(6, 7, 7, 7, true, true, false)]
    [InlineData(7, 6, 7, 7, true, true, false)]
    [InlineData(7, 7, 6, 7, true, true, false)]
    [InlineData(7, 7, 7, 6, true, true, false)]
    [InlineData(7, 7, 7, 7, false, true, false)]
    [InlineData(7, 7, 7, 7, true, false, false)]
    public void About_rejects_stale_generation_resize_and_unacknowledged_pixels(
        int web, int ack, int refresh, int fresh, bool native, bool size, bool expected)
    {
        Assert.Equal(expected, ExternalBootstrapPresentationGate.IsReady(
            "about", 7, web, ack, refresh, fresh, native, size, includeInteractiveBootstrap: true));
    }

    [Fact]
    public void Hidden_and_unknown_surfaces_never_accept_native_reveal()
    {
        Assert.False(ExclusiveBrowserPresentationPolicy.Resolve(false, true, "none", true, true,
            requireNativePresenter: true).IsVisible);
        Assert.False(ExclusiveBrowserPresentationPolicy.Resolve(true, false, "unknown", true, true,
            true, true, requireNativePresenter: true).IsVisible);
        Assert.False(ExternalBootstrapPresentationGate.IsReady("none", 7, 7, 7, 7, 7, true, true, true));
    }

    [Fact]
    public void Gbay_cold_reopen_waits_for_fresh_frame()
    {
        Assert.False(ExclusiveBrowserPresentationPolicy.Resolve(true, true, "none", true, false,
            requireNativePresenter: true).IsVisible);
        Assert.Equal(BrowserPresentationOwner.ExternalGpuProvider,
            ExclusiveBrowserPresentationPolicy.Resolve(true, true, "none", true, true,
                requireNativePresenter: true).Owner);
    }
}
