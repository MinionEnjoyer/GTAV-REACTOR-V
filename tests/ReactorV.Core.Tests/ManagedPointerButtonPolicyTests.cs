using RageWebUI.Script;
using ReactorV.Preloader;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ManagedPointerButtonPolicyTests
{
    [Fact]
    public void CursorAndAttackAliasesProduceOneOrderedHostEdgePair()
    {
        var policy = ActivateNeutralPolicy();
        var ingress = new HostPointerIngressBuffer();

        var down = policy.Observe(
            eligible: true,
            cursorAcceptDown: true,
            gameplayAttackDown: true,
            physicalLeftButtonDown: true,
            physicalPressedSinceLastSample: true);
        Assert.True(down.Pressed);
        Assert.False(down.Released);
        Assert.True(ingress.Enqueue(Frame(down)));

        // Provider timing can differ by a frame. Keeping either alias down
        // must not manufacture a second press or an early release.
        var laggingAlias = policy.Observe(
            eligible: true,
            cursorAcceptDown: false,
            gameplayAttackDown: true,
            physicalLeftButtonDown: false,
            physicalPressedSinceLastSample: false);
        Assert.False(laggingAlias.Pressed);
        Assert.False(laggingAlias.Released);

        var up = policy.Observe(
            eligible: true,
            cursorAcceptDown: false,
            gameplayAttackDown: false,
            physicalLeftButtonDown: false,
            physicalPressedSinceLastSample: false);
        Assert.False(up.Pressed);
        Assert.True(up.Released);
        Assert.False(ingress.Enqueue(Frame(up)));

        var delivered = ingress.Drain();
        Assert.Equal(2, delivered.Frames.Count);
        Assert.True(delivered.Frames[0].Pressed);
        Assert.False(delivered.Frames[0].Released);
        Assert.False(delivered.Frames[1].Pressed);
        Assert.True(delivered.Frames[1].Released);
    }

    [Fact]
    public void InactiveGameplayAttackNeverBecomesAReactorClick()
    {
        var policy = new ManagedPointerButtonPolicy();

        var inactive = policy.Observe(
            eligible: false,
            cursorAcceptDown: false,
            gameplayAttackDown: true,
            physicalLeftButtonDown: true,
            physicalPressedSinceLastSample: true);
        Assert.False(inactive.Pressed);
        Assert.False(inactive.Released);

        var heldWhenInteractive = policy.Observe(
            eligible: true,
            cursorAcceptDown: false,
            gameplayAttackDown: true,
            physicalLeftButtonDown: true,
            physicalPressedSinceLastSample: false);
        Assert.False(heldWhenInteractive.Pressed);
        Assert.False(heldWhenInteractive.Released);

        var outsideRelease = policy.Observe(
            eligible: true,
            cursorAcceptDown: false,
            gameplayAttackDown: false,
            physicalLeftButtonDown: false,
            physicalPressedSinceLastSample: false);
        Assert.False(outsideRelease.Pressed);
        Assert.False(outsideRelease.Released);
    }

    [Fact]
    public void PhysicalSubFrameTapIsDeliveredOnceAndRequiresNeutralRearm()
    {
        var policy = ActivateNeutralPolicy();

        var tap = policy.Observe(
            eligible: true,
            cursorAcceptDown: false,
            gameplayAttackDown: false,
            physicalLeftButtonDown: false,
            physicalPressedSinceLastSample: true);
        Assert.True(tap.Pressed);
        Assert.True(tap.Released);

        var duplicate = policy.Observe(
            eligible: true,
            cursorAcceptDown: false,
            gameplayAttackDown: false,
            physicalLeftButtonDown: false,
            physicalPressedSinceLastSample: true);
        Assert.False(duplicate.Pressed);
        Assert.False(duplicate.Released);

        _ = policy.Observe(true, false, false, false, false);
        var nextTap = policy.Observe(true, false, false, false, true);
        Assert.True(nextTap.Pressed);
        Assert.True(nextTap.Released);
    }

    [Fact]
    public void ExternalProviderForegroundResetsAnOwnedDownWithoutCompletingTheClick()
    {
        var policy = ActivateNeutralPolicy();
        var down = policy.Observe(true, false, false, true, true);
        Assert.True(down.Pressed);

        var providerEligible = WindowedInputPolicy.AllowsManagedPointerSampling(
            gameForeground: false,
            interactiveLease: true,
            requestedVisible: true,
            actualVisible: true,
            trustedProviderForeground: true);
        var held = policy.Observe(providerEligible, false, false, true, false);
        Assert.False(held.Pressed);
        Assert.False(held.Released);
        Assert.False(held.Down);

        var released = policy.Observe(providerEligible, false, false, false, false);
        Assert.False(released.Pressed);
        Assert.False(released.Released);
    }

    [Fact]
    public void RealAltTabResetsAnOwnedDownWithoutADeferredReactAction()
    {
        var policy = ActivateNeutralPolicy();
        Assert.True(policy.Observe(true, false, false, true, true).Pressed);

        var otherAppEligible = WindowedInputPolicy.AllowsManagedPointerSampling(
            gameForeground: false,
            interactiveLease: true,
            requestedVisible: true,
            actualVisible: true,
            trustedProviderForeground: false);
        var boundary = policy.Observe(otherAppEligible, false, false, false, false);
        Assert.False(boundary.Pressed);
        Assert.False(boundary.Released);

        // Returning to GTA after releasing the mouse cannot complete the old
        // browser click; the first eligible sample only reseeds neutrality.
        var returned = policy.Observe(true, false, false, false, false);
        Assert.False(returned.Pressed);
        Assert.False(returned.Released);
    }

    [Fact]
    public void ControlConstantsIdentifyBothEnhancedMouseAliases()
    {
        Assert.Equal(237, GameplayMenuInputBindings.CursorAcceptControl);
        Assert.Equal(24, GameplayMenuInputBindings.GameplayAttackControl);
    }

    private static ManagedPointerButtonPolicy ActivateNeutralPolicy()
    {
        var policy = new ManagedPointerButtonPolicy();
        _ = policy.Observe(true, false, false, false, false);
        return policy;
    }

    private static HostPointerInputFrame Frame(ManagedPointerButtonDecision decision) =>
        new HostPointerInputFrame(
            0.5f,
            0.5f,
            decision.Pressed,
            decision.Released,
            wheel: 0);
}
