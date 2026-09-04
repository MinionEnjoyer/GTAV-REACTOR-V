using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class OverlayTransferSourceContractTests
{
    [Fact]
    public void ColdAndWarmPathsShareOneMonotonicTransferGeneration()
    {
        var source = ReadRepositoryFile(
            "src", "ReactorV.Runtime", "OverlayWindow.cs");

        Assert.Contains("private int _transferGeneration;", source);
        Assert.Equal(2, Count(source, "++_transferGeneration"));
        Assert.Equal(2, Count(source, "if (!_transferState.Begin(transferIdentity))"));
        Assert.Contains("OverlayTransferIdentity transferIdentity", source);
        Assert.Contains("webview_transfer_stale_begin_rejected", source);
    }

    private static int Count(string source, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }
        return count;
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
