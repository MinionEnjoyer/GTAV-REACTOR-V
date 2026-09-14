using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReactorV.WebView2Host
{
    // Owns one asynchronous creation result until it is handed to the caller.
    // All awaits intentionally retain the caller's context: WebView2 Close is
    // apartment-bound. Never move reclamation to Task.Run/ContinueWith(Default).
    internal sealed class BoundedResourceCreation<T> where T : class
    {
        private readonly Task<T> _creation;
        private readonly Task _deadline;
        private readonly CancellationToken _lifetime;
        private readonly Action<T> _release;
        private readonly Action<string, Exception?> _report;
        private int _waitStarted;

        internal BoundedResourceCreation(Task<T> creation, Task deadline,
            CancellationToken lifetime, Action<T> release, Action<string, Exception?> report)
        {
            _creation = creation ?? throw new ArgumentNullException(nameof(creation));
            _deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));
            _lifetime = lifetime;
            _release = release ?? throw new ArgumentNullException(nameof(release));
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        internal Task LateCleanup { get; private set; } = Task.CompletedTask;

        internal async Task<T> WaitAsync()
        {
            if (Interlocked.Exchange(ref _waitStarted, 1) != 0)
                throw new InvalidOperationException("A creation result can only be claimed once.");
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (_lifetime.Register(() => canceled.TrySetResult(true)))
            {
                var winner = await Task.WhenAny(_creation, _deadline, canceled.Task);
                // Cancellation/deadline take priority even when a controller
                // completes while this continuation is queued on the UI thread.
                if (_lifetime.IsCancellationRequested || _deadline.IsCompleted || winner != _creation)
                {
                    LateCleanup = ReclaimWhenAvailableAsync();
                    _lifetime.ThrowIfCancellationRequested();
                    throw new TimeoutException("WebView2 controller creation exceeded its startup deadline.");
                }
                return await _creation;
            }
        }

        private async Task ReclaimWhenAvailableAsync()
        {
            T resource;
            try { resource = await _creation; }
            catch (Exception error) { Report("late_creation_failed", error); return; }
            try { _release(resource); Report("late_controller_closed", null); }
            catch (Exception error) { Report("late_controller_close_failed", error); }
        }

        private void Report(string stage, Exception? error)
        {
            try { _report(stage, error); }
            catch { /* Diagnostic callbacks must not fault a detached cleanup task. */ }
        }
    }
}
