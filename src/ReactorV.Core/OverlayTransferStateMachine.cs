using System;

namespace RageWebUI.Core
{
    /// <summary>
    /// The only legal lifecycle for a Reactor surface crossing from Chromium
    /// into GTA's desktop presentation. Browser paint and a successful
    /// DirectComposition commit are deliberately separate from proof that the
    /// operating-system compositor actually presented the intended pixels.
    /// A promoted external HWND can remain visible but non-interactive when
    /// Windows cannot prove it above an independent/exclusive-flip game frame.
    /// </summary>
    public enum OverlayTransferPhase
    {
        Hidden = 0,
        Preparing = 1,
        BrowserPaintVerified = 2,
        WindowPromoted = 3,
        DesktopPresentationVerified = 4,
        /// <summary>
        /// Chromium's exact transfer marker was captured, DirectComposition
        /// committed successfully, and the external HWND was promoted. This
        /// is sufficient to publish passive visibility for a
        /// WS_EX_NOREDIRECTIONBITMAP surface, but it never grants input: an
        /// independent desktop witness must still advance the same identity.
        /// </summary>
        CompositionCommittedVisible = 5,

        // Source-compatibility alias for receipts/tests produced before the
        // passive DirectComposition contract was named explicitly.
        PresentationUnverifiedVisible = CompositionCommittedVisible,
        Interactive = 6,
        Failed = 7,
        ExplicitUserIntentAuthorized = 8,
    }

    public enum OverlayTransferOwner
    {
        Bootstrap = 0,
        Provider = 1,
    }

