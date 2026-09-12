using System;
using System.Threading;
using System.Threading.Tasks;
using ReactorV.BootstrapHost;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BootstrapAttachmentGateTests
{
    [Fact]
    public void Failed_attachment_can_retry_but_a_completed_attachment_is_not_duplicated()
    {
        var gate = new BootstrapAttachmentGate();
        var calls = 0;

        Assert.False(gate.TryAttach(() => { calls++; return false; }));
        Assert.True(gate.TryAttach(() => { calls++; return true; }));
        Assert.True(gate.TryAttach(() => { calls++; return true; }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_starts_share_one_attachment_attempt_and_never_duplicate_workers()
    {
        var gate = new BootstrapAttachmentGate();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;

        var first = Task.Run(() => gate.TryAttach(() =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            return true;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var second = Task.Run(() => gate.TryAttach(() =>
        {
            Interlocked.Increment(ref calls);
            return true;
        }));
        release.Set();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Dispose_during_an_attachment_prevents_completion_and_all_retries()
    {
        var gate = new BootstrapAttachmentGate();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => gate.TryAttach(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            return true;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        gate.Dispose();
        release.Set();

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gate.TryAttach(() => true));
    }

    [Fact]
    public void Lost_established_transport_is_reported_unhealthy_without_starting_a_second_pair()
    {
        var gate = new BootstrapAttachmentGate();
        var calls = 0;
        Assert.True(gate.TryAttach(() => { calls++; return true; }, () => true));

        Assert.False(gate.TryAttach(() => { calls++; return true; }, () => false));
        Assert.Equal(1, calls);
    }
}
