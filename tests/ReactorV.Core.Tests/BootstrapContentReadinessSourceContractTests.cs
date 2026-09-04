using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BootstrapContentReadinessSourceContractTests
{
    [Fact]
    public void TransientPresentationFailuresPreserveAttachableBrowserContent()
    {
        var source = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");

        AssertPresentationOnly(Region(
            source,
            "private void ExpireExplicitUserIntentInputLease(",
            "private void CompleteQualifiedReveal("));
        AssertPresentationOnly(Region(
            source,
            "private void HandleDesktopPresentationFailure(",
            "private bool KeepCompositionQualifiedPresentationVisible("));
        AssertPresentationOnly(Region(
            source,
            "private void HandleFinalRevealPixelProofFailure(",
            "private OverlayTransferIdentity CreateTransferIdentity("));
        AssertPresentationOnly(Region(
            source,
            "private void FailCompositionReveal(",
            "private void ApplyOverlayTopMost("));
    }

    [Fact]
    public void BrowserAndNavigationInvalidationOwnTheContentGeneration()
    {
        var source = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        var processFailure = Region(
            source,
            "private void OnWebViewProcessFailed(",
            "private async void WatchRendererReloadAsync(");
        var navigation = Region(
            source,
            "private async void OnNavigationCompleted(",
            "private void FlushPendingMessages(");
        var navigationStarting = Region(
            source,
            "private void OnNavigationStarting(",
            "private void OnWebMessageReceived(");
        var readiness = Region(
            source,
            "private void PublishBrowserContentReadiness()",
            "private void OnNavigationStarting(");

        Assert.Contains(
            "InvalidateBrowserContentReadiness(\"browser-process-failed\")",
            processFailure);
        Assert.Contains(
            "InvalidateBrowserContentReadiness(\"navigation-failed\")",
            navigation);
        Assert.Contains(
            "InvalidateBrowserContentReadiness(\"page-readiness-failed\")",
            navigation);
        Assert.Contains("PublishBrowserContentReadiness();", navigation);
        Assert.Contains(
            "InvalidateBrowserContentReadiness(\"navigation-starting\")",
            navigationStarting);
        Assert.Contains("_browserContentUnavailable();", readiness);
        Assert.Equal(
            1,
            CountOccurrences(source, "_browserContentUnavailable();"));
    }

    [Fact]
    public void PersistentPreloaderStillRoutesRealContentLossToReadyEventReset()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        var server = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "BootstrapOverlayServer.cs");
        var presentationFailure = Region(
            server,
            "public void MarkPresentationUnavailable(string reason)",
            "public void PublishVisibility(bool visible)");

        Assert.Contains("_hostServer.MarkContentUnavailable,", program);
        Assert.Contains(
            "presentationUnavailable:\n                        _hostServer.MarkPresentationUnavailable",
            program.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("generation = _contentReadiness.MarkUnavailable();", server);
        Assert.Contains("_ready.Reset();", server);
        Assert.Contains("generation = _contentReadiness.CurrentGeneration;", presentationFailure);
        Assert.Contains("contentReady = _contentReadiness.IsReady;", presentationFailure);
        Assert.DoesNotContain("MarkUnavailable", presentationFailure);
        Assert.DoesNotContain("_ready.Reset()", presentationFailure);
    }

    [Fact]
    public void ProviderProxyTracesContentGenerationOnlyWhenStateChanges()
    {
        var runtime = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "BootstrapOverlayRuntime.cs");

        Assert.Contains("_lastTracedContentGeneration", runtime);
        Assert.Contains("_lastTracedContentReady", runtime);
        Assert.Contains(
            "if (generation != _lastTracedContentGeneration ||",
            runtime);
        Assert.Contains(
            "readyValue != _lastTracedContentReady)",
            runtime);
    }

    private static void AssertPresentationOnly(string region)
    {
        Assert.Contains(
            "PreserveBrowserContentReadinessAfterPresentationFailure(",
            region);
        Assert.DoesNotContain("InvalidateBrowserContentReadiness(", region);
        Assert.DoesNotContain("ScheduleSoftwareBrowserRecovery(", region);
        Assert.DoesNotContain("_browserReady = false", region);
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

    private static string Region(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source.Substring(start, end - start);
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
        return File.ReadAllText(Path.Combine(
            current!.FullName,
            Path.Combine(parts)));
    }
}
