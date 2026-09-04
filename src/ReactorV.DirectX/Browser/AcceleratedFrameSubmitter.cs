using System;
using System.Diagnostics;
using System.Threading;
using CefSharp.Enums;
using RageWebUI.DirectX.Native;
using ReactorV.FrameTransport;

namespace RageWebUI.DirectX.Browser
{
    internal enum AcceleratedFrameSubmitResult
    {
        CallbackStarted,
        Submitted,
        Dropped,
        BootstrapProbeDeferred,
        BootstrapProbeRejected,
        InvalidFrame,
        Unavailable,
        CallbackFaulted
    }

    internal interface IAcceleratedFrameSubmitter
    {
        bool IsReady { get; }
        bool IsBootstrapPending { get; }
        SharedTextureSubmitStatus LastStatus { get; }

        event Action? Ready;
        event Action? Unavailable;

        AcceleratedFrameSubmitResult TrySubmit(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            ColorType colorType,
            ulong generation);
    }

    internal sealed class NativeAcceleratedFrameSubmitter : IAcceleratedFrameSubmitter
    {
        private const int BootstrapPending = 0;
        private const int BootstrapProbing = 1;
        private const int ReadyState = 2;
        private const int UnavailableState = 3;
        private const int BootstrapProbeRetryMilliseconds = 100;

        private SharedTextureCapabilities _capabilities;
        private readonly AcceleratedSubmitHealthPolicy _submitHealth = new AcceleratedSubmitHealthPolicy();
        private int _state;
        private long _nextProbeTimestamp;
        private int _lastStatus = (int)SharedTextureSubmitStatus.UnknownFailure;

        private NativeAcceleratedFrameSubmitter(
            SharedTextureCapabilities capabilities,
            bool bootstrapPending)
        {
            _capabilities = capabilities;
            _state = bootstrapPending ? BootstrapPending : ReadyState;
        }

        public bool IsReady => Volatile.Read(ref _state) == ReadyState;

        public bool IsBootstrapPending =>
            Volatile.Read(ref _state) == BootstrapPending ||
            Volatile.Read(ref _state) == BootstrapProbing;

        public SharedTextureSubmitStatus LastStatus =>
            (SharedTextureSubmitStatus)Volatile.Read(ref _lastStatus);

        public event Action? Ready;
        public event Action? Unavailable;

        public static bool TryCreate(
            bool allowBootstrapProbe,
            out IAcceleratedFrameSubmitter? submitter)
        {
            submitter = null;
            if (!NativeCompositor.TryGetSharedTextureCapabilities(out var capabilities))
                return false;

            if (capabilities.SupportsSynchronousBgra8)
            {
                submitter = new NativeAcceleratedFrameSubmitter(
                    capabilities,
                    bootstrapPending: false);
                return true;
            }

            if (allowBootstrapProbe && capabilities.SupportsBootstrapProbe)
            {
                submitter = new NativeAcceleratedFrameSubmitter(
                    capabilities,
                    bootstrapPending: true);
                return true;
            }

            return false;
        }

