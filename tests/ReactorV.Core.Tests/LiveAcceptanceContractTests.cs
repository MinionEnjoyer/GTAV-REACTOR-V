using System;
using System.Collections.Generic;
using System.Linq;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class LiveAcceptanceContractTests
{
    [Fact]
    public void CommandCatalogIsClosedAndOrdered()
    {
        Assert.Equal(12, LiveAcceptanceContract.OrderedCommands.Count);
        Assert.All(
            LiveAcceptanceContract.OrderedCommands,
            command => Assert.True(LiveAcceptanceContract.IsSupportedCommand(command)));
        Assert.False(LiveAcceptanceContract.IsSupportedCommand("game.launch"));
        Assert.False(LiveAcceptanceContract.IsSupportedCommand(null));
    }

    [Theory]
    [InlineData(
        "2026-08-30T01:00:00.000 stage=bootstrap_host_about_toggle_signaled key=F9 source=scripthook_keyboard",
        LiveAcceptanceRoute.About)]
    [InlineData(
        "2026-08-30T01:00:01.000 stage=bootstrap_host_toggle_signaled key=F9 source=scripthook_keyboard",
        LiveAcceptanceRoute.Initializer)]
    [InlineData(
        "2026-08-30T01:00:01.500 stage=bootstrap_host_verify_toggle_signaled key=F9 source=scripthook_keyboard",
        LiveAcceptanceRoute.Verifying)]
    [InlineData(
        "2026-08-30T01:00:02.000 stage=bootstrap_host_native_toggle visible=True",
        LiveAcceptanceRoute.None)]
    public void BootstrapRouteClassificationIsExact(
        string line,
        LiveAcceptanceRoute expected)
    {
        Assert.Equal(expected, LiveAcceptanceContract.ClassifyBootstrapRoute(line));
    }

    [Fact]
    public void Neutral_route_waits_for_one_in_place_promotion_and_cleans_up_timeout()
    {
        Assert.True(LiveAcceptanceContract.RequiresInPlaceBootstrapPromotion(
            LiveAcceptanceRoute.Verifying));
        Assert.False(LiveAcceptanceContract.RequiresInPlaceBootstrapPromotion(
            LiveAcceptanceRoute.About));
        Assert.True(LiveAcceptanceContract.RequiresUnresolvedVerificationCleanup(
            LiveAcceptanceRoute.Verifying,
            LiveAcceptanceRoute.None));
        Assert.False(LiveAcceptanceContract.RequiresUnresolvedVerificationCleanup(
            LiveAcceptanceRoute.Verifying,
            LiveAcceptanceRoute.About));
        Assert.False(LiveAcceptanceContract.RequiresUnresolvedVerificationCleanup(
            LiveAcceptanceRoute.About,
            LiveAcceptanceRoute.None));
    }

    [Theory]
    [InlineData(
        "2026-08-30T04:10:59.000 stage=stage_changed GTA script threads are starting...",
        LiveAcceptanceStoryTransition.None)]
    [InlineData(
        "2026-08-30T04:11:00.180 stage=stage_changed Managed runtime ready - initializing Reactor V...",
        LiveAcceptanceStoryTransition.ManagedRuntimeStarting)]
    [InlineData(
        "2026-08-30T04:11:04.000 stage=stage_changed Story Mode ready",
        LiveAcceptanceStoryTransition.StoryReady)]
    [InlineData(
        "stage=bootstrap_host_toggle_signaled key=F9 stage=Managed runtime ready - initializing Reactor V...",
        LiveAcceptanceStoryTransition.None)]
    public void StoryTransitionRequiresPassiveObjectiveLifecycleEvidence(
        string line,
        LiveAcceptanceStoryTransition expected)
    {
        Assert.Equal(expected, LiveAcceptanceContract.ClassifyStoryTransition(line));
    }

    [Fact]
    public void EarlyInitializerMustPaintBeforeProviderReadiness()
    {
        const string surface =
            "stage=bootstrap_host_surface_ready mode=initializing generation=17";
        const string provider = "stage=bootstrap_host_provider_ready pid=4420";

        var success = new LiveAcceptanceEarlyInitializerObservation();
        success.Observe("stage=unrelated");
        success.Observe(surface);
        success.Observe(provider);
        Assert.True(success.IsComplete);
        Assert.False(success.ProviderWonRace);
        Assert.Equal(surface, success.SurfaceEvidence);

        var failure = new LiveAcceptanceEarlyInitializerObservation();
        failure.Observe(provider);
        failure.Observe(surface);
        Assert.False(failure.IsComplete);
        Assert.True(failure.ProviderWonRace);
        Assert.Null(failure.SurfaceEvidence);
    }

    [Fact]
    public void SectionMatrixResolvesAgainstTheActualViewport()
    {
        Assert.Equal(
            LiveAcceptanceContract.RequiredTopLevelSectionCount,
            LiveAcceptanceContract.TopLevelSections.Count);
        Assert.Equal(
            new[] { "home", "vehicles", "weapons", "customization", "gear", "garage", "addons", "diagnostics", "about" },
            LiveAcceptanceContract.TopLevelSections.Select(section => section.Id));
        Assert.Equal(
            new[] { "home", "vehicles", "weapons", "weapons.customize", "gear", "garage", "addons", "diagnostics", "about" },
            LiveAcceptanceContract.TopLevelSections.Select(section => section.ExpectedMenuId));

        var hd = LiveAcceptanceContract.ResolveSectionPoint(
            LiveAcceptanceContract.TopLevelSections[2], 1920, 1080);
        var qhd = LiveAcceptanceContract.ResolveSectionPoint(
            LiveAcceptanceContract.TopLevelSections[2], 2560, 1440);
        Assert.InRange(hd.X, 0.0, 1.0);
        Assert.InRange(hd.Y, 0.0, 1.0);
        Assert.InRange(qhd.X, 0.0, 1.0);
        Assert.InRange(qhd.Y, 0.0, 1.0);
        Assert.NotEqual(hd.X, qhd.X);
        Assert.True(qhd.X > hd.X);

        // Live window coordinates are physical pixels while WebView layout is
        // expressed in CSS pixels. The same CSS viewport must therefore
        // resolve to the same normalized point at 100% and 125% scaling.
        var css100 = LiveAcceptanceContract.ResolveSectionPoint(
            LiveAcceptanceContract.TopLevelSections[1], 1920, 1080, 1.0);
        var css125 = LiveAcceptanceContract.ResolveSectionPoint(
            LiveAcceptanceContract.TopLevelSections[1], 2400, 1350, 1.25);
        Assert.Equal(css100.X, css125.X, 10);
        Assert.Equal(css100.Y, css125.Y, 10);

        // These are the exact 2560x1440 / 125% centers from the captured live
        // GBAY navigation row. In particular, Customize is a compact wrench:
        // the old 627.5px point landed inside the already-active Weapons tab
        // and therefore produced no semantic route transition.
        var expectedPhysicalCenters = new[]
        {
            357.5, 470.0, 616.25, 700.0, 792.5, 940.0, 1091.25, 1205.0, 1352.5,
        };
        for (var index = 0; index < LiveAcceptanceContract.TopLevelSections.Count; index++)
        {
            var live125 = LiveAcceptanceContract.ResolveSectionPoint(
                LiveAcceptanceContract.TopLevelSections[index], 2560, 1440, 1.25);
            Assert.Equal(expectedPhysicalCenters[index], live125.X * 2560.0, 6);
            Assert.Equal(195.125, live125.Y * 1440.0, 6);
        }
        var customization125 = LiveAcceptanceContract.ResolveSectionPoint(
            LiveAcceptanceContract.TopLevelSections[3], 2560, 1440, 1.25);
        Assert.InRange(customization125.X * 2560.0, 681.0, 721.0);
        var card125 = LiveAcceptanceContract.ResolveFirstCatalogCardPoint(
            2400, 1350, 1.25);
        var card100 = LiveAcceptanceContract.ResolveFirstCatalogCardPoint(
            1920, 1080, 1.0);
        Assert.Equal(card100.X, card125.X, 10);
        Assert.Equal(card100.Y, card125.Y, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LiveAcceptanceContract.ResolveSectionPoint(
                LiveAcceptanceContract.TopLevelSections[0], 1920, 1080, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LiveAcceptanceContract.ResolveSectionPoint(
                LiveAcceptanceContract.TopLevelSections[0], 800, 600));
        Assert.Equal("menu.click.weapons-tab", LiveAcceptanceContract.MenuClickWeaponsTab);
    }

    [Fact]
    public void ParsesSurfaceModeAndGenerationExactly()
    {
        Assert.True(LiveAcceptanceContract.TryParseSurfaceReady(
            "2026-08-30T01:00:00Z source=preloader stage=bootstrap_host_surface_ready mode=about generation=12",
            out var surface));
        Assert.Equal("about", surface.Mode);
        Assert.Equal(12, surface.Generation);
        Assert.False(LiveAcceptanceContract.TryParseSurfaceReady(
            "stage=bootstrap_host_surface_published mode=about generation=12",
            out _));
    }

    [Fact]
    public void RequiresOrderedPairedNativePointerEdges()
    {
        Assert.True(LiveAcceptanceContract.TryParsePointerEdge(
            "stage=webview_pointer_edge x=0.1600 y=0.1400 pressed=True released=False wheel=0 bootstrap_hit_test_capture=False route=bridge-event forwarded=True",
            out var down));
        Assert.True(LiveAcceptanceContract.TryParsePointerEdge(
            "stage=webview_pointer_edge x=0.1602 y=0.1401 pressed=False released=True wheel=0 bootstrap_hit_test_capture=False route=bridge-event forwarded=True",
            out var up));
        Assert.True(LiveAcceptanceContract.IsValidPointerPair(down, up));

        var compositionDown = new LiveAcceptancePointerEdge(
            down.X, down.Y, down.Pressed, down.Released, "composition", forwarded: true);
        Assert.False(LiveAcceptanceContract.IsValidPointerPair(compositionDown, up));

        var unforwardedUp = new LiveAcceptancePointerEdge(
            up.X, up.Y, up.Pressed, up.Released, up.Route, forwarded: false);
        Assert.False(LiveAcceptanceContract.IsValidPointerPair(down, unforwardedUp));
        Assert.False(LiveAcceptanceContract.IsValidPointerPair(up, down));
    }

    [Fact]
    public void BrowserMenuStateIsBoundedAndTraceRoundTripsExactly()
    {
        const string json = """
            {
              "kind": "acceptance",
              "command": "menu-state",
              "schemaVersion": 1,
              "presentationId": "allin1.gbay:home:42",
              "providerId": "allin1.gbay",
              "rootMenuId": "home",
              "menuId": "weapons.customize",
              "routeId": "weapons.customize",
              "sectionId": "customization",
              "payloadStatus": "ready",
              "itemCount": 16,
              "contentItemCount": 7,
              "actionableItemCount": 5,
              "statusItemCount": 1
            }
            """;

        Assert.True(LiveAcceptanceContract.TryParseBrowserMenuState(json, out var browser));
        Assert.Equal("allin1.gbay", browser.ProviderId);
        Assert.Equal("weapons.customize", browser.MenuId);
        Assert.Equal(6, browser.MeaningfulItemCount);

        const string trace =
            "stage=webview_acceptance_menu_state " +
            "presentation=allin1.gbay:home:42 provider=allin1.gbay root_menu=home " +
            "menu=weapons.customize route=weapons.customize section=customization " +
            "payload=ready items=16 content=7 actionable=5 status=1";
        Assert.True(LiveAcceptanceContract.TryParseMenuStateTrace(trace, out var logged));
        Assert.Equal(browser.PresentationId, logged.PresentationId);
        Assert.Equal(browser.RouteId, logged.RouteId);
        Assert.Equal(browser.ContentItemCount, logged.ContentItemCount);

        Assert.False(LiveAcceptanceContract.TryParseBrowserMenuState(
            json.Replace("\"contentItemCount\": 7", "\"contentItemCount\": 2"),
            out _));
        Assert.False(LiveAcceptanceContract.TryParseMenuStateTrace(
            trace.Replace("content=7", "content=2"),
            out _));
    }

    [Fact]
    public void SectionValidationRequiresExactIdentityAndMeaningfulReadyPayload()
    {
        var target = LiveAcceptanceContract.TopLevelSections.Single(
            section => section.Id == "garage");
        var complete = new LiveAcceptanceMenuState(
            "allin1.gbay:home:42",
            "allin1.gbay",
            "home",
            "garage",
            "garage",
            "garage",
            "ready",
            itemCount: 12,
            contentItemCount: 3,
            actionableItemCount: 2,
            statusItemCount: 1);

        Assert.True(LiveAcceptanceContract.TryValidateSectionIdentity(
            target, complete, out var identityFailure));
        Assert.Empty(identityFailure);
        Assert.True(LiveAcceptanceContract.TryValidateSectionPayload(
            target, complete, out var payloadFailure));
        Assert.Empty(payloadFailure);

        var wrongRoute = new LiveAcceptanceMenuState(
            complete.PresentationId, complete.ProviderId, complete.RootMenuId,
            "vehicles", "vehicles", "vehicles", "ready", 12, 3, 2, 1);
        Assert.False(LiveAcceptanceContract.TryValidateSectionIdentity(
            target, wrongRoute, out identityFailure));
        Assert.Equal("section_menu_mismatch", identityFailure);

        var loading = new LiveAcceptanceMenuState(
            complete.PresentationId, complete.ProviderId, complete.RootMenuId,
            complete.MenuId, complete.RouteId, complete.SectionId,
            "loading", 9, 0, 0, 0);
        Assert.False(LiveAcceptanceContract.TryValidateSectionPayload(
            target, loading, out payloadFailure));
        Assert.Equal("section_payload_not_ready", payloadFailure);

        var decorativeOnly = new LiveAcceptanceMenuState(
            complete.PresentationId, complete.ProviderId, complete.RootMenuId,
            complete.MenuId, complete.RouteId, complete.SectionId,
            "ready", 10, 1, 0, 0);
        Assert.False(LiveAcceptanceContract.TryValidateSectionPayload(
            target, decorativeOnly, out payloadFailure));
        Assert.Equal("section_payload_not_meaningful", payloadFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AboutCloseAcceptsBothSessionLogEventOrders(bool visibilityFirst)
    {
        const string toggle =
            "stage=bootstrap_host_native_about_toggle visible=False mode=about";
        const string visibility =
            "stage=webview_visibility_applied visible=False reason=native-about-toggle";
        var lines = visibilityFirst
            ? new[] { "stage=unrelated", visibility, toggle }
            : new[] { toggle, "stage=unrelated", visibility };
        var observation = new LiveAcceptanceAboutCloseObservation();

        Assert.False(observation.IsComplete);
        foreach (var line in lines) observation.Observe(line);

        Assert.True(observation.IsComplete);
        Assert.Equal(toggle, observation.ToggleEvidence);
        Assert.Equal(visibility, observation.VisibilityEvidence);
        Assert.Contains(toggle, observation.ToEvidence());
        Assert.Contains(visibility, observation.ToEvidence());
    }

    [Fact]
    public void PinnedGtaWindowIsPropagatedToEveryOperation()
    {
        var stableHandle = new nint(0x0BC60A98);
        var laterAuxiliaryMainWindow = new nint(0x00000560);
        var binding = new LiveAcceptanceWindowBinding(stableHandle);
        var observed = new List<nint>();

        binding.WithHandle(handle => { observed.Add(handle); return true; });
        binding.WithHandle(handle => { observed.Add(handle); return true; });

        Assert.NotEqual(stableHandle, laterAuxiliaryMainWindow);
        Assert.Equal(stableHandle, binding.Handle);
        Assert.Equal(new[] { stableHandle, stableHandle }, observed);
        Assert.Throws<ArgumentException>(() => new LiveAcceptanceWindowBinding(nint.Zero));
    }

    [Fact]
    public void FinalGateRejectsReceiptsWithoutActualLiveSession()
    {
        var proof = CompleteProof();
        proof.FreshGtaProcessObserved = false;

        Assert.False(LiveAcceptanceContract.TryValidatePass(proof, out var failure));
        Assert.Equal("fresh_gta_process_not_observed", failure);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("foreground")]
    [InlineData("hashes")]
    [InlineData("about-route")]
    [InlineData("story-transition")]
    [InlineData("initializer-route")]
    [InlineData("about-surface")]
    [InlineData("initializer-surface")]
    [InlineData("about-pixels")]
    [InlineData("initializer-pixels")]
    [InlineData("gbay-pixels")]
    [InlineData("desktop-pixel-count")]
    [InlineData("early-initializer")]
    [InlineData("pointer")]
    [InlineData("pointer-matrix")]
    [InlineData("section-matrix")]
    [InlineData("section-plumbing")]
    [InlineData("section-identity")]
    [InlineData("section-payload")]
    [InlineData("pointer-foreground")]
    [InlineData("semantic-navigation")]
    [InlineData("semantic-accept")]
    [InlineData("semantic-back")]
    [InlineData("back-close")]
    [InlineData("pause-state")]
    [InlineData("pause-leak")]
    [InlineData("customizer-handoff")]
    [InlineData("customizer-camera")]
    [InlineData("customizer-return")]
    [InlineData("timeline")]
    [InlineData("screenshots")]
    [InlineData("cycles")]
    public void FinalGateFailsClosedForEveryRequiredEvidenceClass(string missing)
    {
        var proof = CompleteProof();
        switch (missing)
        {
            case "window": proof.GtaMainWindowObserved = false; break;
            case "foreground": proof.GtaForegroundObserved = false; break;
            case "hashes": proof.InstalledHashCount--; break;
            case "about-route": proof.AboutRouteObserved = false; break;
            case "story-transition": proof.StoryTransitionObserved = false; break;
            case "initializer-route": proof.InitializerRouteObserved = false; break;
            case "about-surface": proof.AboutSurfaceObserved = false; break;
            case "initializer-surface": proof.InitializerSurfaceObserved = false; break;
            case "about-pixels": proof.AboutDesktopPixelsObserved = false; break;
            case "initializer-pixels": proof.InitializerDesktopPixelsObserved = false; break;
            case "gbay-pixels": proof.GbayDesktopPixelsObserved = false; break;
            case "desktop-pixel-count": proof.DesktopPixelEvidenceCount--; break;
            case "early-initializer": proof.EarlyInitializerBeforeProviderObserved = false; break;
            case "pointer": proof.PointerPairObserved = false; break;
            case "pointer-matrix": proof.PointerPairCount--; break;
            case "section-matrix": proof.TopLevelSectionCount--; break;
            case "section-plumbing": proof.SectionPlumbingCount--; break;
            case "section-identity": proof.TopLevelSectionIdentityCount--; break;
            case "section-payload": proof.TopLevelSectionPayloadCount--; break;
            case "pointer-foreground": proof.ForeignForegroundDuringPointer = true; break;
            case "semantic-navigation": proof.SemanticNavigationObserved = false; break;
            case "semantic-accept": proof.SemanticAcceptObserved = false; break;
            case "semantic-back": proof.SemanticBackObserved = false; break;
            case "back-close": proof.BackCloseObserved = false; break;
            case "pause-state": proof.PauseStateChecked = false; break;
            case "pause-leak": proof.PauseMenuLeakObserved = true; break;
            case "customizer-handoff": proof.NativeCustomizerHandoffObserved = false; break;
            case "customizer-camera": proof.NativeCustomizerCameraObserved = false; break;
            case "customizer-return": proof.NativeCustomizerReturnObserved = false; break;
            case "timeline": proof.ForegroundObservationCount = 0; break;
            case "screenshots": proof.ScreenshotCount--; break;
            case "cycles": proof.OpenCloseCycles--; break;
        }

        Assert.False(LiveAcceptanceContract.TryValidatePass(proof, out var failure));
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void FinalGateAcceptsCompleteLiveEvidenceOnly()
    {
        Assert.True(LiveAcceptanceContract.TryValidatePass(
            CompleteProof(),
            out var failure));
        Assert.Empty(failure);
    }

    private static LiveAcceptanceProof CompleteProof() => new()
    {
        FreshGtaProcessObserved = true,
        GtaMainWindowObserved = true,
        GtaForegroundObserved = true,
        InstalledHashCount = LiveAcceptanceContract.MinimumInstalledHashCount,
        AboutRouteObserved = true,
        StoryTransitionObserved = true,
        InitializerRouteObserved = true,
        AboutSurfaceObserved = true,
        InitializerSurfaceObserved = true,
        AboutPixelsObserved = true,
        InitializerPixelsObserved = true,
        GbayPixelsObserved = true,
        AboutDesktopPixelsObserved = true,
        InitializerDesktopPixelsObserved = true,
        GbayDesktopPixelsObserved = true,
        BrowserCaptureEvidenceCount = 3,
        DesktopPixelEvidenceCount = 3,
        EarlyInitializerBeforeProviderObserved = true,
        PointerPairObserved = true,
        PointerPairCount = LiveAcceptanceContract.MinimumPointerPairCount,
        TopLevelSectionCount = LiveAcceptanceContract.RequiredTopLevelSectionCount,
        SectionPlumbingCount = LiveAcceptanceContract.RequiredTopLevelSectionCount,
        TopLevelSectionIdentityCount = LiveAcceptanceContract.RequiredTopLevelSectionCount,
        TopLevelSectionPayloadCount = LiveAcceptanceContract.RequiredTopLevelSectionCount,
        ForeignForegroundDuringPointer = false,
        SemanticNavigationObserved = true,
        SemanticAcceptObserved = true,
        SemanticBackObserved = true,
        BackCloseObserved = true,
        PauseStateChecked = true,
        PauseMenuLeakObserved = false,
        NativeCustomizerHandoffObserved = true,
        NativeCustomizerCameraObserved = true,
        NativeCustomizerReturnObserved = true,
        ForegroundObservationCount = 1,
        ScreenshotCount = LiveAcceptanceContract.MinimumScreenshotCount,
        OpenCloseCycles = LiveAcceptanceContract.MinimumOpenCloseCycles,
    };
}
