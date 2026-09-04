using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;
using ReactorV.Starter;
using Xunit;

namespace RageWebUI.Core.Tests;

[Collection(ReactorIntegrationCollection.Name)]
public sealed class StandalonePrefabTests : IDisposable
{
    public StandalonePrefabTests() => ReactorHostApi.Reset();
    public void Dispose() => ReactorHostApi.Reset();

    [Fact]
    public void Search_filters_stable_bound_ids_and_returns_a_refresh_not_a_reopen()
    {
        using var mod = new StarterExtension("sample.prefabs", "Sample prefabs");
        var result = ReactorHostApi.InvokeMenu("sample.prefabs", "catalog", "query", "set-value",
            new JObject { ["value"] = "item 32" });
        Assert.True(result.Succeeded);
        Assert.Equal("refresh", result.Value!.Value<string>("presentation"));
        var catalog = ReactorHostApi.DescribeMenus("sample.prefabs", "catalog")[0]!;
        Assert.Equal(new[] { "query", "item-32" }, catalog["nodes"]!.Select(node => node.Value<string>("id")));
        Assert.True(ReactorHostApi.InvokeMenu("sample.prefabs", "catalog", "item-32", "activate", new JObject()).Succeeded);
        Assert.Equal(32, mod.SelectedItem);
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.False(ReactorHostApi.InvokeMenu("sample.prefabs", "catalog", "item-32", "activate",
            new JObject { ["item"] = 1 }).Succeeded);
    }

    [Theory]
    [InlineData(81)]
    [InlineData(1000)]
    public void Search_input_is_bounded_before_the_handler(int length)
    {
        using var mod = new StarterExtension("sample.prefabs", "Sample prefabs");
        Assert.False(ReactorHostApi.Invoke("sample.prefabs", "search", new JObject { ["value"] = new string('a', length) }).Succeeded);
        Assert.Equal("", mod.Query);
    }

    [Fact]
    public void Empty_search_has_a_readable_non_actionable_state_and_lists_are_bounded()
    {
        var empty = MenuPrefabs.SearchableList("catalog", "Catalogue", "search", "absent", MenuPrefabs.BoundRows("select", 32));
        Assert.IsType<ReactorStatusNode>(empty.Nodes[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuPrefabs.SearchableList("catalog", "Catalogue", "search", "", MenuPrefabs.BoundRows("select", 256)));
    }

    [Fact]
    public void Side_editor_uses_the_shared_presentation_host_without_taking_F9()
    {
        using var mod = new StarterExtension("sample.prefabs", "Sample prefabs");
        Assert.True(ReactorHostApi.Invoke("sample.prefabs", "editor.open", new JObject()).Succeeded);
        Assert.Empty(ReactorHostApi.DrainMenuPresentations()); // host not available
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.True(ReactorHostApi.Invoke("sample.prefabs", "editor.open", new JObject()).Succeeded);
        var request = Assert.Single(ReactorHostApi.DrainMenuPresentations().OfType<JObject>());
        Assert.Equal("settings", request.Value<string>("menuId"));
        Assert.Equal("side-editor", request["context"]!.Value<string>("reactorLayout"));
        Assert.False(ReactorHostApi.HasExtensionCapability(ReactorExtensionCapabilities.DefaultF9MenuOwner));
    }

    [Fact]
    public void Search_rejects_invalid_rows_even_when_the_filter_would_hide_them()
    {
        Assert.Throws<ArgumentException>(() => MenuPrefabs.SearchableList("catalog", "Catalogue", "search", "absent",
            new ReactorMenuNode[] { null! }));
        foreach (var id in new[] { "query", "empty" })
            Assert.Throws<ArgumentException>(() => MenuPrefabs.SearchableList("catalog", "Catalogue", "search", "absent",
                new[] { new ReactorStatusNode(id, "Hidden", "Status", "neutral") }));
        var row = new ReactorStatusNode("item", "Hidden", "Status", "neutral");
        Assert.Throws<ArgumentException>(() => MenuPrefabs.SearchableList("catalog", "Catalogue", "search", "absent", new[] { row, row }));
    }

    [Fact]
    public void Standalone_startup_reports_only_real_runtime_services()
    {
        var status = StartupStatusContract.CreateRuntimeSnapshot(true, true, false);
        Assert.Equal(3, ((JArray)status["components"]!).Count);
        Assert.DoesNotContain("allin1", status.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gbay", status.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(status.Value<bool>("providerConnected"));
        Assert.Equal("not-reported", status.Value<string>("gameplayReadiness"));
    }
}