        public AcceleratedFrameSubmitResult TrySubmit(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            ColorType colorType,
            ulong generation)
        {
            if (sharedTextureHandle == IntPtr.Zero ||
                colorType != ColorType.Bgra8888 ||
                !_capabilities.SupportsDimensions(width, height))
            {
                if (Volatile.Read(ref _state) == ReadyState &&
                    _submitHealth.Observe(AcceleratedSubmitHealth.HardFailure) ==
                        AcceleratedSubmitDecision.Disable)
                {
                    PublishUnavailableOnce();
                    return AcceleratedFrameSubmitResult.Unavailable;
                }
                FailBootstrapIfPending();
                return AcceleratedFrameSubmitResult.InvalidFrame;
            }

            // CefSharp's ColorType values are not DXGI format constants. Map
            // BGRA explicitly; RGBA is intentionally rejected until the native
            // producer implements a deterministic channel conversion.
            var dxgiFormat = (uint)SharedGpuPixelFormat.Bgra8Unorm;
            if (Volatile.Read(ref _state) == ReadyState)
            {
                var status = NativeCompositor.SubmitSharedTextureStatus(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation);
                Volatile.Write(ref _lastStatus, (int)status);
                var decision = _submitHealth.Observe(Classify(status));
                if (decision == AcceleratedSubmitDecision.Disable &&
                    Volatile.Read(ref _state) != UnavailableState)
                {
                    PublishUnavailableOnce();
                }
                switch (decision)
                {
                    case AcceleratedSubmitDecision.Accepted:
                        return AcceleratedFrameSubmitResult.Submitted;
                    case AcceleratedSubmitDecision.Disable:
                        return AcceleratedFrameSubmitResult.Unavailable;
                    default:
                        return AcceleratedFrameSubmitResult.Dropped;
                }
            }

            var now = Stopwatch.GetTimestamp();
            if (now < Volatile.Read(ref _nextProbeTimestamp))
                return AcceleratedFrameSubmitResult.BootstrapProbeDeferred;

            if (Interlocked.CompareExchange(
                    ref _state,
                    BootstrapProbing,
                    BootstrapPending) != BootstrapPending)
            {
                return AcceleratedFrameSubmitResult.BootstrapProbeDeferred;
            }
            Volatile.Write(
                ref _nextProbeTimestamp,
                now + Math.Max(
                    1L,
                    Stopwatch.Frequency * BootstrapProbeRetryMilliseconds / 1000L));

            // Probe is a distinct ABI so ordinary in-process browsers never
            // weaken their synchronous-copy capability gate. It still consumes
            // the transient handle synchronously inside this CEF callback.
            var probeStatus = NativeCompositor.ProbeSharedTextureStatus(
                sharedTextureHandle,
                width,
                height,
                dxgiFormat,
                generation);
            Volatile.Write(ref _lastStatus, (int)probeStatus);
            if (probeStatus == SharedTextureSubmitStatus.Submitted &&
                NativeCompositor.TryGetSharedTextureCapabilities(out var promoted) &&
                promoted.SupportsSynchronousBgra8)
            {
                _capabilities = promoted;
                Volatile.Write(ref _state, ReadyState);
                PublishActionSafely(Ready);
                return AcceleratedFrameSubmitResult.Submitted;
            }

            // Consumer attachment is independent of CEF startup. A rejected
            // probe is therefore retryable until ExternalGpuBrowserSession's
            // bounded first-frame deadline selects the CPU fallback.
            Volatile.Write(ref _state, BootstrapPending);
            return AcceleratedFrameSubmitResult.BootstrapProbeRejected;
        }

        private static AcceleratedSubmitHealth Classify(SharedTextureSubmitStatus status)
        {
            switch (status)
            {
                case SharedTextureSubmitStatus.Submitted:
                    return AcceleratedSubmitHealth.Submitted;
                case SharedTextureSubmitStatus.Backpressure:
                    return AcceleratedSubmitHealth.Backpressure;
                case SharedTextureSubmitStatus.SessionInvalid:
                case SharedTextureSubmitStatus.AdapterOrResourceInvalid:
                case SharedTextureSubmitStatus.ProducerStopped:
                    return AcceleratedSubmitHealth.HardInvalidation;
                case SharedTextureSubmitStatus.DeviceOrCopyFailure:
                case SharedTextureSubmitStatus.InvalidFrame:
                case SharedTextureSubmitStatus.UnknownFailure:
                default:
                    // Device/copy and unknown legacy failures receive a small
                    // bounded retry allowance before the shadow is disabled.
                    return AcceleratedSubmitHealth.HardFailure;
            }
        }

        private void FailBootstrapIfPending()
        {
            var previous = Interlocked.CompareExchange(
                ref _state,
                UnavailableState,
                BootstrapPending);
            if (previous == BootstrapPending) PublishActionSafely(Unavailable);
        }

        private void PublishUnavailableOnce()
        {
            if (Interlocked.Exchange(ref _state, UnavailableState) == UnavailableState)
                return;

            // The owner queues teardown away from CEF's callback and leaves
            // the authoritative WebView2 surface active.
            PublishActionSafely(Unavailable);
        }

        private static void PublishActionSafely(Action? handlers)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch (Exception)
                {
                    // Never unwind an observer failure into CEF's accelerated
                    // paint callback; other observers still receive the state.
                }
            }
        }
    }
}
