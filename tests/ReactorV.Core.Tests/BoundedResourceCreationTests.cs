using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReactorV.WebView2Host;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class BoundedResourceCreationTests
{
    private sealed class Resource { public int Closed; }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Successful_result_is_handed_over_once_without_cleanup()
    {
        var resource = new Resource();
        var lease = Create(Task.FromResult(resource), Signal<bool>().Task);
        Assert.Same(resource, await lease.WaitAsync());
        Assert.Equal(0, resource.Closed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.WaitAsync());
    }

    [Fact]
    public async Task Original_creation_fault_is_preserved()
    {
        var error = new InvalidOperationException("creation failed");
        var lease = Create(Task.FromException<Resource>(error), Signal<bool>().Task);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => lease.WaitAsync()));
    }

    [Fact]
    public async Task Timeout_returns_without_waiting_for_controller_then_closes_late_result_once()
    {
        var source = Signal<Resource>(); var deadline = Signal<bool>();
        var lease = Create(source.Task, deadline.Task);
        var waiting = lease.WaitAsync();
        deadline.SetResult(true);
        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        Assert.False(lease.LateCleanup.IsCompleted);
        var resource = new Resource(); source.SetResult(resource);
        await lease.LateCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.Closed);
    }

    [Fact]
    public async Task Owner_disposal_cancels_pending_wait_and_reclaims_late_controller()
    {
        using var lifetime = new CancellationTokenSource();
        var source = Signal<Resource>();
        var lease = Create(source.Task, Signal<bool>().Task, lifetime.Token);
        var waiting = lease.WaitAsync(); lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var resource = new Resource(); source.SetResult(resource);
        await lease.LateCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.Closed);
    }

    [Fact]
    public async Task Already_expired_deadline_rejects_even_an_already_completed_result()
    {
        var resource = new Resource();
        var lease = Create(Task.FromResult(resource), Task.CompletedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => lease.WaitAsync());
        await lease.LateCleanup;
        Assert.Equal(1, resource.Closed);
    }

    [Fact]
    public async Task Already_disposed_owner_rejects_completed_result()
    {
        using var lifetime = new CancellationTokenSource(); lifetime.Cancel();
        var resource = new Resource();
        var lease = Create(Task.FromResult(resource), Signal<bool>().Task, lifetime.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.WaitAsync());
        await lease.LateCleanup;
        Assert.Equal(1, resource.Closed);
    }

    [Fact]
    public async Task Late_fault_is_observed_and_reported_without_faulting_cleanup()
    {
        var source = Signal<Resource>(); var events = new List<string>();
        var lease = Create(source.Task, Task.CompletedTask, report:(stage, _) => events.Add(stage));
        await Assert.ThrowsAsync<TimeoutException>(() => lease.WaitAsync());
        source.SetException(new InvalidOperationException("late failure"));
        await lease.LateCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "late_creation_failed" }, events);
    }

    [Fact]
    public async Task Cleanup_and_diagnostic_failures_do_not_escape_detached_task()
    {
        var lease = new BoundedResourceCreation<Resource>(Task.FromResult(new Resource()),
            Task.CompletedTask, CancellationToken.None,
            _ => throw new InvalidOperationException("close failed"),
            (_, _) => throw new InvalidOperationException("logger failed"));
        await Assert.ThrowsAsync<TimeoutException>(() => lease.WaitAsync());
        await lease.LateCleanup;
    }

    [Fact]
    public void Late_close_runs_on_captured_STA_not_the_completion_worker()
    {
        RunSta(async () =>
        {
            int owner = Environment.CurrentManagedThreadId, closedOn = 0;
            var source = Signal<Resource>(); var deadline = Signal<bool>();
            var lease = new BoundedResourceCreation<Resource>(source.Task, deadline.Task,
                CancellationToken.None, _ => closedOn = Environment.CurrentManagedThreadId, (_, _) => { });
            var waiting = lease.WaitAsync();
            await Task.Run(() => deadline.SetResult(true));
            await Assert.ThrowsAsync<TimeoutException>(() => waiting);
            await Task.Run(() => source.SetResult(new Resource()));
            await lease.LateCleanup;
            Assert.Equal(owner, closedOn);
            if (OperatingSystem.IsWindows())
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        });
    }

    [Fact]
    public void Cancellation_wins_if_creation_completion_is_queued_but_not_published()
    {
        RunSta(async () =>
        {
            using var lifetime = new CancellationTokenSource();
            var source = Signal<Resource>(); var resource = new Resource();
            var lease = Create(source.Task, Signal<bool>().Task, lifetime.Token);
            var waiting = lease.WaitAsync();
            source.SetResult(resource); lifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await lease.LateCleanup;
            Assert.Equal(1, resource.Closed);
        });
    }

    [Fact]
    public async Task Deadline_reports_stalled_stage_without_a_UI_message_pump()
    {
        var reported = Signal<string>();
        using var deadline = new ControllerStartupDeadline((stage, detail) =>
        {
            if (stage == "webview_controller_deadline_elapsed") reported.TrySetResult(detail!);
        }, 100);
        deadline.Stage("composition_device_begin");
        var detail = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("last_stage=composition_device_begin", detail);
        Assert.Contains("action=diagnostic_only", detail);
        Assert.Throws<TimeoutException>(() => deadline.Complete());
    }

    [Fact]
    public async Task Successful_startup_stops_deadline_without_expiring()
    {
        using var deadline = new ControllerStartupDeadline((_, _) => { }, 200);
        deadline.Complete();
        await Task.Delay(300);
        Assert.False(deadline.Expired.IsCompleted);
        deadline.ThrowIfExpired();
    }

    [Fact]
    public void Invalid_deadline_duration_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControllerStartupDeadline((_, _) => { }, 0));

    private static BoundedResourceCreation<Resource> Create(Task<Resource> creation, Task deadline,
        CancellationToken lifetime = default, Action<string, Exception?>? report = null) =>
        new(creation, deadline, lifetime, r => r.Closed++, report ?? ((_, _) => { }));

    // A real dedicated STA with a deterministic managed message pump. No game,
    // browser, profile, or native window is needed to test context affinity.
    private static void RunSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new PumpContext();
                SynchronizationContext.SetSynchronizationContext(context);
                var task = action();
                while (!task.IsCompleted) context.Pump();
                task.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test did not complete.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void Pump() { if (_queue.TryTake(out var work, 50)) work.Item1(work.Item2); }
        public void Dispose() => _queue.Dispose();
    }
}
