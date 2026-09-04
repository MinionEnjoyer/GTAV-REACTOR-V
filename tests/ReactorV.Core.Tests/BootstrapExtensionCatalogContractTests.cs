using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BootstrapExtensionCatalogContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ReactorV-BootstrapCatalogTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Valid_preloaded_registry_becomes_a_bounded_read_only_summary()
    {
        var result = BuildRegistry(new JArray(
            Extension("zeta.mod", "Zeta", "2.0.0", enabled: false),
            Extension("allin1.online-content", "ALLIN1 Online Content", "0.6.1", enabled: true)));

        Assert.True(BootstrapExtensionCatalogContract.TryBuildFromSnapshots(
            result, out var catalog, out var outcome));
        Assert.Equal("ready", outcome);
        Assert.Equal(BootstrapExtensionCatalogContract.Authority, catalog!.Value<string>("authority"));
        Assert.Equal(2, catalog.Value<int>("total"));
        Assert.Equal("allin1.online-content", catalog["items"]![0]!.Value<string>("id"));
        Assert.Equal("zeta.mod", catalog["items"]![1]!.Value<string>("id"));
        Assert.Equal(1, catalog["items"]![0]!.Value<int>("extensionApiVersion"));
        Assert.Equal(0, catalog["items"]![0]!.Value<int>("actionCount"));
        Assert.Null(catalog["items"]![0]!["description"]);
        Assert.Null(catalog["items"]![0]!["runtime"]);
        Assert.DoesNotContain(_root, catalog.ToString(Formatting.None), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extensions_list_is_served_locally_and_unavailable_catalog_is_retryable()
    {
        var result = BuildRegistry(new JArray(
            Extension("allin1.online-content", "ALLIN1 Online Content", "0.6.1", enabled: true)));
        Assert.True(BootstrapExtensionCatalogContract.TryBuildFromSnapshots(
            result, out var catalog, out _));
        var request = Request("catalog-request", new JObject());

        Assert.True(BootstrapExtensionCatalogContract.TryCreateLocalResponse(
            request, catalog, out var responseJson));
        var response = JObject.Parse(responseJson!);
        Assert.Equal(1, response["result"]!.Value<int>("total"));
        Assert.Equal(BootstrapExtensionCatalogContract.Authority,
            response["result"]!.Value<string>("authority"));

        Assert.True(BootstrapExtensionCatalogContract.TryCreateLocalResponse(
            request, null, out responseJson));
        response = JObject.Parse(responseJson!);
        Assert.Equal("bootstrap_catalog_preparing", response["error"]!.Value<string>("code"));
        Assert.True(response["error"]!.Value<bool>("retryable"));
    }

    [Fact]
    public void A_valid_registry_entry_survives_an_unrelated_manifest_error()
    {
        var result = BuildRegistry(new JArray(
            Extension("allin1.online-content", "ALLIN1 Online Content", "0.6.1", enabled: true)));
        var snapshot = JObject.Parse(File.ReadAllText(result.SnapshotPaths[0]));
        snapshot["complete"] = false;
        snapshot["errors"] = new JArray(new JObject
        {
            ["code"] = "required_entry_missing",
            ["entry_id"] = "unrelated-data",
            ["message"] = "An unrelated required entry is missing.",
        });
        File.WriteAllText(result.SnapshotPaths[0], snapshot.ToString(Formatting.Indented));

        Assert.True(BootstrapExtensionCatalogContract.TryBuildFromSnapshots(
            result, out var catalog, out var outcome));
        Assert.Equal("ready", outcome);
        Assert.Equal(1, catalog!.Value<int>("total"));
    }

    [Fact]
    public void Invalid_or_duplicate_registry_identity_fails_closed()
    {
        var invalid = BuildRegistry(new JArray(
            Extension("duplicate.mod", "First", "1.0.0", enabled: true),
            Extension("DUPLICATE.MOD", "Second", "1.0.0", enabled: true)));

        Assert.False(BootstrapExtensionCatalogContract.TryBuildFromSnapshots(
            invalid, out var catalog, out var outcome));
        Assert.Null(catalog);
        Assert.Equal("registry-entry-invalid", outcome);
    }

    [Fact]
    public void Other_methods_are_never_intercepted_by_bootstrap_catalog()
    {
        var request = JObject.Parse(Request("other-request", new JObject()));
        request["method"] = "extensions.get";
        Assert.False(BootstrapExtensionCatalogContract.TryCreateLocalResponse(
            request.ToString(Formatting.None), new JObject(), out var response));
        Assert.Null(response);
    }

    private PreloadDataBuildResult BuildRegistry(JArray extensions)
    {
        var gtaRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "gta");
        var cacheRoot = Path.Combine(_root, "cache");
        var manifestDirectory = Path.Combine(gtaRoot, "scripts", ".reactorv", "preload");
        var registryPath = Path.Combine(gtaRoot, "scripts", ".allin1", "extensions", "registry.json");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(registryPath, new JObject
        {
            ["api_version"] = 1,
            ["extensions"] = extensions,
        }.ToString(Formatting.Indented));
        File.WriteAllText(Path.Combine(manifestDirectory, "allin1.json"), new JObject
        {
            ["schema_version"] = 1,
            ["id"] = "allin1",
            ["entries"] = new JArray(new JObject
            {
                ["id"] = "extension-registry",
                ["path"] = "scripts/.allin1/extensions/registry.json",
                ["kind"] = "json",
                ["required"] = true,
                ["max_bytes"] = 4 * 1024 * 1024,
            }),
        }.ToString(Formatting.Indented));
        return PreloadDataCache.Build(
            gtaRoot,
            Process.GetCurrentProcess().Id,
            cacheRoot);
    }

    private static JObject Extension(string id, string name, string version, bool enabled) =>
        new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["version"] = version,
            ["api_version"] = 1,
            ["enabled"] = enabled,
            // These fields prove the bootstrap projection never forwards
            // arbitrary package descriptions, paths, or runtime payloads.
            ["description"] = "untrusted package-authored description",
            ["runtime"] = new JObject { ["path"] = "scripts/example.dll" },
        };

    private static string Request(string id, JObject parameters) =>
        new JObject
        {
            ["kind"] = "request",
            ["id"] = id,
            ["method"] = BootstrapExtensionCatalogContract.Method,
            ["params"] = parameters,
            ["protocolVersion"] = 2,
            ["minimumProtocolVersion"] = 1,
        }.ToString(Formatting.None);
}
