using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class WebView2HostTests
    {
        [Fact]
        public void WindowedOverlayUsesTransparentCompositionBackground()
        {
            var argb = OverlayPresentationPolicy.CompositionBackgroundArgb;

            Assert.Equal(0, (argb >> 24) & 0xFF);
            Assert.Equal(0, argb);
        }

        [Theory]
        [InlineData(true, true, false, true, true, true)]
        [InlineData(false, true, false, true, true, false)]
        [InlineData(true, false, false, true, true, false)]
        [InlineData(true, true, true, true, true, false)]
        [InlineData(true, true, false, false, true, false)]
        [InlineData(true, true, false, true, false, false)]
        public void PresentationRequiresRequestReadinessAndUsableGameWindow(
            bool requested,
            bool ready,
            bool minimized,
            bool foreground,
            bool hasBounds,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldPresent(
                    requested,
                    ready,
                    minimized,
                    foreground,
                    hasBounds));
        }

        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        public void RevealCommitRequiresTheEstablishedReadinessContract(
            bool requested,
            bool ready,
            bool pending,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldCommitReveal(
                    requested,
                    ready,
                    pending));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(unchecked((int)0x80004005), false)]
        [InlineData(unchecked((int)0x887A0005), false)]
        public void RevealFailsClosedWhenCompositionCompletionFails(
            int hresult,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.DidCompositionCommitComplete(hresult));
        }

        [Fact]
        public void WarmReopenKeepsTheProvenCompositionRootAndSynchronizesBeforeShow()
        {
            var refresh = OverlayPresentationPolicy.SelectRevealCompositionRefresh(
                browserReady: true,
                surfacePrepared: true,
                actualVisible: false,
                revealPending: true,
                surfaceWasPreviouslyPresented: true);

            Assert.Equal(RevealCompositionRefresh.Synchronize, refresh);
            Assert.False(OverlayPresentationPolicy.UseLiveDesktopPixelSampling);
        }

        [Fact]
        public void FirstRevealRepublishesTheOffscreenCompositionRoot()
        {
            Assert.Equal(
                RevealCompositionRefresh.RebindRootVisual,
                OverlayPresentationPolicy.SelectRevealCompositionRefresh(
                    browserReady: true,
                    surfacePrepared: true,
                    actualVisible: false,
                    revealPending: true,
                    surfaceWasPreviouslyPresented: false));
        }

        [Fact]
        public void StoryInitializerDefersFreshRootUntilTheVisibleOffscreenLease()
        {
            Assert.Equal(
                RevealCompositionRefresh.Synchronize,
                OverlayPresentationPolicy.SelectRevealCompositionRefresh(
                    browserReady: true,
                    surfacePrepared: true,
                    actualVisible: false,
                    revealPending: true,
                    surfaceWasPreviouslyPresented: true,
                    deferFreshRootUntilVisibleLease: true));
        }

        [Theory]
        [InlineData(false, true, false, true)]
        [InlineData(true, false, false, true)]
        [InlineData(true, true, true, true)]
        [InlineData(true, true, false, false)]
        public void InvalidRevealStateCannotMutateTheCompositionVisual(
            bool browserReady,
            bool surfacePrepared,
            bool actualVisible,
            bool revealPending)
        {
            Assert.Equal(
                RevealCompositionRefresh.None,
                OverlayPresentationPolicy.SelectRevealCompositionRefresh(
                    browserReady,
                    surfacePrepared,
                    actualVisible,
                    revealPending,
                    surfaceWasPreviouslyPresented: true));
        }

        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(true, true, true, false)]
        public void VisibleOrPendingOverlayDismissesInsteadOfReappearingAfterAltTab(
            bool requested,
            bool visibleOrPending,
            bool foreground,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldDismissForForegroundLoss(
                    requested,
                    visibleOrPending,
                    foreground));
        }

        [Theory]
        [InlineData(true, true, true, true, false, true)]
        [InlineData(true, true, true, true, true, false)]
        [InlineData(true, true, true, false, false, false)]
        [InlineData(true, true, false, true, false, false)]
        [InlineData(true, false, true, true, false, false)]
        [InlineData(false, true, true, true, false, false)]
        public void VisibleOverlayRepairsOnlyAProvenGameWindowOvertake(
            bool requested,
            bool visibleOrPending,
            bool foreground,
            bool comparisonKnown,
            bool overlayAboveGame,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldReassertOverlayZOrder(
                    requested,
                    visibleOrPending,
                    foreground,
                    comparisonKnown,
                    overlayAboveGame));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void HiddenOverlayDoesNotRemainCoupledToTheGameWindowLifetime(
            bool actual,
            bool pending,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldAttachToGameWindow(
                    actual,
                    pending));
        }

        [Fact]
        public void RequestedButUnreadyOverlayDoesNotAttachToTheGameWindow()
        {
            // Requested visibility is deliberately absent from the ownership
            // policy. Only BeginDeferredReveal or an actually visible surface
            // may establish the cross-process owner relationship.
            Assert.False(OverlayPresentationPolicy.ShouldAttachToGameWindow(
                actualVisible: false,
                revealPending: false));
        }

        [Theory]
        [InlineData(576, 200, 120, true)]
        [InlineData(576, 5, 4, false)]
        [InlineData(576, 200, 2, false)]
        [InlineData(0, 0, 0, false)]
        [InlineData(10, 11, 2, false)]
        public void BrowserPaintProofRejectsTransparentOrBlackLayoutAcks(
            int samples,
            int opaque,
            int visibleColor,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.HasConcreteBrowserPixels(
                    samples,
                    opaque,
                    visibleColor));
        }

        [Theory]
        [InlineData("initializing", 4, 2, 1920, 1080, "initializing", 4, 2, 1920, 1080, true, true, true)]
        [InlineData("initializing", 4, 2, 1920, 1080, "initializing", 3, 2, 1920, 1080, true, true, false)]
        [InlineData("initializing", 4, 2, 1920, 1080, "initializing", 4, 1, 1920, 1080, true, true, false)]
        [InlineData("initializing", 4, 2, 1920, 1080, "about", 4, 2, 1920, 1080, true, true, false)]
        [InlineData("about", 4, 2, 1920, 1080, "about", 4, 2, 1920, 1080, true, true, false)]
        [InlineData("initializing", 4, 2, 1920, 1080, "initializing", 4, 2, 1280, 720, true, true, false)]
        [InlineData("initializing", 4, 2, 1920, 1080, "initializing", 4, 2, 1920, 1080, true, false, false)]
        [InlineData("initializing", 4, 2, 1920, 1080, "initializing", 4, 2, 1920, 1080, false, true, false)]
        public void BootstrapRevealRequiresConcretePixelsFromTheExactGeneration(
            string currentMode,
            int currentSurfaceGeneration,
            int currentControllerGeneration,
            int currentWidth,
            int currentHeight,
            string proofMode,
            int proofSurfaceGeneration,
            int proofControllerGeneration,
            int proofWidth,
            int proofHeight,
            bool concrete,
            bool generationMarkerMatched,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.HasExactBootstrapPixelProof(
                    currentMode,
                    currentSurfaceGeneration,
                    currentControllerGeneration,
                    currentWidth,
                    currentHeight,
                    proofMode,
                    proofSurfaceGeneration,
                    proofControllerGeneration,
                    proofWidth,
                    proofHeight,
                    concrete,
                    generationMarkerMatched));
        }

        [Theory]
        [InlineData(7, 7, "initializing", "initializing", 9, 9, 3, 3, false, false, false, false, true)]
        [InlineData(7, 8, "initializing", "initializing", 9, 9, 3, 3, false, false, false, false, false)]
        [InlineData(7, 7, "initializing", "none", 9, 9, 3, 3, false, false, false, false, false)]
        [InlineData(7, 7, "initializing", "initializing", 9, 9, 3, 3, true, false, false, false, false)]
        [InlineData(7, 7, "initializing", "initializing", 9, 9, 3, 3, false, false, false, true, false)]
        public void BootstrapPixelProbeLeaseCannotMutateANewerProviderState(
            int leaseProbeGeneration,
            int currentProbeGeneration,
            string leaseMode,
            string currentMode,
            int leaseSurfaceGeneration,
            int currentSurfaceGeneration,
            int leaseControllerGeneration,
            int currentControllerGeneration,
            bool desiredVisible,
            bool actualVisible,
            bool revealPending,
            bool hasMenuPresentation,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.OwnsBootstrapPixelProbeLease(
                    leaseProbeGeneration,
                    currentProbeGeneration,
                    leaseMode,
                    currentMode,
                    leaseSurfaceGeneration,
                    currentSurfaceGeneration,
                    leaseControllerGeneration,
                    currentControllerGeneration,
                    desiredVisible,
                    actualVisible,
                    revealPending,
                    hasMenuPresentation));
        }

        [Theory]
        [InlineData(1, 2, true, false, true)]
        [InlineData(2, 2, true, false, false)]
        [InlineData(1, 2, false, false, false)]
        [InlineData(1, 2, true, true, false)]
        public void BootstrapPixelProbeGetsExactlyOneBoundedRetry(
            int completedAttempt,
            int maximumAttempts,
            bool leaseCurrent,
            bool concrete,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldRetryBootstrapPixelProbe(
                    completedAttempt,
                    maximumAttempts,
                    leaseCurrent,
                    concrete));
        }

        [Fact]
        public void PaintIdentityMatchesWebVectorsAndRejectsGenericWhitePixels()
        {
            Assert.Equal(
                0x9793139a7e096240UL,
                OverlayPresentationPolicy.HostPaintIdentity("initializing", 42));
            Assert.Equal(
                0x937437cfa9254291UL,
                OverlayPresentationPolicy.HostPaintIdentity("about", 7));
            Assert.Equal(
                0x989ab95c6e364c88UL,
                OverlayPresentationPolicy.HostPaintIdentity("verifying", 99));
            Assert.Equal(
                0xc569b3b27f388731UL,
                OverlayPresentationPolicy.HostPaintIdentity("setup-status", 0));
            Assert.Equal(
                0x26895c8d78e86ef8UL,
                OverlayPresentationPolicy.MenuPaintIdentity(
                    1,
                    "allin1.gbay:home:42"));
            Assert.Equal(
                0xf7f189ac2750f682UL,
                OverlayPresentationPolicy.MenuPaintIdentity(12, "gbay-startup"));

            var paintIdentity = OverlayPresentationPolicy.MenuPaintIdentity(
                1,
                "allin1.gbay:home:42");
            OverlayPresentationPolicy.GetPaintIdentityMarkerColor(
                paintIdentity,
                0,
                out var red,
                out var green,
                out var blue);
            Assert.Equal(244, red);
            Assert.Equal(160, green);
            Assert.Equal(208, blue);
            Assert.True(OverlayPresentationPolicy.PaintIdentityMarkerColorMatches(
                paintIdentity,
                0,
                red,
                green,
                blue,
                255));
            Assert.False(OverlayPresentationPolicy.PaintIdentityMarkerColorMatches(
                paintIdentity,
                0,
                255,
                255,
                255,
                255));
        }

        [Theory]
        [InlineData(15, 10, 115, 8)]
        [InlineData(24, 16, 184, 12)]
        [InlineData(36, 24, 276, 18)]
        [InlineData(48, 32, 368, 24)]
        public void PaintIdentityRasterDetectorSupportsHighDpiPhysicalPixels(
            int stride,
            int cellWidth,
            int markerWidth,
            int markerHeight)
        {
            const int width = 2560;
            const int height = 1440;
            var paintIdentity = OverlayPresentationPolicy.MenuPaintIdentity(
                12,
                "gbay-startup");
            var markerLeft = width - markerWidth;
            var markerTop = height - markerHeight;

            uint ReadPixel(int x, int y)
            {
                if (y < markerTop || y >= height)
                    return 0;
                for (var byteIndex = 0; byteIndex < 8; byteIndex++)
                {
                    var cellLeft = markerLeft + byteIndex * stride;
                    if (x < cellLeft || x >= cellLeft + cellWidth)
                        continue;
                    OverlayPresentationPolicy.GetPaintIdentityMarkerColor(
                        paintIdentity,
                        byteIndex,
                        out var red,
                        out var green,
                        out var blue);
                    return 0xFF000000U |
                        ((uint)red << 16) |
                        ((uint)green << 8) |
                        (uint)blue;
                }
                return 0;
            }

            Assert.True(OverlayPresentationPolicy.HasPaintIdentityMarker(
                width,
                height,
                paintIdentity,
                ReadPixel));
            Assert.False(OverlayPresentationPolicy.HasPaintIdentityMarker(
                width,
                height,
                OverlayPresentationPolicy.MenuPaintIdentity(13, "gbay-startup"),
                ReadPixel));
        }

        [Theory]
        [InlineData(24, 24, true)]
        [InlineData(24, 12, true)]
        [InlineData(24, 11, false)]
        [InlineData(3, 2, true)]
        [InlineData(3, 1, false)]
        [InlineData(0, 0, false)]
        public void DesktopPaintProofRequiresAReadableMatchingMajority(
            int readable,
            int matching,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.HasConcreteDesktopPixels(
                    readable,
                    matching));
        }

        [Theory]
        [InlineData(false, 0, true, 0)]
        [InlineData(true, -1, true, 3)]
        [InlineData(true, 0, false, 2)]
        [InlineData(true, 0, true, 1)]
        public void CompositionDeviceStateIsTypedBeforeRecovery(
            bool available,
            int hresult,
            bool valid,
            int expected)
        {
            Assert.Equal(
                (CompositionDeviceState)expected,
                OverlayPresentationPolicy.ClassifyCompositionDeviceState(
                    available,
                    hresult,
                    valid));
        }

        [Theory]
        [InlineData(7, null, true, false, true)]
        [InlineData(7, 6, true, false, true)]
        [InlineData(7, 7, true, false, false)]
        [InlineData(7, null, false, false, false)]
        [InlineData(7, null, true, true, false)]
        [InlineData(-1, null, true, false, false)]
        public void RootVisualRecoveryIsBoundedPerFailedSurfaceGeneration(
            int failedGeneration,
            int? attemptedGeneration,
            bool visible,
            bool desktopConcrete,
            bool expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.ShouldAttemptRootVisualRebind(
                    failedGeneration,
                    attemptedGeneration,
                    visible,
                    desktopConcrete));
        }

        [Theory]
        [InlineData(false, false, false, false, "none")]
        [InlineData(true, false, false, false, "dom_ready_no_pixel_evidence")]
        [InlineData(true, true, false, false, "browser_surface_captured_not_desktop")]
        [InlineData(true, true, true, false, "browser_surface_pixels_verified_not_desktop")]
        [InlineData(true, true, true, true, "desktop_presentation_verified")]
        public void PresentationReceiptNeverConflatesBrowserAndDesktopPixels(
            bool domReady,
            bool browserCaptured,
            bool browserVerified,
            bool desktopVerified,
            string expected)
        {
            Assert.Equal(
                expected,
                OverlayPresentationPolicy.DescribePresentationEvidence(
                    domReady,
                    browserCaptured,
                    browserVerified,
                    desktopVerified));
        }

        [Fact]
        public void LiveHostNeverRunsSynchronousDesktopPixelSampling()
        {
            Assert.False(OverlayPresentationPolicy.UseLiveDesktopPixelSampling);
            Assert.False(OverlayPresentationPolicy.UseLiveBrowserCaptureDiagnostics);
        }

        [Theory]
        [InlineData(-1f, 0f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(2f, 1f)]
        [InlineData(float.NaN, 0f)]
        [InlineData(float.PositiveInfinity, 0f)]
        public void WindowedPointerCoordinatesAreFiniteAndBounded(
            float value,
            float expected)
        {
            Assert.Equal(expected, WindowedInputPolicy.Normalize(value));
        }

        [Fact]
        public void WindowedPointerForwardsMovementAndDiscreteActionsOnly()
        {
            Assert.True(WindowedInputPolicy.ShouldForward(
                0f, 0f, false, 0.5f, 0.5f, false, false, 0));
            Assert.False(WindowedInputPolicy.ShouldForward(
                0.5f, 0.5f, true, 0.5f, 0.5f, false, false, 0));
            Assert.True(WindowedInputPolicy.ShouldForward(
                0.5f, 0.5f, true, 0.5f, 0.5f, true, false, 0));
            Assert.True(WindowedInputPolicy.ShouldForward(
                0.5f, 0.5f, true, 0.5f, 0.5f, false, true, 0));
            Assert.True(WindowedInputPolicy.ShouldForward(
                0.5f, 0.5f, true, 0.5f, 0.5f, false, false, 120));
            Assert.False(WindowedInputPolicy.ShouldForward(
                0.5f, 0.5f, true,
                0.5f + (WindowedInputPolicy.PositionEpsilon / 2f),
                0.5f,
                false,
                false,
                0));
            Assert.True(WindowedInputPolicy.ShouldForward(
                0.5f, 0.5f, true,
                0.5f + WindowedInputPolicy.PositionEpsilon,
                0.5f,
                false,
                false,
                0));
        }

        [Theory]
        [InlineData(true, true, false, "menu-1", "menu-1", "menu-1", true)]
        [InlineData(true, false, false, "menu-1", "menu-1", "menu-1", false)]
        [InlineData(true, true, true, "menu-1", "menu-1", "menu-1", false)]
        [InlineData(false, true, false, "menu-1", "menu-1", "menu-1", false)]
        [InlineData(true, true, false, "menu-1", null, null, false)]
        [InlineData(true, true, false, "menu-1", "menu-1", null, false)]
        [InlineData(true, true, false, "menu-2", "menu-1", "menu-1", false)]
        [InlineData(true, true, false, "menu-2", "menu-2", "menu-1", false)]
        [InlineData(true, true, false, null, null, null, false)]
        [InlineData(true, true, false, "", "", "", false)]
        public void ProviderPointerRequiresExactAcceptedAndCommittedPresentation(
            bool requestedVisible,
            bool actualVisible,
            bool revealPending,
            string? activePresentationId,
            string? acceptedPresentationId,
            string? committedPresentationId,
            bool expected)
        {
            Assert.Equal(
                expected,
                WindowedInputPolicy.ShouldForwardProviderPointer(
                    requestedVisible,
                    actualVisible,
                    revealPending,
                    activePresentationId,
                    acceptedPresentationId,
                    committedPresentationId));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void BootstrapAndProviderPointersNeverCaptureTheExternalHostWindow(
            bool bootstrapCaptureRequested,
            bool providerPointerIsolationActive)
        {
            Assert.False(WindowedInputPolicy.ShouldCaptureHostHitTest(
                bootstrapCaptureRequested,
                providerPointerIsolationActive));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PointerLeasesNeverTreatTheExternalHostAsInteractionForeground(
            bool bootstrapCaptureRequested,
            bool providerPointerIsolationActive)
        {
            Assert.False(WindowedInputPolicy.AllowsInteractionForeground(
                bootstrapCaptureRequested,
                providerPointerIsolationActive));
        }

        [Theory]
        [InlineData(true, false, false, false, false, true)]
        [InlineData(false, true, true, true, true, false)]
        [InlineData(false, false, true, true, true, false)]
        [InlineData(false, true, false, true, true, false)]
        [InlineData(false, true, true, false, true, false)]
        [InlineData(false, true, true, true, false, false)]
        public void ManagedPointerSamplingTrustsOnlyTheAuthenticatedVisibleProviderLease(
            bool gameForeground,
            bool interactiveLease,
            bool requestedVisible,
            bool actualVisible,
            bool trustedProviderForeground,
            bool expected)
        {
            Assert.Equal(
                expected,
                WindowedInputPolicy.AllowsManagedPointerSampling(
                    gameForeground,
                    interactiveLease,
                    requestedVisible,
                    actualVisible,
                    trustedProviderForeground));
        }

        [Theory]
        [InlineData(96368u, 96368u, true)]
        [InlineData(96368u, 33772u, false)]
        [InlineData(96368u, 1234u, false)]
        [InlineData(0u, 0u, false)]
        public void ProviderForegroundIdentityRequiresTheAuthenticatedPipeServer(
            uint authenticatedHostProcessId,
            uint foregroundProcessId,
            bool expected)
        {
            Assert.Equal(
                expected,
                WindowedInputPolicy.IsTrustedProviderForeground(
                    authenticatedHostProcessId,
                    foregroundProcessId));
        }

        [Fact]
        public void PointerBridgeUsesOnlyTypedDomEventNames()
        {
            Assert.Equal("input.pointer", WindowedInputPolicy.ProviderPointerEventName);
            Assert.Equal("input.pointerReset", WindowedInputPolicy.ProviderPointerResetEventName);
            Assert.Equal("input.bootstrapPointer", WindowedInputPolicy.BootstrapPointerEventName);
            Assert.Equal(
                "input.bootstrapPointerReset",
                WindowedInputPolicy.BootstrapPointerResetEventName);
        }

        [Fact]
        public void ProviderPointerEventClampsAndPreservesTheCompleteTypedPayload()
        {
            var message = JObject.Parse(
                WindowedInputPolicy.SerializeProviderPointerEvent(
                    float.NaN,
                    1.5f,
                    pressed: true,
                    released: false,
                    wheelDelta: 2400));
            var payload = Assert.IsType<JObject>(message["payload"]);

            Assert.Equal("event", message.Value<string>("kind"));
            Assert.Equal("input.pointer", message.Value<string>("event"));
            Assert.Equal(0f, payload.Value<float>("x"));
            Assert.Equal(1f, payload.Value<float>("y"));
            Assert.True(payload.Value<bool>("pressed"));
            Assert.False(payload.Value<bool>("released"));
            Assert.Equal(1200, payload.Value<int>("wheelDelta"));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void InputParentAlwaysRemainsTheSameProcessOverlay(
            bool actualVisible,
            bool revealPending,
            bool ignoredLegacyExpectation)
        {
            var gameWindow = new IntPtr(0x1111);
            var overlayWindow = new IntPtr(0x2222);

            _ = ignoredLegacyExpectation;

            Assert.Equal(
                overlayWindow,
                WindowedInputPolicy.ResolveInputParent(
                    actualVisible,
                    revealPending,
                    gameWindow,
                    overlayWindow));
        }

        [Fact]
        public void MissingGameWindowFallsBackToTheOverlayInputParent()
        {
            var overlayWindow = new IntPtr(0x2222);

            Assert.Equal(
                overlayWindow,
                WindowedInputPolicy.ResolveInputParent(
                    actualVisible: true,
                    revealPending: false,
                    gameWindow: IntPtr.Zero,
                    overlayWindow));
        }

        [Fact]
        public void ColdPreloadNeverParentsTheHiddenBrowserToGta()
        {
            var gameWindow = new IntPtr(0x1111);
            var overlayWindow = new IntPtr(0x2222);

            Assert.Equal(
                overlayWindow,
                WindowedInputPolicy.ResolveInputParent(
                    actualVisible: false,
                    revealPending: false,
                    gameWindow,
                    overlayWindow));
        }

        [Fact]
        public void VisibleProviderInputDoesNotCrossTheProcessBoundary()
        {
            var gameWindow = new IntPtr(0x1111);
            var overlayWindow = new IntPtr(0x2222);

            Assert.Equal(
                overlayWindow,
                WindowedInputPolicy.ResolveInputParent(
                    actualVisible: true,
                    revealPending: true,
                    gameWindow,
                    overlayWindow));
        }

        [Fact]
        public void NavigationPolicyAllowsOnlyOneInlineDocument()
        {
            var pending = true;

            Assert.True(WebView2LocalPagePolicy.IsAllowedNavigation(
                "about:blank", ref pending));
            Assert.True(pending);
            Assert.True(WebView2LocalPagePolicy.IsAllowedNavigation(
                "data:text/html,reactor", ref pending));
            Assert.False(pending);
            Assert.False(WebView2LocalPagePolicy.IsAllowedNavigation(
                "data:text/html,replacement", ref pending));
        }

        [Theory]
        [InlineData("https://reactorv.local/assets/app.js", true)]
        [InlineData("https://reactorv.local.evil.example/app.js", false)]
        [InlineData("http://reactorv.local/app.js", false)]
        [InlineData("https://example.com/", false)]
        [InlineData("file:///C:/temp/index.html", false)]
        [InlineData("javascript:alert(1)", false)]
        public void NavigationPolicyPinsTheMappedOrigin(string uri, bool expected)
        {
            var pending = false;

            Assert.Equal(expected, WebView2LocalPagePolicy.IsAllowedNavigation(
                uri, ref pending));
        }

        [Theory]
        [InlineData("about:blank", true)]
        [InlineData("data:text/html,reactor", true)]
        [InlineData("https://reactorv.local/", true)]
        [InlineData("https://example.com/", false)]
        [InlineData("file:///C:/temp/index.html", false)]
        public void BridgeMessagesRequireTheTrustedDocumentSource(
            string source,
            bool expected)
        {
            Assert.Equal(expected, WebView2LocalPagePolicy.IsTrustedMessageSource(source));
        }

        [Fact]
        public void InlineDocumentInjectsBaseAndRestrictivePolicy()
        {
            using var fixture = TemporaryDirectory.Create();
            File.WriteAllText(
                Path.Combine(fixture.Path, "index.html"),
                "<!doctype html><html><head><title>Fixture</title></head>" +
                "<body><div id=\"root\"></div></body></html>");

            var html = WebView2LocalPagePolicy.InlineIndexHtml(fixture.Path);

            Assert.Contains("Content-Security-Policy", html);
            Assert.Contains("default-src 'none'", html);
            Assert.Contains("script-src https://reactorv.local", html);
            Assert.Contains("frame-src 'none'", html);
            Assert.Contains("<base href=\"https://reactorv.local/\">", html);
            Assert.True(
                html.IndexOf("Content-Security-Policy", StringComparison.Ordinal) <
                html.IndexOf("<title>", StringComparison.Ordinal));
        }

        [Fact]
        public void InlineDocumentRejectsMalformedIndex()
        {
            using var fixture = TemporaryDirectory.Create();
            File.WriteAllText(
                Path.Combine(fixture.Path, "index.html"),
                "<html><body>missing head</body></html>");

            Assert.Throws<InvalidOperationException>(
                () => WebView2LocalPagePolicy.InlineIndexHtml(fixture.Path));
        }

        [Fact]
        public async Task ReadinessIgnoresEmptyMarkersUntilPageIsReady()
        {
            var results = new Queue<string>(new[]
            {
                "null",
                "{}",
                "{\"readyState\":\"complete\",\"rootChildren\":1}",
            });

            var result = await WebView2ReadinessPolicy.WaitForMarkerAsync(
                () => Task.FromResult(results.Dequeue()),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);

            Assert.Contains("rootChildren", result);
            Assert.Empty(results);
        }

        [Fact]
        public async Task ReadinessTimeoutNeverBecomesAFalseSuccess()
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                WebView2ReadinessPolicy.WaitForMarkerAsync(
                    () => Task.FromResult("null"),
                    TimeSpan.Zero,
                    TimeSpan.Zero));
        }

        [Fact]
        public void BrowserFailureGetsOneSoftwareRecoveryBeforeCircuitOpens()
        {
            Assert.True(WebView2ProcessFailurePolicy.CanRecover(0));
            Assert.False(WebView2ProcessFailurePolicy.CanRecover(1));
            Assert.False(WebView2ProcessFailurePolicy.CanRecover(-1));
            Assert.InRange(
                WebView2ProcessFailurePolicy.BrowserExitTimeoutMilliseconds,
                250,
                5000);
            Assert.InRange(
                WebView2ProcessFailurePolicy.RendererReloadTimeoutMilliseconds,
                250,
                5000);
            Assert.Contains("--disable-gpu", WebView2ProcessFailurePolicy.SoftwareCompositionArguments);
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        [InlineData(false, true, true)]
        [InlineData(false, false, false)]
        public void PersistentPresentedOverlayAvoidsTheHiddenShowGpuSurfaceBug(
            bool persistentPresentedOverlay,
            bool recovering,
            bool expected)
        {
            Assert.Equal(
                expected,
                WebView2ProcessFailurePolicy.ShouldUseSoftwareComposition(
                    persistentPresentedOverlay,
                    recovering));
        }

        [Theory]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        public void DuplicateOrStaleProcessFailuresCannotConsumeRecoveryTwice(
            bool recoveryQueuedOrInProgress,
            bool senderIsCurrentGeneration,
            bool expected)
        {
            Assert.Equal(
                expected,
                WebView2ProcessFailurePolicy.ShouldAcceptFailure(
                    recoveryQueuedOrInProgress,
                    senderIsCurrentGeneration));
        }

        [Theory]
        [InlineData(4, 4, true, true, true)]
        [InlineData(3, 4, true, true, false)]
        [InlineData(4, 4, false, true, false)]
        [InlineData(4, 4, true, false, false)]
        public void NavigationCompletionIsBoundToItsControllerGeneration(
            int callbackGeneration,
            int currentGeneration,
            bool coreIsCurrent,
            bool controlIsCurrent,
            bool expected)
        {
            Assert.Equal(
                expected,
                WebView2ProcessFailurePolicy.IsCurrentControllerGeneration(
                    callbackGeneration,
                    currentGeneration,
                    coreIsCurrent,
                    controlIsCurrent));
        }

        [Theory]
        [InlineData(false, false, false, true)]
        [InlineData(true, false, false, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, true, true, true)]
        public void RecoveredMenuWaitsForCurrentPresentationPaintAcknowledgement(
            bool recoveryInProgress,
            bool hasActivePresentation,
            bool paintAcknowledged,
            bool expected)
        {
            Assert.Equal(
                expected,
                WebView2ProcessFailurePolicy.CanRevealRecoveredSurface(
                    recoveryInProgress,
                    hasActivePresentation,
                    paintAcknowledged));
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            private TemporaryDirectory(string path) => Path = path;

            public string Path { get; }

            public static TemporaryDirectory Create()
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "ReactorV-Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                return new TemporaryDirectory(path);
            }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
        }
    }
}
