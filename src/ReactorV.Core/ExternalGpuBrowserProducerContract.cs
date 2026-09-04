using System;
using System.IO;
using RageWebUI.Core;
using ReactorV.FrameTransport;

namespace ReactorV.ExternalGpu
{
    /// <summary>
    /// Immutable construction contract shared by the persistent Preloader and
    /// its optional accelerated producer. The existing bridge sink remains the
    /// only browser-to-game authority; the producer owns only browser/frame
    /// resources for this exact GTA process.
    /// </summary>
    public sealed class ExternalGpuBrowserProducerContext
    {
        public ExternalGpuBrowserProducerContext(
            int targetGtaProcessId,
            string uiDirectory,
            string runtimeDirectory,
            string userDataDirectory,
            IBridgeMessageSink bridgeSink,
            int width,
            int height,
            int frameRate,
            bool enableDevTools,
            IntPtr parentWindow = default)
        {
            if (targetGtaProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetGtaProcessId));
            if (string.IsNullOrWhiteSpace(uiDirectory))
                throw new ArgumentException(
                    "The UI directory is required.", nameof(uiDirectory));
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new ArgumentException(
                    "The runtime directory is required.", nameof(runtimeDirectory));
            if (string.IsNullOrWhiteSpace(userDataDirectory))
                throw new ArgumentException(
                    "The browser profile directory is required.",
                    nameof(userDataDirectory));
            if (bridgeSink == null)
                throw new ArgumentNullException(nameof(bridgeSink));
            if (width <= 0 || width > SharedGpuFrameProtocol.MaximumDimension)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > SharedGpuFrameProtocol.MaximumDimension)
                throw new ArgumentOutOfRangeException(nameof(height));
            if ((ulong)width * (ulong)height * 4ul >
                SharedGpuFrameProtocol.MaximumBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "The browser surface exceeds the shared-GPU frame byte limit.");
            }
            if (frameRate < 15 || frameRate > 60)
                throw new ArgumentOutOfRangeException(nameof(frameRate));

            TargetGtaProcessId = targetGtaProcessId;
            UiDirectory = Path.GetFullPath(uiDirectory);
            RuntimeDirectory = Path.GetFullPath(runtimeDirectory);
            UserDataDirectory = Path.GetFullPath(userDataDirectory);
            BridgeSink = bridgeSink;
            Width = width;
            Height = height;
            FrameRate = frameRate;
            EnableDevTools = enableDevTools;
            ParentWindow = parentWindow;
        }

        public int TargetGtaProcessId { get; }
        public string UiDirectory { get; }
        public string RuntimeDirectory { get; }
        public string UserDataDirectory { get; }
        public IBridgeMessageSink BridgeSink { get; }
        public int Width { get; }
        public int Height { get; }
        public int FrameRate { get; }
        public bool EnableDevTools { get; }
        public IntPtr ParentWindow { get; }
        public string TransportDiscoveryName =>
            SharedGpuFrameTransportNames.DiscoveryMapping(TargetGtaProcessId);
    }

    /// <summary>
    /// Browser/frame producer surface shared without a Preloader-to-DirectX
    /// project reference. Shared texture negotiation remains implementation
    /// owned; the Preloader forwards only its existing authorized state/input.
    /// </summary>
    public interface IExternalGpuBrowserProducer : IDisposable
    {
        string RendererName { get; }
        bool IsContentReady { get; }

        event Action? ContentReady;
        event Action? ContentUnavailable;
        event Action<Exception>? StartupFailed;

        bool Start();
        void SetVisible(bool visible);
        void PostJson(string json);
        void PostPointerInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta);
    }

    /// <summary>
    /// Optional surface contract for an external GPU producer whose browser
    /// viewport follows the target game's client area. Presentation readiness
    /// is intentionally stronger than DOM readiness: it is true only after a
    /// frame at <see cref="SurfaceWidth"/> x <see cref="SurfaceHeight"/> has
    /// been accepted and acknowledged by the in-game consumer.
    /// </summary>
    public interface IResizableExternalGpuBrowserProducer :
        IExternalGpuBrowserProducer
    {
        int SurfaceWidth { get; }
        int SurfaceHeight { get; }
        bool IsPresentationReady { get; }

        event Action<bool, int, int>? PresentationReadinessChanged;

        /// <summary>
        /// Requests a bounded browser viewport. Returning true means the
        /// dimensions were accepted; callers must still wait for
        /// <see cref="IsPresentationReady"/> or <see cref="ContentReady"/>.
        /// </summary>
        bool Resize(int width, int height);

        /// <summary>
        /// Begins a new presentation paint boundary at the current surface
        /// size. The producer must hide the previous surface, invalidate
        /// presentation readiness, request a new browser paint, and become
        /// ready again only after that newer frame is acknowledged by the
        /// target consumer.
        /// </summary>
        bool RefreshPresentation();
    }

    /// <summary>
    /// Optional same-plane replacement contract. A producer implementing this
    /// contract can stage a newer acknowledged frame while the target keeps
    /// presenting the last qualified frame. This is used only for an atomic
    /// initializer-to-provider handoff; ordinary resize, hide, and cold-open
    /// boundaries remain fail-closed through
    /// <see cref="IResizableExternalGpuBrowserProducer.RefreshPresentation"/>.
    /// </summary>
    public interface IRetainedExternalGpuBrowserProducer :
        IResizableExternalGpuBrowserProducer
    {
        /// <summary>
        /// Begins a new current-size presentation boundary without publishing
        /// a native hidden edge. Readiness must still become false immediately
        /// and may become true only after a strictly newer exact-size frame is
        /// acknowledged by the target consumer.
        /// </summary>
        bool RefreshPresentationRetainingCurrentFrame();
    }
}
