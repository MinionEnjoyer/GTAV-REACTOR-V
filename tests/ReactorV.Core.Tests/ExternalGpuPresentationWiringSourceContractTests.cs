using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Protects the production wiring around the pure presentation/resize policies.
/// These assertions intentionally cover call ordering because briefly showing
/// both presenters produces a real double-rendered frame in fullscreen GTA.
/// </summary>
public sealed class ExternalGpuPresentationWiringSourceContractTests
{
    [Fact]
    public void External_browser_may_acknowledge_only_known_bootstrap_generations()
    {
        var server = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "BootstrapOverlayServer.cs")
            .Replace("\r\n", "\n");
        var app = ReadRepositoryFile("web", "src", "App.tsx")
            .Replace("\r\n", "\n");

        Assert.Contains(
            "PresentationReadyBrowserRole.ExternalGpuShadow &&\n" +
            "                                 HostSurfaceMode.RequiresPaintProof(mode)",
            server,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExternalSurfaceReady?.Invoke(mode, generation);",
            server,
            StringComparison.Ordinal);
        Assert.Contains(
            "The external GPU browser may acknowledge only known bootstrap surfaces; visibility remains host-owned.",
            server,
            StringComparison.Ordinal);
        Assert.Contains(
            "canAcknowledgeHostSurface(\n" +
            "      browserRole,\n" +
            "      surfaceView,",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_initializer_reveal_is_generation_size_and_fresh_frame_bound()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var setVisible = ExtractMethod(program, "private void SetBrowserVisible(");
        var complete = ExtractMethod(
            program,
            "private bool TryCompleteHostSurfaceReveal(");

        Assert.Contains("_webViewInitializerReadyGeneration", complete);
        Assert.Contains("_externalInitializerAckGeneration != generation", complete);
        Assert.Contains("_externalInitializerRefreshGeneration != generation", complete);
        Assert.Contains("_externalInitializerFreshGeneration != generation", complete);
        Assert.Contains("session.SurfaceWidth != targetWidth", complete);
        Assert.Contains("session.SurfaceHeight != targetHeight", complete);
        Assert.Contains("ExternalBootstrapPresentationGate.IsReady(", setVisible);
        Assert.Contains(
            "decision.Owner == BrowserPresentationOwner.ExternalGpuProvider",
            setVisible);
        Assert.Contains(
            "!previousPresentation.ExternalGpuVisible",
            setVisible);
    }


