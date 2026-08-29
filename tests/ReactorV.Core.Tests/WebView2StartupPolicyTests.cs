using System;
using RageWebUI.Core;
using Xunit;

namespace ReactorV.Core.Tests;

public sealed class WebView2StartupPolicyTests
{
    [Fact]
    public void RetriesOnlyTheSharedEnvironmentInvalidStateFailure()
    {
        Assert.True(WebView2StartupPolicy.CanRetry(
            WebView2StartupPolicy.ErrorInvalidState,
            1));
        Assert.False(WebView2StartupPolicy.CanRetry(
            unchecked((int)0x80004005),
            1));
        Assert.False(WebView2StartupPolicy.CanRetry(
            WebView2StartupPolicy.ErrorInvalidState,
            0));
        Assert.False(WebView2StartupPolicy.CanRetry(
            WebView2StartupPolicy.ErrorInvalidState,
            WebView2StartupPolicy.MaximumAttempts));
    }

    [Fact]
    public void RetryScheduleIsPositiveBoundedAndIncreasing()
    {
        var total = 0;
        var previous = 0;
        for (var failure = 1;
            failure < WebView2StartupPolicy.MaximumAttempts;
            failure++)
        {
            var delay = WebView2StartupPolicy.RetryDelayMilliseconds(failure);
            Assert.True(delay > previous);
            previous = delay;
            total += delay;
        }

        Assert.InRange(total, 1, 5000);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebView2StartupPolicy.RetryDelayMilliseconds(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebView2StartupPolicy.RetryDelayMilliseconds(
                WebView2StartupPolicy.MaximumAttempts));
    }
}
