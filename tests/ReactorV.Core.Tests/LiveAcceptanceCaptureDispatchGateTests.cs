using System.Linq;
using System.Threading.Tasks;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests
{
    public sealed class LiveAcceptanceCaptureDispatchGateTests
    {
        [Fact]
        public async Task ConcurrentNotificationsReserveExactlyOneStaDispatch()
        {
            var gate = new LiveAcceptanceCaptureDispatchGate();
            var attempts = Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => gate.TryReserve()))
                .ToArray();

            var results = await Task.WhenAll(attempts);

            Assert.Single(results, result => result);
            Assert.True(gate.IsQueued);
        }

        [Fact]
        public void CompletionAllowsTheNextRequestToWakeTheSta()
        {
            var gate = new LiveAcceptanceCaptureDispatchGate();
            Assert.True(gate.TryReserve());
            Assert.False(gate.TryReserve());

            gate.Complete();

            Assert.False(gate.IsQueued);
            Assert.True(gate.TryReserve());
        }

        [Fact]
        public void StopRejectsLateWatcherCallbacks()
        {
            var gate = new LiveAcceptanceCaptureDispatchGate();
            Assert.True(gate.TryReserve());

            gate.Stop();

            Assert.True(gate.IsStopped);
            Assert.False(gate.IsQueued);
            Assert.False(gate.TryReserve());
            gate.Complete();
            Assert.False(gate.TryReserve());
        }
    }
}
