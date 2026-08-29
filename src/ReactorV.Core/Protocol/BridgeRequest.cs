using System;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core.Protocol
{
    public sealed class BridgeRequest
    {
        private readonly object _enqueueSync = new object();
        private long _enqueuedAtTimestamp;
        private long _deadlineTimestamp;

        public BridgeRequest(string id, string method, JObject parameters)
            : this(
                id,
                method,
                parameters,
                protocolVersion: BridgeProtocol.MinimumSupportedProtocolVersion,
                requestedProtocolVersion: BridgeProtocol.MinimumSupportedProtocolVersion,
                minimumProtocolVersion: BridgeProtocol.MinimumSupportedProtocolVersion,
                deadlineMs: null,
                idempotencyKey: null,
                confirmed: false)
        {
        }

        internal BridgeRequest(
            string id,
            string method,
            JObject parameters,
            int protocolVersion,
            int requestedProtocolVersion,
            int minimumProtocolVersion,
            int? deadlineMs,
            string? idempotencyKey,
            bool confirmed)
        {
            Id = id;
            Method = method;
            Parameters = parameters;
            ProtocolVersion = protocolVersion;
            RequestedProtocolVersion = requestedProtocolVersion;
            MinimumProtocolVersion = minimumProtocolVersion;
            DeadlineMs = deadlineMs;
            IdempotencyKey = idempotencyKey;
            Confirmed = confirmed;
        }

        public string Id { get; }

        public string Method { get; }

        public JObject Parameters { get; }

        /// <summary>
        /// Highest mutually supported protocol version selected for this
        /// request. Version 1 is used for legacy envelopes with no metadata.
        /// </summary>
        public int ProtocolVersion { get; }

        /// <summary>The highest protocol version offered by the page.</summary>
        public int RequestedProtocolVersion { get; }

        /// <summary>The lowest protocol version acceptable to the page.</summary>
        public int MinimumProtocolVersion { get; }

        /// <summary>
        /// Optional relative execution deadline supplied by a v2 page. The
        /// game-thread dispatcher remains responsible for enforcing it.
        /// </summary>
        public int? DeadlineMs { get; }

        /// <summary>
        /// Optional session-scoped idempotency key for a mutating v2 action.
        /// </summary>
        public string? IdempotencyKey { get; }

        /// <summary>
        /// True only when the page explicitly confirms an action that the
        /// method contract identifies as confirmation-gated.
        /// </summary>
        public bool Confirmed { get; }

        /// <summary>
        /// Monotonic Stopwatch timestamp recorded by BridgeBroker immediately
        /// before the request becomes visible to the game-thread queue.
        /// </summary>
        public long? EnqueuedAtTimestamp
        {
            get
            {
                var value = Volatile.Read(ref _enqueuedAtTimestamp);
                return value == 0 ? (long?)null : value;
            }
        }

        /// <summary>
        /// Absolute monotonic deadline derived from DeadlineMs at enqueue time.
        /// This avoids queue latency silently extending a page's deadline.
        /// </summary>
        public long? DeadlineTimestamp
        {
            get
            {
                var value = Volatile.Read(ref _deadlineTimestamp);
                return value == 0 ? (long?)null : value;
            }
        }

        public bool IsExpired => IsExpiredAt(Stopwatch.GetTimestamp());

        public bool IsExpiredAt(long timestamp)
        {
            var deadline = Volatile.Read(ref _deadlineTimestamp);
            return deadline != 0 && timestamp >= deadline;
        }

        internal void MarkEnqueued(long timestamp)
        {
            if (timestamp <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timestamp));
            }

            lock (_enqueueSync)
            {
                if (_enqueuedAtTimestamp != 0)
                {
                    return;
                }

                if (DeadlineMs.HasValue)
                {
                    var duration = checked((long)Math.Ceiling(
                        DeadlineMs.Value * (double)Stopwatch.Frequency / 1000d));
                    _deadlineTimestamp = checked(timestamp + duration);
                }
                Volatile.Write(ref _enqueuedAtTimestamp, timestamp);
            }
        }
    }
}
