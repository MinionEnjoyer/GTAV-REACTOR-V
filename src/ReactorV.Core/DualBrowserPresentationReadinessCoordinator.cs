using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// Identifies the browser document contributing a provider-presentation
    /// paint acknowledgement. The WebView document remains the bridge
    /// authority; the accelerated document contributes the second paint proof.
    /// </summary>
    public enum PresentationReadyBrowserRole
    {
        WebViewAuthority = 0,
        ExternalGpuShadow = 1,
    }

    /// <summary>
    /// Describes how one browser-ready submission affected the current gate.
    /// </summary>
    public enum PresentationReadySubmissionStatus
    {
        Ignored = 0,
        Buffered = 1,
        DispatchReady = 2,
    }

    /// <summary>
    /// Maps the one authoritative provider response onto a second browser's
    /// request ID. The host copies the result or error and replaces only the
    /// response ID with <see cref="AliasRequestId"/>.
    /// </summary>
    public readonly struct PresentationReadyResponseAlias
    {
        internal PresentationReadyResponseAlias(
            string authoritativeRequestId,
            string aliasRequestId,
            PresentationReadyBrowserRole aliasRole)
        {
            AuthoritativeRequestId = authoritativeRequestId;
            AliasRequestId = aliasRequestId;
            AliasRole = aliasRole;
        }

        public string AuthoritativeRequestId { get; }
        public string AliasRequestId { get; }
        public PresentationReadyBrowserRole AliasRole { get; }
    }

    /// <summary>
    /// The single request that may be forwarded to the GTA provider after all
    /// required browser documents have independently crossed their paint
    /// boundary. <typeparamref name="TPayload"/> lets the host retain its
    /// already-validated wire envelope without coupling Core to that envelope.
    /// </summary>
    public sealed class PresentationReadyDispatch<TPayload>
    {
        internal PresentationReadyDispatch(
            int providerSessionGeneration,
            string presentationId,
            string authoritativeRequestId,
            TPayload authoritativePayload,
            PresentationReadyResponseAlias? responseAlias)
        {
            ProviderSessionGeneration = providerSessionGeneration;
            PresentationId = presentationId;
            AuthoritativeRequestId = authoritativeRequestId;
            AuthoritativePayload = authoritativePayload;
            ResponseAlias = responseAlias;
        }

        public int ProviderSessionGeneration { get; }
        public string PresentationId { get; }
        public string AuthoritativeRequestId { get; }
        public TPayload AuthoritativePayload { get; }
        public PresentationReadyResponseAlias? ResponseAlias { get; }
    }

    /// <summary>
    /// Thread-safe, transport-neutral two-browser paint barrier. It forwards
    /// exactly one WebView-authored request for an exact active presentation.
    /// When the accelerated shadow is required, the request remains buffered
    /// until that document supplies a distinct request ID. Session changes and
    /// presentation replacement invalidate every buffered acknowledgement.
    /// </summary>
    public sealed class DualBrowserPresentationReadinessCoordinator<TPayload>
    {
        public const int MaximumRequestIdLength = 64;

        private readonly object _sync = new object();
        private int _highestProviderSessionGeneration;
        private int _providerSessionGeneration;
        private bool _sessionActive;
        private bool _externalGpuShadowRequired;
        private bool _dispatched;
        private string _presentationId = string.Empty;
        private PendingReady? _webViewReady;
        private PendingReady? _externalGpuReady;

        /// <summary>
        /// Starts a newer provider session and atomically clears all state from
        /// the previous one. Equal or older generations are ignored.
        /// </summary>
        public bool BeginSession(
            int providerSessionGeneration,
            bool externalGpuShadowRequired)
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

                _highestProviderSessionGeneration = providerSessionGeneration;
                _providerSessionGeneration = providerSessionGeneration;
                _sessionActive = true;
                _externalGpuShadowRequired = externalGpuShadowRequired;
                ClearPresentationLocked();
                return true;
            }
        }

        /// <summary>
        /// Clears the exact current session. A late reset from an older session
        /// cannot erase a newer browser/provider relationship.
        /// </summary>
        public bool ResetSession(int providerSessionGeneration)
        {
            lock (_sync)
            {
                if (!_sessionActive ||
                    providerSessionGeneration != _providerSessionGeneration)
                {
                    return false;
                }

                _sessionActive = false;
                _providerSessionGeneration = 0;
                _externalGpuShadowRequired = false;
                ClearPresentationLocked();
                return true;
            }
        }

        /// <summary>
        /// Selects the exact active presentation. Selecting a replacement
        /// invalidates every request buffered for the previous token.
        /// </summary>
        public bool BeginPresentation(
            int providerSessionGeneration,
            string presentationId)
        {
            ValidatePresentationId(presentationId);
            lock (_sync)
            {
                if (!IsCurrentSessionLocked(providerSessionGeneration))
                    return false;
                if (string.Equals(
                        _presentationId,
                        presentationId,
                        StringComparison.Ordinal))
                {
                    // A provider may close and later re-present the same
                    // durable menu token. Once its previous barrier completed,
                    // the new menu.presentation event is a fresh paint cycle;
                    // while incomplete, an identical replay remains a no-op.
                    if (!_dispatched) return false;
                    ClearPresentationLocked();
                    _presentationId = presentationId;
                    return true;
                }

                ClearPresentationLocked();
                _presentationId = presentationId;
                return true;
            }
        }

        /// <summary>
        /// Cancels only the exact active presentation. A late dismissal for a
        /// superseded token cannot erase the replacement's paint barrier.
        /// </summary>
        public bool CancelPresentation(
            int providerSessionGeneration,
            string presentationId)
        {
            ValidatePresentationId(presentationId);
            lock (_sync)
            {
                if (!IsCurrentSessionLocked(providerSessionGeneration) ||
                    !string.Equals(
                        _presentationId,
                        presentationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                ClearPresentationLocked();
                return true;
            }
        }

        /// <summary>
        /// Records one browser's paint-ready request. Only the exact active
        /// session and presentation are eligible, each role gets one request,
        /// and the two roles must use distinct request IDs.
        /// </summary>
        public PresentationReadySubmissionStatus Submit(
            int providerSessionGeneration,
            PresentationReadyBrowserRole role,
            string presentationId,
            string requestId,
            TPayload requestPayload,
            out PresentationReadyDispatch<TPayload>? dispatch)
        {
            ValidateRole(role);
            ValidatePresentationId(presentationId);
            ValidateRequestId(requestId);
            if (ReferenceEquals(requestPayload, null))
                throw new ArgumentNullException(nameof(requestPayload));

            lock (_sync)
            {
                dispatch = null;
                if (!IsCurrentSessionLocked(providerSessionGeneration) ||
                    _dispatched ||
                    !string.Equals(
                        _presentationId,
                        presentationId,
                        StringComparison.Ordinal))
                {
                    return PresentationReadySubmissionStatus.Ignored;
                }

                switch (role)
                {
                    case PresentationReadyBrowserRole.WebViewAuthority:
                        if (_webViewReady != null ||
                            RequestIdsMatch(_externalGpuReady, requestId))
                        {
                            return PresentationReadySubmissionStatus.Ignored;
                        }
                        _webViewReady = new PendingReady(requestId, requestPayload);
                        break;

                    case PresentationReadyBrowserRole.ExternalGpuShadow:
                        if (!_externalGpuShadowRequired ||
                            _externalGpuReady != null ||
                            RequestIdsMatch(_webViewReady, requestId))
                        {
                            return PresentationReadySubmissionStatus.Ignored;
                        }
                        _externalGpuReady = new PendingReady(requestId, requestPayload);
                        break;
                }

                return TryCreateDispatchLocked(out dispatch)
                    ? PresentationReadySubmissionStatus.DispatchReady
                    : PresentationReadySubmissionStatus.Buffered;
            }
        }

        /// <summary>
        /// Removes the accelerated document from the current presentation's
        /// barrier. This is the fault/fallback path: a buffered WebView request
        /// becomes dispatchable immediately without waiting for another frame.
        /// </summary>
        public bool DisableExternalGpuShadow(
            int providerSessionGeneration,
            out PresentationReadyDispatch<TPayload>? dispatch)
        {
            lock (_sync)
            {
                dispatch = null;
                if (!IsCurrentSessionLocked(providerSessionGeneration))
                    return false;

                _externalGpuShadowRequired = false;
                if (_dispatched || _webViewReady == null)
                    return true;

                TryCreateDispatchLocked(out dispatch);
                return true;
            }
        }

        private bool TryCreateDispatchLocked(
            out PresentationReadyDispatch<TPayload>? dispatch)
        {
            dispatch = null;
            if (_dispatched || _webViewReady == null ||
                (_externalGpuShadowRequired && _externalGpuReady == null))
            {
                return false;
            }

            PresentationReadyResponseAlias? alias = null;
            if (_externalGpuReady != null)
            {
                alias = new PresentationReadyResponseAlias(
                    _webViewReady.RequestId,
                    _externalGpuReady.RequestId,
                    PresentationReadyBrowserRole.ExternalGpuShadow);
            }

            _dispatched = true;
            dispatch = new PresentationReadyDispatch<TPayload>(
                _providerSessionGeneration,
                _presentationId,
                _webViewReady.RequestId,
                _webViewReady.Payload,
                alias);
            return true;
        }

        private bool IsCurrentSessionLocked(int providerSessionGeneration) =>
            _sessionActive &&
            providerSessionGeneration == _providerSessionGeneration;

        private void ClearPresentationLocked()
        {
            _presentationId = string.Empty;
            _webViewReady = null;
            _externalGpuReady = null;
            _dispatched = false;
        }

        private static bool RequestIdsMatch(
            PendingReady? pending,
            string requestId) =>
            pending != null &&
            string.Equals(pending.RequestId, requestId, StringComparison.Ordinal);

        private static void ValidateRole(PresentationReadyBrowserRole role)
        {
            if (role != PresentationReadyBrowserRole.WebViewAuthority &&
                role != PresentationReadyBrowserRole.ExternalGpuShadow)
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }
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

        private static void ValidateRequestId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId) ||
                requestId.Length > MaximumRequestIdLength)
            {
                throw new ArgumentException(
                    "A bounded bridge request ID is required.",
                    nameof(requestId));
            }
        }

        private sealed class PendingReady
        {
            internal PendingReady(string requestId, TPayload payload)
            {
                RequestId = requestId;
                Payload = payload;
            }

            internal string RequestId { get; }
            internal TPayload Payload { get; }
        }
    }
}
