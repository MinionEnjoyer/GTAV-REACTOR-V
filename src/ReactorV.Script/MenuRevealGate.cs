using System;

namespace RageWebUI.Script
{
    /// <summary>
    /// Keeps an extension menu hidden until the browser confirms that the
    /// matching presentation has committed and measured its full layout. A stale browser
    /// acknowledgement can never reveal a replacement presentation.
    /// </summary>
    internal sealed class MenuRevealGate
    {
        internal const long DefaultTimeoutMilliseconds = 5000;

        private readonly long _timeoutMilliseconds;
        private string? _pendingPresentationId;
        private long _dispatchedAtMilliseconds;

        internal MenuRevealGate(long timeoutMilliseconds = DefaultTimeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        internal string? PendingPresentationId => _pendingPresentationId;

        internal void Begin(string presentationId, long dispatchedAtMilliseconds)
        {
            if (!MenuPresentationPolicy.IsValidPresentationId(presentationId))
                throw new ArgumentException("Invalid presentation id.", nameof(presentationId));

            _pendingPresentationId = presentationId;
            _dispatchedAtMilliseconds = dispatchedAtMilliseconds;
        }

        internal bool TryAccept(
            string presentationId,
            long acknowledgedAtMilliseconds,
            out long waitMilliseconds)
        {
            waitMilliseconds = 0;
            if (_pendingPresentationId == null ||
                !string.Equals(
                    _pendingPresentationId,
                    presentationId,
                    StringComparison.Ordinal) ||
                acknowledgedAtMilliseconds - _dispatchedAtMilliseconds >=
                    _timeoutMilliseconds)
                return false;

            waitMilliseconds = Math.Max(0, acknowledgedAtMilliseconds - _dispatchedAtMilliseconds);
            _pendingPresentationId = null;
            return true;
        }

        internal bool TryExpire(long currentMilliseconds, out string? presentationId)
        {
            presentationId = null;
            if (_pendingPresentationId == null ||
                currentMilliseconds - _dispatchedAtMilliseconds < _timeoutMilliseconds)
                return false;

            presentationId = _pendingPresentationId;
            _pendingPresentationId = null;
            return true;
        }

        internal void Cancel() => _pendingPresentationId = null;
    }
}
