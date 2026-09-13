using RageWebUI.Core;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DefaultOwnerCloseIntentTests
{
    [Fact]
    public void Test3CloseThenDelayedKeyDownDoesNotMintAnOpeningIntent()
    {
        var lease = InteractiveLease();
        var authority = new ProviderInputIntentGate(25536);
        Assert.True(authority.TryArm(new ProviderInputIntentToken(25536, 1, 1500), 21146));
        Assert.True(authority.TryBind(25536, 1, "gbay-first", 21151));
        Assert.True(authority.TryConsume("gbay-first", 21825, out _));

        // Replay the ordering/times in Test 3. The owner has cleared its menu
        // and intent before Reactor receives the delayed SHVDN close KeyDown.
        authority.Cancel(25536, 1);
        lease.Advance(false, true, 22492);
        Assert.Equal(MenuInputLeaseState.Disarming, lease.State);
        Assert.Equal(ManagedF9EdgeDisposition.YieldToDefaultOwner,
            MenuPresentationPolicy.ResolveManagedF9Edge(true, true, false, lease.SuppressGameInput));
        Assert.False(authority.TryBind(25536, 2, "phantom-opening", 22499));

        lease.Advance(false, true, 22692);
        lease.Advance(false, true, 22693);
        Assert.Equal(MenuInputLeaseState.Hidden, lease.State);
        Assert.Equal(ProviderIntentEscapeDisposition.ContinueNormalRouting,
            MenuPresentationPolicy.ResolveProviderIntentEscape(false, false, false, false));

        // A fresh post-release opening is not blocked by a sticky close token.
        Assert.Equal(ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent,
            MenuPresentationPolicy.ResolveManagedF9Edge(true, true, false, lease.SuppressGameInput));
        Assert.True(authority.TryArm(new ProviderInputIntentToken(25536, 2, 1500), 24000));
        Assert.True(authority.TryBind(25536, 2, "gbay-second", 24001));
        Assert.True(authority.TryConsume("gbay-second", 24002, out _));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void EveryOwnedLeasePhaseYieldsAfterPresentationStateClears(int phase, bool yields)
    {
        var lease = new MenuInputLease();
        if (phase >= 1) lease.Advance(true, true, 0);
        if (phase >= 2) lease.Advance(true, true, 1);
        if (phase >= 3) lease.Advance(false, true, 2);
        Assert.Equal(yields ? ManagedF9EdgeDisposition.YieldToDefaultOwner : ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent,
            MenuPresentationPolicy.ResolveManagedF9Edge(true, true, false, lease.SuppressGameInput));
    }

    [Fact]
    public void KeyDownBeforeOwnerCloseAndRepeatedHeldCloseBothYield()
    {
        var lease = InteractiveLease();
        Assert.Equal(ManagedF9EdgeDisposition.YieldToDefaultOwner,
            MenuPresentationPolicy.ResolveManagedF9Edge(true, true, true, lease.SuppressGameInput));
        lease.Advance(false, false, 100);
        foreach (var time in new[] { 101, 200, 300, 1000 }) {
            lease.Advance(false, false, time);
            Assert.Equal(ManagedF9EdgeDisposition.YieldToDefaultOwner,
                MenuPresentationPolicy.ResolveManagedF9Edge(true, true, false, lease.SuppressGameInput));
        }
    }

    [Fact]
    public void FreshTypedOwnerOpeningDuringCloseGraceStillUsesDispatchAuthority()
    {
        var lease = InteractiveLease();
        lease.Advance(false, true, 100);
        // Only stale managed-key arming is fenced. The existing accepted,
        // physically-held, fresh owner-dispatch path is not changed.
        Assert.True(MenuPresentationPolicy.ShouldArmDefaultOwnerAtDispatch(
            true, true, true, true, false, false, false, true));
        Assert.Equal(MenuInputLeaseState.Arming, lease.Advance(true, true, 101).State);
    }

    [Fact]
    public void GenericToggleAndNonF9KeysAreNotChangedByTheOwnerLeaseFence()
    {
        foreach (var physicalF9 in new[] { false, true })
        foreach (var owner in new[] { false, true })
        foreach (var active in new[] { false, true }) {
            if (physicalF9 && owner) continue;
            Assert.Equal(ManagedF9EdgeDisposition.GenericToggle,
                MenuPresentationPolicy.ResolveManagedF9Edge(physicalF9, owner, active, true));
        }
    }

    [Theory]
    [InlineData(0, 0, 5000, false)]
    [InlineData(-1, 1500, 5000, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(1, -1, 0, true)]
    [InlineData(1, 1500, -1, true)]
    [InlineData(1, 1500, 1499, false)]
    [InlineData(1, 1500, 1500, false)]
    [InlineData(1, 1500, 1501, true)]
    [InlineData(1, 1500, 60000, true)]
    public void UnboundExpiryIsInclusiveAtTheExistingHostDeadline(long epoch, long deadline, long now, bool expires)
    {
        Assert.Equal(expires, MenuPresentationPolicy.ShouldExpirePendingProviderIntent(epoch, deadline, now));
    }

    [Fact]
    public void ExpiredUnboundIntentCannotBindAfterLongTickGapAndNewEpochStillWorks()
    {
        var gate = new ProviderInputIntentGate(7);
        Assert.True(gate.TryArm(new ProviderInputIntentToken(7, 1, 1500), 100));
        Assert.True(MenuPresentationPolicy.ShouldExpirePendingProviderIntent(1, 1600, 60000));
        gate.Cancel(7, 1);
        Assert.False(gate.TryBind(7, 1, "late", 60000));
        Assert.True(gate.TryArm(new ProviderInputIntentToken(7, 2, 1500), 60001));
        Assert.True(gate.TryBind(7, 2, "new", 60002));
    }

    [Fact]
    public void EscapeRoutesAllIntentAndPresentationCombinationsWithoutInventingAMenu()
    {
        for (var bits = 0; bits < 16; bits++) {
            bool Bit(int bit) => (bits & (1 << bit)) != 0;
            var expected = Bit(1) || Bit(2) || (Bit(0) && Bit(3))
                ? ProviderIntentEscapeDisposition.ClosePresentation
                : Bit(0) ? ProviderIntentEscapeDisposition.CancelUnboundIntent
                : ProviderIntentEscapeDisposition.ContinueNormalRouting;
            Assert.Equal(expected, MenuPresentationPolicy.ResolveProviderIntentEscape(Bit(0), Bit(1), Bit(2), Bit(3)));
        }
    }

    [Fact]
    public void VeryLateUnboundKeyIsCancelledOnEscapeWithoutMenuDismissalAuthority()
    {
        var gate = new ProviderInputIntentGate(7);
        Assert.True(gate.TryArm(new ProviderInputIntentToken(7, 3, 1500), 3000));
        Assert.Equal(ProviderIntentEscapeDisposition.CancelUnboundIntent,
            MenuPresentationPolicy.ResolveProviderIntentEscape(true, false, false, false));
        gate.Cancel(7, 3);
        Assert.False(gate.TryBind(7, 3, "unrequested", 3001));
        Assert.Equal(ProviderIntentEscapeDisposition.ContinueNormalRouting,
            MenuPresentationPolicy.ResolveProviderIntentEscape(false, false, false, false));
    }

    private static MenuInputLease InteractiveLease()
    {
        var lease = new MenuInputLease();
        lease.Advance(true, true, 0);
        lease.Advance(true, true, 1);
        return lease;
    }
}
