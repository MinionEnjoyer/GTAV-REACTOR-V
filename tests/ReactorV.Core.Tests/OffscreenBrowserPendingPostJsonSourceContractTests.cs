using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class OffscreenBrowserPendingPostJsonSourceContractTests
{
    [Fact]
    public void MessagesWaitForTheCefDocumentAndUseABoundedQueue()
    {
        var source = Normalize(ReadBrowserSource());

        Assert.Contains("private const int MaxPendingPostJsonMessages = 256;", source);
        Assert.Contains("private readonly Queue<string> _pendingPostJson", source);
        Assert.Contains("if (!_documentReady || !_browser.IsBrowserInitialized)", source);
        Assert.Contains("if (_pendingPostJson.Count == MaxPendingPostJsonMessages)\n                        _pendingPostJson.Dequeue();\n                    _pendingPostJson.Enqueue(json);", source);
        Assert.DoesNotContain("if (_disposed || !_browser.IsBrowserInitialized) return;\n            var script", source);
    }

    [Fact]
    public void CompletedNavigationFlushesQueuedMessagesBeforeLiveMessagesCanOvertakeThem()
    {
        var source = Normalize(ReadBrowserSource());
        var loadingHandler = Slice(
            source,
            "private void OnLoadingStateChanged",
            "private void DispatchJsonUnsafe");
        var postJson = Slice(source, "public void PostJson", "public void SendInput");

        Assert.Contains("lock (_postJsonSync)", loadingHandler);
        Assert.Contains("_documentReady = true;", loadingHandler);
        Assert.Contains("while (_pendingPostJson.Count > 0)\n                    DispatchJsonUnsafe(_pendingPostJson.Dequeue());", loadingHandler);
        Assert.Contains("lock (_postJsonSync)", postJson);
        Assert.Contains("DispatchJsonUnsafe(json);", postJson);
    }

    [Fact]
    public void ReloadAndDisposeCloseTheReadinessGateAndDisposeClearsPendingMessages()
    {
        var source = Normalize(ReadBrowserSource());
        var loadingHandler = Slice(
            source,
            "private void OnLoadingStateChanged",
            "private void DispatchJsonUnsafe");
        var dispose = Slice(source, "public void Dispose", "private void OnPaint");

        Assert.Contains("if (args.IsLoading)", loadingHandler);
        Assert.Contains("_documentReady = false;", loadingHandler);
        Assert.Contains("_documentReady = false;", dispose);
        Assert.Contains("_pendingPostJson.Clear();", dispose);
    }

    private static string ReadBrowserSource()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "ReactorV.DirectX",
                "Browser",
                "OffscreenBrowser.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ReactorV repository root.");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        Assert.True(end > start, $"Missing source marker after {startMarker}: {endMarker}");
        return source.Substring(start, end - start);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n");
}
