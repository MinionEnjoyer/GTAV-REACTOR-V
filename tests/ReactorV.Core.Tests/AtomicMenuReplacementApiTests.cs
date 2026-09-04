using System;
using Newtonsoft.Json.Linq;
using ReactorV.Integration;
using Xunit;

namespace RageWebUI.Core.Tests;

[Collection(ReactorIntegrationCollection.Name)]
public sealed class AtomicMenuReplacementApiTests : IDisposable
{
    public AtomicMenuReplacementApiTests() => ReactorHostApi.Reset();

    public void Dispose() => ReactorHostApi.Reset();

    [Theory]
    [InlineData("home", "vehicles")]
    [InlineData("vehicles", "vehicles")]
    public void ActiveOwnerIsSupersededOnlyAfterExactReplacementBecomesReady(
        string committedMenuId,
        string replacementMenuId)
    {
        using var extension = ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(
                "atomic.fixture",
                "Atomic fixture",
                "1.0.0",
                capabilities: new[] { "menu.routes" }),
            builder => builder
                .AddAction(
                    new ReactorActionDescriptor(
                        "read",
                        "Read",
                        ReactorActionRisk.Read),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("home", "Home"))
                .AddMenu(Menu("vehicles", "Vehicles")));
        var menus = (IReactorMenuPresentationHandle)extension;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        Assert.True(menus.TryPresentMenu(committedMenuId));
        var committed = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var committedPresentationId = committed.Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "atomic.fixture",
            committedMenuId,
            committedPresentationId,
            out var firstSuperseded));
        Assert.Null(firstSuperseded);

        Assert.True(menus.TryPresentMenu(
            replacementMenuId,
            new JObject { ["menuRevision"] = "replacement" }));
        var replacement = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var replacementPresentationId = replacement.Value<string>("presentationId")!;

        // Merely dispatching a cross-key or same-key replacement cannot emit a
        // dismissal or retire the committed owner. That happens only when the
        // host reports the exact replacement presentation as painted.
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());
        Assert.True(menus.IsMenuPresented(committedMenuId));
        Assert.True(menus.IsMenuPresented(replacementMenuId));

        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "atomic.fixture",
            replacementMenuId,
            replacementPresentationId,
            out var superseded));
        Assert.NotNull(superseded);
        Assert.Equal(committedMenuId, superseded!.Value<string>("menuId"));
        Assert.Equal(
            committedPresentationId,
            superseded.Value<string>("presentationId"));

        var active = ReactorHostApi.TakeActiveMenuPresentation();
        Assert.NotNull(active);
        Assert.Equal(replacementMenuId, active!.Value<string>("menuId"));
        Assert.Equal(
            replacementPresentationId,
            active.Value<string>("presentationId"));
    }

    [Fact]
    public void PostHandoffMenuClosesHidesAndReopensAsOneFreshReadyGeneration()
    {
        using var extension = ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(
                "allin1.gbay.lifecycle",
                "ALLIN1 GBAY lifecycle fixture",
                "1.0.0",
                capabilities: new[]
                {
                    "menu.routes",
                    ReactorExtensionCapabilities.DefaultF9MenuOwner,
                }),
            builder => builder
                .AddAction(
                    new ReactorActionDescriptor(
                        "read",
                        "Read",
                        ReactorActionRisk.Read),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("home", "GBAY")));
        var menus = (IReactorMenuPresentationHandle)extension;
        var state = (IReactorMenuPresentationStateHandle)extension;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        // The startup/post-handoff request becomes authoritative only after
        // the exact browser generation is both active and paint-ready.
        Assert.True(menus.TryPresentMenu("home"));
        var startup = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var startupPresentationId = startup.Value<string>("presentationId")!;
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay.lifecycle",
            "home",
            startupPresentationId,
            out var startupSuperseded));
        Assert.Null(startupSuperseded);
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(
            startupPresentationId));
        Assert.True(state.IsMenuPresentationReady("home"));

        // This is the registry boundary used by the managed F9 owner. A close
        // request remains authoritative until the script host acknowledges
        // that this exact presentation has been hidden.
        Assert.True(menus.TryDismissMenu("home"));
        Assert.True(menus.IsMenuPresented("home"));
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.True(menus.TryDismissMenu("home"));
        var startupDismissal = Assert.Single(
            ReactorHostApi.DrainMenuDismissals()).Value<JObject>()!;
        Assert.Equal(
            startupPresentationId,
            startupDismissal.Value<string>("presentationId"));
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());

        // A released F9/open retry cannot race the host hide request.
        Assert.False(menus.TryPresentMenu(
            "home",
            new JObject { ["attempt"] = 0 }));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());

        var hidden = ReactorHostApi.AcknowledgeMenuPresentationHidden(
            startupPresentationId);
        Assert.NotNull(hidden);
        Assert.Equal(
            startupPresentationId,
            hidden!.Value<string>("presentationId"));
        Assert.False(menus.IsMenuPresented("home"));
        Assert.Null(ReactorHostApi.AcknowledgeMenuPresentationHidden(
            startupPresentationId));

        // Repeated open intent before dispatch coalesces to one generation.
        // This models a caller retry without allowing duplicate GBAY surfaces.
        Assert.True(menus.TryPresentMenu(
            "home",
            new JObject { ["attempt"] = 1 }));
        Assert.True(menus.TryPresentMenu(
            "home",
            new JObject { ["attempt"] = 2 }));
        var reopened = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var reopenedPresentationId = reopened.Value<string>("presentationId")!;
        Assert.NotEqual(startupPresentationId, reopenedPresentationId);
        Assert.Equal(2, reopened["context"]!.Value<int>("attempt"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "allin1.gbay.lifecycle",
            "home",
            reopenedPresentationId,
            out var reopenedSuperseded));
        Assert.Null(reopenedSuperseded);
        Assert.True(ReactorHostApi.MarkMenuPresentationReady(
            reopenedPresentationId));
        Assert.True(state.IsMenuPresentationReady("home"));

        // If the host observes a manual/API hide before the queued extension
        // dismissal is drained, consuming the exact active generation also
        // removes that stale queue item. The browser gets one dismissal edge.
        Assert.True(menus.TryDismissMenu("home"));
        Assert.True(menus.IsMenuPresented("home"));
        var manualClose = ReactorHostApi.TakeActiveMenuPresentation();
        Assert.NotNull(manualClose);
        Assert.Equal(
            reopenedPresentationId,
            manualClose!.Value<string>("presentationId"));
        Assert.False(menus.IsMenuPresented("home"));
        Assert.False(state.IsMenuPresentationReady("home"));
        Assert.Empty(ReactorHostApi.DrainMenuPresentations());
        Assert.Empty(ReactorHostApi.DrainMenuDismissals());
        Assert.Null(ReactorHostApi.TakeActiveMenuPresentation());
    }

    [Fact]
    public void BrowserPreparedValidationDoesNotMarkRegistryReady()
    {
        using var extension = ReactorApi.RegisterExtension(
            new ReactorExtensionDescriptor(
                "atomic.two-phase.fixture",
                "Atomic two phase fixture",
                "1.0.0",
                capabilities: new[] { "menu.routes" }),
            builder => builder
                .AddAction(
                    new ReactorActionDescriptor(
                        "read",
                        "Read",
                        ReactorActionRisk.Read),
                    (_, __) => ReactorActionResult.Success())
                .AddMenu(Menu("home", "Home")));
        var menus = (IReactorMenuPresentationHandle)extension;
        var state = (IReactorMenuPresentationStateHandle)extension;
        ReactorHostApi.SetMenuPresentationHostAvailable(true);

        Assert.True(menus.TryPresentMenu("home"));
        var presentation = Assert.Single(
            ReactorHostApi.DrainMenuPresentations()).Value<JObject>()!;
        var presentationId = presentation.Value<string>("presentationId")!;
        Assert.True(ReactorHostApi.MarkMenuPresentationActive(
            "atomic.two-phase.fixture",
            "home",
            presentationId,
            out _));

        Assert.False(ReactorHostApi.CanMarkMenuPresentationReady(
            "presentation:stale"));
        Assert.True(ReactorHostApi.CanMarkMenuPresentationReady(
            presentationId));
        Assert.False(state.IsMenuPresentationReady("home"));

        Assert.True(ReactorHostApi.MarkMenuPresentationReady(presentationId));
        Assert.True(state.IsMenuPresentationReady("home"));
    }

    private static ReactorMenuDescriptor Menu(string id, string label) =>
        new ReactorMenuDescriptor(
            id,
            label,
            new ReactorMenuNode[]
            {
                new ReactorActionNode("inspect", "Inspect", "read"),
            });
}
