using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PointerDeliverySourceContractTests
{
    [Fact]
    public void LiveOverlayUsesTypedDomPointerEventsWithoutNativeMouseOrForegroundRepair()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));

        Assert.Contains("WindowedInputPolicy.SerializeProviderPointerEvent", source);
        Assert.Contains("WindowedInputPolicy.ProviderPointerResetEventName", source);
        Assert.Contains("WindowedInputPolicy.BootstrapPointerEventName", source);
        Assert.Contains("WindowedInputPolicy.BootstrapPointerResetEventName", source);
        Assert.Contains("new IntPtr(HtTransparent)", source);
        Assert.Contains("NativeMethods.WsExNoActivate", source);
        Assert.Contains("NativeMethods.WsExTransparent", source);
        Assert.DoesNotContain(".SendMouseInput(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ResetMouseInput(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HtClient", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPointerWaitsForExactAcceptedCompositionCommit()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));

        var postPointer = source.IndexOf(
            "public void PostPointerInput(",
            StringComparison.Ordinal);
        var exactGate = source.IndexOf(
            "WindowedInputPolicy.ShouldForwardProviderPointer(",
            postPointer,
            StringComparison.Ordinal);
        var providerEvent = source.IndexOf(
            "WindowedInputPolicy.SerializeProviderPointerEvent",
            postPointer,
            StringComparison.Ordinal);

        Assert.True(postPointer >= 0);
        Assert.True(exactGate > postPointer);
        Assert.True(providerEvent > exactGate);
        Assert.Contains("_acceptedMenuPresentationId", source);
        Assert.Contains("_committedProviderInputPresentationId", source);
        Assert.Contains("CompleteProviderInputCommit", source);
        Assert.Contains("WaitForCommitCompletion", source);
        Assert.Contains("fence_thread=overlay-sta", source);
        Assert.Contains("ResetProviderInputAuthorization(\"presentation-replaced\")", source);
        Assert.Contains("ResetProviderInputAuthorization(\"presentation-dismissed\")", source);
        Assert.Contains("ResetProviderInputAuthorization(\"browser-process-failed\")", source);
    }

    [Fact]
    public void ExternalGpuProviderUsesExactlyOneTypedDomPointerRoute()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.DirectX",
            "ExternalGpuBrowserSession.cs"));
        var method = MethodRegion(
            source,
            "public void PostPointerInput(",
            "public void Stop()");

        Assert.Equal(
            1,
            CountOccurrences(
                method,
                "WindowedInputPolicy.SerializeProviderPointerEvent("));
        Assert.Equal(1, CountOccurrences(method, "_browser?.PostJson(pointerEventJson);"));
        Assert.DoesNotContain("SendGameCursor", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMouse", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderInputCommitRequiresExactPostResponsePaintBeforeAuthorization()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var verifier = MethodRegion(
            source,
            "private async void VerifyProviderPresentationPixelsAndCommitAsync(",
            "private bool OwnsProviderPaintCommit(");

        var browserTurn = verifier.IndexOf("await Task.Yield();", StringComparison.Ordinal);
        var identity = verifier.IndexOf("OverlayPresentationPolicy.MenuPaintIdentity(", StringComparison.Ordinal);
        var capture = verifier.IndexOf("_webView.CapturePreviewAsync()", StringComparison.Ordinal);
        var concrete = verifier.IndexOf("evidence.IsConcrete", StringComparison.Ordinal);
        var marker = verifier.IndexOf("evidence.PaintIdentityMarkerMatched", StringComparison.Ordinal);
        var targetSize = verifier.IndexOf("targetSizeMatches", StringComparison.Ordinal);
        var authorize = verifier.IndexOf("CompleteProviderInputCommit(", StringComparison.Ordinal);

        Assert.True(browserTurn >= 0);
        Assert.True(identity > browserTurn);
        Assert.True(capture > identity);
        Assert.True(concrete > capture);
        Assert.True(marker > capture);
        Assert.True(targetSize > capture);
        Assert.True(authorize > concrete);
        Assert.True(authorize > marker);
        Assert.True(authorize > targetSize);
        Assert.Contains("webview_provider_pixels_verified", verifier);
        Assert.Contains("reason=exact-provider-pixels-unavailable fail_closed=true", verifier);
    }

    [Fact]
    public void ProviderCommitEventFollowsTheExactCompositionFence()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Runtime",
            "OverlayWindow.cs"));
        var nativeCommit = MethodRegion(
            source,
            "private void CompleteProviderInputCommit(",
            "private void CommitProviderInputAfterRevealFence()");

        var synchronize = nativeCommit.IndexOf("_webView.SynchronizeBounds()", StringComparison.Ordinal);
        var fence = nativeCommit.IndexOf("_webView.WaitForCommitCompletion()", StringComparison.Ordinal);
        var identity = nativeCommit.IndexOf("OwnsProviderPaintCommit(", StringComparison.Ordinal);
        var committed = nativeCommit.IndexOf("if (committed)", StringComparison.Ordinal);
        var desktopProof = nativeCommit.IndexOf("BeginDesktopPresentationCommit(", StringComparison.Ordinal);

        Assert.True(synchronize >= 0);
        Assert.True(fence > synchronize);
        Assert.True(identity > fence);
        Assert.True(committed > identity);
        Assert.True(desktopProof > committed);
        Assert.DoesNotContain("PublishProviderPresentationCommitted", nativeCommit);
        Assert.Contains("paint_boundary=exact-menu-marker", nativeCommit);

        var interactiveCommit = MethodRegion(
            source,
            "private void CommitProviderInputAfterRevealFence()",
            "private void PublishProviderPresentationCommitted(");
        var desktopGate = interactiveCommit.IndexOf(
            "_transferState.IsInteractive",
            StringComparison.Ordinal);
        var authorize = interactiveCommit.IndexOf(
            "_committedProviderInputPresentationId = _activeMenuPresentationId",
            StringComparison.Ordinal);
        var publish = interactiveCommit.IndexOf(
            "PublishProviderPresentationCommitted(_activeMenuPresentationId!);",
            StringComparison.Ordinal);
        Assert.True(desktopGate >= 0);
        Assert.True(authorize > desktopGate);
        Assert.True(publish > authorize);
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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
            "Could not locate the ReactorV source root for the pointer delivery contract.");
    }
}
