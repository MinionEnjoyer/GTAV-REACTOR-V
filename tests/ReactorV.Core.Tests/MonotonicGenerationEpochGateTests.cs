using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class MonotonicGenerationEpochGateTests
{
    [Fact]
    public void Drain_timeout_revokes_authority_without_allowing_replacement_or_double_release()
    {
        var gate = new MonotonicGenerationEpochGate();
        var first = gate.BeginReplacement();
        gate.Activate(first);
        Assert.True(gate.TryAcquireSubmission(first, out var held));
        Assert.False(gate.RetireAndDrain(0));
        Assert.False(gate.TryAcquireSubmission(first, out _));
        Assert.Throws<System.InvalidOperationException>(() => gate.BeginReplacement());
        held!.Dispose(); held.Dispose();
        Assert.True(gate.RetireAndDrain(0));
        var next = gate.BeginReplacement();
        gate.Activate(next);
        Assert.True(gate.TryAcquireSubmission(next, out var fresh));
        fresh!.Dispose();
    }
    [Fact]
    public void Replaced_epoch_cannot_submit_after_successor_activates()
    {
        var gate = new MonotonicGenerationEpochGate();
        var first = gate.BeginReplacement();
        gate.Activate(first);
        Assert.True(gate.IsActive(first));

        var second = gate.BeginReplacement();
        Assert.False(gate.IsActive(first));
        Assert.False(gate.IsActive(second));
        gate.Activate(second);

        Assert.False(gate.IsActive(first));
        Assert.True(gate.IsActive(second));
        gate.Retire();
        Assert.False(gate.IsActive(second));
    }

    [Fact]
    public async Task Parallel_allocations_are_unique_and_strictly_monotonic_as_a_set()
    {
        var gate = new MonotonicGenerationEpochGate();
        var allocated = new ConcurrentBag<ulong>();
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                Assert.True(gate.TryAllocate(out var generation));
                allocated.Add(generation);
            }
        }));

        await Task.WhenAll(workers);
        var ordered = allocated.OrderBy(value => value).ToArray();
        Assert.Equal(4_000, ordered.Length);
        Assert.Equal(Enumerable.Range(1, 4_000).Select(value => (ulong)value), ordered);
    }

    [Fact]
    public void Observed_high_watermark_advances_the_next_allocation()
    {
        var gate = new MonotonicGenerationEpochGate();
        Assert.True(gate.TryObserve(41));
        Assert.True(gate.TryAllocate(out var next));
        Assert.Equal(42ul, next);
        Assert.False(gate.TryObserve(ulong.MaxValue));
    }

    [Fact]
    public async Task Retirement_waits_for_held_old_submission_before_a_replacement_can_activate()
    {
        var gate = new MonotonicGenerationEpochGate();
        var first = gate.BeginReplacement();
        gate.Activate(first);
        Assert.True(gate.TryAcquireSubmission(first, out var held));
        Assert.NotNull(held);

        // The gate itself rejects replacement activation while the old native
        // submission is held; callers cannot bypass drain safety.
        gate.Retire();
        Assert.Throws<InvalidOperationException>(() => gate.BeginReplacement());

        var retiring = Task.Run(() => gate.RetireAndDrain(1_000));
        await Task.Delay(30);
        Assert.False(retiring.IsCompleted);
        Assert.False(gate.IsActive(first));

        held!.Dispose();
        Assert.True(await retiring);
        var second = gate.BeginReplacement();
        gate.Activate(second);
        Assert.False(gate.TryAcquireSubmission(first, out _));
        Assert.True(gate.TryAcquireSubmission(second, out var current));
        current!.Dispose();
    }
}
