using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ReactorV.WebView2Host
{
    internal sealed class ControllerStartupDeadline : IDisposable
    {
        internal const int TimeoutMilliseconds = 30000;
        private readonly TaskCompletionSource<bool> _expired =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action<string, string?> _trace;
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly int _timeoutMilliseconds;
        private readonly Timer _timer;
        private string _stage = "begin";
        private int _finished;

        internal ControllerStartupDeadline(Action<string, string?> trace,
            int timeoutMilliseconds = TimeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            _trace = trace;
            _timeoutMilliseconds = timeoutMilliseconds;
            _timer = new Timer(OnDeadline, null, timeoutMilliseconds, Timeout.Infinite);
        }

        internal Task Expired => _expired.Task;

        internal void Stage(string stage)
        {
            Volatile.Write(ref _stage, stage);
            _trace("webview_controller_" + stage,
                $"owner_thread={_ownerThread} elapsed_ms={_clock.Elapsed.TotalMilliseconds:F3}");
        }

        internal void ThrowIfExpired()
        {
            if (Volatile.Read(ref _finished) == 2) throw new TimeoutException(
                $"WebView2 controller startup exceeded {_timeoutMilliseconds} ms at {Volatile.Read(ref _stage)}.");
        }

        internal void Complete()
        {
            if (Interlocked.CompareExchange(ref _finished, 1, 0) != 0)
                ThrowIfExpired();
            _timer.Dispose();
        }

        private void OnDeadline(object? unused)
        {
            if (Interlocked.CompareExchange(ref _finished, 2, 0) != 0) return;
            _expired.TrySetResult(true);
            // This is diagnostic-only. A synchronous COM call can block the
            // owner STA; do not touch a Form/controller or attempt to abort it.
            try
            {
                _trace("webview_controller_deadline_elapsed",
                    $"last_stage={Volatile.Read(ref _stage)} timeout_ms={_timeoutMilliseconds} " +
                    $"owner_thread={_ownerThread} observer_thread={Thread.CurrentThread.ManagedThreadId} " +
                    $"elapsed_ms={_clock.Elapsed.TotalMilliseconds:F3} action=diagnostic_only");
            }
            catch { /* Timer callbacks must not propagate logging failures. */ }
        }

        public void Dispose()
        {
            Interlocked.CompareExchange(ref _finished, 1, 0);
            _timer.Dispose();
        }
    }
}
