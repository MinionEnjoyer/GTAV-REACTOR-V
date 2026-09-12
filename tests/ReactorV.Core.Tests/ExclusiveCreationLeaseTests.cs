using RageWebUI.Core;
using Xunit;

namespace ReactorV.Core.Tests
{
    public sealed class ExclusiveCreationLeaseTests
    {
        [Fact]
        public void Invalidated_old_construction_blocks_restart_until_cleanup_completes()
        {
            var lease = new ExclusiveCreationLease();
            Assert.True(lease.TryBegin(1));

            // Stop may return now; epoch 2 must not create a second
            // process-global CEF instance until epoch 1 disposes its result.
            Assert.False(lease.TryBegin(2));
            lease.Complete(1);
            Assert.True(lease.TryBegin(2));
        }

        [Fact]
        public void Non_owner_cannot_release_an_inflight_construction()
        {
            var lease = new ExclusiveCreationLease();
            Assert.True(lease.TryBegin(4));
            lease.Complete(5);

            Assert.True(lease.IsOwnedBy(4));
            Assert.False(lease.TryBegin(5));
            lease.Complete(4);
            Assert.True(lease.TryBegin(5));
        }
    }
}
