using System;
using System.Diagnostics;
using System.Threading;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class LiveAcceptanceVisualCapturePolicyTests
{
    [Theory]
    [InlineData(LiveAcceptanceLifecycleEvidenceSource.Dom)]
    [InlineData(LiveAcceptanceLifecycleEvidenceSource.UiAutomation)]
    [InlineData(LiveAcceptanceLifecycleEvidenceSource.BrowserCapture)]
    [InlineData(LiveAcceptanceLifecycleEvidenceSource.RuntimeTrace)]
    public void LogicalAndBrowserEvidenceCannotProveDesktopVisibility(
        LiveAcceptanceLifecycleEvidenceSource source)
    {
        Assert.False(LiveAcceptanceVisualCapturePolicy.CanProveDesktopVisibility(source));
        Assert.True(LiveAcceptanceVisualCapturePolicy.CanProveDesktopVisibility(
            LiveAcceptanceLifecycleEvidenceSource.DesktopPixels));
    }

    private static readonly LiveAcceptanceVisualFrameMetrics About = new(
        contentFraction: 0.12,
        blackFraction: 0.88,
        greenFraction: 0.003,
        blueFraction: 0.018,
        whiteFraction: 0.02,
        darkGreenFraction: 0.012);

    private static readonly LiveAcceptanceVisualFrameMetrics Preloader = new(
        contentFraction: 0.18,
        blackFraction: 0.82,
        greenFraction: 0.025,
        blueFraction: 0.001,
        whiteFraction: 0.08,
        darkGreenFraction: 0.04);

    private static readonly LiveAcceptanceVisualFrameMetrics Gbay = new(
        contentFraction: 0.76,
        blackFraction: 0.24,
        greenFraction: 0.11,
        blueFraction: 0.001,
        whiteFraction: 0.42,
        darkGreenFraction: 0.14);

    [Fact]
    public void Capture_deadline_is_fixed_at_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2),
            LiveAcceptanceVisualCapturePolicy.CaptureTimeout);
    }

    [Fact]
    public void Blocking_capture_cannot_hold_the_acceptance_thread_past_deadline()
    {
        using var release = new ManualResetEventSlim();
        var timer = Stopwatch.StartNew();
        try
        {
            Assert.Throws<TimeoutException>(() =>
                LiveAcceptanceCaptureDeadline.Execute(
                    () =>
                    {
                        release.Wait();
                        return 1;
                    },
                    TimeSpan.FromMilliseconds(75)));
        }
        finally
        {
            release.Set();
        }

        Assert.InRange(timer.ElapsedMilliseconds, 40, 750);
    }

    [Fact]
    public void Late_capture_result_is_disposed_after_timeout()
    {
        using var release = new ManualResetEventSlim();
        var disposed = false;
        Assert.Throws<TimeoutException>(() =>
            LiveAcceptanceCaptureDeadline.Execute(
                () =>
                {
                    release.Wait();
                    return 42;
                },
                TimeSpan.FromMilliseconds(50),
                _ => disposed = true));

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => disposed, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Route_classifiers_reject_late_or_wrong_surfaces()
    {
        Assert.True(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.ReactorAbout,
            About));
        Assert.False(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.ReactorAbout,
            Preloader));

        Assert.True(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.Allin1Preloader,
            Preloader));
        Assert.False(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.Allin1Preloader,
            Gbay));

        Assert.True(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.GbayMenu,
            Gbay));
        Assert.False(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.GbayMenu,
            Preloader));
    }

    [Fact]
    public void Black_frame_cannot_prove_any_named_route()
    {
        var black = new LiveAcceptanceVisualFrameMetrics(
            contentFraction: 0,
            blackFraction: 1,
            greenFraction: 0,
            blueFraction: 0,
            whiteFraction: 0,
            darkGreenFraction: 0);

        Assert.False(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.ReactorAbout,
            black));
        Assert.False(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.Allin1Preloader,
            black));
        Assert.False(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.GbayMenu,
            black));
        Assert.True(LiveAcceptanceVisualCapturePolicy.IsQualified(
            LiveAcceptanceVisualExpectation.EvidenceOnly,
            black));
    }

    [Fact]
    public void Named_route_requires_two_stable_consecutive_frames()
    {
        var tracker = new LiveAcceptanceVisualStabilityTracker(
            LiveAcceptanceVisualExpectation.GbayMenu);

        Assert.False(tracker.Observe(Gbay, 0));
        Assert.Equal(1, tracker.ConsecutiveQualifiedFrames);
        Assert.True(tracker.Observe(Gbay, 0.01));
        Assert.Equal(2, tracker.ConsecutiveQualifiedFrames);
    }

    [Fact]
    public void Wrong_or_unstable_frame_resets_consecutive_route_proof()
    {
        var tracker = new LiveAcceptanceVisualStabilityTracker(
            LiveAcceptanceVisualExpectation.GbayMenu);

        Assert.False(tracker.Observe(Gbay, 0));
        Assert.False(tracker.Observe(Preloader, 0.01));
        Assert.Equal(0, tracker.ConsecutiveQualifiedFrames);
        Assert.False(tracker.Observe(Gbay, 0.01));
        Assert.False(tracker.Observe(Gbay, 0.40));
        Assert.Equal(1, tracker.ConsecutiveQualifiedFrames);
        Assert.True(tracker.Observe(Gbay, 0.01));
    }

    [Fact]
    public void Evidence_only_capture_needs_one_frame_and_no_overlay()
    {
        Assert.Equal(1,
            LiveAcceptanceVisualCapturePolicy.RequiredConsecutiveFrames(
                LiveAcceptanceVisualExpectation.EvidenceOnly));
        Assert.False(LiveAcceptanceVisualCapturePolicy.RequiresRouteClassification(
            LiveAcceptanceVisualExpectation.EvidenceOnly));
        Assert.True(LiveAcceptanceVisualCapturePolicy.RequiresRouteClassification(
            LiveAcceptanceVisualExpectation.GbayMenu));
    }

    [Fact]
    public void Invalid_metric_fractions_fail_closed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveAcceptanceVisualFrameMetrics(
                contentFraction: 1.1,
                blackFraction: 0,
                greenFraction: 0,
                blueFraction: 0,
                whiteFraction: 0,
                darkGreenFraction: 0));
        Assert.False(LiveAcceptanceVisualCapturePolicy.IsStableTransition(
            LiveAcceptanceVisualExpectation.GbayMenu,
            double.NaN));
    }
}
