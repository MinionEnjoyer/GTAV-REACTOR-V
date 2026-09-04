using System.Collections.Generic;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Regression coverage for the Enhanced presentation handoff. A presentation
/// epoch may use WebView2 as the bootstrap presenter or the in-game native
/// compositor as the provider presenter, but must never publish both.
/// </summary>
public sealed class ExclusiveBrowserPresentationEpochTests
{
    public static IEnumerable<object[]> PresentationEpochs()
    {
        yield return new object[]
        {
            false, false, HostSurfaceMode.None, false,
            false,
            BrowserPresentationOwner.None, false, false,
        };
        yield return new object[]
        {
            false, true, HostSurfaceMode.None, true,
            true,
            BrowserPresentationOwner.None, false, false,
        };
        yield return new object[]
        {
            true, false, HostSurfaceMode.Initializing, true,
            true,
            BrowserPresentationOwner.WebViewBootstrap, true, false,
        };
        yield return new object[]
        {
            true, true, HostSurfaceMode.About, true,
            true,
            BrowserPresentationOwner.WebViewBootstrap, true, false,
        };
        yield return new object[]
        {
            true, true, HostSurfaceMode.None, false,
            false,
            BrowserPresentationOwner.WebViewBootstrap, true, false,
        };
        yield return new object[]
        {
            true, false, HostSurfaceMode.None, true,
            true,
            BrowserPresentationOwner.WebViewBootstrap, true, false,
        };
        yield return new object[]
        {
            true, true, HostSurfaceMode.None, true,
            true,
            BrowserPresentationOwner.ExternalGpuProvider, false, true,
        };
        yield return new object[]
        {
            true, true, HostSurfaceMode.None, true,
            false,
            BrowserPresentationOwner.None, false, false,
        };
    }

    [Theory]
    [MemberData(nameof(PresentationEpochs))]
    public void Each_epoch_selects_exactly_one_or_zero_visible_presenters(
        bool requestedVisible,
        bool providerConnected,
        string hostSurfaceMode,
        bool externalGpuActive,
        bool externalGpuPresentationReady,
        BrowserPresentationOwner expectedOwner,
        bool expectedWebViewVisible,
        bool expectedExternalGpuVisible)
    {
        var decision = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible,
            providerConnected,
            hostSurfaceMode,
            externalGpuActive,
            externalGpuPresentationReady);

