using RageWebUI.Core;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DefaultOwnerDispatchIntentTests
{
    [Fact]
    public void EveryDispatchPrerequisiteIsRequired()
    {
        // Includes background, released key, startup, replacement, close,
        // duplicate/pending intent and debounce rejection in every combination.
        for (var bits = 0; bits < 256; bits++)
        {
            bool Bit(int n) => (bits & (1 << n)) != 0;
            var allowed = MenuPresentationPolicy.ShouldArmDefaultOwnerAtDispatch(
                Bit(0), Bit(1), Bit(2), Bit(3), Bit(4), Bit(5), Bit(6), Bit(7));
            Assert.Equal(bits == 143, allowed);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherOpeningOrderBindsExactlyOnceAndLaterKeyDownYields(bool keyDownFirst)
    {
        var gate = new ProviderInputIntentGate(77);
        var pending = false;
        var arms = 0;
        void Arm()
        {
            arms++;
            Assert.True(gate.TryArm(new ProviderInputIntentToken(77, arms, 1500), 100));
            pending = true;
        }

        if (keyDownFirst)
        {
            Assert.Equal(ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent,
                MenuPresentationPolicy.ResolveManagedF9Edge(true, true, false));
            Arm();
        }

        if (MenuPresentationPolicy.ShouldArmDefaultOwnerAtDispatch(
                true, true, true, true, false, false, pending, !keyDownFirst))
            Arm();

        Assert.True(gate.TryBind(77, arms, "gbay-opening", 110));
        Assert.Equal(1, arms);
        Assert.Equal(ManagedF9EdgeDisposition.YieldToDefaultOwner,
            MenuPresentationPolicy.ResolveManagedF9Edge(true, true, true));
        Assert.False(gate.TryConsume("stale-menu", 200, out _));
        Assert.True(gate.TryConsume("gbay-opening", 200, out var epoch));
        Assert.Equal(1, epoch);
        Assert.False(gate.TryConsume("gbay-opening", 201, out _));
    }
}
