using System;
using System.Threading;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;

namespace RageWebUI.DirectX.Browser
{
    internal readonly struct AcceleratedPaintObservation
    {
        internal AcceleratedPaintObservation(
            ulong generation,
            IntPtr sharedTextureHandle,
            int width,
            int height,
            ColorType colorType,
            AcceleratedFrameSubmitResult result)
        {
            Generation = generation;
            SharedTextureHandle = sharedTextureHandle;
            Width = width;
            Height = height;
            ColorType = colorType;
            Result = result;
        }

        internal ulong Generation { get; }
        internal IntPtr SharedTextureHandle { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal ColorType ColorType { get; }
        internal AcceleratedFrameSubmitResult Result { get; }
    }

    internal sealed class AcceleratedRenderHandler : DefaultRenderHandler
    {
        private readonly ChromiumWebBrowser _browser;
        private readonly IAcceleratedFrameSubmitter _submitter;
        private readonly Action<AcceleratedPaintObservation>? _observer;
        private long _generation;

        public AcceleratedRenderHandler(
            ChromiumWebBrowser browser,
            IAcceleratedFrameSubmitter submitter,
            Action<AcceleratedPaintObservation>? observer = null)
            : base(browser)
        {
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
            _observer = observer;
        }

        public override void OnAcceleratedPaint(
            PaintElementType type,
            Rect dirtyRect,
            AcceleratedPaintInfo acceleratedPaintInfo)
        {
            if (type != PaintElementType.View || acceleratedPaintInfo == null)
            {
                return;
            }

            var size = _browser.Size;
            var generation = unchecked((ulong)Interlocked.Increment(ref _generation));
            var result = AcceleratedFrameSubmitResult.CallbackFaulted;

            PublishObservation(new AcceleratedPaintObservation(
                generation,
                acceleratedPaintInfo.SharedTextureHandle,
                size.Width,
                size.Height,
                acceleratedPaintInfo.Format,
                AcceleratedFrameSubmitResult.CallbackStarted));

            // Cef owns this handle and releases it as soon as this callback
            // returns. The native boundary must open and copy it synchronously;
            // no managed or native caller may retain the transient handle.
            try
            {
                result = _submitter.TrySubmit(
                    acceleratedPaintInfo.SharedTextureHandle,
                    size.Width,
                    size.Height,
                    acceleratedPaintInfo.Format,
                    generation);
            }
            catch
            {
                // No managed exception may unwind through CefRenderHandler.
                // Native submission is fail-open and the external session's
                // bounded timeout will select the fallback path.
            }
            finally
            {
                PublishObservation(new AcceleratedPaintObservation(
                    generation,
                    acceleratedPaintInfo.SharedTextureHandle,
                    size.Width,
                    size.Height,
                    acceleratedPaintInfo.Format,
                    result));
            }
        }

        private void PublishObservation(AcceleratedPaintObservation observation)
        {
            try
            {
                _observer?.Invoke(observation);
            }
            catch
            {
                // Diagnostics must never unwind through CefRenderHandler.
            }
        }
    }
}
