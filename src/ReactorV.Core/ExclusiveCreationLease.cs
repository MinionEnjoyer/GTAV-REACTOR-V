using System.Threading;

namespace RageWebUI.Core
{
    /// <summary>
    /// Serializes process-global construction without making cancellation wait
    /// for the constructor.
    /// </summary>
    public sealed class ExclusiveCreationLease
    {
        private int _ownerEpoch;

        public bool TryBegin(int epoch) =>
            epoch > 0 && Interlocked.CompareExchange(ref _ownerEpoch, epoch, 0) == 0;

        public bool IsOwnedBy(int epoch) =>
            epoch > 0 && Volatile.Read(ref _ownerEpoch) == epoch;

        public void Complete(int epoch)
        {
            if (epoch > 0) Interlocked.CompareExchange(ref _ownerEpoch, 0, epoch);
        }
    }
}
