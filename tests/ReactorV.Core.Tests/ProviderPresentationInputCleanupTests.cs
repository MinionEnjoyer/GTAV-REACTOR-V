using RageWebUI.Core;
using RageWebUI.Script;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class ProviderPresentationInputCleanupTests
{
    [Fact]
    public void Test4FailureAfterRegistryRemovalReleasesBoundIntentAndAllowsFreshOpen()
    {
        var host = new ProviderInputIntentGate(5240);
        Assert.True(host.TryArm(new ProviderInputIntentToken(5240, 1, 1500), 23759));
        Assert.True(host.TryBind(5240, 1, "first-gbay", 23764));
        long boundEpoch = 1;
        string? bound = "first-gbay", fallback = null;

        // The exact failure ID is still usable after the registry record is
        // gone. Execute the same cleanup implementation linked by Script.
        Assert.True(ProviderPresentationInputCleanup.TryRevoke("first-gbay",
            ref boundEpoch, ref bound, ref fallback, out var revoked));
        host.Cancel(5240, revoked);
        Assert.Equal(1, revoked);
        Assert.Equal(0, boundEpoch);
        Assert.Null(bound);
        Assert.False(host.TryConsume("first-gbay", 24952, out _));
        Assert.Equal(ManagedF9EdgeDisposition.ArmDefaultOwnerInputIntent,
            MenuPresentationPolicy.ResolveManagedF9Edge(true, true, boundEpoch > 0, false));
        Assert.Equal(ProviderIntentEscapeDisposition.ContinueNormalRouting,
            MenuPresentationPolicy.ResolveProviderIntentEscape(false, boundEpoch > 0, fallback != null, false));

        Assert.True(host.TryArm(new ProviderInputIntentToken(5240, 2, 1500), 28770));
        Assert.True(host.TryBind(5240, 2, "second-gbay", 28771));
        Assert.True(host.TryConsume("second-gbay", 28772, out var freshEpoch));
        Assert.Equal(2, freshEpoch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("FIRST")]
    [InlineData("other")]
    public void InvalidOrDifferentIdCannotTouchBoundOrFallback(string? terminalId)
    {
        long epoch = 4;
        string? bound = "first", fallback = "first";
        Assert.False(ProviderPresentationInputCleanup.TryRevoke(terminalId,
            ref epoch, ref bound, ref fallback, out var revoked));
        Assert.Equal(0, revoked);
        Assert.Equal(4, epoch);
        Assert.Equal("first", bound);
        Assert.Equal("first", fallback);
    }

    [Fact]
    public void StaleFailureCannotRevokeAlreadyBoundReplacement()
    {
        var host = new ProviderInputIntentGate(7);
        Assert.True(host.TryArm(new ProviderInputIntentToken(7, 2, 1500), 100));
        Assert.True(host.TryBind(7, 2, "replacement", 101));
        long epoch = 2;
        string? bound = "replacement", fallback = null;
        Assert.False(ProviderPresentationInputCleanup.TryRevoke("old",
            ref epoch, ref bound, ref fallback, out var revoked));
        host.Cancel(7, revoked);
        Assert.Equal(2, epoch);
        Assert.True(host.TryConsume("replacement", 102, out _));
    }

    [Fact]
    public void OldBoundCancellationCannotConsumeNewUnboundEpochAcrossPipe()
    {
        var host = new ProviderInputIntentGate(7);
        Assert.True(host.TryArm(new ProviderInputIntentToken(7, 1, 1500), 100));
        Assert.True(host.TryBind(7, 1, "old", 101));
        Assert.True(host.TryArm(new ProviderInputIntentToken(7, 2, 1500), 102));
        long epoch = 1;
        string? bound = "old", fallback = null;
        Assert.True(ProviderPresentationInputCleanup.TryRevoke("old",
            ref epoch, ref bound, ref fallback, out var revoked));
        host.Cancel(7, revoked);
        Assert.True(host.TryBind(7, 2, "new", 103));
        Assert.True(host.TryConsume("new", 104, out _));
    }

    [Theory]
    [InlineData("old", "new", true, 3, "new", null, 0)]
    [InlineData("new", "old", true, 0, null, "new", 3)]
    [InlineData("old", "old", true, 0, null, null, 3)]
    [InlineData("new", "new", false, 3, "new", "new", 0)]
    public void BoundAndCommittedFallbackIdentitiesAreIndependent(string fallbackId, string boundId,
        bool changed, long expectedEpoch, string? expectedBound, string? expectedFallback, long cancelledEpoch)
    {
        long epoch = 3;
        string? bound = boundId, fallback = fallbackId;
        Assert.Equal(changed, ProviderPresentationInputCleanup.TryRevoke("old",
            ref epoch, ref bound, ref fallback, out var revoked));
        Assert.Equal(expectedEpoch, epoch);
        Assert.Equal(expectedBound, bound);
        Assert.Equal(expectedFallback, fallback);
        Assert.Equal(cancelledEpoch, revoked);
    }

    [Fact]
    public void DuplicateHideAndAbortCleanupAreIdempotent()
    {
        long epoch = 9;
        string? bound = "same", fallback = "same";
        Assert.True(ProviderPresentationInputCleanup.TryRevoke("same",
            ref epoch, ref bound, ref fallback, out var first));
        Assert.Equal(9, first);
        Assert.False(ProviderPresentationInputCleanup.TryRevoke("same",
            ref epoch, ref bound, ref fallback, out var duplicate));
        Assert.Equal(0, duplicate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MatchingInconsistentLocalStateNeverSendsInvalidEpoch(long epoch)
    {
        string? bound = "same", fallback = null;
        Assert.True(ProviderPresentationInputCleanup.TryRevoke("same",
            ref epoch, ref bound, ref fallback, out var revoked));
        Assert.Equal(0, epoch);
        Assert.Null(bound);
        Assert.Equal(0, revoked);
    }
}
