using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class LiveAcceptanceDesktopCaptureSourceContractTests
{
    [Fact]
    public void DesktopCaptureIsDispatchedAfterBrowserFailureIsRecorded()
    {
        var harness = ReadRepositoryFile(
            "src",
            "ReactorV.Harness",
            "LiveAcceptanceHarness.cs");
        var method = MethodRegion(
            harness,
            "private static void CaptureArtifact(",
            "private static void RecordVisualCaptureFailure(");

        var browserFailure = method.IndexOf(
            "browserFailure = error;",
            StringComparison.Ordinal);
        var desktopCapture = method.IndexOf(
            "visualCapture.CaptureDesktop(",
            StringComparison.Ordinal);
        var combinedFailure = method.IndexOf(
            "browserFailure != null || desktopFailure != null",
            StringComparison.Ordinal);

        Assert.True(browserFailure >= 0);
        Assert.True(desktopCapture > browserFailure);
        Assert.True(combinedFailure > desktopCapture);
        Assert.Contains("visualCaptureFailures", harness);
        Assert.Contains("browser-preview-unavailable", method);
    }

    [Fact]
    public void DesktopCaptureUsesDxgiBeforeTheGdiFallback()
    {
        var capture = ReadRepositoryFile(
            "src",
            "ReactorV.Harness",
            "LiveAcceptanceWindowCaptureSession.cs");
        var method = MethodRegion(
            capture,
            "internal Bitmap CaptureDesktop(",
            "private Bitmap CaptureHostPreview(");

        var dxgi = method.IndexOf(
            "new DxgiDesktopCaptureSession(initialBounds)",
            StringComparison.Ordinal);
        var duplicatedFrame = method.IndexOf(
            "desktop-dxgi-duplication",
            dxgi,
            StringComparison.Ordinal);
        var gdiFallback = method.IndexOf(
            "desktop-bitblt-captureblt-fallback",
            duplicatedFrame,
            StringComparison.Ordinal);

        Assert.True(dxgi >= 0);
        Assert.True(duplicatedFrame > dxgi);
        Assert.True(gdiFallback > duplicatedFrame);
        Assert.Contains("last-desktop-attempt.png", method);
        Assert.Contains("lastAttemptPreserved", method);
        Assert.Contains("LastDesktopAttemptArtifact = rawPath", method);
    }

    [Fact]
    public void HarnessDeclaresTheDesktopDuplicationDependencies()
    {
        var project = ReadRepositoryFile(
            "src",
            "ReactorV.Harness",
            "RageWebUI.Harness.csproj");
        var implementation = ReadRepositoryFile(
            "src",
            "ReactorV.Harness",
            "DxgiDesktopCaptureSession.cs");
        var build = ReadRepositoryFile("build-package.ps1");

        Assert.Contains("SharpDX.Direct3D11", project);
        Assert.Contains("SharpDX.DXGI", project);
        Assert.Contains("DuplicateOutput", implementation);
        Assert.Contains("AcquireNextFrame", implementation);
        Assert.Contains("CopyResource", implementation);
        Assert.Contains("desktopPresentationProbeDependencies", build);
        Assert.Contains("SharpDX.Direct3D11.dll", build);
        Assert.Contains("SharpDX.DXGI.dll", build);
        Assert.Contains("The harness is copied into staging only", build);
    }

    private static string MethodRegion(
        string source,
        string startMarker,
        string endMarker)
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
            current = current.Parent;
        Assert.NotNull(current);
        return File.ReadAllText(Path.Combine(
            current!.FullName,
            Path.Combine(parts)));
    }
}
