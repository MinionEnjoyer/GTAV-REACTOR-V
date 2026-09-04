using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;
using ReactorV.Starter;
using Xunit;

namespace RageWebUI.Core.Tests;

[Collection(ReactorIntegrationCollection.Name)]
public sealed class StandaloneStarterTests : IDisposable
{
    public StandaloneStarterTests() => ReactorHostApi.Reset();
    public void Dispose() => ReactorHostApi.Reset();

    [Fact]
    public void Two_mods_share_the_runtime_without_allin1_or_f9_ownership()
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        using var b = new StarterExtension("reactorv.starter-b", "Starter B");
        Assert.Equal(2, ReactorHostApi.DescribeExtensions().Count);
        Assert.False(ReactorHostApi.HasExtensionCapability(ReactorExtensionCapabilities.DefaultF9MenuOwner));
        Assert.Null(ReactorHostApi.DescribeExtension("allin1"));
        Assert.Equal(8, ReactorHostApi.DescribeMenus("reactorv.starter-a").Count);
        Assert.Equal(8, ReactorHostApi.DescribeMenus("reactorv.starter-b").Count);

        Assert.True(Invoke("reactorv.starter-a", "enabled", new JObject { ["value"] = false }).Succeeded);
        Assert.False(a.Enabled);
        Assert.True(b.Enabled);
        Assert.True(Invoke("reactorv.starter-b", "strength", new JObject { ["value"] = 75 }).Succeeded);
        Assert.Equal(50, a.Strength);
        Assert.Equal(75, b.Strength);
        var settings = ReactorHostApi.DescribeMenus("reactorv.starter-a", "settings")[0]!;
        Assert.False(settings["nodes"]![0]!.Value<bool>("value"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Range_parameters_are_rejected_before_callback(double value)
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        Assert.False(Invoke("reactorv.starter-a", "strength", new JObject { ["value"] = value }).Succeeded);
        Assert.Equal(50, a.Strength);
    }

    [Fact]
    public void Typed_settings_and_confirmation_remain_host_authoritative()
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        Assert.False(Invoke("reactorv.starter-a", "enabled", new JObject { ["value"] = "false" }).Succeeded);
        Assert.True(Invoke("reactorv.starter-a", "enabled", new JObject { ["value"] = false }).Succeeded);
        Assert.True(Invoke("reactorv.starter-a", "reset", new JObject()).ConfirmationRequired);
        Assert.False(a.Enabled);
        Assert.True(ReactorHostApi.Invoke("reactorv.starter-a", "reset", new JObject(), confirmed: true).Succeeded);
        Assert.True(a.Enabled);
    }

    [Fact]
    public void Scroll_and_grid_preserve_bound_item_identity_without_pagination()
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        var list = ReactorHostApi.DescribeMenus("reactorv.starter-a", "list")[0]!;
        Assert.Equal("list", list["nodes"]![0]!.Value<string>("kind"));
        Assert.DoesNotContain("pagination", list.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(ReactorHostApi.InvokeMenu("reactorv.starter-a", "list", "item-32", "activate", new JObject()).Succeeded);
        Assert.Equal(32, a.SelectedItem);
        Assert.False(ReactorHostApi.InvokeMenu("reactorv.starter-a", "grid", "item-2", "activate",
            new JObject { ["item"] = 31 }).Succeeded);
        Assert.Equal(32, a.SelectedItem);
    }

    [Fact]
    public void Unload_a_does_not_close_or_unregister_b_and_stale_a_cannot_remove_replacement()
    {
        var a = new StarterExtension("reactorv.starter-a", "Starter A");
        using var b = new StarterExtension("reactorv.starter-b", "Starter B");
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        var active = Activate(b, "reactorv.starter-b");
        a.Dispose();
        a.Dispose();
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());
        Assert.True(ReactorHostApi.CanMarkMenuPresentationReady(active));
        Assert.Single(ReactorHostApi.DescribeExtensions());
        Assert.True(Invoke("reactorv.starter-b", "strength", new JObject { ["value"] = 80 }).Succeeded);
        using var replacement = new StarterExtension("reactorv.starter-a", "New A");
        a.Dispose();
        Assert.False(a.ToggleMenu());
        Assert.NotNull(ReactorHostApi.DescribeExtension("reactorv.starter-a"));
    }

    [Fact]
    public void Pending_close_and_unavailable_host_never_queue_a_surprise_reopen()
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        Assert.False(a.ToggleMenu());
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.True(a.ToggleMenu());
        Assert.True(a.ToggleMenu());
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.True(a.ToggleMenu());
        ReactorHostApi.SetMenuPresentationHostAvailable(false);
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
    }

    [Fact]
    public void Repeated_active_open_close_requires_exact_hidden_ack_and_unloads_cleanly()
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        for (var i = 0; i < 100; i++)
        {
            var active = Activate(a, "reactorv.starter-a");
            Assert.True(a.ToggleMenu());
            Assert.Equal(active, Assert.Single(ReactorHostApi.DrainMenuDismissals()).Value<string>("presentationId"));
            Assert.Null(ReactorHostApi.AcknowledgeMenuPresentationHidden("old-ack"));
            Assert.Empty(ReactorHostApi.DrainMenuPresentations());
            Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(active));
        }
        var last = Activate(a, "reactorv.starter-a");
        a.Dispose();
        Assert.Equal(last, Assert.Single(ReactorHostApi.DrainMenuDismissals()).Value<string>("presentationId"));
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(last));
        Assert.Empty(ReactorHostApi.DescribeExtensions());
    }

    [Fact]
    public void Duplicate_consumer_id_fails_without_changing_existing_registration()
    {
        using var a = new StarterExtension("reactorv.starter-a", "Starter A");
        Assert.Throws<InvalidOperationException>(() => new StarterExtension("reactorv.starter-a", "Imposter"));
        Assert.Single(ReactorHostApi.DescribeExtensions());
        Assert.True(Invoke("reactorv.starter-a", "enabled", new JObject { ["value"] = false }).Succeeded);
        Assert.False(a.Enabled);
    }

    private static ReactorActionResult Invoke(string id, string action, JObject parameters) =>
        ReactorHostApi.Invoke(id, action, parameters);

    private static string Activate(StarterExtension extension, string id)
    {
        Assert.True(extension.ToggleMenu());
        var request = Assert.Single(ReactorHostApi.DrainMenuPresentations());
        var presentationId = request.Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(id, "main", presentationId, out _));
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(presentationId));
        return presentationId;
    }
}
