using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace RageWebUI.Core
{
    public sealed class BridgeBroker : IBridgeMessageSink
    {
        public const int MaximumPendingRequests = 256;

        private readonly ConcurrentQueue<QueuedRequest> _requests =
            new ConcurrentQueue<QueuedRequest>();
        private readonly ConcurrentDictionary<string, QueuedRequest> _pending =
            new ConcurrentDictionary<string, QueuedRequest>();
        private int _pendingCount;

        public int PendingCount => Volatile.Read(ref _pendingCount);

        public bool TryEnqueue(string json, out BridgeError? error)
        {
            if (!BridgeProtocol.TryParseInbound(
                    json,
                    out var request,
                    out var cancellation,
                    out error))
            {
                return false;
            }

            if (cancellation != null)
            {
                // Cancellation is deliberately idempotent at the transport
                // boundary. A late or repeated cancellation is still a valid
                // message even when the request has already left the queue.
                TryCancel(cancellation.Id);
                return true;
            }
            if (request == null)
            {
                error = new BridgeError(
                    "invalid_request",
                    "The bridge message did not contain a request.");
                return false;
            }

            if (Interlocked.Increment(ref _pendingCount) > MaximumPendingRequests)
            {
                Interlocked.Decrement(ref _pendingCount);
                error = new BridgeError(
                    "queue_full",
                    "The game bridge is busy. Try again next frame.",
                    retryable: true,
                    details: new JObject
                    {
                        ["maximumPendingRequests"] = MaximumPendingRequests,
                    });
                return false;
            }

            var queued = new QueuedRequest(request);
            if (!_pending.TryAdd(request.Id, queued))
            {
                Interlocked.Decrement(ref _pendingCount);
                error = new BridgeError(
                    "duplicate_request_id",
                    $"Request id '{request.Id}' is already pending.");
                return false;
            }

            request.MarkEnqueued(Stopwatch.GetTimestamp());
            _requests.Enqueue(queued);
            error = null;
            return true;
        }

        /// <summary>
        /// Marks an existing queued request as cancelled. Cancellation remains
        /// lock-free and the entry is physically drained by TryDequeue, which
        /// keeps the bounded queue resistant to enqueue/cancel flooding.
        /// </summary>
        public bool TryCancel(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) ||
                !_pending.TryGetValue(requestId, out var queued))
            {
                return false;
            }

            Interlocked.Exchange(ref queued.Cancelled, 1);
            return true;
        }

        public bool TryDequeue(out BridgeRequest? request)
        {
            while (_requests.TryDequeue(out var queued))
            {
                _pending.TryRemove(queued.Request.Id, out _);
                Interlocked.Decrement(ref _pendingCount);
                if (Volatile.Read(ref queued.Cancelled) == 0)
                {
                    request = queued.Request;
                    return true;
                }
            }

            request = null;
            return false;
        }

        private sealed class QueuedRequest
        {
            internal QueuedRequest(BridgeRequest request)
            {
                Request = request;
            }

            internal BridgeRequest Request { get; }

            internal int Cancelled;
        }
    }
}
