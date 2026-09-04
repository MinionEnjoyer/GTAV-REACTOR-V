using System;
using RageWebUI.Harness;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class GbayPresentationTimingPolicyTests
    {
        [Fact]
        public void ElapsedBetweenUsesOuterProductionRequestBoundary()
        {
            Assert.Equal(
                313.565d,
                GbayPresentationTimingPolicy.ElapsedBetween(
                    8977.788d,
                    9291.353d),
                precision: 3);
        }

        [Fact]
        public void CorrelatedPresentationsPreserveExactFiveHundredMillisecondBudget()
        {
            // These are complete presentation lifecycles from the production
            // trace. Adding unrelated phase maxima from different cycles
            // (206.3 + 352.086) would incorrectly fail this healthy run.
            var correlatedLatencies = new[] { 328.831d, 367.063d, 500d };

            Assert.True(206.3d + 352.086d > 500d);
            Assert.Equal(
                500d,
                GbayPresentationTimingPolicy.Maximum(correlatedLatencies));
            Assert.True(
                GbayPresentationTimingPolicy.MeetsBudget(correlatedLatencies, 500d));
            Assert.False(
                GbayPresentationTimingPolicy.MeetsBudget(
                    new[] { 328.831d, 500.001d },
                    500d));
        }

        [Fact]
        public void MissingOrInvalidSamplesFailClosed()
        {
            Assert.False(
                GbayPresentationTimingPolicy.MeetsBudget(Array.Empty<double>(), 500d));
            Assert.False(
                GbayPresentationTimingPolicy.MeetsBudget(
                    new[] { 250d, double.PositiveInfinity },
                    500d));
            Assert.False(
                GbayPresentationTimingPolicy.MeetsBudget(new[] { 250d }, double.NaN));
        }

        [Fact]
        public void InitializerCannotReappearAfterGbayPhaseBegins()
        {
            Assert.True(
                GbayPresentationTimingPolicy.IsInitializerFramePermitted(
                    allowStartupTransition: true,
                    gbayPhaseEntered: false,
                    isStartupTransition: true));
            Assert.False(
                GbayPresentationTimingPolicy.IsInitializerFramePermitted(
                    allowStartupTransition: true,
                    gbayPhaseEntered: true,
                    isStartupTransition: true));
            Assert.False(
                GbayPresentationTimingPolicy.IsInitializerFramePermitted(
                    allowStartupTransition: false,
                    gbayPhaseEntered: false,
                    isStartupTransition: true));
        }

        [Fact]
        public void HandoffRequiresFourHundredMillisecondsOfStablePresentation()
        {
            Assert.Equal(
                400d,
                GbayPresentationTimingPolicy.StableHandoffSettleMilliseconds);
            Assert.False(
                GbayPresentationTimingPolicy.HasStableHandoffSettled(100d, 499.999d));
            Assert.True(
                GbayPresentationTimingPolicy.HasStableHandoffSettled(100d, 500d));
            Assert.False(
                GbayPresentationTimingPolicy.HasStableHandoffSettled(500d, 499d));
            Assert.False(
                GbayPresentationTimingPolicy.HasStableHandoffSettled(double.NaN, 500d));
        }

        [Theory]
        [InlineData(double.NaN, 100d)]
        [InlineData(double.PositiveInfinity, 100d)]
        [InlineData(-1d, 100d)]
        [InlineData(200d, 199d)]
        public void InvalidTraceBoundariesFailClosed(double requested, double committed)
        {
            Assert.Equal(
                double.PositiveInfinity,
                GbayPresentationTimingPolicy.ElapsedBetween(requested, committed));
        }
    }
}
