using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Script;
using ReactorV.Integration;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Consumer regressions for the two menu contracts used by ALLIN1 GBAY and
/// Chop. The Chop fixture uses its public extension ID but exercises Reactor's
/// registry contract rather than copying the external plug-in.
/// </summary>
[Collection(ReactorIntegrationCollection.Name)]
public sealed class Allin1ConsumerReadinessRegressionTests : IDisposable
{
    public Allin1ConsumerReadinessRegressionTests() => ReactorHostApi.Reset();

    public void Dispose() => ReactorHostApi.Reset();

    [Fact]
    public void GbayPublishingRecoversAfterRendererReadinessWithoutAcceptingPendingInputOrStaleAcknowledgements()
    {
        using var gbay = RegisterMenuConsumer(
            "allin1.gbay", "GBAY", "home", defaultOwner: true);
        var menus = Assert.IsAssignableFrom<IReactorMenuPresentationHandle>(gbay);
        var state = Assert.IsAssignableFrom<IReactorMenuPresentationStateHandle>(gbay);

        Assert.False(menus.TryPresentMenu("home", new JObject { ["attempt"] = 1 }));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.False(MenuPresentationPolicy.ShouldAcquireManagedInputLease(
            overlayRequestedVisible: true, overlayPresented: false,
            MenuPresentationPolicy.PendingPresentationInputMode));

        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.True(menus.TryPresentMenu("home", new JObject { ["attempt"] = 2 }));
        Assert.True(menus.TryPresentMenu("home", new JObject { ["attempt"] = 3 }));
        var pending = SinglePresentation();
        var firstId = pending.Value<string>("presentationId")!;
        Assert.Equal(3, pending["context"]!.Value<int>("attempt"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());

        Assert.False(ReactorHostApi.MarkMenuPresentationActive(
            "gtav.chop-it-up", "companion", firstId, out _));
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay", "home", firstId, out _));
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.False(MenuPresentationPolicy.ShouldAcquireManagedInputLease(
            overlayRequestedVisible: true, overlayPresented: true,
            MenuPresentationPolicy.PendingPresentationInputMode));

        Assert.True(menus.TryDismissMenu("home"));
        Assert.False(ReactorHostApi.MarkMenuPresentationReady(firstId));
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(firstId));

        Assert.True(menus.TryPresentMenu("home"));
        var recovered = SinglePresentation();
        var recoveredId = recovered.Value<string>("presentationId")!;
        Assert.NotEqual(firstId, recoveredId);
        Assert.False(ReactorHostApi.MarkMenuPresentationReady(firstId));
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay", "home", recoveredId, out _));
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(recoveredId));
        Assert.True(state.IsMenuPresentationReady("home"));
        Assert.True(MenuPresentationPolicy.ShouldAcquireManagedInputLease(
            overlayRequestedVisible: true, overlayPresented: true,
            MenuPresentationPolicy.ReadyPresentationInputMode));
    }

    [Fact]
    public void ChopPublishingCannotCommitOrDuplicateTheSupersededGbayOwner()
    {
        using var gbay = RegisterMenuConsumer(
            "allin1.gbay", "GBAY", "home", defaultOwner: true);
        using var chop = RegisterMenuConsumer(
            "gtav.chop-it-up", "Chop", "companion", defaultOwner: false);
        var gbayMenus = Assert.IsAssignableFrom<IReactorMenuPresentationHandle>(gbay);
        var gbayState = Assert.IsAssignableFrom<IReactorMenuPresentationStateHandle>(gbay);
        var chopMenus = Assert.IsAssignableFrom<IReactorMenuPresentationHandle>(chop);
        var chopState = Assert.IsAssignableFrom<IReactorMenuPresentationStateHandle>(chop);
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        Assert.True(gbayMenus.TryPresentMenu("home"));
        var gbayId = SinglePresentation().Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay", "home", gbayId, out _));

        Assert.True(chopMenus.TryPresentMenu("companion", new JObject { ["source"] = "world-interaction" }));
        Assert.True(chopMenus.TryPresentMenu("companion", new JObject { ["source"] = "retry" }));
        var chopPending = SinglePresentation();
        var chopId = chopPending.Value<string>("presentationId")!;
        Assert.Equal("retry", chopPending["context"]!.Value<string>("source"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());

        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "gtav.chop-it-up", "companion", chopId, out var superseded));
        Assert.NotNull(superseded);
        Assert.Equal(gbayId, superseded!.Value<string>("presentationId"));
        Assert.False(ReactorHostApi.MarkMenuPresentationReady(gbayId));
        Assert.False(gbayState.IsMenuPresentationReady("home"));
        Assert.False(chopState.IsMenuPresentationReady("companion"));

        Assert.True(ReactorHostApi.MarkMenuPresentationReady(chopId));
        Assert.True(chopState.IsMenuPresentationReady("companion"));
        Assert.False(gbayState.IsMenuPresentationReady("home"));
    }

    private static IReactorExtensionHandle RegisterMenuConsumer(
        string extensionId, string label, string menuId, bool defaultOwner)
    {
        var capabilities = defaultOwner
            ? new[] { "menu.routes", ReactorExtensionCapabilities.DefaultF9MenuOwner }
            : new[] { "menu.routes" };
        return ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(extensionId, label, "1.0.0",
                capabilities: capabilities),
            builder => builder
                .AddAction(new ReactorActionDescriptor(
                    "inspect", "Inspect", ReactorActionRisk.Read),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(new ReactorMenuDescriptor(menuId, label,
                    new ReactorMenuNode[]
                    {
                        new ReactorActionNode("inspect", "Inspect", "inspect"),
                    })));
    }

    private static JObject SinglePresentation() => Assert.IsType<JObject>(
        Assert.Single(ReactorHostApi.DrainMenuPresentations()));
}
