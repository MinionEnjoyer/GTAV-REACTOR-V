using System;
using System.Threading;

namespace RageWebUI.Core
{
    /// <summary>
    /// Process-local lease around WebView2 acceptance captures.  The watchdog
    /// is armed before any WebView COM method is invoked, so even a synchronous
    /// COM stall produces a terminal response.  A timed-out controller is
    /// poisoned until a different controller generation is observed; issuing
    /// overlapping captures against the same stalled COM object is unsafe.
    /// </summary>
    public sealed class LiveAcceptanceCaptureGate : IDisposable
    {
        private readonly object _sync = new object();
        private LiveAcceptanceCaptureGateState _state;
        private int _controllerGeneration;
        private string _activeRequestId = string.Empty;
        private Timer? _watchdog;

        public LiveAcceptanceCaptureGateState State
        {
            get
            {
                lock (_sync) return _state;
            }
        }

        public int ControllerGeneration
        {
            get
            {
                lock (_sync) return _controllerGeneration;
            }
        }

        public bool IsActiveRequest(string requestId)
        {
            lock (_sync)
            {
                return _state == LiveAcceptanceCaptureGateState.Active &&
                    string.Equals(_activeRequestId, requestId, StringComparison.Ordinal);
            }
        }

        public bool TryBegin(
            int controllerGeneration,
            string requestId,
            TimeSpan timeout,
            Action<LiveAcceptanceCaptureTimeout> timedOut,
            out LiveAcceptanceCaptureLease? lease,
            out string rejection)
        {
            if (controllerGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(controllerGeneration));
            if (string.IsNullOrWhiteSpace(requestId))
                throw new ArgumentException("Capture request ID is required.", nameof(requestId));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (timedOut == null)
                throw new ArgumentNullException(nameof(timedOut));

            lease = null;
            rejection = string.Empty;
            Timer? retiredWatchdog = null;
            lock (_sync)
            {
                if (_state == LiveAcceptanceCaptureGateState.Disposed)
                {
                    rejection = "capture_gate_disposed";
                    return false;
                }

                if (_controllerGeneration != controllerGeneration)
                {
                    retiredWatchdog = _watchdog;
                    _watchdog = null;
                    _controllerGeneration = controllerGeneration;
                    _activeRequestId = string.Empty;
                    _state = LiveAcceptanceCaptureGateState.Idle;
                }

                if (_state == LiveAcceptanceCaptureGateState.Poisoned)
                {
                    rejection = "capture_controller_poisoned";
                    return false;
                }
                if (_state == LiveAcceptanceCaptureGateState.Active)
                {
                    rejection = string.Equals(
                            _activeRequestId,
                            requestId,
                            StringComparison.Ordinal)
                        ? "capture_request_in_progress"
                        : "capture_controller_busy";
                    return false;
                }

                _state = LiveAcceptanceCaptureGateState.Active;
                _activeRequestId = requestId;
                var timeoutState = new TimeoutState(
                    this,
                    controllerGeneration,
                    requestId,
                    timedOut);
                _watchdog = new Timer(
                    static value => ((TimeoutState)value!).Owner.OnTimeout(
                        (TimeoutState)value),
                    timeoutState,
                    timeout,
                    Timeout.InfiniteTimeSpan);
                lease = new LiveAcceptanceCaptureLease(
                    this,
                    controllerGeneration,
                    requestId);
            }
            retiredWatchdog?.Dispose();
            return true;
        }

        internal bool IsActive(int controllerGeneration, string requestId)
        {
            lock (_sync)
            {
                return _state == LiveAcceptanceCaptureGateState.Active &&
                    _controllerGeneration == controllerGeneration &&
                    string.Equals(_activeRequestId, requestId, StringComparison.Ordinal);
            }
        }

        internal bool TryComplete(int controllerGeneration, string requestId)
        {
            Timer? watchdog;
            lock (_sync)
            {
                if (_state != LiveAcceptanceCaptureGateState.Active ||
                    _controllerGeneration != controllerGeneration ||
                    !string.Equals(_activeRequestId, requestId, StringComparison.Ordinal))
                {
                    return false;
                }

                watchdog = _watchdog;
                _watchdog = null;
                _activeRequestId = string.Empty;
                _state = LiveAcceptanceCaptureGateState.Idle;
            }
            watchdog?.Dispose();
            return true;
        }

        private void OnTimeout(TimeoutState timeout)
        {
            Timer? watchdog;
            lock (_sync)
            {
                if (_state != LiveAcceptanceCaptureGateState.Active ||
                    _controllerGeneration != timeout.ControllerGeneration ||
                    !string.Equals(
                        _activeRequestId,
                        timeout.RequestId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                watchdog = _watchdog;
                _watchdog = null;
                _activeRequestId = string.Empty;
                _state = LiveAcceptanceCaptureGateState.Poisoned;
            }
            watchdog?.Dispose();
            try
            {
                timeout.Callback(new LiveAcceptanceCaptureTimeout(
                    timeout.ControllerGeneration,
                    timeout.RequestId));
            }
            catch
            {
                // A watchdog must never terminate the host process. The gate
                // remains poisoned even if diagnostics cannot be written.
            }
        }

        public void Dispose()
        {
            Timer? watchdog;
            lock (_sync)
            {
                if (_state == LiveAcceptanceCaptureGateState.Disposed) return;
                watchdog = _watchdog;
                _watchdog = null;
                _activeRequestId = string.Empty;
                _state = LiveAcceptanceCaptureGateState.Disposed;
            }
            watchdog?.Dispose();
        }

        private sealed class TimeoutState
        {
            internal TimeoutState(
                LiveAcceptanceCaptureGate owner,
                int controllerGeneration,
                string requestId,
                Action<LiveAcceptanceCaptureTimeout> callback)
            {
                Owner = owner;
                ControllerGeneration = controllerGeneration;
                RequestId = requestId;
                Callback = callback;
            }

            internal LiveAcceptanceCaptureGate Owner { get; }
            internal int ControllerGeneration { get; }
            internal string RequestId { get; }
            internal Action<LiveAcceptanceCaptureTimeout> Callback { get; }
        }
    }

    public enum LiveAcceptanceCaptureGateState
    {
        Idle,
        Active,
        Poisoned,
        Disposed,
    }

    public readonly struct LiveAcceptanceCaptureTimeout
    {
        public LiveAcceptanceCaptureTimeout(
            int controllerGeneration,
            string requestId)
        {
            ControllerGeneration = controllerGeneration;
            RequestId = requestId ?? string.Empty;
        }

        public int ControllerGeneration { get; }
        public string RequestId { get; }
    }

    public sealed class LiveAcceptanceCaptureLease
    {
        private readonly LiveAcceptanceCaptureGate _owner;

        internal LiveAcceptanceCaptureLease(
            LiveAcceptanceCaptureGate owner,
            int controllerGeneration,
            string requestId)
        {
            _owner = owner;
            ControllerGeneration = controllerGeneration;
            RequestId = requestId;
        }

        public int ControllerGeneration { get; }
        public string RequestId { get; }

        public bool IsActive => _owner.IsActive(ControllerGeneration, RequestId);

        public bool TryComplete() =>
            _owner.TryComplete(ControllerGeneration, RequestId);
    }
}
