using Newtonsoft.Json.Linq;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class MenuPresentationPolicyTests
{
    [Theory]
    [InlineData(false, true, "exclusive", false)]
    [InlineData(false, false, "exclusive", false)]
    [InlineData(true, false, "game", false)]
    [InlineData(true, false, "interactive-menu", false)]
    [InlineData(true, true, "interactive-menu", true)]
    public void PassiveBootstrapSurfaceNeverAcquiresManagedInput(
        bool requestedVisible,
        bool presented,
        string inputMode,
        bool expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ShouldAcquireManagedInputLease(
                requestedVisible,
                presented,
                inputMode));
    }

    [Fact]
    public void InteractiveMenuOwnsBackAndUsesPointer()
    {
        Assert.Equal("menu.presentation", MenuPresentationPolicy.EventName);
        Assert.Equal("menu.dismissed", MenuPresentationPolicy.DismissedEventName);
        Assert.True(MenuPresentationPolicy.OwnsBack("interactive-menu"));
        Assert.True(MenuPresentationPolicy.UsesPointer("interactive-menu"));
        Assert.False(MenuPresentationPolicy.OwnsBack("exclusive"));
    }

    [Fact]
    public void PresentationInputActivatesOnlyAfterThePaintAcknowledgement()
    {
        Assert.Equal("game", MenuPresentationPolicy.PendingPresentationInputMode);
        Assert.False(MenuPresentationPolicy.UsesPointer(
            MenuPresentationPolicy.PendingPresentationInputMode));
        Assert.Equal("interactive-menu", MenuPresentationPolicy.ReadyPresentationInputMode);
        Assert.True(MenuPresentationPolicy.UsesPointer(
            MenuPresentationPolicy.ReadyPresentationInputMode));
    }

    [Theory]
    [InlineData(false, "initializing", false)]
    [InlineData(true, "initializing", true)]
    [InlineData(true, "about", false)]
    [InlineData(true, "none", false)]
    public void InitializerRetiresOnlyAtTheMatchingPresentationPaintBoundary(
        bool matchingPresentationReady,
        string currentHostSurface,
        bool expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ShouldRetireInitializerAfterPaint(
                matchingPresentationReady,
                currentHostSurface));
    }

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "none")]
    [InlineData(false, true, "none")]
    [InlineData(true, true, "none")]
    [InlineData(false, true, "initializing")]
    [InlineData(true, true, "initializing")]
    [InlineData(false, true, "about")]
    public void PersistentDocumentPresentationDispatchNeverHidesTheCommittedFrame(
        bool requestedVisible,
        bool visible,
        string currentHostSurface)
    {
        Assert.False(MenuPresentationPolicy.RequiresHideBeforeDispatch(
            requestedVisible,
            visible,
            currentHostSurface));
    }

    [Theory]
    [InlineData("initializing", "initializing")]
    [InlineData("about", "about")]
    [InlineData("verifying", "verifying")]
    [InlineData("unknown", "none")]
    [InlineData(null, "none")]
    public void BootstrapSurfaceModesFailClosed(string? value, string expected)
    {
        Assert.Equal(expected, HostSurfaceMode.Normalize(value));
    }

    [Theory]
    [InlineData(false, "none", false, false)]
    [InlineData(false, "about", false, true)]
    [InlineData(false, "initializing", false, true)]
    [InlineData(false, "initializing", true, false)]
    [InlineData(true, "none", false, false)]
    [InlineData(true, "initializing", true, false)]
    public void RuntimeHandoffRetiresAnyUnclaimedBootstrapSurface(
        bool requestedVisible,
        string currentHostSurface,
        bool defaultMenuIntentActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ShouldReleaseBootstrapSurface(
                requestedVisible,
                currentHostSurface,
                defaultMenuIntentActive));
    }

    [Theory]
    [InlineData(false, 500L, 500L, "initializing", true)]
    [InlineData(false, 499L, 500L, "initializing", false)]
    [InlineData(true, 500L, 500L, "initializing", false)]
    [InlineData(false, 500L, 500L, "about", false)]
    [InlineData(false, 500L, 500L, "none", false)]
    public void ManagedStartupStatusRefreshesOnlyUnderInitializerAuthority(
        bool complete,
        long elapsedMilliseconds,
        long nextRefreshAt,
        string currentHostSurface,
        bool expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ShouldRefreshManagedStartupStatus(
                complete,
                elapsedMilliseconds,
                nextRefreshAt,
                currentHostSurface));
    }

    [Fact]
    public void RegisteredDefaultMenuOwnerExclusivelyOwnsManagedPhysicalF9()
    {
        Assert.True(MenuPresentationPolicy.ShouldDeferPhysicalF9ToExtension(
            hasDefaultMenuOwner: true));
        Assert.False(MenuPresentationPolicy.ShouldDeferPhysicalF9ToExtension(
            hasDefaultMenuOwner: false));
    }

    [Theory]
    [InlineData(false, false, false, "GenericToggle")]
    [InlineData(false, true, false, "GenericToggle")]
    [InlineData(true, false, false, "GenericToggle")]
    [InlineData(true, false, true, "GenericToggle")]
    [InlineData(true, true, false, "ArmDefaultOwnerInputIntent")]
    [InlineData(true, true, true, "YieldToDefaultOwner")]
    public void DefaultOwnerIsTheOnlyAuthorityForItsPhysicalF9Edge(
        bool isPhysicalF9,
        bool hasDefaultMenuOwner,
        bool defaultOwnerPresentationOrIntentActive,
        string expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ResolveManagedF9Edge(
                isPhysicalF9,
                hasDefaultMenuOwner,
                defaultOwnerPresentationOrIntentActive).ToString());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ExtensionQueueUsesStableStoryOwnershipInsteadOfTransientPlayability(
        bool storyModeReady,
        bool browserReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ShouldServiceExtensionMenuQueue(
                storyModeReady,
                browserReady));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void AuthoritativeHostHideReleasesRequestedMenuOwnership(
        bool requestedVisible,
        bool hostVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            MenuPresentationPolicy.ShouldReconcileHostHide(
                requestedVisible,
                hostVisible));
    }

    [Fact]
    public void StartupPresentationCarriesOnlyABoundedProcessScopedMarker()
    {
        var payload = new JObject
        {
            ["context"] = new JObject
            {
                [MenuPresentationPolicy.StartupIntentProcessIdContextKey] = 4242,
            },
        };
        Assert.True(MenuPresentationPolicy.TryGetStartupIntentProcessId(
            payload,
            out var processId));
        Assert.Equal(4242, processId);
        payload["context"]![MenuPresentationPolicy.StartupIntentProcessIdContextKey] =
            (long)int.MaxValue + 1;
        Assert.False(MenuPresentationPolicy.TryGetStartupIntentProcessId(
            payload,
            out _));
    }

    [Fact]
    public void PresentationPayloadIsTypedAndDetached()
    {
        var context = new JObject { ["source"] = "gbay" };
        var record = new JObject
        {
            ["extensionId"] = "allin1.online",
            ["menuId"] = "gbay",
            ["presentationId"] = "0123456789abcdef0123456789abcdef",
            ["context"] = context,
            ["inputMode"] = "untrusted",
        };

        Assert.True(MenuPresentationPolicy.TryCreatePayload(record, out var payload));
        context["source"] = "changed";

        Assert.NotNull(payload);
        Assert.Equal("interactive-menu", payload!.Value<string>("inputMode"));
        Assert.Equal("gbay", payload.Value<string>("menuId"));
        Assert.Equal("gbay", payload["context"]!.Value<string>("source"));
        Assert.Equal(5, payload.Count);
    }

    [Theory]
    [InlineData("valid-presentation:1")]
    [InlineData("A_b.c-9")]
    public void PresentationIdentifierMatchesBrowserBoundary(string value)
    {
        Assert.True(MenuPresentationPolicy.IsValidPresentationId(value));
        Assert.False(MenuPresentationPolicy.IsValidPresentationId(" has-space"));
        Assert.False(MenuPresentationPolicy.IsValidPresentationId("unsafe/path"));
        Assert.False(MenuPresentationPolicy.IsValidPresentationId(new string('a', 129)));
    }
}
