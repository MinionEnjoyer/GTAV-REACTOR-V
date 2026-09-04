using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// Binds the accelerated browser's post-accept paint proof to the exact
    /// provider session and presentation that completed the dual-browser
    /// readiness barrier. The proof is one-shot so a late or duplicated
    /// browser callback cannot qualify a replacement presentation.
    /// </summary>
    public sealed class ExternalGpuPostAcceptPaintGate
    {
        private readonly object _sync = new object();
        private int _highestProviderSessionGeneration;
        private int _providerSessionGeneration;
        private bool _sessionActive;
        private bool _consumed;
        private string _activePresentationId = string.Empty;
        private string _dualBrowserReadyPresentationId = string.Empty;

        public bool BeginSession(int providerSessionGeneration)
        {
            if (providerSessionGeneration <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(providerSessionGeneration));

            lock (_sync)
            {
                if (providerSessionGeneration <=
                    _highestProviderSessionGeneration)
                {
                    return false;
                }

                _highestProviderSessionGeneration =
                    providerSessionGeneration;
                _providerSessionGeneration = providerSessionGeneration;
                _sessionActive = true;
                ClearPresentationLocked();
                return true;
            }
        }

        public bool ResetSession(int providerSessionGeneration)
        {
            lock (_sync)
            {
                if (!_sessionActive ||
                    providerSessionGeneration !=
                        _providerSessionGeneration)
                {
                    return false;
                }

                _sessionActive = false;
                _providerSessionGeneration = 0;
                ClearPresentationLocked();
                return true;
            }
        }

        public bool BeginPresentation(
            int providerSessionGeneration,
            string presentationId)
        {
            ValidatePresentationId(presentationId);
            lock (_sync)
            {
                if (!IsCurrentSessionLocked(providerSessionGeneration))
                    return false;

                _activePresentationId = presentationId;
                _dualBrowserReadyPresentationId = string.Empty;
                _consumed = false;
                return true;
            }
        }

        public bool CancelPresentation(
            int providerSessionGeneration,
            string presentationId)
        {
            ValidatePresentationId(presentationId);
            lock (_sync)
            {
                if (!IsCurrentSessionLocked(providerSessionGeneration) ||
                    !string.Equals(
                        _activePresentationId,
                        presentationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                ClearPresentationLocked();
                return true;
            }
        }

        public bool RecordDualBrowserReady(
            int providerSessionGeneration,
            string presentationId)
        {
            ValidatePresentationId(presentationId);
            lock (_sync)
            {
                if (!IsCurrentSessionLocked(providerSessionGeneration) ||
                    !string.Equals(
                        _activePresentationId,
                        presentationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                _dualBrowserReadyPresentationId = presentationId;
                _consumed = false;
                return true;
            }
        }

        public bool TryAcceptPostAcceptPaint(
            int providerSessionGeneration,
            string presentationId)
        {
            ValidatePresentationId(presentationId);
            lock (_sync)
            {
                if (!IsCurrentSessionLocked(providerSessionGeneration) ||
                    _consumed ||
                    !string.Equals(
                        _activePresentationId,
                        presentationId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        _dualBrowserReadyPresentationId,
                        presentationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                _consumed = true;
                return true;
            }
        }

        private bool IsCurrentSessionLocked(
            int providerSessionGeneration) =>
            _sessionActive &&
            providerSessionGeneration == _providerSessionGeneration;

        private void ClearPresentationLocked()
        {
            _activePresentationId = string.Empty;
            _dualBrowserReadyPresentationId = string.Empty;
            _consumed = false;
        }

        private static void ValidatePresentationId(string presentationId)
        {
            if (!ProviderPresentationCommitContract.IsValidPresentationId(
                    presentationId))
            {
                throw new ArgumentException(
                    "A bounded presentation ID is required.",
                    nameof(presentationId));
            }
        }
    }
}
