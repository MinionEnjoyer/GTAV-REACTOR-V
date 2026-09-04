using System;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    /// <summary>One PNG plus the exact browser presentation that produced it.</summary>
    public sealed class OverlayAcceptancePreviewFrame
    {
        public OverlayAcceptancePreviewFrame(
            byte[] png,
            string surfaceMode,
            int surfaceGeneration,
            int controllerGeneration,
            string? menuPresentationId,
            bool domReady,
            bool browserSurfaceCaptured,
            bool browserPixelsVerified,
            bool desktopPresentationVerified)
        {
            Png = png ?? throw new ArgumentNullException(nameof(png));
            SurfaceMode = surfaceMode ?? string.Empty;
            SurfaceGeneration = surfaceGeneration;
            ControllerGeneration = controllerGeneration;
            MenuPresentationId = menuPresentationId ?? string.Empty;
            DomReady = domReady;
            BrowserSurfaceCaptured = browserSurfaceCaptured;
            BrowserPixelsVerified = browserPixelsVerified;
            DesktopPresentationVerified = desktopPresentationVerified;
        }

        public byte[] Png { get; }
        public string SurfaceMode { get; }
        public int SurfaceGeneration { get; }
        public int ControllerGeneration { get; }
        public string MenuPresentationId { get; }
        /// <summary>Navigation/React readiness; this is not pixel evidence.</summary>
        public bool DomReady { get; }
        /// <summary>CapturePreview returned browser-compositor pixels.</summary>
        public bool BrowserSurfaceCaptured { get; }
        /// <summary>The optional browser-surface pixel analyzer passed.</summary>
        public bool BrowserPixelsVerified { get; }
        /// <summary>
        /// Independent desktop presentation evidence. Browser capture alone
        /// must never set this value.
        /// </summary>
        public bool DesktopPresentationVerified { get; }
        public string EvidenceBoundary =>
            OverlayPresentationPolicy.DescribePresentationEvidence(
                DomReady,
                BrowserSurfaceCaptured,
                BrowserPixelsVerified,
                DesktopPresentationVerified);
    }
}
