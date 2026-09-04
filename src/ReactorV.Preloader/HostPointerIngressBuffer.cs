using System;
using System.Collections.Generic;

namespace ReactorV.Preloader
{
    internal readonly struct HostPointerInputFrame
    {
        public HostPointerInputFrame(
            float x,
            float y,
            bool pressed,
            bool released,
            int wheel)
        {
            X = x;
            Y = y;
            Pressed = pressed;
            Released = released;
            Wheel = wheel;
        }

        public float X { get; }
        public float Y { get; }
        public bool Pressed { get; }
        public bool Released { get; }
        public int Wheel { get; }

        public bool IsEdge => Pressed || Released || Wheel != 0;
    }

    internal readonly struct HostPointerIngressBatch
    {
        public HostPointerIngressBatch(
            IReadOnlyList<HostPointerInputFrame> frames,
            long coalescedNeutralFrames)
        {
            Frames = frames;
            CoalescedNeutralFrames = coalescedNeutralFrames;
        }

        public IReadOnlyList<HostPointerInputFrame> Frames { get; }
        public long CoalescedNeutralFrames { get; }
    }

    internal readonly struct HostPointerCoalescingTraceSnapshot
    {
        public HostPointerCoalescingTraceSnapshot(
            bool shouldTrace,
            long batchCoalescedFrames,
            long intervalCoalescedFrames,
            long totalCoalescedFrames)
        {
            ShouldTrace = shouldTrace;
            BatchCoalescedFrames = batchCoalescedFrames;
            IntervalCoalescedFrames = intervalCoalescedFrames;
            TotalCoalescedFrames = totalCoalescedFrames;
        }

        public bool ShouldTrace { get; }
        public long BatchCoalescedFrames { get; }
        public long IntervalCoalescedFrames { get; }
        public long TotalCoalescedFrames { get; }
    }

    /// <summary>
    /// Keeps neutral pointer coalescing observable without synchronously
    /// writing a log record for every browser drain. The first observation is
    /// reported immediately; later observations are aggregated into bounded
    /// interval summaries. Press, release, and wheel edges are logged through
    /// their existing paths and are not affected by this gate.
    /// </summary>
    internal sealed class HostPointerCoalescingTraceGate
    {
        private readonly TimeSpan _minimumInterval;
        private TimeSpan _nextTraceAt = TimeSpan.Zero;
        private long _totalCoalescedFrames;
        private long _reportedCoalescedFrames;

        public HostPointerCoalescingTraceGate(TimeSpan minimumInterval)
        {
            if (minimumInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(minimumInterval));
            _minimumInterval = minimumInterval;
        }

        public HostPointerCoalescingTraceSnapshot Observe(
            long batchCoalescedFrames,
            TimeSpan elapsed)
        {
            if (batchCoalescedFrames <= 0)
                return default;

            _totalCoalescedFrames += batchCoalescedFrames;
            if (_nextTraceAt != TimeSpan.Zero && elapsed < _nextTraceAt)
            {
                return new HostPointerCoalescingTraceSnapshot(
                    false,
                    batchCoalescedFrames,
                    0,
                    _totalCoalescedFrames);
            }

            var intervalCoalescedFrames =
                _totalCoalescedFrames - _reportedCoalescedFrames;
            _reportedCoalescedFrames = _totalCoalescedFrames;
            _nextTraceAt = elapsed + _minimumInterval;
            return new HostPointerCoalescingTraceSnapshot(
                true,
                batchCoalescedFrames,
                intervalCoalescedFrames,
                _totalCoalescedFrames);
        }
    }

    /// <summary>
    /// Bounds provider pointer work queued to the browser STA. Neutral movement
    /// samples are snapshots, so only the newest unsent sample is useful.
    /// Press, release, and wheel edges remain ordered and lossless.
    /// </summary>
    internal sealed class HostPointerIngressBuffer
    {
        private readonly object _sync = new object();
        private readonly Queue<HostPointerInputFrame> _orderedFrames =
            new Queue<HostPointerInputFrame>();
        private HostPointerInputFrame? _pendingNeutralFrame;
        private bool _dispatchScheduled;
        private long _coalescedNeutralFrames;

        /// <summary>
        /// Returns true only when the caller must schedule the single browser
        /// STA drain. Further samples merge into that already-scheduled drain.
        /// </summary>
        public bool Enqueue(HostPointerInputFrame frame)
        {
            lock (_sync)
            {
                if (frame.IsEdge)
                {
                    FlushPendingNeutralFrame();
                    _orderedFrames.Enqueue(frame);
                }
                else
                {
                    if (_pendingNeutralFrame.HasValue)
                        _coalescedNeutralFrames++;
                    _pendingNeutralFrame = frame;
                }

                if (_dispatchScheduled) return false;
                _dispatchScheduled = true;
                return true;
            }
        }

        public HostPointerIngressBatch Drain()
        {
            lock (_sync)
            {
                FlushPendingNeutralFrame();
                var frames = _orderedFrames.ToArray();
                _orderedFrames.Clear();
                var coalesced = _coalescedNeutralFrames;
                _coalescedNeutralFrames = 0;
                _dispatchScheduled = false;
                return new HostPointerIngressBatch(frames, coalesced);
            }
        }

        public bool HasPending
        {
            get
            {
                lock (_sync)
                {
                    return _dispatchScheduled;
                }
            }
        }

        public void Abandon()
        {
            lock (_sync)
            {
                _orderedFrames.Clear();
                _pendingNeutralFrame = null;
                _coalescedNeutralFrames = 0;
                _dispatchScheduled = false;
            }
        }

        private void FlushPendingNeutralFrame()
        {
            if (!_pendingNeutralFrame.HasValue) return;
            _orderedFrames.Enqueue(_pendingNeutralFrame.Value);
            _pendingNeutralFrame = null;
        }
    }
}
