using System;

namespace RageWebUI.Script
{
    /// <summary>
    /// Separates the browser's layout-ready acknowledgement from the native
    /// host's proof that those exact pixels reached the provider surface.
    /// Input and registry readiness remain fail-closed until both phases match.
    /// </summary>
    internal sealed class ProviderPresentationCommitGate
    {
        internal const long DefaultTimeoutMilliseconds = 5000;

        private readonly long _timeoutMilliseconds;
        private string? _pendingPresentationId;
        private long _preparedAtMilliseconds;
        private long _browserPreparationWaitMilliseconds;

        internal ProviderPresentationCommitGate(
            long timeoutMilliseconds = DefaultTimeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        internal string? PendingPresentationId => _pendingPresentationId;

        internal long BrowserPreparationWaitMilliseconds =>
            _browserPreparationWaitMilliseconds;

        internal void Begin(
            string presentationId,
            long preparedAtMilliseconds,
            long browserPreparationWaitMilliseconds)
        {
            if (!MenuPresentationPolicy.IsValidPresentationId(presentationId))
                throw new ArgumentException(
                    "Invalid presentation id.",
                    nameof(presentationId));
            if (browserPreparationWaitMilliseconds < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(browserPreparationWaitMilliseconds));

            _pendingPresentationId = presentationId;
            _preparedAtMilliseconds = preparedAtMilliseconds;
            _browserPreparationWaitMilliseconds = browserPreparationWaitMilliseconds;
        }

        internal bool TryCommit(
            string presentationId,
            long committedAtMilliseconds,
            out long providerCommitWaitMilliseconds,
            out long browserPreparationWaitMilliseconds)
        {
            providerCommitWaitMilliseconds = 0;
            browserPreparationWaitMilliseconds = 0;
            if (_pendingPresentationId == null ||
                !string.Equals(
                    _pendingPresentationId,
                    presentationId,
                    StringComparison.Ordinal) ||
                committedAtMilliseconds - _preparedAtMilliseconds >=
                    _timeoutMilliseconds)
                return false;

            providerCommitWaitMilliseconds = Math.Max(
                0,
                committedAtMilliseconds - _preparedAtMilliseconds);
            browserPreparationWaitMilliseconds =
                _browserPreparationWaitMilliseconds;
            Cancel();
            return true;
        }

        internal bool TryExpire(
            long currentMilliseconds,
            out string? presentationId,
            out long browserPreparationWaitMilliseconds)
        {
            presentationId = null;
            browserPreparationWaitMilliseconds = 0;
            if (_pendingPresentationId == null ||
                currentMilliseconds - _preparedAtMilliseconds <
                    _timeoutMilliseconds)
                return false;

            presentationId = _pendingPresentationId;
            browserPreparationWaitMilliseconds =
                _browserPreparationWaitMilliseconds;
            Cancel();
            return true;
        }

        internal void Cancel()
        {
            _pendingPresentationId = null;
            _preparedAtMilliseconds = 0;
            _browserPreparationWaitMilliseconds = 0;
        }
    }
}
