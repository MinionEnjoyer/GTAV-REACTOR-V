using System;
using System.Collections.Generic;

namespace RageWebUI.Core
{
    /// <summary>
    /// Correlation rules for acceptance pixels captured directly from the
    /// persistent WebView2 composition surface.  The browser capture is owned
    /// by the preloader process, so it never waits on GTA's swap chain or DWM.
    /// </summary>
    public static class LiveAcceptancePreviewCaptureContract
    {
        public const int SchemaVersion = 1;
        public const int RequiredFrameCount = 2;
        public const int MaximumPngBytes = 16 * 1024 * 1024;
        public static readonly TimeSpan CaptureDeadline =
            TimeSpan.FromMilliseconds(1500);

        public static bool RequiresHostPreview(
            LiveAcceptanceVisualExpectation expectation) =>
            expectation != LiveAcceptanceVisualExpectation.EvidenceOnly;

        public static string? ExpectedSurfaceMode(
            LiveAcceptanceVisualExpectation expectation)
        {
            switch (expectation)
            {
                case LiveAcceptanceVisualExpectation.ReactorAbout:
                    return "about";
                case LiveAcceptanceVisualExpectation.Allin1Preloader:
                    return "initializing";
                default:
                    return null;
            }
        }

        public static bool TryValidateCorrelatedFrames(
            LiveAcceptanceVisualExpectation expectation,
            string? expectedSurfaceMode,
            int? expectedSurfaceGeneration,
            IReadOnlyList<LiveAcceptancePreviewIdentity>? frames,
            out string failure)
        {
            failure = string.Empty;
            if (!RequiresHostPreview(expectation))
            {
                failure = "evidence-only captures do not use the host preview exchange";
                return false;
            }
            if (frames == null || frames.Count != RequiredFrameCount)
            {
                failure = $"expected exactly {RequiredFrameCount} browser frames";
                return false;
            }

            var first = frames[0];
            if (!first.IsStructurallyValid)
            {
                failure = "the first browser frame has no valid controller identity";
                return false;
            }
            for (var index = 1; index < frames.Count; index++)
            {
                if (!frames[index].IsStructurallyValid ||
                    !first.Equals(frames[index]))
                {
                    failure = "the browser presentation changed between capture frames";
                    return false;
                }
            }

            var requiredMode = expectedSurfaceMode ?? ExpectedSurfaceMode(expectation);
            if (!string.IsNullOrWhiteSpace(requiredMode) &&
                !string.Equals(first.SurfaceMode, requiredMode, StringComparison.Ordinal))
            {
                failure = $"captured surface '{first.SurfaceMode}' did not match '{requiredMode}'";
                return false;
            }
            if (expectedSurfaceGeneration.HasValue &&
                first.SurfaceGeneration != expectedSurfaceGeneration.Value)
            {
                failure = $"captured generation {first.SurfaceGeneration} did not match " +
                    $"acknowledged generation {expectedSurfaceGeneration.Value}";
                return false;
            }
            if (expectation == LiveAcceptanceVisualExpectation.GbayMenu &&
                string.IsNullOrWhiteSpace(first.MenuPresentationId))
            {
                failure = "the GBAY frame was not bound to an active menu presentation";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Desktop pixels are sampled outside Chromium and carry no intrinsic
        /// WebView identity. Require the same correlated browser identity
        /// immediately before and after that sample so a hide, controller
        /// replacement, or presentation swap cannot lend pixels to the wrong
        /// lifecycle receipt.
        /// </summary>
        public static bool TryValidateDesktopIdentityBracket(
            LiveAcceptancePreviewIdentity before,
            LiveAcceptancePreviewIdentity after,
            out string failure)
        {
            failure = string.Empty;
            if (!before.IsStructurallyValid || !after.IsStructurallyValid)
            {
                failure = "the desktop capture bracket has no valid browser identity";
                return false;
            }
            if (!before.Equals(after))
            {
                failure = "the browser presentation changed across the desktop capture";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Binds a stable desktop-capture bracket to the lifecycle identity
        /// that will receive the pixel evidence. Stability alone is not
        /// sufficient: a different, unchanged presentation must not be
        /// credited to the active lifecycle.
        /// </summary>
        public static bool TryValidateDesktopIdentityBracket(
            LiveAcceptancePreviewIdentity before,
            LiveAcceptancePreviewIdentity after,
            int expectedSurfaceGeneration,
            string? expectedMenuPresentationId,
            out string failure)
        {
            if (!TryValidateDesktopIdentityBracket(before, after, out failure))
                return false;
            if (expectedSurfaceGeneration <= 0 ||
                before.SurfaceGeneration != expectedSurfaceGeneration)
            {
                failure = "the desktop capture bracket did not match the lifecycle surface generation";
                return false;
            }
            if (!string.Equals(
                    before.MenuPresentationId,
                    expectedMenuPresentationId ?? string.Empty,
                    StringComparison.Ordinal))
            {
                failure = "the desktop capture bracket did not match the lifecycle presentation";
                return false;
            }
            return true;
        }
    }

    public readonly struct LiveAcceptancePreviewIdentity : IEquatable<LiveAcceptancePreviewIdentity>
    {
        public LiveAcceptancePreviewIdentity(
            string surfaceMode,
            int surfaceGeneration,
            int controllerGeneration,
            string? menuPresentationId)
        {
            SurfaceMode = surfaceMode ?? string.Empty;
            SurfaceGeneration = surfaceGeneration;
            ControllerGeneration = controllerGeneration;
            MenuPresentationId = menuPresentationId ?? string.Empty;
        }

        public string SurfaceMode { get; }
        public int SurfaceGeneration { get; }
        public int ControllerGeneration { get; }
        public string MenuPresentationId { get; }

        public bool IsStructurallyValid =>
            ControllerGeneration > 0 &&
            SurfaceGeneration >= 0 &&
            !string.IsNullOrWhiteSpace(SurfaceMode);

        public bool Equals(LiveAcceptancePreviewIdentity other) =>
            string.Equals(SurfaceMode, other.SurfaceMode, StringComparison.Ordinal) &&
            SurfaceGeneration == other.SurfaceGeneration &&
            ControllerGeneration == other.ControllerGeneration &&
            string.Equals(MenuPresentationId, other.MenuPresentationId, StringComparison.Ordinal);

        public override bool Equals(object? value) =>
            value is LiveAcceptancePreviewIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(SurfaceMode);
                hash = (hash * 397) ^ SurfaceGeneration;
                hash = (hash * 397) ^ ControllerGeneration;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(MenuPresentationId);
                return hash;
            }
        }
    }
}
