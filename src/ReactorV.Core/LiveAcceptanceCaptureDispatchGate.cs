using System.Threading;

namespace RageWebUI.Core
{
    /// <summary>
    /// Coalesces out-of-thread acceptance request notifications into one
    /// pending host-STA dispatch.  File-system notifications can be repeated
    /// for a single atomic request move; allowing them to enqueue freely can
    /// place capture work behind an unbounded callback backlog.
    /// </summary>
    public sealed class LiveAcceptanceCaptureDispatchGate
    {
        private int _queued;
        private int _stopped;

        public bool IsQueued => Volatile.Read(ref _queued) != 0;

        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        public bool TryReserve()
        {
            if (Volatile.Read(ref _stopped) != 0)
                return false;
            if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0)
                return false;
            if (Volatile.Read(ref _stopped) == 0)
                return true;

            Volatile.Write(ref _queued, 0);
            return false;
        }

        public void Complete() => Volatile.Write(ref _queued, 0);

        public void Stop()
        {
            Interlocked.Exchange(ref _stopped, 1);
            Volatile.Write(ref _queued, 0);
        }
    }
}
