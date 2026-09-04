using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class HostHideInputLeaseSourceContractTests
{
    [Fact]
    public void AuthoritativeHiddenEdgeCancelsPendingRevealBeforeReleasingInput()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ReactorV.Script",
            "RageWebUiScript.cs"));
        var blockStart = source.IndexOf(
            "if (MenuPresentationPolicy.ShouldReconcileHostHide(",
            StringComparison.Ordinal);
        Assert.True(blockStart >= 0);
        var blockEnd = source.IndexOf(
            "if (_presentationPreparationDismissalSuppressionId != null)",
            blockStart,
            StringComparison.Ordinal);
        Assert.True(blockEnd > blockStart);
        var block = source.Substring(blockStart, blockEnd - blockStart);

        Assert.Contains("_menuRevealGate.Cancel();", block);
        Assert.Contains(
            "_presentationPreparationDismissalSuppressionId = null;",
            block);
        Assert.Contains("_overlayRequestedVisible = false;", block);
        Assert.Contains(
            "_inputMode = MenuPresentationPolicy.HiddenInputMode;",
            block);
        Assert.True(
            block.IndexOf("_menuRevealGate.Cancel();", StringComparison.Ordinal) <
            block.IndexOf("_overlayRequestedVisible = false;", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "build-package.ps1")) &&
                Directory.Exists(Path.Combine(current.FullName, "src", "ReactorV.Script")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Reactor V repository root.");
    }
}
