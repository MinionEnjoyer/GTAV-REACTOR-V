using System;
using System.Diagnostics;
using System.Threading;

namespace RageWebUI.Core
{
    /// <summary>
    /// Owns monotonically increasing frame identities and the active browser
    /// epoch. Exhaustion is terminal: identities are never reused.
    /// </summary>
    public sealed class MonotonicGenerationEpochGate
    {
        private long _generation;
        private int _epoch;
        private int _acceptedEpoch;
        private int _activeSubmissions;
        private readonly object _submissionSync = new object();

        public long CurrentGeneration => Interlocked.Read(ref _generation);

        public int BeginReplacement()
        {
            lock (_submissionSync)
            {
                if (_activeSubmissions != 0)
                    throw new InvalidOperationException("Previous epoch submissions have not drained.");
                Volatile.Write(ref _acceptedEpoch, 0);
                return AdvanceEpoch();
            }
        }

        public void Activate(int epoch)
        {
            lock (_submissionSync)
            {
                if (_activeSubmissions != 0)
                    throw new InvalidOperationException("Previous epoch submissions have not drained.");
                if (epoch == 0 || epoch != Volatile.Read(ref _epoch))
                    throw new InvalidOperationException("Cannot activate a stale browser epoch.");
                Volatile.Write(ref _acceptedEpoch, epoch);
            }
        }

        public void Retire()
        {
            lock (_submissionSync)
            {
                Volatile.Write(ref _acceptedEpoch, 0);
                AdvanceEpoch();
            }
        }

        public bool RetireAndDrain(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            lock (_submissionSync)
            {
                Retire();
                var deadline = Stopwatch.GetTimestamp() +
                    Stopwatch.Frequency * timeoutMilliseconds / 1000L;
                while (_activeSubmissions != 0)
                {
                    var remaining = deadline - Stopwatch.GetTimestamp();
                    if (remaining <= 0) return false;
                    Monitor.Wait(_submissionSync, Math.Max(1, (int)Math.Min(
                        1000L, remaining * 1000L / Stopwatch.Frequency)));
                }
                return true;
            }
        }

        public void WaitForDrain()
        {
            lock (_submissionSync)
            {
                while (_activeSubmissions != 0) Monitor.Wait(_submissionSync);
            }
        }

        private int AdvanceEpoch()
        {
            while (true)
            {
                var previous = Volatile.Read(ref _epoch);
                if (previous == int.MaxValue)
                    throw new InvalidOperationException("Browser epoch exhausted.");
                if (Interlocked.CompareExchange(ref _epoch, previous + 1, previous) == previous)
                    return previous + 1;
            }
        }

        public bool IsActive(int epoch) => epoch != 0 &&
            epoch == Volatile.Read(ref _epoch) &&
            epoch == Volatile.Read(ref _acceptedEpoch);

        public bool TryAcquireSubmission(int epoch, out SubmissionLease? lease)
        {
            lock (_submissionSync)
            {
                if (!IsActive(epoch) || !TryAllocate(out var generation))
                {
                    lease = null;
                    return false;
                }
                ++_activeSubmissions;
                lease = new SubmissionLease(this, generation);
                return true;
            }
        }

        private void ReleaseSubmission()
        {
            lock (_submissionSync)
            {
                --_activeSubmissions;
                if (_activeSubmissions == 0) Monitor.PulseAll(_submissionSync);
            }
        }

        public sealed class SubmissionLease : IDisposable
        {
            private MonotonicGenerationEpochGate? _owner;

            internal SubmissionLease(MonotonicGenerationEpochGate owner, ulong generation)
            {
                _owner = owner;
                Generation = generation;
            }

            public ulong Generation { get; }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null) owner.ReleaseSubmission();
            }
        }

        public bool TryAllocate(out ulong generation)
        {
            while (true)
            {
                var observed = Interlocked.Read(ref _generation);
                if (observed >= long.MaxValue)
                {
                    generation = 0;
                    return false;
                }
                var next = observed + 1;
                if (Interlocked.CompareExchange(ref _generation, next, observed) == observed)
                {
                    generation = unchecked((ulong)next);
                    return true;
                }
            }
        }

        public bool TryObserve(ulong generation)
        {
            if (generation > long.MaxValue) return false;
            var candidate = unchecked((long)generation);
            while (true)
            {
                var observed = Interlocked.Read(ref _generation);
                if (candidate <= observed) return true;
                if (Interlocked.CompareExchange(ref _generation, candidate, observed) == observed)
                    return true;
            }
        }
    }
}
