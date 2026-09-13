using System;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using RageWebUI.Script;
using ReactorV.Integration;
using Xunit;

namespace RageWebUI.Core.Tests;

/// <summary>
/// Consumer-level regressions for the three presentation shapes used by the
/// ALLIN1 ecosystem: GBAY's default menu, a passive speedometer, and a
/// representative third-party companion menu.  The companion provider uses
/// Chop's real extension ID, while its descriptor stays local so this covers
/// the public Reactor contract rather than duplicating the external plug-in.
/// </summary>
[Collection(ReactorIntegrationCollection.Name)]
public sealed class Allin1ConsumerReadinessRegressionTests : IDisposable
{
    public Allin1ConsumerReadinessRegressionTests() => ReactorHostApi.Reset();

    public void Dispose() => ReactorHostApi.Reset();

    [Fact]
    public void GbayRecoversAfterRendererReadinessWithoutAcceptingPendingInputOrStaleAcknowledgements()
    {
        using var gbay = RegisterMenuConsumer(
            "allin1.gbay", "GBAY", "home", defaultOwner: true);
        var menus = Assert.IsAssignableFrom<IReactorMenuPresentationHandle>(gbay);
        var state = Assert.IsAssignableFrom<IReactorMenuPresentationStateHandle>(gbay);

        // A menu request made while the renderer host is unavailable cannot
        // manufacture a presentation or a cursor/input owner.
        Assert.False(menus.TryPresentMenu("home", new JObject { ["attempt"] = 1 }));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.False(MenuPresentationPolicy.ShouldAcquireManagedInputLease(
            overlayRequestedVisible: true,
            overlayPresented: false,
            MenuPresentationPolicy.PendingPresentationInputMode));

        ReactorHostApi.SetMenuPresentationHostAvailable(true);
        Assert.True(menus.TryPresentMenu("home", new JObject { ["attempt"] = 2 }));
        Assert.True(menus.TryPresentMenu("home", new JObject { ["attempt"] = 3 }));
        var pending = SinglePresentation();
        var firstId = pending.Value<string>("presentationId")!;
        Assert.Equal(3, pending["context"]!.Value<int>("attempt"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());

        // A different provider cannot claim GBAY's queued generation.
        Assert.False(ReactorHostApi.MarkMenuPresentationActive(
            "gtav.chop-it-up", "companion", firstId, out _));
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay", "home", firstId, out _));
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.False(MenuPresentationPolicy.ShouldAcquireManagedInputLease(
            overlayRequestedVisible: true,
            overlayPresented: true,
            MenuPresentationPolicy.PendingPresentationInputMode));

        // A close before paint-ready revokes that exact pending generation.
        Assert.True(menus.TryDismissMenu("home"));
        Assert.False(ReactorHostApi.MarkMenuPresentationReady(firstId));
        Assert.NotNull(ReactorHostApi.AcknowledgeMenuPresentationHidden(firstId));
        Assert.False(state.IsMenuPresentationReady("home"));

        // The next exact ready acknowledgement creates a fresh generation;
        // the old acknowledgement cannot capture the recovered menu.
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
            overlayRequestedVisible: true,
            overlayPresented: true,
            MenuPresentationPolicy.ReadyPresentationInputMode));
    }

    [Fact]
    public void DelayedThirdPartyChopReadinessCannotCommitOrDuplicateTheSupersededGbayOwner()
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
        var gbayPending = SinglePresentation();
        var gbayId = gbayPending.Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay", "home", gbayId, out _));
        Assert.False(gbayState.IsMenuPresentationReady("home"));

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

    [Fact]
    public void SpeedometerCanPublishBeforeTheRendererWithoutOpeningAMenuOrAcquiringInput()
    {
        using var speedometer = ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(
                "allin1.gbay", "ALLIN1 speedometer", "1.0.0",
                capabilities: new[] { PassiveHudContract.Capability }),
            builder => builder.AddEvent(new ReactorEventDescriptor(
                PassiveHudContract.EventId, "Passive driving readout")));
        var frame = new JObject
        {
            ["schema"] = 1,
            ["visible"] = true,
            ["kind"] = "speedometer",
            ["speed"] = 42,
            ["units"] = "MPH",
            ["gear"] = "3",
            ["manual"] = false,
            ["notice"] = "",
        };

        // Publishing is independent of the menu renderer: it must not create
        // a menu intent, and the passive slot has no pointer/input authority.
        Assert.True(speedometer.TryPublishEvent(PassiveHudContract.EventId, frame));
        Assert.Single(ReactorHostApi.DrainEvents());
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.False(MenuPresentationPolicy.ShouldAcquireManagedInputLease(
            overlayRequestedVisible: true,
            overlayPresented: true,
            MenuPresentationPolicy.PendingPresentationInputMode));

        var hostLease = new PassiveHudHostLease();
        var stamped = PassiveHudHostLease.CreateFrame(
            frame, generation: 7, utcNow: DateTime.UtcNow);
        Assert.False(hostLease.Accept(stamped, DateTime.UtcNow, now: 0));
        hostLease.BeginSurface(HostSurfaceMode.PassiveHud, generation: 7, now: 1);
        Assert.True(hostLease.Accept(stamped, DateTime.UtcNow, now: 1));

        var slot = new PassiveHudLease();
        Assert.True(slot.Accept("allin1.gbay", frame, now: 0));
        Assert.False(slot.Accept("gtav.chop-it-up", new JObject
        {
            ["schema"] = 1,
            ["visible"] = false,
        }, now: 1));
        Assert.Equal("allin1.gbay", slot.Owner);
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
