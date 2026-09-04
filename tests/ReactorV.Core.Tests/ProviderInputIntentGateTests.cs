using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderInputIntentGateTests
{
    [Fact]
    public void ReconnectResetsEpochNamespaceButRejectsStaleSessionFrames()
    {
        var gate = new ProviderInputIntentGate(77);
        Assert.True(gate.BeginProviderSession(1));
        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 9, 1000), 10, 1));
        Assert.True(gate.TryBind(77, 9, "old-menu", 20, 1));

        Assert.True(gate.BeginProviderSession(2));
        Assert.False(gate.TryConsume("old-menu", 30, 1, out _));
        Assert.False(gate.TryArm(
            new ProviderInputIntentToken(77, 10, 1000), 30, 1));
        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 1, 1000), 30, 2));
        Assert.True(gate.TryBind(77, 1, "new-menu", 40, 2));
        Assert.True(gate.TryConsume("new-menu", 50, 2, out var epoch));
        Assert.Equal(1, epoch);
    }

    [Fact]
    public void DisconnectRevokesCurrentSessionWithoutReopeningEpoch()
    {
        var gate = new ProviderInputIntentGate(77);
        Assert.True(gate.BeginProviderSession(1));
        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 4, 1000), 10, 1));
        Assert.True(gate.RevokeProviderSession(1));
        Assert.False(gate.TryBind(77, 4, "menu", 20, 1));
        Assert.False(gate.TryArm(
            new ProviderInputIntentToken(77, 4, 1000), 20, 1));
    }

    [Fact]
    public void ExactBoundPresentationConsumesOneShotTokenOnce()
    {
        var gate = new ProviderInputIntentGate(77);
        var token = new ProviderInputIntentToken(77, 4, 1500);

        Assert.True(gate.TryArm(token, 100));
        Assert.True(gate.TryBind(77, 4, "gbay-4", 200));
        Assert.False(gate.TryConsume("other", 250, out _));
        Assert.True(gate.TryConsume("gbay-4", 250, out var epoch));
        Assert.Equal(4, epoch);
        Assert.False(gate.TryConsume("gbay-4", 251, out _));
    }

    [Fact]
    public void WrongProcessStaleEpochAndExpiredArmFailClosed()
    {
        var gate = new ProviderInputIntentGate(77);

        Assert.False(gate.TryArm(
            new ProviderInputIntentToken(78, 1, 1500),
            0));
        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 2, 1500),
            0));
        Assert.False(gate.TryArm(
            new ProviderInputIntentToken(77, 2, 1500),
            1));
        Assert.False(gate.TryBind(77, 2, "gbay-2", 1501));
        Assert.False(gate.TryConsume("gbay-2", 1501, out _));
    }

    [Fact]
    public void BoundTokenExpiresAndNewerArmInvalidatesOlderAuthority()
    {
        var gate = new ProviderInputIntentGate(77);

        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 1, 1500),
            0));
        Assert.True(gate.TryBind(77, 1, "gbay-old", 100));
        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 2, 1500),
            200));
        Assert.False(gate.TryConsume("gbay-old", 201, out _));
        Assert.True(gate.TryBind(77, 2, "gbay-new", 300));
        Assert.False(gate.TryConsume(
            "gbay-new",
            300 + ProviderInputIntentGate.BoundPresentationLifetimeMilliseconds + 1,
            out _));
    }

    [Fact]
    public void ExplicitCancelRevokesArmedAndBoundForms()
    {
        var gate = new ProviderInputIntentGate(77);
        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 1, 1500),
            0));
        gate.Cancel(77, 1);
        Assert.False(gate.TryBind(77, 1, "gbay-1", 10));

        Assert.True(gate.TryArm(
            new ProviderInputIntentToken(77, 2, 1500),
            20));
        Assert.True(gate.TryBind(77, 2, "gbay-2", 30));
        gate.Cancel(77, 2);
        Assert.False(gate.TryConsume("gbay-2", 40, out _));
    }
}
