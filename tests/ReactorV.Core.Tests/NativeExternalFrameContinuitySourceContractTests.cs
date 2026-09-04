using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Guards the Enhanced external-GPU hot path against transparent GTA Presents
/// while CEF is importing or copying the next shared frame.
/// </summary>
public sealed class NativeExternalFrameContinuitySourceContractTests
{
    [Fact]
    public void Producer_wait_does_not_own_the_present_context_gate()
    {
        var source = ReadRepositoryFile(
            "native", "src", "SharedGpuFrameConsumer.cpp")
            .Replace("\r\n", "\n");
        var copy = ExtractMethod(source, "bool SharedGpuFrameConsumer::CopyAndPublish(");

        var producerWait = copy.IndexOf(
            "for (unsigned attempt = 0; attempt != 25",
            StringComparison.Ordinal);
        var contextGate = copy.IndexOf(
            "std::scoped_lock contextLock(contextMutex_);",
            StringComparison.Ordinal);

        Assert.True(producerWait >= 0);
        Assert.True(contextGate > producerWait);
        Assert.Contains("std::scoped_lock lock(frameMutex_);", copy);
    }

    [Fact]
    public void Visible_present_waits_only_for_local_copy_and_reuses_published_frame()
    {
        var source = ReadRepositoryFile(
            "native", "src", "SharedGpuFrameConsumer.cpp")
            .Replace("\r\n", "\n");
        var acquire = ExtractMethod(
            source,
            "SharedGpuFrameConsumer::TryAcquireLatestForPresent() noexcept");

        Assert.Contains("std::unique_lock lock(contextMutex_);", acquire);
        Assert.DoesNotContain("std::try_to_lock", acquire);
        Assert.Contains("latestView_.Get(), latestGeneration_", acquire);
    }

    [Fact]
    public void Recoverable_replacement_failure_does_not_retire_last_good_frame()
    {
        var source = ReadRepositoryFile(
            "native", "src", "SharedGpuFrameConsumer.cpp")
            .Replace("\r\n", "\n");
        var worker = ExtractMethod(source, "void SharedGpuFrameConsumer::Worker() noexcept");
        var failureStart = worker.IndexOf("if (!copied)", StringComparison.Ordinal);
        var successStart = worker.IndexOf("} else {", failureStart, StringComparison.Ordinal);

        Assert.True(failureStart >= 0 && successStart > failureStart);
        var failure = worker.Substring(failureStart, successStart - failureStart);
        Assert.DoesNotContain("ClearLatestLocked", failure);
        Assert.Contains("last consumer-owned texture", failure);
    }

    [Fact]
    public void Repeated_browser_visibility_does_not_force_another_cef_paint()
    {
        var browser = ReadRepositoryFile(
            "src", "ReactorV.DirectX", "Browser", "OffscreenBrowser.cs")
            .Replace("\r\n", "\n");
        var setVisible = ExtractMethod(browser, "public void SetVisible(bool visible)");

        var idempotence = setVisible.IndexOf(
            "if (_desiredVisible == visible) return;",
            StringComparison.Ordinal);
        var invalidate = setVisible.IndexOf(
            "Invalidate(PaintElementType.View)",
            StringComparison.Ordinal);

        Assert.True(idempotence >= 0);
        Assert.True(invalidate > idempotence);
    }

    [Fact]
    public void Readiness_callback_reports_evidence_without_promoting_visibility()
    {
        var wrapper = ReadRepositoryFile(
            "src", "ReactorV.Preloader", "ExternalGpuBrowserSession.cs")
            .Replace("\r\n", "\n");
        var readiness = ExtractMethod(
            wrapper,
            "private void OnPresentationReadinessChanged(");

        Assert.Contains("visibility_authority=host-arbiter", readiness);
        Assert.DoesNotContain("producer => producer.SetVisible(true)", readiness);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method: {signature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace > start);

        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
                return source.Substring(start, index - start + 1);
        }

        throw new InvalidOperationException($"Unterminated method: {signature}");
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var root = FindRepositoryRoot();
        var path = root;
        foreach (var part in parts) path = Path.Combine(path, part);
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ReactorV.json")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ReactorV repository root.");
    }
}