    /// <summary>
    /// Immutable causal identity for one presentation attempt. Every async
    /// acknowledgement must match every field before it can advance state.
    /// </summary>
    public readonly struct OverlayTransferIdentity : IEquatable<OverlayTransferIdentity>
    {
        public OverlayTransferIdentity(
            OverlayTransferOwner owner,
            int transferGeneration,
            long gameWindow,
            int width,
            int height,
            int controllerGeneration,
            int compositionGeneration,
            int providerSessionGeneration,
            string? surfaceMode,
            int surfaceGeneration,
            string? presentationId)
        {
            if (transferGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(transferGeneration));
            if (gameWindow == 0)
                throw new ArgumentOutOfRangeException(nameof(gameWindow));
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (controllerGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(controllerGeneration));
            if (compositionGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(compositionGeneration));
            if (providerSessionGeneration < 0)
                throw new ArgumentOutOfRangeException(nameof(providerSessionGeneration));
            if (surfaceGeneration < 0)
                throw new ArgumentOutOfRangeException(nameof(surfaceGeneration));

            var normalizedMode = surfaceMode?.Trim() ?? string.Empty;
            var normalizedPresentation = presentationId?.Trim() ?? string.Empty;
            if (owner == OverlayTransferOwner.Provider &&
                normalizedPresentation.Length == 0)
            {
                throw new ArgumentException(
                    "A provider transfer requires a presentation ID.",
                    nameof(presentationId));
            }
            if (owner == OverlayTransferOwner.Bootstrap &&
                normalizedMode.Length == 0)
            {
                throw new ArgumentException(
                    "A bootstrap transfer requires a surface mode.",
                    nameof(surfaceMode));
            }

            Owner = owner;
            TransferGeneration = transferGeneration;
            GameWindow = gameWindow;
            Width = width;
            Height = height;
            ControllerGeneration = controllerGeneration;
            CompositionGeneration = compositionGeneration;
            ProviderSessionGeneration = providerSessionGeneration;
            SurfaceMode = normalizedMode;
            SurfaceGeneration = surfaceGeneration;
            PresentationId = normalizedPresentation;
        }

        public OverlayTransferOwner Owner { get; }
        public int TransferGeneration { get; }
        public long GameWindow { get; }
        public int Width { get; }
        public int Height { get; }
        public int ControllerGeneration { get; }
        public int CompositionGeneration { get; }
        public int ProviderSessionGeneration { get; }
        public string SurfaceMode { get; }
        public int SurfaceGeneration { get; }
        public string PresentationId { get; }

        public bool Equals(OverlayTransferIdentity other) =>
            Owner == other.Owner &&
            TransferGeneration == other.TransferGeneration &&
            GameWindow == other.GameWindow &&
            Width == other.Width &&
            Height == other.Height &&
            ControllerGeneration == other.ControllerGeneration &&
            CompositionGeneration == other.CompositionGeneration &&
            ProviderSessionGeneration == other.ProviderSessionGeneration &&
            string.Equals(SurfaceMode, other.SurfaceMode, StringComparison.Ordinal) &&
            SurfaceGeneration == other.SurfaceGeneration &&
            string.Equals(PresentationId, other.PresentationId, StringComparison.Ordinal);

        public override bool Equals(object? value) =>
            value is OverlayTransferIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Owner;
                hash = (hash * 397) ^ TransferGeneration;
                hash = (hash * 397) ^ GameWindow.GetHashCode();
                hash = (hash * 397) ^ Width;
                hash = (hash * 397) ^ Height;
                hash = (hash * 397) ^ ControllerGeneration;
                hash = (hash * 397) ^ CompositionGeneration;
                hash = (hash * 397) ^ ProviderSessionGeneration;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(SurfaceMode);
                hash = (hash * 397) ^ SurfaceGeneration;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(PresentationId);
                return hash;
            }
        }

        public static bool operator ==(
            OverlayTransferIdentity left,
            OverlayTransferIdentity right) => left.Equals(right);

        public static bool operator !=(
            OverlayTransferIdentity left,
            OverlayTransferIdentity right) => !left.Equals(right);
    }

    /// <summary>
    /// A small, lock-protected transfer ledger. It rejects stale identities,
    /// skipped phases, resurrection after failure, and input authorization
    /// before independent desktop-pixel evidence.
    /// </summary>
    public sealed class OverlayTransferStateMachine
    {
        private readonly object _sync = new object();
        private OverlayTransferIdentity? _identity;
        private OverlayTransferPhase _phase = OverlayTransferPhase.Hidden;
        private string _failureReason = string.Empty;
        private int _highestTransferGeneration;

        public OverlayTransferPhase Phase
        {
            get { lock (_sync) return _phase; }
        }

        public OverlayTransferIdentity? Identity
        {
            get { lock (_sync) return _identity; }
        }

        public string FailureReason
        {
            get { lock (_sync) return _failureReason; }
        }

        public bool IsInteractive
        {
            get { lock (_sync) return _phase == OverlayTransferPhase.Interactive; }
        }

        public bool Begin(OverlayTransferIdentity identity)
        {
            lock (_sync)
            {
                // Generation is monotonic for the lifetime of the native
                // window. Hide invalidates the active identity, but it must
                // not make an old queued begin callback current again.
                if (identity.TransferGeneration <= _highestTransferGeneration)
                    return false;

                _highestTransferGeneration = identity.TransferGeneration;
                _identity = identity;
                _phase = OverlayTransferPhase.Preparing;
                _failureReason = string.Empty;
                return true;
            }
        }

        public bool TryAdvance(
            OverlayTransferIdentity identity,
            OverlayTransferPhase expected,
            OverlayTransferPhase next)
        {
            if (!IsLegalTransition(expected, next))
                return false;

            lock (_sync)
            {
                if (!_identity.HasValue || !_identity.Value.Equals(identity) ||
                    _phase != expected || _phase == OverlayTransferPhase.Failed)
                {
                    return false;
                }

                _phase = next;
                return true;
            }
        }

        public bool TryFail(OverlayTransferIdentity identity, string reason)
        {
            lock (_sync)
            {
                if (!_identity.HasValue || !_identity.Value.Equals(identity) ||
                    _phase == OverlayTransferPhase.Hidden ||
                    _phase == OverlayTransferPhase.Failed)
                {
                    return false;
                }

                _phase = OverlayTransferPhase.Failed;
                _failureReason = string.IsNullOrWhiteSpace(reason)
                    ? "unspecified"
                    : reason.Trim();
                return true;
            }
        }

        public bool Matches(OverlayTransferIdentity identity)
        {
            lock (_sync)
                return _identity.HasValue && _identity.Value.Equals(identity);
        }

        public void Hide()
        {
            lock (_sync)
            {
                _identity = null;
                _phase = OverlayTransferPhase.Hidden;
                _failureReason = string.Empty;
            }
        }

        private static bool IsLegalTransition(
            OverlayTransferPhase expected,
            OverlayTransferPhase next) =>
            (expected == OverlayTransferPhase.Preparing &&
             next == OverlayTransferPhase.BrowserPaintVerified) ||
            (expected == OverlayTransferPhase.BrowserPaintVerified &&
             next == OverlayTransferPhase.WindowPromoted) ||
            (expected == OverlayTransferPhase.WindowPromoted &&
             next == OverlayTransferPhase.CompositionCommittedVisible) ||
            (expected == OverlayTransferPhase.CompositionCommittedVisible &&
             next == OverlayTransferPhase.DesktopPresentationVerified) ||
            (expected == OverlayTransferPhase.CompositionCommittedVisible &&
             next == OverlayTransferPhase.ExplicitUserIntentAuthorized) ||
            (expected == OverlayTransferPhase.DesktopPresentationVerified &&
             next == OverlayTransferPhase.Interactive) ||
            (expected == OverlayTransferPhase.ExplicitUserIntentAuthorized &&
             next == OverlayTransferPhase.Interactive);
    }
}
