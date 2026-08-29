using System;
using System.IO;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class RuntimeDirectoryLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ReactorV.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveUsesScriptsChildWhenAssemblyWasShadowCopied()
    {
        var shadowCopy = Path.Combine(_root, "assembly", "dl3", "shadow");
        var scriptsDirectory = Path.Combine(_root, "game", "scripts");
        var installation = Path.Combine(scriptsDirectory, "ReactorV");
        CreateUi(installation);

        var actual = RuntimeDirectoryLocator.Resolve(shadowCopy, scriptsDirectory);

        Assert.Equal(Path.GetFullPath(installation), actual);
    }

    [Fact]
    public void ResolveUsesScriptsDirectoryBelowGameRoot()
    {
        var gameRoot = Path.Combine(_root, "game");
        var installation = Path.Combine(gameRoot, "scripts", "ReactorV");
        CreateUi(installation);

        var actual = RuntimeDirectoryLocator.Resolve(gameRoot);

        Assert.Equal(Path.GetFullPath(installation), actual);
    }

    [Fact]
    public void ResolveAcceptsThePhysicalInstallationDirectory()
    {
        var installation = Path.Combine(_root, "portable", "ReactorV");
        CreateUi(installation);

        var actual = RuntimeDirectoryLocator.Resolve(installation);

        Assert.Equal(Path.GetFullPath(installation), actual);
    }

    [Fact]
    public void ResolveRejectsDirectoryWithoutEntryPage()
    {
        var candidate = Path.Combine(_root, "missing");
        Directory.CreateDirectory(Path.Combine(candidate, "ui"));

        var error = Assert.Throws<DirectoryNotFoundException>(
            () => RuntimeDirectoryLocator.Resolve(candidate));

        Assert.Contains(Path.GetFullPath(candidate), error.Message);
    }

    [Fact]
    public void ResolveBootstrapUsesPhysicalScriptsDirectory()
    {
        var gameRoot = Path.Combine(_root, "game");
        var bootstrap = Path.Combine(gameRoot, "scripts", "ReactorV");
        Directory.CreateDirectory(bootstrap);
        File.WriteAllText(Path.Combine(bootstrap, "ReactorV.json"), "{}");
        File.WriteAllBytes(Path.Combine(bootstrap, "RageWebUI.Script.dll"), new byte[] { 1 });

        var actual = RuntimeDirectoryLocator.ResolveBootstrap(gameRoot);

        Assert.Equal(Path.GetFullPath(bootstrap), actual);
    }

    [Fact]
    public void ResolveRendererFindsGameRootSiblingFromBootstrap()
    {
        var gameRoot = Path.Combine(_root, "game");
        var bootstrap = Path.Combine(gameRoot, "scripts", "ReactorV");
        var renderer = Path.Combine(gameRoot, "plugins", "ReactorV");
        Directory.CreateDirectory(bootstrap);
        Directory.CreateDirectory(Path.Combine(renderer, "ui"));
        File.WriteAllBytes(Path.Combine(renderer, "RageWebUI.Runtime.dll"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(renderer, "ui", "index.html"), "<!doctype html>");

        var actual = RuntimeDirectoryLocator.ResolveRenderer(bootstrap);

        Assert.Equal(Path.GetFullPath(renderer), actual);
    }

    [Fact]
    public void ResolveBootstrapStillAcceptsLegacyFolderAndConfigName()
    {
        var gameRoot = Path.Combine(_root, "legacy-game");
        var bootstrap = Path.Combine(gameRoot, "scripts", "RageWebUI");
        Directory.CreateDirectory(bootstrap);
        File.WriteAllText(Path.Combine(bootstrap, "RageWebUI.json"), "{}");
        File.WriteAllBytes(Path.Combine(bootstrap, "RageWebUI.Script.dll"), new byte[] { 1 });

        var actual = RuntimeDirectoryLocator.ResolveBootstrap(gameRoot);

        Assert.Equal(Path.GetFullPath(bootstrap), actual);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateUi(string installation)
    {
        var uiDirectory = Path.Combine(installation, "ui");
        Directory.CreateDirectory(uiDirectory);
        File.WriteAllText(Path.Combine(uiDirectory, "index.html"), "<!doctype html>");
    }
}
