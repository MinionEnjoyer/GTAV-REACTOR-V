using System.Linq;
using System.Threading.Tasks;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BridgeBrokerTests
{
    [Fact]
    public void PreservesRequestOrderAndPendingCount()
    {
        var broker = new BridgeBroker();
        Assert.True(broker.TryEnqueue(Request("one"), out _));
        Assert.True(broker.TryEnqueue(Request("two"), out _));
        Assert.Equal(2, broker.PendingCount);

        Assert.True(broker.TryDequeue(out var first));
        Assert.True(broker.TryDequeue(out var second));
        Assert.Equal("one", first!.Id);
        Assert.Equal("two", second!.Id);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void AppliesBackpressure()
    {
        var broker = new BridgeBroker();
        for (var index = 0; index < BridgeBroker.MaximumPendingRequests; index++)
        {
            Assert.True(broker.TryEnqueue(Request(index.ToString()), out _));
        }

        Assert.False(broker.TryEnqueue(Request("overflow"), out var error));
        Assert.Equal("queue_full", error!.Code);
        Assert.True(error.Retryable);
        Assert.Equal(
            BridgeBroker.MaximumPendingRequests,
            error.Details!.Value<int>("maximumPendingRequests"));
        Assert.Equal(BridgeBroker.MaximumPendingRequests, broker.PendingCount);
    }

    [Fact]
    public void RejectsDuplicatePendingRequestIds()
    {
        var broker = new BridgeBroker();
        Assert.True(broker.TryEnqueue(Request("same"), out _));

        Assert.False(broker.TryEnqueue(Request("same"), out var error));

        Assert.Equal("duplicate_request_id", error!.Code);
        Assert.Equal(1, broker.PendingCount);
    }

    [Fact]
    public void CancellationEnvelopePreventsQueuedRequestExecution()
    {
        var broker = new BridgeBroker();
        Assert.True(broker.TryEnqueue(VersionTwoRequest("one"), out _));
        Assert.True(broker.TryEnqueue(VersionTwoRequest("two"), out _));

        Assert.True(broker.TryEnqueue(Cancel("one"), out var error));
        Assert.Null(error);
        // Cancelled entries remain physically bounded until the game thread
        // drains them, preventing enqueue/cancel flooding from growing memory.
        Assert.Equal(2, broker.PendingCount);

        Assert.True(broker.TryDequeue(out var request));
        Assert.Equal("two", request!.Id);
        Assert.False(broker.TryDequeue(out _));
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void DirectCancellationIsIdempotentWhileRequestIsQueued()
    {
        var broker = new BridgeBroker();
        Assert.True(broker.TryEnqueue(VersionTwoRequest("one"), out _));

        Assert.True(broker.TryCancel("one"));
        Assert.True(broker.TryCancel("one"));
        Assert.False(broker.TryDequeue(out _));
        Assert.False(broker.TryCancel("one"));
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void LateOrUnknownCancellationEnvelopeIsTransportSuccess()
    {
        var broker = new BridgeBroker();

        Assert.True(broker.TryEnqueue(Cancel("missing"), out var error));
        Assert.Null(error);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void CancelledIdCanBeReusedAfterQueueDrain()
    {
        var broker = new BridgeBroker();
        Assert.True(broker.TryEnqueue(VersionTwoRequest("one"), out _));
        Assert.True(broker.TryCancel("one"));
        Assert.False(broker.TryEnqueue(VersionTwoRequest("one"), out var duplicate));
        Assert.Equal("duplicate_request_id", duplicate!.Code);

        Assert.False(broker.TryDequeue(out _));
        Assert.True(broker.TryEnqueue(VersionTwoRequest("one"), out _));
        Assert.True(broker.TryDequeue(out var replacement));
        Assert.Equal("one", replacement!.Id);
    }

    [Fact]
    public async Task ConcurrentUniqueRequestsRemainBoundedAndDrainExactlyOnce()
    {
        var broker = new BridgeBroker();
        var attempts = Enumerable.Range(0, BridgeBroker.MaximumPendingRequests)
            .Select(index => Task.Run(() => broker.TryEnqueue(Request($"r{index}"), out _)))
            .ToArray();

        var accepted = await Task.WhenAll(attempts);

        Assert.All(accepted, Assert.True);
        Assert.Equal(BridgeBroker.MaximumPendingRequests, broker.PendingCount);
        var ids = new System.Collections.Generic.HashSet<string>();
        while (broker.TryDequeue(out var request))
        {
            Assert.True(ids.Add(request!.Id));
        }
        Assert.Equal(BridgeBroker.MaximumPendingRequests, ids.Count);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void BrokerTurnsRelativeDeadlineIntoEnforceableMonotonicDeadline()
    {
        var broker = new BridgeBroker();
        const string json =
            "{\"kind\":\"request\",\"id\":\"timed\",\"method\":\"menu.invoke\"," +
            "\"params\":{},\"protocolVersion\":2,\"deadlineMs\":5000}";

        Assert.True(broker.TryEnqueue(json, out _));
        Assert.True(broker.TryDequeue(out var request));

        Assert.NotNull(request!.EnqueuedAtTimestamp);
        Assert.NotNull(request.DeadlineTimestamp);
        Assert.True(request.DeadlineTimestamp > request.EnqueuedAtTimestamp);
        Assert.False(request.IsExpiredAt(request.DeadlineTimestamp!.Value - 1));
        Assert.True(request.IsExpiredAt(request.DeadlineTimestamp.Value));
    }

    [Fact]
    public void CancelFloodCannotBypassPhysicalQueueBound()
    {
        var broker = new BridgeBroker();
        for (var index = 0; index < BridgeBroker.MaximumPendingRequests; index++)
        {
            var id = $"r{index}";
            Assert.True(broker.TryEnqueue(VersionTwoRequest(id), out _));
            Assert.True(broker.TryCancel(id));
        }

        Assert.False(broker.TryEnqueue(VersionTwoRequest("overflow"), out var full));
        Assert.Equal("queue_full", full!.Code);
        Assert.False(broker.TryDequeue(out _));
        Assert.Equal(0, broker.PendingCount);
        Assert.True(broker.TryEnqueue(VersionTwoRequest("after-drain"), out _));
    }

    private static string Request(string id) =>
        $"{{\"kind\":\"request\",\"id\":\"{id}\",\"method\":\"game.getState\",\"params\":{{}}}}";

    private static string VersionTwoRequest(string id) =>
        $"{{\"kind\":\"request\",\"id\":\"{id}\",\"method\":\"menu.invoke\"," +
        "\"params\":{},\"protocolVersion\":2}";

    private static string Cancel(string id) =>
        $"{{\"kind\":\"cancel\",\"id\":\"{id}\",\"protocolVersion\":2}}";
}
