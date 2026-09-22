using System;
using System.Drawing;
using System.Threading;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using RageWebUI.Core;

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
        private readonly Func<ulong>? _generationSource;
        private readonly Func<bool>? _frameAcceptance;
        private readonly Func<MonotonicGenerationEpochGate.SubmissionLease?>? _submissionLeaseSource;
        private long _generation;

        public AcceleratedRenderHandler(
            ChromiumWebBrowser browser,
            IAcceleratedFrameSubmitter submitter,
            Action<AcceleratedPaintObservation>? observer = null,
            Func<ulong>? generationSource = null,
            Func<bool>? frameAcceptance = null,
            Func<MonotonicGenerationEpochGate.SubmissionLease?>? submissionLeaseSource = null)
            : base(browser)
        {
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
            _observer = observer;
            _generationSource = generationSource;
            _frameAcceptance = frameAcceptance;
            _submissionLeaseSource = submissionLeaseSource;
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

            // The owner may already have replaced or closed this browser
            // while CEF drains a queued callback. Reject before handing the
            // transient texture to native code; ignoring its later observer
            // event would be too late because submission is synchronous.
            try
            {
                if (_frameAcceptance?.Invoke() == false) return;
            }
            catch
            {
                return;
            }

            ulong generation;
            MonotonicGenerationEpochGate.SubmissionLease? submissionLease = null;
            try
            {
                submissionLease = _submissionLeaseSource?.Invoke();
                if (_submissionLeaseSource != null && submissionLease == null) return;
                generation = submissionLease?.Generation ?? _generationSource?.Invoke() ??
                    unchecked((ulong)Interlocked.Increment(ref _generation));
            }
            catch
            {
                // Generation exhaustion is fail-closed: do not reuse an
                // identity or unwind through CefRenderHandler.
                submissionLease?.Dispose();
                return;
            }

            System.Drawing.Size size;
            // A replacement CEF browser can receive a late paint callback
            // after its successor is already staging. External sessions pass
            // one allocator shared by every browser instance, so the native
            // producer never sees a generation rewind at that boundary.
            var result = AcceleratedFrameSubmitResult.CallbackFaulted;
            try
            {
                size = _browser.Size;

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
            }
            catch
            {
                // Browser size/property access may race CEF teardown too.
                return;
            }
            finally
            {
                // Release before publishing the completed observation. Session
                // observers can take the lifecycle lock while Stop waits for
                // leases; retaining it through that observer would invert the
                // drain dependency.
                submissionLease?.Dispose();
            }
            PublishObservation(new AcceleratedPaintObservation(
                generation,
                acceleratedPaintInfo.SharedTextureHandle,
                size.Width,
                size.Height,
                acceleratedPaintInfo.Format,
                result));
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
