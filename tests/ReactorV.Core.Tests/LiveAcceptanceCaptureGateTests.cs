using System;
using System.Threading;
using System.Threading.Tasks;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class LiveAcceptanceCaptureGateTests
    {
        [Fact]
        public void SuccessfulCaptureReleasesGateForSameController()
        {
            using var gate = new LiveAcceptanceCaptureGate();
            Assert.True(gate.TryBegin(
                7,
                "request-one",
                TimeSpan.FromSeconds(2),
                _ => throw new InvalidOperationException("watchdog must not fire"),
                out var first,
                out var rejection), rejection);
            Assert.NotNull(first);
            Assert.True(first!.TryComplete());
            Assert.Equal(LiveAcceptanceCaptureGateState.Idle, gate.State);

            Assert.True(gate.TryBegin(
                7,
                "request-two",
                TimeSpan.FromSeconds(2),
                _ => { },
                out var second,
                out rejection), rejection);
            Assert.True(second!.TryComplete());
        }

        [Fact]
        public void NeverCompletingCapturePoisonsOnlyItsControllerGeneration()
        {
            using var gate = new LiveAcceptanceCaptureGate();
            using var timedOut = new ManualResetEventSlim();
            var never = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(gate.TryBegin(
                3,
                "never",
                TimeSpan.FromMilliseconds(50),
                timeout => timedOut.Set(),
                out var lease,
                out var rejection), rejection);
            _ = never.Task;

            Assert.True(timedOut.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(lease!.IsActive);
            Assert.False(lease.TryComplete());
            Assert.Equal(LiveAcceptanceCaptureGateState.Poisoned, gate.State);
            Assert.False(gate.TryBegin(
                3,
                "same-controller",
                TimeSpan.FromSeconds(1),
                _ => { },
                out _,
                out rejection));
            Assert.Equal("capture_controller_poisoned", rejection);

            Assert.True(gate.TryBegin(
                4,
                "replacement-controller",
                TimeSpan.FromSeconds(1),
                _ => { },
                out var replacement,
                out rejection), rejection);
            Assert.True(replacement!.TryComplete());
        }

        [Fact]
        public async Task WatchdogExistsBeforeSynchronouslyBlockingDelegateRuns()
        {
            using var gate = new LiveAcceptanceCaptureGate();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var timedOut = new ManualResetEventSlim();

            Func<Task> synchronouslyBlockingCapture = () =>
            {
                entered.Set();
                release.Wait();
                return Task.CompletedTask;
            };

            var invocation = Task.Run(() =>
            {
                Assert.True(gate.TryBegin(
                    8,
                    "synchronous-stall",
                    TimeSpan.FromMilliseconds(50),
                    _ => timedOut.Set(),
                    out _,
                    out var rejection), rejection);
                return synchronouslyBlockingCapture();
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(timedOut.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(LiveAcceptanceCaptureGateState.Poisoned, gate.State);
            release.Set();
            await invocation;
        }

        [Fact]
        public void ActiveRequestCannotBeDuplicatedOrOverlapped()
        {
            using var gate = new LiveAcceptanceCaptureGate();
            Assert.True(gate.TryBegin(
                2,
                "active",
                TimeSpan.FromSeconds(1),
                _ => { },
                out var lease,
                out var rejection), rejection);
            Assert.True(gate.IsActiveRequest("active"));

            Assert.False(gate.TryBegin(
                2,
                "active",
                TimeSpan.FromSeconds(1),
                _ => { },
                out _,
                out rejection));
            Assert.Equal("capture_request_in_progress", rejection);
            Assert.False(gate.TryBegin(
                2,
                "other",
                TimeSpan.FromSeconds(1),
                _ => { },
                out _,
                out rejection));
            Assert.Equal("capture_controller_busy", rejection);
            Assert.True(lease!.TryComplete());
        }

        [Fact]
        public void DisposeCancelsWatchdogAndRejectsFurtherCaptureWork()
        {
            using var timedOut = new ManualResetEventSlim();
            var gate = new LiveAcceptanceCaptureGate();
            Assert.True(gate.TryBegin(
                9,
                "shutdown",
                TimeSpan.FromMilliseconds(250),
                _ => timedOut.Set(),
                out var lease,
                out var rejection), rejection);

            gate.Dispose();

            Assert.Equal(LiveAcceptanceCaptureGateState.Disposed, gate.State);
            Assert.False(lease!.IsActive);
            Assert.False(lease.TryComplete());
            Assert.False(gate.TryBegin(
                10,
                "after-shutdown",
                TimeSpan.FromSeconds(1),
                _ => { },
                out _,
                out rejection));
            Assert.Equal("capture_gate_disposed", rejection);
            Assert.False(timedOut.Wait(TimeSpan.FromMilliseconds(500)));
        }
    }
}
