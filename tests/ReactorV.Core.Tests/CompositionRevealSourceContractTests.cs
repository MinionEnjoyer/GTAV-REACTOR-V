using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class CompositionRevealSourceContractTests
{
    [Fact]
    public void CompositionCompletionIsRestrictedToQualifiedPresentationBoundaries()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var host = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "CompositionWebViewHost.cs"));

        Assert.Contains("_webView.WaitForCommitCompletion(", overlay);
        Assert.DoesNotContain("WaitForCommitCompletionAsync", overlay);
        Assert.Contains("completion_wait=True", overlay);
        Assert.DoesNotContain("RevealCompositionSettleMilliseconds", overlay);
        Assert.Contains("webview_reveal_commit_queued", overlay);
        Assert.Contains("dispatch_boundary=winforms-begin-invoke", overlay);

        var presentationReady = overlay.IndexOf(
            "webview_menu_presentation_ready_received",
            StringComparison.Ordinal);
        var wait = overlay.IndexOf(
            "webview_reveal_composition_wait_completed",
            StringComparison.Ordinal);
        var committed = overlay.IndexOf(
            "webview_reveal_committed",
            StringComparison.Ordinal);
        var qualificationMethod = overlay.IndexOf(
            "public async Task<bool> VerifyBootstrapSurfacePixelsAsync(",
            StringComparison.Ordinal);
        var qualificationWait = overlay.IndexOf(
            "_webView.WaitForCommitCompletion(",
            qualificationMethod,
            StringComparison.Ordinal);
        var qualificationCapture = overlay.IndexOf(
            "_webView.CapturePreviewAsync()",
            qualificationWait,
            StringComparison.Ordinal);
        var commitMethod = overlay.IndexOf(
            "private void CommitDeferredReveal(",
            StringComparison.Ordinal);
        var waitCall = overlay.IndexOf(
            "_webView.WaitForCommitCompletion(",
            commitMethod,
            StringComparison.Ordinal);
        var cancellationGuard = overlay.IndexOf(
            "generation != _revealGeneration",
            commitMethod,
            StringComparison.Ordinal);
        var finalizer = overlay.IndexOf(
            "private async void FinalizeDeferredRevealAfterBrowserEventDrain(",
            waitCall,
            StringComparison.Ordinal);
        var showBoundaryLock = overlay.IndexOf(
            "lock (_revealIngressSync)",
            finalizer,
            StringComparison.Ordinal);
        var promotion = overlay.IndexOf(
            "var promoted = NativeMethods.SetWindowPos(",
            showBoundaryLock,
            StringComparison.Ordinal);
        Assert.True(presentationReady >= 0 && wait > presentationReady);
        Assert.True(committed > wait);
        Assert.True(qualificationMethod >= 0 && qualificationWait > qualificationMethod);
        Assert.True(qualificationCapture > qualificationWait);
        Assert.True(commitMethod >= 0 && cancellationGuard > commitMethod);
        Assert.True(waitCall > cancellationGuard);
        Assert.True(finalizer > waitCall);
        Assert.True(
            showBoundaryLock > finalizer && promotion > showBoundaryLock);
        var postFenceIdentity = overlay.IndexOf(
            "post-fence-identity-mismatch",
            waitCall,
            StringComparison.Ordinal);
        var postFenceForeground = overlay.IndexOf(
            "NativeMethods.IsIconic(_gtaWindow)",
            waitCall,
            StringComparison.Ordinal);
        Assert.True(
            postFenceIdentity > waitCall && postFenceIdentity < promotion);
        Assert.True(
            postFenceForeground > waitCall && postFenceForeground < promotion);
        Assert.Contains("RevealIdentityMatches(", overlay);
        Assert.Contains("CancelPendingRevealForIdentityChange(", overlay);
        Assert.Contains("webview_reveal_identity_superseded", overlay);
        Assert.Contains("webview_provider_boundary_ignored", overlay);
        Assert.Contains("CompositionDeviceRecoveryOutcome.NotRequired", overlay);
        Assert.Contains("SignalRevealIngress()", overlay);
        Assert.Contains("_pendingRevealIngress", overlay);
        Assert.Contains("lock (_revealIngressSync)", overlay);
        Assert.Contains("deferred-commit-ingress-superseded", overlay);
        Assert.Contains("post-fence-ingress-superseded", overlay);
        Assert.Contains("show-boundary-ingress-superseded", overlay);
        Assert.Contains("_finalRevealIngressBoundary?.Invoke()", overlay);
        Assert.Contains("HasLiveBrowserSurface()", overlay);
        Assert.Contains("webview_reveal_browser_health_withheld", overlay);
        Assert.Contains("show-boundary-browser-or-renderer-unavailable", overlay);
        Assert.Contains("_browserSurfaceHealthGeneration", overlay);
        Assert.Contains("FinalizeDeferredRevealAfterBrowserEventDrain(", overlay);
        Assert.Contains("webview_reveal_browser_event_drain_queued", overlay);
        Assert.Contains("native-postmessage-post-fence", overlay);
        Assert.Contains("WmFinalizeRevealAfterBrowserDrain", overlay);
        Assert.Contains("TryQueueNativeBrowserEventDrain(", overlay);
        Assert.Contains("passesRemaining: 2", overlay);
        Assert.Contains("post-fence-health-generation-superseded", overlay);
        Assert.Contains("VerifyFinalRevealSurfacePixelsAsync(", overlay);
        Assert.Contains("webview_final_reveal_pixels_verified", overlay);
        Assert.Contains("webview_final_reveal_pixel_probe_timeout", overlay);
        Assert.Contains("webview_reveal_post_pixel_drain_queued", overlay);
        Assert.Contains("native-postmessage-post-pixel-proof", overlay);
        Assert.Contains("HandleFinalRevealPixelProofFailure(", overlay);
        Assert.Contains("webview_final_reveal_pixel_retry", overlay);
        Assert.Contains("webview_final_reveal_offscreen_lease_retained", overlay);
        Assert.Contains("webview_final_reveal_offscreen_lease_promoted", overlay);
        Assert.Contains("webview_final_reveal_offscreen_lease_revoked", overlay);
        Assert.Contains("webview_composition_failure_ignored_stale", overlay);
        Assert.Contains("_webView.CapturePreviewAsync()", overlay);

        Assert.Contains("[PreserveSig] int WaitForCommitCompletion();", host);
        Assert.Contains("return _composition.WaitForCommitCompletion();", host);
        Assert.DoesNotContain("Task.Run(", host);
        Assert.DoesNotContain("WaitForCommitCompletionAsync", host);
        Assert.Contains("composition.ReplaceRoot(controller);", host);
    }

    [Fact]
    public void CrossThreadOwnershipEdgesInvalidateAnInFlightRevealBeforeQueueDispatch()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "WindowedOverlaySession.cs"));
        var preloader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Preloader",
            "Program.cs"));
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));

        Assert.Contains("window.SignalRevealIngress()", session);
        Assert.Contains("window.SignalHostMessageIngress(json)", session);
        Assert.Contains("queuedWindow.ApplyRevealIngress()", session);
        Assert.Contains("queuedWindow.ResumeRevealAfterIngress()", session);
        Assert.Contains("signalRevealIngress: true", preloader);
        Assert.Contains("hostMessageIngress: json", preloader);
        Assert.Contains("window.SignalRevealIngress();", preloader);
        Assert.Contains("window.SignalHostMessageIngress(hostMessageIngress!)", preloader);
        Assert.Contains("queuedWindow.ApplyRevealIngress(ingressAnnouncements)", preloader);
        Assert.Contains("queuedWindow.ResumeRevealAfterIngress()", preloader);
        Assert.Contains("PollHostSignalsFromWorker", preloader);
        Assert.Contains("TryAnnounceHostSignalsAtRevealBoundary", preloader);
        Assert.Contains("finalRevealIngressBoundary:", preloader);
        Assert.Contains("private bool PollHostSignals()", preloader);
        Assert.Contains("observed = true;", preloader);
        Assert.Contains("scanWindow.ReserveRevealIngressScan();", preloader);
        Assert.Contains("signalObserved = PollHostSignals();", preloader);
        Assert.Contains("scanWindow.ReleaseRevealIngressScan();", preloader);
        Assert.Contains("if (signalObserved || revealResumeRequired)", preloader);
        Assert.Contains("Interlocked.CompareExchange(\n                    ref _hostSignalPollActive", preloader);
        Assert.Contains("public void ReserveRevealIngressScan()", overlay);
        Assert.Contains("public bool ReleaseRevealIngressScan()", overlay);
        Assert.Contains("_revealDeferredForIngress = true;", overlay);
        Assert.DoesNotContain("PollHostSignals();\n                var deadlineArmed", preloader);
    }

    [Fact]
    public void BootstrapPixelProbeParksPromotionAndCoalescesAsyncReadyEdges()
    {
        var root = FindRepositoryRoot();
        var preloader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Preloader",
            "Program.cs"));
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));

        Assert.Contains(
            "HostSurfaceIntentPolicy.ShouldParkForBootstrapPixelProof(",
            preloader);
        Assert.Contains(
            "bootstrap_host_surface_pixel_verification_coalesced",
            preloader);
        Assert.Contains("_hostSurfacePixelVerificationGeneration", preloader);
        Assert.Contains("PostHostSurface(window, \"none\");", preloader);
        Assert.Contains("webview_bootstrap_pixel_probe_cleanup_skipped", overlay);
        Assert.Contains("OwnsBootstrapPixelProbeLease(", overlay);
        Assert.Contains("MaximumBootstrapPixelProbeAttempts = 2", overlay);

        var retireMethod = preloader.IndexOf(
            "private void RetireBootstrapSurface(",
            StringComparison.Ordinal);
        var requestMethod = preloader.IndexOf(
            "private void RequestHostSurface(",
            retireMethod,
            StringComparison.Ordinal);
        Assert.True(retireMethod >= 0 && requestMethod > retireMethod);
        var retireBody = preloader.Substring(
            retireMethod,
            requestMethod - retireMethod);
        Assert.Contains("HostSurfaceIntentPolicy.RetirementHandoff(hide)", retireBody);
        Assert.Contains("payload[\"handoff\"] = HostSurfaceIntentPolicy.PresentationHandoff", preloader);
        Assert.DoesNotContain("_hostSurfaceMode = \"none\";", retireBody);

        var revealMethod = overlay.IndexOf(
            "private void BeginDeferredReveal()",
            StringComparison.Ordinal);
        var boundsMethod = overlay.IndexOf(
            "private void SynchronizeBounds()",
            StringComparison.Ordinal);
        var hideProbeLeaseBeforeBounds = overlay.IndexOf(
            "RetireUncommittedNativeVisibilityLease(\"bounds-sync\");",
            boundsMethod,
            StringComparison.Ordinal);
        var prepareSurface = overlay.IndexOf(
            "PrepareSurface(target);",
            boundsMethod,
            StringComparison.Ordinal);
        var hideProbeLeaseAtReveal = overlay.IndexOf(
            "RetireUncommittedNativeVisibilityLease(\"deferred-reveal\");",
            revealMethod,
            StringComparison.Ordinal);
        var promote = overlay.IndexOf(
            "var promoted = NativeMethods.SetWindowPos(",
            revealMethod,
            StringComparison.Ordinal);
        Assert.True(boundsMethod >= 0 && hideProbeLeaseBeforeBounds > boundsMethod);
        Assert.True(prepareSurface > hideProbeLeaseBeforeBounds);
        Assert.True(revealMethod >= 0 && hideProbeLeaseAtReveal > revealMethod);
        Assert.True(promote > hideProbeLeaseAtReveal);
        Assert.Contains("RevokeFinalRevealOffscreenLease(reason);", overlay);
        Assert.Contains(
            "webview_bootstrap_probe_visibility_retired",
            overlay);
    }

    [Fact]
    public void RevealProofIsBoundToExactPaintIdentityAndTargetSize()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var app = File.ReadAllText(Path.Combine(root, "web", "src", "App.tsx"));
        var marker = File.ReadAllText(Path.Combine(
            root,
            "web",
            "src",
            "menu",
            "PaintIdentityMarker.tsx"));

        Assert.Contains("evidence.PaintIdentityMarkerMatched", overlay);
        Assert.Contains("targetSizeMatches", overlay);
        Assert.Contains("_bootstrapPaintProofWidth", overlay);
        Assert.Contains("_bootstrapPaintProofCompositionGeneration", overlay);
        Assert.Contains("_finalRevealOffscreenLeaseRootVisualRevision", overlay);
        Assert.Contains("_webView.CompositionGeneration", overlay);
        Assert.Contains("_webView.RootVisualRevision", overlay);
        Assert.Contains("HasPaintIdentityMarker(", overlay);
        Assert.Contains("MenuPaintIdentity(", overlay);
        Assert.Contains("HostPaintIdentity(", overlay);
        Assert.Contains("providerSessionGeneration", overlay);
        Assert.Contains("surfaceGeneration={hostSurfaceGeneration}", app);
        Assert.Contains("surfaceView !== 'setup-status'", app);
        Assert.Contains("resolveVisiblePaintIdentity(", app);
        Assert.Contains("data-reactor-paint-fingerprint", marker);
        Assert.Contains("reactor-paint-identity-marker", marker);
    }

    [Fact]
    public void FinalBrowserPixelProofNeverDemotesOrRepromotesTheOverlayZOrder()
    {
        var overlay = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var proof = MethodRegion(
            overlay,
            "private async Task<bool> VerifyFinalRevealSurfacePixelsAsync(",
            "private async void FinalizeDeferredRevealAfterBrowserEventDrain(");

        Assert.Contains("_webView.CapturePreviewAsync()", proof);
        Assert.Contains("Show();", proof);
        Assert.Contains("TryAcquireFinalRevealOffscreenLease(", proof);
        Assert.Contains("webview_final_reveal_offscreen_lease_retained", proof);
        Assert.DoesNotContain("Hide();", proof);
        Assert.DoesNotContain("ApplyOverlayTopMost(false)", proof);
        Assert.DoesNotContain("ApplyOverlayTopMost(true)", proof);
        Assert.True(
            proof.IndexOf(
                "TryAcquireFinalRevealOffscreenLease(",
                StringComparison.Ordinal) <
            proof.IndexOf("Show();", StringComparison.Ordinal),
            "The off-screen HWND must never become visible before its lease exists.");
    }

    [Fact]
    public void ColdInitializerPublishesFreshRootOnlyThroughTheVisibleOffscreenLease()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var host = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "CompositionWebViewHost.cs"));
        var preparation = MethodRegion(
            overlay,
            "private void BeginDeferredReveal()",
            "private void RetireUncommittedNativeVisibilityLease(");
        var proof = MethodRegion(
            overlay,
            "private async Task<bool> VerifyFinalRevealSurfacePixelsAsync(",
            "private async void FinalizeDeferredRevealAfterBrowserEventDrain(");
        var commit = MethodRegion(
            overlay,
            "private void CommitVerifiedRevealAfterPixelProof(",
            "private void HandleFinalRevealPixelProofFailure(");

        Assert.Contains(
            "deferFreshRootUntilVisibleLease:",
            preparation);
        Assert.Contains(
            "publishInitializerRootAfterFinalShow",
            preparation);
        Assert.DoesNotContain(
            "webview_initializer_root_published_while_hidden",
            overlay);
        Assert.DoesNotContain(
            "_coldHostVisibleRootPublishRequired = false;",
            preparation);

        var lease = proof.IndexOf(
            "TryAcquireFinalRevealOffscreenLease(",
            StringComparison.Ordinal);
        var show = proof.IndexOf("Show();", lease, StringComparison.Ordinal);
        var coldGate = proof.IndexOf(
            "var publishColdInitializerRoot =",
            show,
            StringComparison.Ordinal);
        var rebind = proof.IndexOf(
            "_webView.RebindRootVisual();",
            coldGate,
            StringComparison.Ordinal);
        var rootIdentity = proof.IndexOf(
            "TryAdvanceFinalRevealOffscreenLeaseRootVisualRevision(",
            rebind,
            StringComparison.Ordinal);
        var synchronize = proof.IndexOf(
            "_webView.SynchronizeBounds();",
            rootIdentity,
            StringComparison.Ordinal);
        var fence = proof.IndexOf(
            "_webView.WaitForCommitCompletion()",
            synchronize,
            StringComparison.Ordinal);
        var capture = proof.IndexOf(
            "_webView.CapturePreviewAsync()",
            fence,
            StringComparison.Ordinal);
        var verified = proof.IndexOf(
            "verified = leaseCurrent && evidence.IsConcrete",
            capture,
            StringComparison.Ordinal);
        var leaseCommit = commit.IndexOf(
            "CommitFinalRevealOffscreenLease(generation)",
            StringComparison.Ordinal);
        var clearColdFlag = commit.IndexOf(
            "_coldHostVisibleRootPublishRequired = false;",
            leaseCommit,
            StringComparison.Ordinal);

        Assert.True(lease >= 0 && show > lease);
        Assert.True(coldGate > show && rebind > coldGate);
        Assert.True(rootIdentity > rebind && synchronize > rootIdentity);
        Assert.True(fence > synchronize && capture > fence);
        Assert.True(verified > capture);
        Assert.True(leaseCommit >= 0 && clearColdFlag > leaseCommit);
        Assert.Equal(
            rebind,
            proof.LastIndexOf(
                "_webView.RebindRootVisual();",
                StringComparison.Ordinal));
        Assert.DoesNotContain("Hide();", proof);
        Assert.DoesNotContain(
            "_coldHostVisibleRootPublishRequired = false;",
            proof);
        Assert.Contains("FailCompositionReveal(", proof);
        Assert.Contains(
            "RevealCompositionRefresh.RebindRootVisual,",
            proof);
        Assert.Contains(
            "webview_initializer_root_published_while_visible_offscreen",
            proof);
        Assert.Contains(
            "webview_initializer_visible_offscreen_root_committed",
            commit);
        Assert.Contains(
            "_finalRevealOffscreenLeaseRootVisualRevision ==",
            overlay);
        Assert.Contains(
            "internal int RootVisualRevision =>",
            host);
        Assert.Contains(
            "_rootVisualRevision = unchecked(_rootVisualRevision + 1);",
            host);
    }

    [Fact]
    public void PixelQualifiedOffscreenWindowIsPromotedOnceAndRevokedFailClosed()
    {
        var overlay = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var commit = MethodRegion(
            overlay,
            "private void CommitVerifiedRevealAfterPixelProof(",
            "private void HandleFinalRevealPixelProofFailure(");
        var visibility = MethodRegion(
            overlay,
            "private void ApplyVisibility(bool visible)",
            "private void BeginDeferredReveal()");
        var boundsSync = MethodRegion(
            overlay,
            "private void SynchronizeBounds()",
            "private void MaintainOverlayZOrder(bool gameForeground)");
        var leaseAcquisition = MethodRegion(
            overlay,
            "private bool TryAcquireFinalRevealOffscreenLease(",
            "private bool OwnsFinalRevealOffscreenLease(");
        var leaseOwnership = MethodRegion(
            overlay,
            "private bool OwnsFinalRevealOffscreenLease(",
            "private void ClearFinalRevealOffscreenLease()");
        var leaseCommitMethod = MethodRegion(
            overlay,
            "private bool CommitFinalRevealOffscreenLease(",
            "private void QueueDeferredRevealCommit(");
        var zOrder = MethodRegion(
            overlay,
            "private void MaintainOverlayZOrder(bool gameForeground)",
            "private void PrepareSurface(Rectangle target)");
        var closing = MethodRegion(
            overlay,
            "protected override void OnFormClosing(FormClosingEventArgs args)",
            "protected override CreateParams CreateParams");
        var proofFailure = MethodRegion(
            overlay,
            "private void HandleFinalRevealPixelProofFailure(",
            "private bool RevealIdentityMatches(");
        var compositionFailure = MethodRegion(
            overlay,
            "private void FailCompositionReveal(",
            "private void ApplyOverlayTopMost(bool enabled)");

        var leaseGuard = commit.IndexOf(
            "OwnsFinalRevealOffscreenLease(generation)",
            StringComparison.Ordinal);
        var nativePromotion = commit.IndexOf(
            "NativeMethods.SetWindowPos(",
            leaseGuard,
            StringComparison.Ordinal);
        var noActivateFlag = commit.IndexOf(
            "NativeMethods.SwpNoActivate",
            nativePromotion,
            StringComparison.Ordinal);
        var parentPositionNotification = commit.IndexOf(
            "_webView.NotifyParentWindowPositionChanged();",
            noActivateFlag,
            StringComparison.Ordinal);
        var leaseCommit = commit.IndexOf(
            "CommitFinalRevealOffscreenLease(generation)",
            parentPositionNotification,
            StringComparison.Ordinal);
        var inputCommit = commit.IndexOf(
            "CommitProviderInputAfterRevealFence();",
            leaseCommit,
            StringComparison.Ordinal);

        Assert.True(leaseGuard >= 0);
        Assert.True(nativePromotion > leaseGuard);
        Assert.True(noActivateFlag > nativePromotion);
        Assert.True(parentPositionNotification > noActivateFlag);
        Assert.True(leaseCommit > parentPositionNotification);
        Assert.True(inputCommit > leaseCommit);
        Assert.Contains("NativeMethods.HwndTopMost", commit);
        Assert.DoesNotContain("NativeMethods.SwpShowWindow", commit);
        Assert.DoesNotContain("Show();", commit);
        Assert.DoesNotContain("Hide();", commit);
        Assert.DoesNotContain("RebindRootVisual()", commit);
        Assert.DoesNotContain("ReassertOverlayZOrder(", commit);
        var boundaryGuard = commit.IndexOf(
            "if (ingressBoundaryError == null &&",
            StringComparison.Ordinal);
        Assert.True(boundaryGuard >= 0 && boundaryGuard < nativePromotion);

        Assert.Contains(
            "RevokeFinalRevealOffscreenLease(\"visibility-hidden\");",
            visibility);
        Assert.Contains("RevokeFinalRevealOffscreenLease(", closing);
        Assert.Contains("RevokeFinalRevealOffscreenLease(", proofFailure);
        Assert.Contains("RevokeFinalRevealOffscreenLease(", compositionFailure);
        Assert.Contains("_actualVisible,", zOrder);
        Assert.DoesNotContain("_actualVisible || _revealPending", zOrder);

        var retainedLeaseBranch = boundsSync.IndexOf(
            "if (_finalRevealOffscreenLeaseActive)",
            StringComparison.Ordinal);
        var genericRetirement = boundsSync.IndexOf(
            "RetireUncommittedNativeVisibilityLease(\"bounds-sync\");",
            StringComparison.Ordinal);
        var prepareSurface = boundsSync.IndexOf(
            "PrepareSurface(target);",
            StringComparison.Ordinal);
        Assert.True(retainedLeaseBranch >= 0);
        Assert.True(genericRetirement > retainedLeaseBranch);
        Assert.True(prepareSurface > genericRetirement);
        Assert.Contains("OwnsFinalRevealOffscreenLease(retainedGeneration)", boundsSync);
        Assert.Contains("ApplyVisibility(false);", boundsSync);

        Assert.Contains("_finalRevealOffscreenLeaseActive || Visible", leaseAcquisition);
        Assert.Contains("RevealIdentityMatches(", leaseAcquisition);
        Assert.Contains("_revealPending && _desiredVisible && _browserReady", leaseOwnership);
        Assert.Contains("_finalRevealOffscreenLeaseTarget == _lastBounds", leaseOwnership);
        Assert.Contains("RevealIdentityMatches(", leaseOwnership);
        var commitActual = leaseCommitMethod.IndexOf(
            "_actualVisible = true;",
            StringComparison.Ordinal);
        var restartTimer = leaseCommitMethod.IndexOf(
            "_boundsTimer.Start();",
            StringComparison.Ordinal);
        Assert.True(commitActual >= 0 && restartTimer > commitActual);
    }

    [Fact]
    public void AcceptedPresentationResponseReachesTheBrowserBeforeInputCommitBegins()
    {
        var overlay = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var postJson = MethodRegion(
            overlay,
            "public void PostJson(string json)",
            "private void ObserveHostMessage(string json)");
        var browserPost = postJson.IndexOf(
            "core.PostWebMessageAsJson(json);",
            StringComparison.Ordinal);
        var observation = postJson.IndexOf(
            "ObserveHostMessage(json);",
            StringComparison.Ordinal);

        Assert.True(browserPost >= 0, "PostJson must deliver the response to WebView2.");
        Assert.True(
            observation > browserPost,
            "The host must not begin an accepted presentation commit before React receives the response that lets it paint the new presentation.");
    }

    [Fact]
    public void SurfaceReplacementPreservesOpenIntentWhileDismissalClearsIt()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var normalized = overlay.Replace("\r\n", "\n");

        Assert.Contains(
            "\"host-surface-replaced\",\n                                preserveDesiredVisibility: true",
            normalized);
        Assert.Contains(
            "\"presentation-replaced\",\n                                preserveDesiredVisibility: true",
            normalized);
        Assert.Contains(
            "\"presentation-dismissed\",\n                                preserveDesiredVisibility: replacementPending",
            normalized);
        Assert.Contains(
            "\"provider-disconnected\",\n",
            normalized);
        Assert.Contains("preserveDesiredVisibility: false", overlay);

        var cancellationMethod = overlay.IndexOf(
            "private void CancelPendingRevealForIdentityChange(",
            StringComparison.Ordinal);
        var nextMethod = overlay.IndexOf(
            "private bool HasPendingRevealIngress()",
            cancellationMethod,
            StringComparison.Ordinal);
        Assert.True(cancellationMethod >= 0 && nextMethod > cancellationMethod);
        var body = overlay.Substring(
            cancellationMethod,
            nextMethod - cancellationMethod);
        Assert.Contains("if (!preserveDesiredVisibility)", body);
        Assert.Contains("hasUncommittedRevealLease", body);
        Assert.Contains("_desiredVisible = false;", body);
        Assert.Contains("ApplyVisibility(false);", body);
        Assert.Contains("preserve_desired_visibility={preserveDesiredVisibility}", body);
        Assert.DoesNotContain("_actualVisible", body);
        Assert.DoesNotContain("!Visible", body);

        Assert.Contains("var replacementPending = string.Equals(", overlay);
        Assert.Contains("\"superseded\"", overlay);
        Assert.Contains("webview_reveal_waiting_for_menu_paint", overlay);
        Assert.Contains("webview_reveal_waiting_for_render_identity", overlay);
        Assert.Contains("webview_provider_disconnect_host_surface_preserved", overlay);
    }

    private static string MethodRegion(
        string source,
        string signature,
        string nextSignature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var end = source.IndexOf(nextSignature, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature '{signature}'.");
        Assert.True(end > start, $"Could not find method boundary '{nextSignature}'.");
        return source.Substring(start, end - start);
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate != null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "ReactorV.json")) &&
                Directory.Exists(Path.Combine(candidate.FullName, "src")))
            {
                return candidate.FullName;
            }
            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ReactorV source root for the composition reveal contract.");
    }
}
