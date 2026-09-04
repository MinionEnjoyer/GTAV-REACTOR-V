using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class OverlayTransferStateMachineTests
{
    [Fact]
    public void FullDesktopVerifiedTransferBecomesInteractive()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.BrowserPaintVerified,
            OverlayTransferPhase.WindowPromoted));
        Assert.False(machine.IsInteractive);
        Assert.False(machine.TryAdvance(
            identity,
            OverlayTransferPhase.WindowPromoted,
            OverlayTransferPhase.DesktopPresentationVerified));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.WindowPromoted,
            OverlayTransferPhase.CompositionCommittedVisible));
        Assert.False(machine.IsInteractive);
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.CompositionCommittedVisible,
            OverlayTransferPhase.DesktopPresentationVerified));
        Assert.False(machine.IsInteractive);
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.DesktopPresentationVerified,
            OverlayTransferPhase.Interactive));
        Assert.True(machine.IsInteractive);
    }

    [Fact]
    public void BrowserPixelsCannotSkipDesktopProof()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        Assert.False(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.Interactive));
        Assert.False(machine.IsInteractive);
    }

    [Fact]
    public void CompositionQualifiedWindowCanRemainVisibleWithoutBecomingInteractive()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.BrowserPaintVerified,
            OverlayTransferPhase.WindowPromoted));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.WindowPromoted,
            OverlayTransferPhase.CompositionCommittedVisible));
        Assert.False(machine.IsInteractive);
        Assert.False(machine.TryAdvance(
            identity,
            OverlayTransferPhase.CompositionCommittedVisible,
            OverlayTransferPhase.Interactive));
    }

    [Fact]
    public void IndependentDesktopProofCanUpgradePassiveCompositionToInteractive()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.BrowserPaintVerified,
            OverlayTransferPhase.WindowPromoted));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.WindowPromoted,
            OverlayTransferPhase.CompositionCommittedVisible));
        Assert.False(machine.IsInteractive);
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.CompositionCommittedVisible,
            OverlayTransferPhase.DesktopPresentationVerified));
        Assert.False(machine.IsInteractive);
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.DesktopPresentationVerified,
            OverlayTransferPhase.Interactive));
        Assert.True(machine.IsInteractive);
    }

    [Fact]
    public void ExplicitUserIntentCanUpgradePassiveCompositionWithoutDesktopProof()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.BrowserPaintVerified,
            OverlayTransferPhase.WindowPromoted));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.WindowPromoted,
            OverlayTransferPhase.CompositionCommittedVisible));
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.CompositionCommittedVisible,
            OverlayTransferPhase.ExplicitUserIntentAuthorized));
        Assert.False(machine.IsInteractive);
        Assert.True(machine.TryAdvance(
            identity,
            OverlayTransferPhase.ExplicitUserIntentAuthorized,
            OverlayTransferPhase.Interactive));
        Assert.True(machine.IsInteractive);
    }

    [Fact]
    public void StaleAcknowledgementCannotAdvanceReplacement()
    {
        var machine = new OverlayTransferStateMachine();
        var stale = Identity(1, "gbay-1");
        var current = Identity(2, "gbay-2");

        Assert.True(machine.Begin(stale));
        Assert.True(machine.Begin(current));

        Assert.False(machine.TryAdvance(
            stale,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
        Assert.Equal(OverlayTransferPhase.Preparing, machine.Phase);
        Assert.Equal(current, machine.Identity);
    }

    [Fact]
    public void FailedTransferCannotBeResurrected()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        Assert.True(machine.TryFail(identity, "desktop pixels absent"));
        Assert.Equal(OverlayTransferPhase.Failed, machine.Phase);
        Assert.Equal("desktop pixels absent", machine.FailureReason);
        Assert.False(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
    }

    [Fact]
    public void HideInvalidatesEveryOutstandingAcknowledgement()
    {
        var machine = new OverlayTransferStateMachine();
        var identity = Identity(1, "gbay-1");

        Assert.True(machine.Begin(identity));
        machine.Hide();

        Assert.Equal(OverlayTransferPhase.Hidden, machine.Phase);
        Assert.Null(machine.Identity);
        Assert.False(machine.TryAdvance(
            identity,
            OverlayTransferPhase.Preparing,
            OverlayTransferPhase.BrowserPaintVerified));
    }

    [Fact]
    public void LateBeginCannotReplaceANewerTransferOrReviveAfterHide()
    {
        var machine = new OverlayTransferStateMachine();
        var stale = Identity(3, "gbay-old");
        var current = Identity(4, "gbay-current");

        Assert.True(machine.Begin(current));
        Assert.False(machine.Begin(stale));
        Assert.Equal(current, machine.Identity);
        Assert.Equal(OverlayTransferPhase.Preparing, machine.Phase);

        machine.Hide();
        Assert.False(machine.Begin(stale));
        Assert.Equal(OverlayTransferPhase.Hidden, machine.Phase);
        Assert.Null(machine.Identity);
    }

    private static OverlayTransferIdentity Identity(
        int generation,
        string presentationId) => new(
            OverlayTransferOwner.Provider,
            generation,
            gameWindow: 123,
            width: 1920,
            height: 1080,
            controllerGeneration: 4,
            compositionGeneration: 8,
            providerSessionGeneration: 2,
            surfaceMode: "none",
            surfaceGeneration: 0,
            presentationId);
}