        Assert.Equal(expectedOwner, decision.Owner);
        Assert.Equal(expectedWebViewVisible, decision.WebViewVisible);
        Assert.Equal(expectedExternalGpuVisible, decision.ExternalGpuVisible);
        Assert.False(decision.WebViewVisible && decision.ExternalGpuVisible);
        Assert.Equal(
            expectedWebViewVisible || expectedExternalGpuVisible,
            decision.IsVisible);
    }

    [Fact]
    public void Bootstrap_to_provider_to_hidden_sequence_never_overlaps_presenters()
    {
        var epochs = new[]
        {
            ExclusiveBrowserPresentationPolicy.Resolve(
                requestedVisible: true,
                providerConnected: false,
                hostSurfaceMode: HostSurfaceMode.Initializing,
                externalGpuActive: true,
                externalGpuPresentationReady: false),
            ExclusiveBrowserPresentationPolicy.Resolve(
                requestedVisible: true,
                providerConnected: true,
                hostSurfaceMode: HostSurfaceMode.None,
                externalGpuActive: true,
                externalGpuPresentationReady: false),
            ExclusiveBrowserPresentationPolicy.Resolve(
                requestedVisible: true,
                providerConnected: true,
                hostSurfaceMode: HostSurfaceMode.None,
                externalGpuActive: true,
                externalGpuPresentationReady: true),
            ExclusiveBrowserPresentationPolicy.Resolve(
                requestedVisible: false,
                providerConnected: true,
                hostSurfaceMode: HostSurfaceMode.None,
                externalGpuActive: true,
                externalGpuPresentationReady: true),
        };

        Assert.Collection(
            epochs,
            bootstrap =>
            {
                Assert.Equal(
                    BrowserPresentationOwner.WebViewBootstrap,
                    bootstrap.Owner);
                Assert.True(bootstrap.WebViewVisible);
                Assert.False(bootstrap.ExternalGpuVisible);
            },
            waitingForExactSize =>
            {
                Assert.Equal(
                    BrowserPresentationOwner.None,
                    waitingForExactSize.Owner);
                Assert.False(waitingForExactSize.WebViewVisible);
                Assert.False(waitingForExactSize.ExternalGpuVisible);
            },
            provider =>
            {
                Assert.Equal(
                    BrowserPresentationOwner.ExternalGpuProvider,
                    provider.Owner);
                Assert.False(provider.WebViewVisible);
                Assert.True(provider.ExternalGpuVisible);
            },
            hidden =>
            {
                Assert.Equal(BrowserPresentationOwner.None, hidden.Owner);
                Assert.False(hidden.WebViewVisible);
                Assert.False(hidden.ExternalGpuVisible);
            });
    }

    [Fact]
    public void Enhanced_initializer_waits_for_its_fresh_native_generation_then_uses_one_presenter()
    {
        var waiting = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: false,
            hostSurfaceMode: HostSurfaceMode.Initializing,
            externalGpuActive: true,
            externalGpuPresentationReady: true,
            externalGpuBootstrapRequested: true,
            externalGpuBootstrapReady: false);
        var ready = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: false,
            hostSurfaceMode: HostSurfaceMode.Initializing,
            externalGpuActive: true,
            externalGpuPresentationReady: true,
            externalGpuBootstrapRequested: true,
            externalGpuBootstrapReady: true);

        Assert.Equal(BrowserPresentationOwner.None, waiting.Owner);
        Assert.False(waiting.IsVisible);
        Assert.Equal(
            BrowserPresentationOwner.ExternalGpuBootstrap,
            ready.Owner);
        Assert.False(ready.WebViewVisible);
        Assert.True(ready.ExternalGpuVisible);
        Assert.Equal("external-gpu-bootstrap", ready.OwnerTraceValue);
    }

    [Fact]
    public void Enhanced_initializer_fails_closed_when_native_presenter_is_unavailable()
    {
        var unavailable = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: false,
            hostSurfaceMode: HostSurfaceMode.Initializing,
            externalGpuActive: false,
            externalGpuPresentationReady: false,
            failClosedInitializerFallback: true);

        Assert.Equal(BrowserPresentationOwner.None, unavailable.Owner);
        Assert.False(unavailable.IsVisible);
        Assert.Equal(
            "initializer-native-presenter-unavailable",
            unavailable.Reason);
    }

    [Fact]
    public void Native_initializer_requirement_does_not_remove_interactive_webview_fallbacks()
    {
        var about = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: false,
            hostSurfaceMode: HostSurfaceMode.About,
            externalGpuActive: false,
            externalGpuPresentationReady: false,
            failClosedInitializerFallback: true);
        var providerMenu = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.None,
            externalGpuActive: false,
            externalGpuPresentationReady: false,
            failClosedInitializerFallback: true);

        Assert.Equal(BrowserPresentationOwner.WebViewBootstrap, about.Owner);
        Assert.True(about.WebViewVisible);
        Assert.Equal(BrowserPresentationOwner.WebViewBootstrap, providerMenu.Owner);
        Assert.True(providerMenu.WebViewVisible);
    }

    [Fact]
    public void Initializer_to_provider_replacement_never_crosses_a_blank_owner()
    {
        var initializer = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.Initializing,
            externalGpuActive: true,
            externalGpuPresentationReady: true,
            externalGpuBootstrapRequested: true,
            externalGpuBootstrapReady: true);
        var staging = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.Initializing,
            externalGpuActive: true,
            externalGpuPresentationReady: false,
            externalGpuBootstrapRequested: true,
            externalGpuBootstrapReady: false,
            externalProviderReplacementPending: true,
            externalProviderReplacementReady: false,
            retainedExternalOwner: initializer.Owner);
        var replacement = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.Initializing,
            externalGpuActive: true,
            externalGpuPresentationReady: true,
            externalGpuBootstrapRequested: true,
            externalGpuBootstrapReady: false,
            externalProviderReplacementPending: true,
            externalProviderReplacementReady: true,
            retainedExternalOwner: staging.Owner);
        var retired = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.None,
            externalGpuActive: true,
            externalGpuPresentationReady: true);
        var closed = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: false,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.None,
            externalGpuActive: true,
            externalGpuPresentationReady: true);

        Assert.Collection(
            new[] { initializer, staging, replacement, retired, closed },
            value => Assert.Equal(
                BrowserPresentationOwner.ExternalGpuBootstrap,
                value.Owner),
            value =>
            {
                Assert.Equal(
                    BrowserPresentationOwner.ExternalGpuBootstrap,
                    value.Owner);
                Assert.Equal("retained-external-frame", value.Reason);
            },
            value =>
            {
                Assert.Equal(
                    BrowserPresentationOwner.ExternalGpuProvider,
                    value.Owner);
                Assert.Equal("fresh-provider-replacement", value.Reason);
            },
            value => Assert.Equal(
                BrowserPresentationOwner.ExternalGpuProvider,
                value.Owner),
            value => Assert.Equal(BrowserPresentationOwner.None, value.Owner));
        Assert.All(
            new[] { initializer, staging, replacement, retired },
            value =>
            {
                Assert.True(value.ExternalGpuVisible);
                Assert.False(value.WebViewVisible);
            });
    }

    [Fact]
    public void Rapid_replacement_is_queued_while_a_retained_refresh_is_pending()
    {
        Assert.True(
            ExclusiveBrowserPresentationPolicy.ShouldQueueRapidReplacement(
                replacementPending: true,
                externalGpuActive: true,
                retainedRefreshSupported: true,
                externalGpuPresentationReady: false,
                externalGpuVisible: true));

        Assert.False(
            ExclusiveBrowserPresentationPolicy.ShouldQueueRapidReplacement(
                replacementPending: true,
                externalGpuActive: true,
                retainedRefreshSupported: true,
                externalGpuPresentationReady: true,
                externalGpuVisible: true));
        Assert.False(
            ExclusiveBrowserPresentationPolicy.ShouldQueueRapidReplacement(
                replacementPending: false,
                externalGpuActive: true,
                retainedRefreshSupported: true,
                externalGpuPresentationReady: false,
                externalGpuVisible: true));
    }

    [Fact]
    public void Resize_aborts_retained_ownership_until_the_exact_size_frame_is_ready()
    {
        var retained = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.None,
            externalGpuActive: true,
            externalGpuPresentationReady: false,
            externalProviderReplacementPending: true,
            externalProviderReplacementReady: false,
            retainedExternalOwner:
                BrowserPresentationOwner.ExternalGpuProvider);
        var resizing = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.None,
            externalGpuActive: true,
            externalGpuPresentationReady: false,
            externalProviderReplacementPending: false,
            externalProviderReplacementReady: false,
            retainedExternalOwner:
                BrowserPresentationOwner.ExternalGpuProvider);
        var exactSizeReady = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: true,
            hostSurfaceMode: HostSurfaceMode.None,
            externalGpuActive: true,
            externalGpuPresentationReady: true);

        Assert.Equal(
            BrowserPresentationOwner.ExternalGpuProvider,
            retained.Owner);
        Assert.Equal(BrowserPresentationOwner.None, resizing.Owner);
        Assert.False(resizing.IsVisible);
        Assert.Equal(
            BrowserPresentationOwner.ExternalGpuProvider,
            exactSizeReady.Owner);
    }

    [Theory]
    [InlineData(HostSurfaceMode.About)]
    [InlineData(HostSurfaceMode.Verifying)]
    [InlineData(HostSurfaceMode.SetupStatus)]
    public void Interactive_bootstrap_surfaces_remain_on_webview(
        string surfaceMode)
    {
        var decision = ExclusiveBrowserPresentationPolicy.Resolve(
            requestedVisible: true,
            providerConnected: false,
            hostSurfaceMode: surfaceMode,
            externalGpuActive: true,
            externalGpuPresentationReady: true,
            externalGpuBootstrapRequested: true,
            externalGpuBootstrapReady: true);

        Assert.Equal(BrowserPresentationOwner.WebViewBootstrap, decision.Owner);
        Assert.True(decision.WebViewVisible);
        Assert.False(decision.ExternalGpuVisible);
    }

    [Fact]
    public void Native_initializer_gate_requires_one_exact_current_generation_and_size()
    {
        Assert.True(ExternalBootstrapPresentationGate.IsReady(
            HostSurfaceMode.Initializing,
            currentGeneration: 9,
            webViewReadyGeneration: 9,
            externalAckGeneration: 9,
            externalRefreshGeneration: 9,
            externalFreshGeneration: 9,
            externalPresentationReady: true,
            exactSurfaceSize: true));

        Assert.False(ExternalBootstrapPresentationGate.IsReady(
            HostSurfaceMode.Initializing,
            currentGeneration: 9,
            webViewReadyGeneration: 9,
            externalAckGeneration: 8,
            externalRefreshGeneration: 9,
            externalFreshGeneration: 9,
            externalPresentationReady: true,
            exactSurfaceSize: true));
        Assert.False(ExternalBootstrapPresentationGate.IsReady(
            HostSurfaceMode.Initializing,
            currentGeneration: 9,
            webViewReadyGeneration: 9,
            externalAckGeneration: 9,
            externalRefreshGeneration: 9,
            externalFreshGeneration: 9,
            externalPresentationReady: true,
            exactSurfaceSize: false));
        Assert.False(ExternalBootstrapPresentationGate.IsReady(
            HostSurfaceMode.About,
            currentGeneration: 9,
            webViewReadyGeneration: 9,
            externalAckGeneration: 9,
            externalRefreshGeneration: 9,
            externalFreshGeneration: 9,
            externalPresentationReady: true,
            exactSurfaceSize: true));
    }

    [Fact]
    public void Decision_constructor_rejects_a_double_visible_epoch()
    {
        Assert.Throws<System.ArgumentException>(() =>
            new BrowserPresentationDecision(
                BrowserPresentationOwner.WebViewBootstrap,
                webViewVisible: true,
                externalGpuVisible: true,
                reason: "invalid-double-render"));
    }
}
