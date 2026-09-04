using System;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderPresentationCommitGateTests
{
    [Fact]
    public void OnlyTheExactPendingPresentationCanCommit()
    {
        var gate = new ProviderPresentationCommitGate(timeoutMilliseconds: 5000);
        gate.Begin(
            "allin1.gbay:home:42",
            preparedAtMilliseconds: 1000,
            browserPreparationWaitMilliseconds: 75);

        Assert.False(gate.TryCommit(
            "allin1.gbay:home:41",
            committedAtMilliseconds: 1200,
            out _,
            out _));
        Assert.Equal("allin1.gbay:home:42", gate.PendingPresentationId);

        Assert.True(gate.TryCommit(
            "allin1.gbay:home:42",
            committedAtMilliseconds: 1250,
            out var providerWait,
            out var browserWait));
        Assert.Equal(250, providerWait);
        Assert.Equal(75, browserWait);
        Assert.Null(gate.PendingPresentationId);
    }

    [Fact]
    public void AnUncommittedPresentationExpiresFailClosedAtItsExactDeadline()
    {
        var gate = new ProviderPresentationCommitGate(timeoutMilliseconds: 5000);
        gate.Begin(
            "allin1.gbay:home:42",
            preparedAtMilliseconds: 1000,
            browserPreparationWaitMilliseconds: 75);

        Assert.False(gate.TryExpire(
            currentMilliseconds: 5999,
            out _,
            out _));
        Assert.False(gate.TryCommit(
            "allin1.gbay:home:42",
            committedAtMilliseconds: 6000,
            out _,
            out _));
        Assert.True(gate.TryExpire(
            currentMilliseconds: 6000,
            out var expiredPresentation,
            out var browserWait));
        Assert.Equal("allin1.gbay:home:42", expiredPresentation);
        Assert.Equal(75, browserWait);
        Assert.Null(gate.PendingPresentationId);
    }

    [Fact]
    public void BeginningAReplacementInvalidatesTheOlderPresentation()
    {
        var gate = new ProviderPresentationCommitGate(timeoutMilliseconds: 5000);
        gate.Begin("old", 1000, 20);
        gate.Begin("replacement", 1100, 30);

        Assert.False(gate.TryCommit("old", 1200, out _, out _));
        Assert.True(gate.TryCommit(
            "replacement",
            1300,
            out var providerWait,
            out var browserWait));
        Assert.Equal(200, providerWait);
        Assert.Equal(30, browserWait);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidPresentationIdentityIsRejected(string presentationId)
    {
        var gate = new ProviderPresentationCommitGate();
        Assert.Throws<ArgumentException>(() => gate.Begin(
            presentationId,
            preparedAtMilliseconds: 0,
            browserPreparationWaitMilliseconds: 0));
    }
}
