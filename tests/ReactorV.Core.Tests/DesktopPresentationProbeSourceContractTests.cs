using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DesktopPresentationProbeSourceContractTests
{
    [Fact]
    public void PreloaderDispatchesProbeBeforeSettingsParsingAndSingleton()
    {
        var program = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "Program.cs");
        var childDispatch = program.IndexOf(
            "DesktopPresentationProbeChild.TryRun(args",
            StringComparison.Ordinal);
        var settings = program.IndexOf(
            "PreloaderSettings.Load(",
            StringComparison.Ordinal);
        var singleton = program.IndexOf(
            "new Mutex(",
            StringComparison.Ordinal);

        Assert.True(childDispatch >= 0);
        Assert.True(settings > childDispatch);
        Assert.True(singleton > settings);
    }

    [Fact]
    public void ChildUsesCompositedDesktopProofWithDesktopDuplicationFallback()
    {
        var child = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "DesktopPresentationProbeChild.cs");

        Assert.Contains("--desktop-presentation-probe", child);
        Assert.Contains("Convert.FromBase64String", child);
        Assert.Contains("MaximumEncodedRequestLength", child);
        Assert.Contains("MaximumSamples", child);
        Assert.Contains("BitBlt(", child);
        Assert.Contains("CaptureLayeredWindows", child);
        Assert.Contains("gdi-composited-desktop", child);
        Assert.Contains("DuplicateOutput", child);
        Assert.Contains("AcquireNextFrame", child);
        Assert.Contains("CopyResource", child);
        Assert.DoesNotContain("PrintWindow", child);
        Assert.DoesNotContain("CapturePreview", child);
        Assert.Contains("[JsonProperty(\"readable\")]", child);
        Assert.Contains("[JsonProperty(\"matching\")]", child);
        Assert.Contains("[JsonProperty(\"concrete\")]", child);
        Assert.Contains("[JsonProperty(\"source\")]", child);
        Assert.Contains("[JsonProperty(\"error\")]", child);
        Assert.Contains("OverlayPresentationPolicy.HasConcreteDesktopPixels", child);
    }

    [Fact]
    public void RuntimeClientRedirectsChildAndFailsClosedOnHardTimeout()
    {
        var client = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "DesktopPresentationProbeClient.cs");

        Assert.Contains("internal static async Task<DesktopPresentationProbeResult> VerifyAsync(", client);
        Assert.Contains("RedirectStandardOutput = true", client);
        Assert.Contains("RedirectStandardError = true", client);
        Assert.Contains("TaskCreationOptions.LongRunning", client);
        Assert.Contains("process.WaitForExit(timeoutMilliseconds)", client);
        Assert.Contains("CancellationToken.None", client);
        Assert.Contains("TryKill(process)", client);
        Assert.Contains("process.WaitForExit(250)", client);
        Assert.Contains("\"hard-timeout\"", client);
        Assert.Contains("RequiredIdentitySampleCount = 8", client);
        Assert.Contains("readable.Value == expectedSampleCount", client);
        Assert.Contains("(expectedSampleCount * 3 + 3) / 4", client);
        Assert.Contains("concrete.Value && independentlyConcrete", client);

        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        Assert.Contains("Path.GetDirectoryName(_uiDirectory)", overlay);
        Assert.DoesNotContain(
            "AppDomain.CurrentDomain.BaseDirectory,\n                \"ReactorV.Preloader.exe\"",
            overlay.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeKeepsInconclusivePromotedWindowVisibleButNonInteractive()
    {
        var overlay = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");
        var probe = Region(
            overlay,
            "private async void BeginDesktopPresentationCommit(",
            "private bool TryCompleteExplicitUserIntentReveal(");
        var unverified = Region(
            overlay,
            "private bool KeepCompositionQualifiedPresentationVisible(",
            "private void HandleFinalRevealPixelProofFailure(");
        var hardFailure = Region(
            overlay,
            "private void HandleDesktopPresentationFailure(",
            "private bool KeepCompositionQualifiedPresentationVisible(");

        Assert.Contains("KeepCompositionQualifiedPresentationVisible(", probe);
        Assert.Contains("awaiting-independent-desktop-witness", probe);
        Assert.Contains("CompositionCommittedVisible", probe);
        Assert.Contains("CompositionCommittedVisible", unverified);
        Assert.Contains("input_enabled=False", unverified);
        Assert.Contains("external_hwnd_exclusive_limit=True", unverified);
        Assert.Contains("_visibilityChanged(true);", unverified);
        Assert.DoesNotContain("ProviderPresentationCommitted", unverified);
        Assert.DoesNotContain("ForceRecreateCompositionDevice", hardFailure);
        Assert.DoesNotContain("webview_desktop_presentation_target_recreated", hardFailure);
        Assert.Contains("ApplyVisibility(false);", hardFailure);
    }

    [Fact]
    public void ProbeUsesBoundedCompositorRetriesAndDesktopDuplicationWithoutDeviceRecreation()
    {
        var child = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "DesktopPresentationProbeChild.cs");

        Assert.Contains("MaximumGdiCaptureAttempts = 2", child);
        Assert.Contains("GdiCaptureRetryMilliseconds = 32", child);
        Assert.Contains("ChildResultReserveMilliseconds = 75", child);
        Assert.Contains("gdiAttempts < MaximumGdiCaptureAttempts", child);
        Assert.Contains("request.TimeoutMilliseconds - ChildResultReserveMilliseconds", child);
        Assert.Contains("gdi-composited-desktop:bounded-attempt-", child);
        Assert.Contains("dxgi-gdi-unavailable", child);
        Assert.DoesNotContain("dxgi-after-gdi-unverified", child);
        Assert.Contains("matching >= (request.Samples.Count * 3 + 3) / 4", child);
        Assert.DoesNotContain("ForceRecreateCompositionDevice", child);
    }

    [Fact]
    public void ProductionPackageRetainsPreloaderSharpDxDependencies()
    {
        var project = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "ReactorV.Preloader.csproj");
        var build = ReadRepositoryFile("build-package.ps1");

        Assert.Contains("SharpDX.Direct3D11", project);
        Assert.Contains("SharpDX.DXGI", project);
        Assert.Contains("$desktopPresentationProbeDependencies", build);
        Assert.Contains("Join-Path $preloaderOutput $file", build);
        Assert.Contains("'SharpDX.dll'", build);
        Assert.Contains("'SharpDX.Direct3D11.dll'", build);
        Assert.Contains("'SharpDX.DXGI.dll'", build);

        var removal = Region(
            build,
            "foreach ($harnessFile in @(",
            "$unexpectedPackagedArtifacts");
        Assert.Contains("RageWebUI.Harness.exe", removal);
        Assert.DoesNotContain("SharpDX.dll", removal);
    }

    [Fact]
    public void SecondaryDomainHarnessWaitsForDesktopProofBeforeTransitions()
    {
        var harness = ReadRepositoryFile(
            "src", "ReactorV.Harness", "SecondaryAppDomainHarness.cs");

        Assert.Contains("desktopPresentationReady", harness);
        Assert.Contains("webview_desktop_presentation_verified", harness);
        Assert.Contains("WaitForVisibleWithDesktopProof", harness);
        Assert.Contains("requiredVerifiedPresentationCount", harness);
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
        return File.ReadAllText(Path.Combine(current!.FullName, Path.Combine(parts)));
    }
}