    [Fact]
    public void Provider_reveal_synchronizes_to_the_resolved_gta_client_before_visibility()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var wrapper = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "ExternalGpuBrowserSession.cs")
            .Replace("\r\n", "\n");

        Assert.Contains(
            "window.TryGetTargetClientSize(out width, out height)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrySynchronizeExternalGpuSurfaceSize(window, trigger);",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalGpuBrowserSession?.IsPresentationReady == true",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "visible && ProducerPresentationReady(producer)",
            wrapper,
            StringComparison.Ordinal);

        var setVisible = ExtractMethod(program, "private void SetBrowserVisible(");
        var synchronize = setVisible.IndexOf(
            "TrySynchronizeExternalGpuSurfaceSize(window, trigger);",
            StringComparison.Ordinal);
        var resolve = setVisible.IndexOf(
            "ExclusiveBrowserPresentationPolicy.Resolve(",
            StringComparison.Ordinal);
        var revealNative = setVisible.IndexOf(
            "_externalGpuBrowserSession?.SetVisible(true);",
            StringComparison.Ordinal);

        Assert.True(synchronize >= 0);
        Assert.True(resolve > synchronize);
        Assert.True(revealNative > resolve);
    }

    [Fact]
    public void Presenter_handoff_parks_the_non_owner_before_revealing_the_owner()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var setVisible = ExtractMethod(program, "private void SetBrowserVisible(");

        var nativeBranch = ExtractBlock(
            setVisible,
            "if (decision.ExternalGpuVisible)");
        var hideNative = nativeBranch.IndexOf(
            "_externalGpuBrowserSession?.SetVisible(false);",
            StringComparison.Ordinal);
        var parkWebView = nativeBranch.IndexOf(
            "window.SetExternalPresentationOwnership(true);",
            StringComparison.Ordinal);
        var showNative = nativeBranch.IndexOf(
            "_externalGpuBrowserSession?.SetVisible(true);",
            StringComparison.Ordinal);

        Assert.True(hideNative >= 0);
        Assert.True(parkWebView > hideNative);
        Assert.True(showNative > parkWebView);

        var webViewBranch = ExtractBlock(
            setVisible,
            "else if (decision.WebViewVisible)");
        var parkNative = webViewBranch.IndexOf(
            "_externalGpuBrowserSession?.SetVisible(false);",
            StringComparison.Ordinal);
        var releaseWebView = webViewBranch.IndexOf(
            "window.SetExternalPresentationOwnership(false);",
            StringComparison.Ordinal);
        var showWebView = webViewBranch.IndexOf(
            "window.SetOverlayVisible(true);",
            StringComparison.Ordinal);

        Assert.True(parkNative >= 0);
        Assert.True(releaseWebView > parkNative);
        Assert.True(showWebView > releaseWebView);
        Assert.Contains("exclusive_presenter_selected", setVisible);
    }

    [Fact]
    public void Client_size_changes_request_a_new_surface_and_commit_checks_exact_dimensions()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");

        Assert.Contains(
            "_hostWindow.ClientSizeChanged +=",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetBrowserVisible(\n" +
            "                                    window,\n" +
            "                                    _browserPresentationRequestedVisible,\n" +
            "                                    \"host-client-size-changed\")",
            program,
            StringComparison.Ordinal);

        var synchronize = ExtractMethod(
            program,
            "private bool TrySynchronizeExternalGpuSurfaceSize(");
        Assert.Contains(
            "!TryResolveExternalGpuSurfaceSize(window, out width, out height)",
            synchronize,
            StringComparison.Ordinal);
        Assert.Contains(
            "session.SurfaceWidth == width && session.SurfaceHeight == height",
            synchronize,
            StringComparison.Ordinal);
        Assert.Contains(
            "session.Resize(width, height)",
            synchronize,
            StringComparison.Ordinal);

        var commit = ExtractMethod(
            program,
            "private void TryCommitExternalProviderPresentation(");
        Assert.Contains("session?.IsPresentationReady != true", commit);
        Assert.Contains("session.SurfaceWidth != width", commit);
        Assert.Contains("session.SurfaceHeight != height", commit);
        Assert.Contains("external_gpu_provider_presentation_committed", commit);
    }

    [Fact]
    public void Target_window_discovery_resizes_the_hidden_external_surface_before_reveal()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var observe = ExtractMethod(
            program,
            "private void ObserveTargetWindowLifecycle(");
        var earlySync = ExtractMethod(
            program,
            "private void TrySynchronizeExternalGpuSurfaceFromTargetWindow(");
        var synchronize = ExtractMethod(
            program,
            "private bool TrySynchronizeExternalGpuSurfaceSize(");

        var capture = observe.IndexOf("var state = probe.Capture(", StringComparison.Ordinal);
        var journal = observe.IndexOf("journal.Observe(", StringComparison.Ordinal);
        var resize = observe.IndexOf(
            "TrySynchronizeExternalGpuSurfaceFromTargetWindow(state, reason);",
            StringComparison.Ordinal);

        Assert.True(capture >= 0);
        Assert.True(journal > capture);
        Assert.True(resize > journal);
        Assert.Contains("session?.IsActive != true", earlySync, StringComparison.Ordinal);
        Assert.Contains("!state.Exists", earlySync, StringComparison.Ordinal);
        Assert.Contains("session.SurfaceWidth == state.ClientWidth", earlySync, StringComparison.Ordinal);
        Assert.Contains("session.SurfaceHeight == state.ClientHeight", earlySync, StringComparison.Ordinal);
        Assert.Contains("state.ClientWidth,", earlySync, StringComparison.Ordinal);
        Assert.Contains("state.ClientHeight);", earlySync, StringComparison.Ordinal);
        Assert.Contains("int knownWidth = 0", synchronize, StringComparison.Ordinal);
        Assert.Contains("int knownHeight = 0", synchronize, StringComparison.Ordinal);
        Assert.Contains("var knownTargetSize = width > 0 && height > 0;", synchronize, StringComparison.Ordinal);
        Assert.Contains("session.SetVisible(false);", synchronize, StringComparison.Ordinal);
        Assert.Contains("session.Resize(width, height)", synchronize, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_id_readiness_refreshes_external_pixels_before_commit_is_attempted()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");

        var handlerStart = program.IndexOf(
            "_hostServer.DualBrowserPresentationReady +=",
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        var handlerEnd = program.IndexOf(
            "_hostServer.ProviderInputIntentArmRequested +=",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerEnd > handlerStart);
        var handler = program.Substring(
            handlerStart,
            handlerEnd - handlerStart);

        var postAcceptSubscription = handler.IndexOf(
            "_hostServer.ExternalGpuPostAcceptPaintReady +=",
            StringComparison.Ordinal);
        Assert.True(postAcceptSubscription >= 0);
        var dualReadyHandler = handler.Substring(0, postAcceptSubscription);
        Assert.DoesNotContain(
            "BeginExternalProviderPresentationRefresh(",
            dualReadyHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinueExternalProviderPresentationAfterPaint(",
            handler.Substring(postAcceptSubscription),
            StringComparison.Ordinal);
        var continueMethod = ExtractMethod(
            program,
            "private void ContinueExternalProviderPresentationAfterPaint(");
        Assert.Contains(
            "BeginExternalProviderPresentationRefresh(",
            continueMethod,
            StringComparison.Ordinal);
        var refreshMethod = ExtractMethod(
            program,
            "private void BeginExternalProviderPresentationRefresh(");
        var refresh = refreshMethod.IndexOf(
            "RefreshPresentation(",
            StringComparison.Ordinal);
        var commit = refreshMethod.IndexOf(
            "TryCommitExternalProviderPresentation(",
            StringComparison.Ordinal);

        Assert.True(refresh >= 0);
        Assert.True(commit > refresh);

        var commitMethod = ExtractMethod(
            program,
            "private void TryCommitExternalProviderPresentation(");
        Assert.Contains(
            "session?.IsPresentationReady != true",
            commitMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalFreshPresentationId",
            commitMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalFreshPresentationId,\n" +
            "                    presentationId,",
            commitMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Accelerated_refresh_waits_for_the_exact_post_accept_browser_paint()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var server = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "BootstrapOverlayServer.cs")
            .Replace("\r\n", "\n");
        var app = ReadRepositoryFile("web", "src", "App.tsx")
            .Replace("\r\n", "\n");

        var dualStart = program.IndexOf(
            "_hostServer.DualBrowserPresentationReady +=",
            StringComparison.Ordinal);
        var paintedStart = program.IndexOf(
            "_hostServer.ExternalGpuPostAcceptPaintReady +=",
            dualStart,
            StringComparison.Ordinal);
        Assert.True(dualStart >= 0 && paintedStart > dualStart);
        Assert.DoesNotContain(
            "BeginExternalProviderPresentationRefresh(",
            program.Substring(dualStart, paintedStart - dualStart),
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinueExternalProviderPresentationAfterPaint(",
            program.Substring(paintedStart),
            StringComparison.Ordinal);

        var serverPaint = ExtractMethod(
            server,
            "private bool TryAcceptExternalGpuPostAcceptPaint(");
        Assert.Contains(
            "browserRole != PresentationReadyBrowserRole.ExternalGpuShadow",
            serverPaint,
            StringComparison.Ordinal);
        Assert.Contains(
            "providerSessionGeneration != _providerSessionGeneration",
            serverPaint,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalGpuPostAcceptPaintGate.TryAcceptPostAcceptPaint(",
            serverPaint,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExternalGpuPostAcceptPaintReady?.Invoke(",
            serverPaint,
            StringComparison.Ordinal);

        var strictPaint = app.IndexOf(
            "void waitForHostSurfacePaint(",
            StringComparison.Ordinal);
        var publish = app.IndexOf(
            "bridge.markExternalProviderSurfacePainted(",
            strictPaint,
            StringComparison.Ordinal);
        Assert.True(strictPaint >= 0 && publish > strictPaint);
        Assert.Contains(
            "browserRole === 'gpu-renderer'",
            app.Substring(strictPaint, publish - strictPaint),
            StringComparison.Ordinal);
        Assert.Contains(
            "committedPresentationRef.current?.presentationId !== presentationId",
            app.Substring(strictPaint, publish - strictPaint),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Cold_reopen_prepares_an_exact_provider_frame_while_hidden()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var refresh = ExtractMethod(
            program,
            "private void BeginExternalProviderPresentationRefresh(");
        var commit = ExtractMethod(
            program,
            "private void TryCommitExternalProviderPresentation(");
        var retire = ExtractMethod(
            program,
            "private void RetireExternalProviderProof(");

        var retainDecision = refresh.IndexOf(
            "var retainCurrentExternalFrame =",
            StringComparison.Ordinal);
        var hiddenIdentity = refresh.IndexOf(
            "_hiddenExternalPreparationPresentationId =\n" +
            "                !retainCurrentExternalFrame",
            StringComparison.Ordinal);
        var refreshRequest = refresh.IndexOf(
            "RefreshPresentation(",
            StringComparison.Ordinal);

        Assert.True(retainDecision >= 0);
        Assert.True(hiddenIdentity > retainDecision);
        Assert.True(refreshRequest > hiddenIdentity);
        Assert.Contains(
            "externalSession.IsPresentationReady &&\n" +
            "                _browserPresentation.ExternalGpuVisible",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalReplacementPresentationId = retainCurrentExternalFrame\n" +
            "                ? presentationId\n" +
            "                : null;",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "!retainCurrentExternalFrame &&\n" +
            "                !_browserPresentationRequestedVisible",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "var revealWebViewFallback =\n" +
            "                !_requireNativePresenter &&\n" +
            "                !externalRefreshAccepted &&\n" +
            "                !_browserPresentationRequestedVisible;",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "webview_provider_reveal_after_browser_prepare",
            refresh,
            StringComparison.Ordinal);

        var hiddenCommit = commit.IndexOf(
            "var hiddenPreparationCommit =",
            StringComparison.Ordinal);
        var exactFreshness = commit.IndexOf(
            "_externalFreshPresentationId,",
            StringComparison.Ordinal);
        var publish = commit.IndexOf(
            "PublishProviderPresentationCommitted(",
            StringComparison.Ordinal);

        Assert.True(hiddenCommit >= 0);
        Assert.True(exactFreshness > hiddenCommit);
        Assert.True(publish > exactFreshness);
        Assert.Contains(
            "_browserPresentation.Owner == BrowserPresentationOwner.None",
            commit,
            StringComparison.Ordinal);
        Assert.Contains(
            "!_browserPresentationRequestedVisible",
            commit,
            StringComparison.Ordinal);
        Assert.Contains(
            "hostServer?.IsVisible != true",
            commit,
            StringComparison.Ordinal);
        Assert.Contains(
            "_hiddenExternalPreparationPresentationId = null;",
            retire,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_visual_harness_keeps_external_cef_active_but_webview_is_the_presenter()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var build = ReadRepositoryFile("build-package.ps1")
            .Replace("\r\n", "\n");

        Assert.Contains(
            "--bootstrap-harness-webview-presenter",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool BootstrapHarnessWebViewPresenter { get; private set; }",
            program,
            StringComparison.Ordinal);

        var setVisible = ExtractMethod(program, "private void SetBrowserVisible(");
        Assert.Contains(
            "_options.BootstrapHarnessWebViewPresenter",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "externalGpuActive && externalGpuPresentationReady",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserPresentationOwner.WebViewBootstrap",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "reason: \"bootstrap-harness-shadow-presenter\"",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (decision.ExternalGpuVisible)",
            setVisible,
            StringComparison.Ordinal);
        var waitingBranchStart = setVisible.IndexOf(
            "else if (externalPresentationRequested &&",
            StringComparison.Ordinal);
        Assert.True(waitingBranchStart >= 0);
        var waitingBranchEnd = setVisible.IndexOf(
            "else if (decision.WebViewVisible)",
            waitingBranchStart,
            StringComparison.Ordinal);
        Assert.True(waitingBranchEnd > waitingBranchStart);
        var waitingBranch = setVisible.Substring(
            waitingBranchStart,
            waitingBranchEnd - waitingBranchStart);
        Assert.Contains(
            "_externalGpuBrowserSession?.SetVisible(false);",
            waitingBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SetVisible(true)",
            waitingBranch,
            StringComparison.Ordinal);

        // This is a CLI-only harness override. External CEF remains enabled by
        // the packaged settings/experimental switch and continues receiving
        // mirrored messages and resize/readiness work.
        Assert.DoesNotContain(
            "bootstrapHarnessWebViewPresenter",
            ReadRepositoryFile("ReactorV.Preloader.json"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "_externalGpuBrowserSession?.PostJson(json);",
            program,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                build,
                "'--bootstrap-harness-webview-presenter'"));
        var bootstrapInvocation = build.IndexOf(
            "'--scenario', 'bootstrap-host'",
            StringComparison.Ordinal);
        var presenterSwitch = build.IndexOf(
            "'--bootstrap-harness-webview-presenter'",
            StringComparison.Ordinal);
        Assert.True(bootstrapInvocation >= 0);
        Assert.True(presenterSwitch > bootstrapInvocation);
    }

    [Fact]
    public void External_handoff_fails_closed_on_stale_session_size_or_refresh()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var setVisible = ExtractMethod(program, "private void SetBrowserVisible(");

        Assert.Contains(
            "_externalPresentationFallbackToWebView",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "externalSurfaceSynchronized",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "disconnectedSurfaceLessRequestClamped",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "transitioningToExternal",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dualBrowserReadyProviderSessionGeneration ==",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "Volatile.Read(ref _providerSessionGeneration)",
            setVisible,
            StringComparison.Ordinal);

        var record = ExtractMethod(
            program,
            "private bool TryRecordDualBrowserPresentationReady(");
        Assert.Contains("_hostServer?.IsConnected != true", record);
        Assert.Contains(
            "providerSessionGeneration !=",
            record,
            StringComparison.Ordinal);
        var refreshMethod = ExtractMethod(
            program,
            "private void BeginExternalProviderPresentationRefresh(");
        Assert.Contains(
            "_externalPresentationFallbackToWebView =",
            refreshMethod,
            StringComparison.Ordinal);

        var synchronize = ExtractMethod(
            program,
            "private bool TrySynchronizeExternalGpuSurfaceSize(");
        Assert.Contains("if (!accepted)", synchronize, StringComparison.Ordinal);
        Assert.Contains(
            "_externalPresentationFallbackToWebView = true;",
            synchronize,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Enhanced_initializer_withholds_the_fullscreen_webview_fallback()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var reveal = ExtractMethod(
            program,
            "private bool TryCompleteHostSurfaceReveal(");
        var arbitration = ExtractMethod(
            program,
            "private void SetBrowserVisible(");

        Assert.Contains(
            "_options.ExternalGpuBrowserShadow &&",
            reveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "!ShouldUseExternalInitializerPresenter()",
            reveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "external_gpu_initializer_reveal_withheld",
            reveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "action=fail-closed-hide",
            reveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "trigger: \"initializer-native-presenter-unavailable\"",
            reveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "failClosedInitializerFallback:",
            arbitration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Rapid_replacement_is_coalesced_without_a_false_visibility_edge()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var handler = ExtractMethod(
            program,
            "private void ContinueExternalProviderPresentationAfterPaint(");
        var queueDecision = handler.IndexOf(
            "ShouldQueueRapidReplacement(",
            StringComparison.Ordinal);
        var queuedIdentity = handler.IndexOf(
            "_queuedExternalReplacementPresentationId =",
            StringComparison.Ordinal);
        var queuedReturn = handler.IndexOf(
            "return;",
            queuedIdentity,
            StringComparison.Ordinal);
        var beginRefresh = handler.IndexOf(
            "BeginExternalProviderPresentationRefresh(",
            StringComparison.Ordinal);

        Assert.True(queueDecision >= 0);
        Assert.True(queuedIdentity > queueDecision);
        Assert.True(queuedReturn > queuedIdentity);
        Assert.True(beginRefresh > queuedReturn);
        var queueBranch = handler.Substring(
            queueDecision,
            queuedReturn - queueDecision);
        Assert.DoesNotContain("SetVisible(false)", queueBranch);
        Assert.DoesNotContain("RefreshPresentation(", queueBranch);

        var readinessStart = program.IndexOf(
            "_externalGpuBrowserSession.PresentationReadinessChanged +=",
            StringComparison.Ordinal);
        var readinessEnd = program.IndexOf(
            "_hostWindow.ClientSizeChanged +=",
            readinessStart,
            StringComparison.Ordinal);
        Assert.True(readinessStart >= 0 && readinessEnd > readinessStart);
        var readiness = program.Substring(
            readinessStart,
            readinessEnd - readinessStart);
        Assert.True(
            readiness.IndexOf(
                "TryStartQueuedExternalProviderReplacement(",
                StringComparison.Ordinal) <
            readiness.IndexOf(
                "SetBrowserVisible(",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_disconnect_preserves_the_verified_initializer_generation()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var disconnectStart = program.IndexOf(
            "_hostServer.ProviderDisconnected +=",
            StringComparison.Ordinal);
        var disconnectEnd = program.IndexOf(
            "_hostToggle = new EventWaitHandle(",
            disconnectStart,
            StringComparison.Ordinal);
        Assert.True(disconnectStart >= 0 && disconnectEnd > disconnectStart);
        var disconnect = program.Substring(
            disconnectStart,
            disconnectEnd - disconnectStart);

        Assert.Contains(
            "bootstrap_host_initializer_preserved_on_provider_disconnect",
            disconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "retainedProviderPromotionInFlight",
            disconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "!retainedProviderPromotionInFlight",
            disconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "bootstrap_host_initializer_reproof_requested_on_provider_disconnect",
            disconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequestHostSurface(",
            disconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"initializing\",\n                                visible: true",
            disconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "generation={_hostSurfaceGeneration}",
            disconnect,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RequestHostSurface(window, \"initializing\", true);",
            disconnect,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_aborts_retained_replacement_before_hiding_the_old_surface()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var synchronize = ExtractMethod(
            program,
            "private bool TrySynchronizeExternalGpuSurfaceSize(");
        var abort = synchronize.IndexOf(
            "_externalReplacementPresentationId = null;",
            StringComparison.Ordinal);
        var hide = synchronize.IndexOf(
            "session.SetVisible(false);",
            StringComparison.Ordinal);
        var resize = synchronize.IndexOf(
            "session.Resize(width, height)",
            StringComparison.Ordinal);

        Assert.Contains(
            "external_gpu_replacement_aborted_for_resize",
            synchronize,
            StringComparison.Ordinal);
        Assert.Contains(
            "_queuedExternalReplacementPresentationId = null;",
            synchronize,
            StringComparison.Ordinal);
        Assert.Contains(
            "_queuedExternalReplacementProviderSessionGeneration = 0;",
            synchronize,
            StringComparison.Ordinal);
        Assert.True(abort >= 0);
        Assert.True(hide > abort);
        Assert.True(resize > hide);

        var begin = ExtractMethod(
            program,
            "private void BeginExternalProviderPresentationRefresh(");
        var clearQueued = begin.IndexOf(
            "_queuedExternalReplacementPresentationId = null;",
            StringComparison.Ordinal);
        var selectIncoming = begin.IndexOf(
            "_dualBrowserReadyPresentationId = presentationId;",
            StringComparison.Ordinal);
        Assert.True(clearQueued >= 0);
        Assert.True(selectIncoming > clearQueued);
        Assert.Contains(
            "external_gpu_provider_queued_replacement_superseded",
            begin,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Rapid_A_B_C_then_resize_invalidates_A_before_C_pixels_can_be_ready()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var synchronize = ExtractMethod(
            program,
            "private bool TrySynchronizeExternalGpuSurfaceSize(");
        var cancellation = synchronize.IndexOf(
            "external_gpu_replacement_aborted_for_resize",
            StringComparison.Ordinal);
        var clearDualId = synchronize.IndexOf(
            "_dualBrowserReadyPresentationId = null;",
            cancellation,
            StringComparison.Ordinal);
        var clearDualSession = synchronize.IndexOf(
            "_dualBrowserReadyProviderSessionGeneration = 0;",
            cancellation,
            StringComparison.Ordinal);
        var clearFresh = synchronize.IndexOf(
            "_externalFreshPresentationId = null;",
            cancellation,
            StringComparison.Ordinal);
        var clearCommitted = synchronize.IndexOf(
            "_externalCommittedPresentationId = null;",
            cancellation,
            StringComparison.Ordinal);
        var clearActive = synchronize.IndexOf(
            "_externalReplacementPresentationId = null;",
            cancellation,
            StringComparison.Ordinal);
        var clearQueued = synchronize.IndexOf(
            "_queuedExternalReplacementPresentationId = null;",
            cancellation,
            StringComparison.Ordinal);
        var hide = synchronize.IndexOf(
            "session.SetVisible(false);",
            cancellation,
            StringComparison.Ordinal);

        Assert.True(cancellation >= 0);
        Assert.True(clearDualId > cancellation);
        Assert.True(clearDualSession > cancellation);
        Assert.True(clearFresh > cancellation);
        Assert.True(clearCommitted > cancellation);
        Assert.True(clearActive > cancellation);
        Assert.True(clearQueued > cancellation);
        Assert.True(hide > clearDualId);
        Assert.True(hide > clearDualSession);
        Assert.True(hide > clearFresh);
        Assert.True(hide > clearCommitted);
        Assert.True(hide > clearActive);
        Assert.True(hide > clearQueued);
    }

    [Fact]
    public void Authoritative_hide_invalidates_exact_provider_freshness()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs").Replace("\r\n", "\n");
        var visibilityHandlerStart = program.IndexOf(
            "_hostServer.VisibilityRequested +=",
            StringComparison.Ordinal);
        Assert.True(visibilityHandlerStart >= 0);
        var visibilityHandlerEnd = program.IndexOf(
            "_hostServer.BootstrapSurfaceRetirementRequested +=",
            visibilityHandlerStart,
            StringComparison.Ordinal);
        Assert.True(visibilityHandlerEnd > visibilityHandlerStart);
        var visibilityHandler = program.Substring(
            visibilityHandlerStart,
            visibilityHandlerEnd - visibilityHandlerStart);
        var authoritativeHide = ExtractBlock(visibilityHandler, "if (!visible)");
        var setVisible = ExtractMethod(
            program,
            "private void SetBrowserVisible(");
        var retireProof = ExtractMethod(
            program,
            "private void RetireExternalProviderProof(");

        Assert.Contains(
            "RetireExternalProviderProof(",
            authoritativeHide,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_dualBrowserReadyPresentationId = null;",
            setVisible,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dualBrowserReadyPresentationId = null;",
            retireProof,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dualBrowserReadyProviderSessionGeneration = 0;",
            retireProof,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalFreshPresentationId = null;",
            retireProof,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalCommittedPresentationId = null;",
            retireProof,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalReplacementPresentationId = null;",
            retireProof,
            StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing production method: {signature}");
        var nextMethod = source.IndexOf("\n        private ", start + signature.Length,
            StringComparison.Ordinal);
        return nextMethod < 0
            ? source.Substring(start)
            : source.Substring(start, nextMethod - start);
    }

    private static string ExtractBlock(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing production branch: {marker}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace > start);
        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
                return source.Substring(start, index - start + 1);
        }

        throw new Xunit.Sdk.XunitException($"Unclosed production branch: {marker}");
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
            !(File.Exists(Path.Combine(current.FullName, "ReactorV.json")) &&
              Directory.Exists(Path.Combine(current.FullName, "src"))))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return File.ReadAllText(
            Path.Combine(current!.FullName, Path.Combine(parts)));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                    value,
                    offset,
                    StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
