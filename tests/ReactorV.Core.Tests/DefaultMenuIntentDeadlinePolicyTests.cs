using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class DefaultMenuIntentDeadlinePolicyTests
{
    [Fact]
    public void VisibleInitializerRefreshesAcrossSlowStartupInsteadOfExpiring()
    {
        const int leaseSeconds = 120;
        var deadlineSeconds = leaseSeconds;
        var active = true;
        for (var elapsedSeconds = 0; elapsedSeconds <= 240; elapsedSeconds++)
        {
            if (elapsedSeconds < deadlineSeconds)
                continue;
            var action = DefaultMenuIntentDeadlinePolicy.Evaluate(
                deadlineArmed: active,
                claimObserved: false,
                deadlineExpired: true,
                initializerVisible: true);
            Assert.Equal(
                DefaultMenuIntentDeadlineAction.RefreshVisibleInitializer,
                action);
            deadlineSeconds = elapsedSeconds + leaseSeconds;
        }

        // The observed production gap exceeded 219 seconds. Two simulated
        // leases still leave the user's visible request active at 240s.
        Assert.True(active);
        Assert.Equal(360, deadlineSeconds);
    }

    [Fact]
    public void HiddenIntentStillExpiresWithoutLeavingAStaleRequest()
    {
        Assert.Equal(
            DefaultMenuIntentDeadlineAction.ExpireWithoutHide,
            DefaultMenuIntentDeadlinePolicy.Evaluate(
                deadlineArmed: true,
                claimObserved: false,
                deadlineExpired: true,
                initializerVisible: false));
    }

    [Fact]
    public void ClaimedPresentationDisarmsExpiryWithoutHiding()
    {
        Assert.Equal(
            DefaultMenuIntentDeadlineAction.CompleteClaim,
            DefaultMenuIntentDeadlinePolicy.Evaluate(
                deadlineArmed: true,
                claimObserved: true,
                deadlineExpired: true,
                initializerVisible: true));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    public void NonExpiredOrUnarmedIntentDoesNothing(
        bool armed,
        bool claimed,
        bool expired,
        bool visible)
    {
        Assert.Equal(
            DefaultMenuIntentDeadlineAction.None,
            DefaultMenuIntentDeadlinePolicy.Evaluate(
                armed, claimed, expired, visible));
    }
}
