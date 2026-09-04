using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class RenderHookWorkerSourceContractTests
{
    [Fact]
    public void Bound_render_hook_does_not_rebind_or_poll_at_discovery_cadence()
    {
        var source = ReadRepositoryFile(
            "native", "renderhook", "RenderHookMain.cpp");

        Assert.Contains("bool rendererBound = false;", source);
        Assert.Contains(
            "if (!rendererBound || window != boundWindow)",
            source);
        Assert.Contains(
            "Sleep(rendererBound ? 1000 : 250);",
            source);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
            !(File.Exists(Path.Combine(current.FullName, "ReactorV.json")) &&
              Directory.Exists(Path.Combine(current.FullName, "native"))))
        {
            current = current.Parent;
        }
        Assert.NotNull(current);
        return File.ReadAllText(Path.Combine(current!.FullName, Path.Combine(parts)));
    }
}
