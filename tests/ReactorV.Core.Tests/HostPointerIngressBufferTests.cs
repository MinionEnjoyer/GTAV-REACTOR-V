using System;
using System.Collections.Generic;
using ReactorV.Preloader;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class HostPointerIngressBufferTests
    {
        [Fact]
        public void NeutralPointerFloodKeepsPresentationBehindAtMostOneHostCallback()
        {
            var ingress = new HostPointerIngressBuffer();
            var hostCallbacks = new Queue<Action>();
            HostPointerIngressBatch delivered = default;

            for (var index = 0; index < 100_000; index++)
            {
                if (ingress.Enqueue(new HostPointerInputFrame(
                        index / 100_000f,
                        0.5f,
                        false,
                        false,
                        0)))
                {
                    hostCallbacks.Enqueue(() => delivered = ingress.Drain());
                }
            }

            var presentationCompleted = false;
            hostCallbacks.Enqueue(() => presentationCompleted = true);

            Assert.Equal(2, hostCallbacks.Count);
            hostCallbacks.Dequeue()();
            hostCallbacks.Dequeue()();
            Assert.True(presentationCompleted);
            Assert.Single(delivered.Frames);
            Assert.Equal(99_999, delivered.CoalescedNeutralFrames);
            Assert.Equal(0.99999f, delivered.Frames[0].X, 5);
        }

        [Fact]
        public void PressReleaseAndWheelEdgesRemainOrderedAcrossCoalescedMoves()
        {
            var ingress = new HostPointerIngressBuffer();
            Assert.True(ingress.Enqueue(Move(0.1f)));
            Assert.False(ingress.Enqueue(Move(0.2f)));
            Assert.False(ingress.Enqueue(new HostPointerInputFrame(
                0.2f, 0.5f, pressed: true, released: false, wheel: 0)));
            Assert.False(ingress.Enqueue(Move(0.3f)));
            Assert.False(ingress.Enqueue(new HostPointerInputFrame(
                0.3f, 0.5f, pressed: false, released: false, wheel: 120)));
            Assert.False(ingress.Enqueue(new HostPointerInputFrame(
                0.3f, 0.5f, pressed: false, released: true, wheel: 0)));

            var batch = ingress.Drain();

            Assert.Equal(5, batch.Frames.Count);
            Assert.Equal(0.2f, batch.Frames[0].X);
            Assert.True(batch.Frames[1].Pressed);
            Assert.Equal(0.3f, batch.Frames[2].X);
            Assert.Equal(120, batch.Frames[3].Wheel);
            Assert.True(batch.Frames[4].Released);
            Assert.Equal(1, batch.CoalescedNeutralFrames);
        }

        [Fact]
        public void DrainRearmsExactlyOneFutureDispatch()
        {
            var ingress = new HostPointerIngressBuffer();
            Assert.False(ingress.HasPending);
            Assert.True(ingress.Enqueue(Move(0.1f)));
            Assert.True(ingress.HasPending);
            _ = ingress.Drain();
            Assert.False(ingress.HasPending);
            Assert.True(ingress.Enqueue(Move(0.2f)));
            Assert.False(ingress.Enqueue(Move(0.3f)));
        }

        [Fact]
        public void PointerCoalescingTraceIsImmediateThenRateLimitedAndAggregated()
        {
            var gate = new HostPointerCoalescingTraceGate(TimeSpan.FromSeconds(5));

            var first = gate.Observe(4, TimeSpan.FromSeconds(1));
            var suppressed = gate.Observe(6, TimeSpan.FromSeconds(4));
            var boundary = gate.Observe(3, TimeSpan.FromSeconds(6));

            Assert.True(first.ShouldTrace);
            Assert.Equal(4, first.IntervalCoalescedFrames);
            Assert.Equal(4, first.TotalCoalescedFrames);
            Assert.False(suppressed.ShouldTrace);
            Assert.Equal(10, suppressed.TotalCoalescedFrames);
            Assert.True(boundary.ShouldTrace);
            Assert.Equal(9, boundary.IntervalCoalescedFrames);
            Assert.Equal(13, boundary.TotalCoalescedFrames);
        }

        [Fact]
        public void PointerCoalescingTraceIgnoresEmptyBatchesAndRejectsInvalidIntervals()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new HostPointerCoalescingTraceGate(TimeSpan.Zero));
            var gate = new HostPointerCoalescingTraceGate(TimeSpan.FromSeconds(5));

            var empty = gate.Observe(0, TimeSpan.Zero);
            var negative = gate.Observe(-1, TimeSpan.Zero);

            Assert.False(empty.ShouldTrace);
            Assert.False(negative.ShouldTrace);
            Assert.Equal(0, empty.TotalCoalescedFrames);
            Assert.Equal(0, negative.TotalCoalescedFrames);
        }

        private static HostPointerInputFrame Move(float x) =>
            new HostPointerInputFrame(
                x,
                0.5f,
                pressed: false,
                released: false,
                wheel: 0);
    }
}
