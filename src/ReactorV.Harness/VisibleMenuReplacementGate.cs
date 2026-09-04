using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Release-gate coverage for an already-visible provider menu being
    /// replaced. The browser is deliberately held inside menu.get so its
    /// loading tree exists for several desktop frames. During that interval
    /// the previously committed paint identity must remain the sole visible
    /// owner. Both a different menu key and a fresh presentation of the same
    /// key are exercised before the fixture returns to GBAY Home.
    /// </summary>
    internal static class VisibleMenuReplacementGate
    {
        private const int ProviderSessionGeneration = 0;
        private static readonly TimeSpan LoadingObservation =
            TimeSpan.FromMilliseconds(140);

        internal static Result Run(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayLifecycleHarness.GbayHarnessRouter router,
            TimeSpan readyBudget,
            string evidenceDirectory,
            string initiallyCommittedPresentationId)
        {
            if (string.IsNullOrWhiteSpace(initiallyCommittedPresentationId))
                throw new ArgumentException(
                    "A committed presentation id is required.",
                    nameof(initiallyCommittedPresentationId));

            var frames = new List<GbayLifecycleHarness.VisualFrame>();
            const string crossKeyPresentationId = "gbay-atomic-cross-key";
            const string sameKeyPresentationId = "gbay-atomic-same-key";
            const string restoredHomePresentationId = "gbay-atomic-home-restored";

            ExerciseReplacement(
                host,
                visualCapture,
                runtime,
                broker,
                router,
                readyBudget,
                evidenceDirectory,
                phase: "cross-key",
                previousPresentationId: initiallyCommittedPresentationId,
                replacementPresentationId: crossKeyPresentationId,
                menuId: "vehicles",
                menuRevision: "gbay-atomic-cross-key",
                frames: frames);

            ExerciseReplacement(
                host,
                visualCapture,
                runtime,
                broker,
                router,
                readyBudget,
                evidenceDirectory,
                phase: "same-key",
                previousPresentationId: crossKeyPresentationId,
                replacementPresentationId: sameKeyPresentationId,
                menuId: "vehicles",
                menuRevision: "gbay-atomic-same-key",
                frames: frames);

            // Return the production route matrix to its expected Home root.
            // This revision is already cached from the first presentation, so
            // it also proves that an exact ready acknowledgement remains the
            // swap boundary on the cached fast path.
            PostPresentation(
                runtime,
                router,
                restoredHomePresentationId,
                menuId: "home",
                menuRevision: $"gbay-harness-{router.MenuRevision}");
            WaitForCommittedIdentity(
                host,
                visualCapture,
                runtime,
                broker,
                router,
                restoredHomePresentationId,
                sameKeyPresentationId,
                readyBudget,
                Path.Combine(evidenceDirectory, "atomic-home-restored.png"),
                frames);
            PumpFor(runtime, broker, router, TimeSpan.FromMilliseconds(60));

            return new Result(
                frames,
                additionalMenuGetCount: 2,
                crossKeyPreserved: true,
                sameKeyPreserved: true,
                noIntermediateFrame: true);
        }

        private static void ExerciseReplacement(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayLifecycleHarness.GbayHarnessRouter router,
            TimeSpan readyBudget,
            string evidenceDirectory,
            string phase,
            string previousPresentationId,
            string replacementPresentationId,
            string menuId,
            string menuRevision,
            ICollection<GbayLifecycleHarness.VisualFrame> frames)
        {
            var exactReadyBefore = router.ExactPresentationReadyCount;
            if (!WindowProbe.EnsureForeground(host.Handle, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException(
                    $"The synthetic GTA host could not be activated for the {phase} replacement gate.");
            router.HoldNextMenuGet();
            PostPresentation(
                runtime,
                router,
                replacementPresentationId,
                menuId,
                menuRevision);

            PumpUntil(
                runtime,
                broker,
                router,
                () => router.HasHeldMenuGet,
                readyBudget,
                $"The {phase} replacement did not enter its held loading state.");

            // menu.get has not completed, so no exact ready acknowledgement is
            // possible. Sample the real desktop throughout that staged load.
            // The previous marker must remain present and the new one must not
            // leak through opacity, a loading shell, or a partial asset frame.
            var observation = Stopwatch.StartNew();
            Bitmap? lastImage = null;
            try
            {
                while (observation.Elapsed < LoadingObservation)
                {
                    Application.DoEvents();
                    Pump(runtime, broker, router);
                    if (!runtime.IsVisible)
                        throw new InvalidOperationException(
                            $"The visible {phase} replacement hid its committed owner while loading.");
                    if (router.ExactPresentationReadyCount != exactReadyBefore)
                        throw new InvalidOperationException(
                            $"The {phase} replacement acknowledged ready before menu.get completed.");

                    lastImage?.Dispose();
                    lastImage = visualCapture.Capture(host);
                    var frame = GbayLifecycleHarness.VisualFrame.Measure(lastImage);
                    frames.Add(frame);
                    if (!frame.IsGbay || !frame.SurroundMatchesHost)
                        throw new InvalidOperationException(
                            $"The {phase} replacement exposed a loading, blank, opaque, or intermediate frame.");
                    if (!MenuPaintIdentityProbe.Contains(
                            lastImage,
                            ProviderSessionGeneration,
                            previousPresentationId))
                    {
                        throw new InvalidOperationException(
                            $"The {phase} replacement retired the old committed paint owner before exact ready.");
                    }
                    if (MenuPaintIdentityProbe.Contains(
                            lastImage,
                            ProviderSessionGeneration,
                            replacementPresentationId))
                    {
                        throw new InvalidOperationException(
                            $"The {phase} replacement became visible before exact ready.");
                    }
                    Thread.Sleep(12);
                }

                if (lastImage == null)
                    throw new InvalidOperationException(
                        $"The {phase} replacement produced no loading-state desktop samples.");
                lastImage.Save(Path.Combine(
                    evidenceDirectory,
                    $"atomic-{phase}-old-owner.png"));
            }
            finally
            {
                lastImage?.Dispose();
            }

            router.ReleaseHeldMenuGet(runtime);
            WaitForCommittedIdentity(
                host,
                visualCapture,
                runtime,
                broker,
                router,
                replacementPresentationId,
                previousPresentationId,
                readyBudget,
                Path.Combine(evidenceDirectory, $"atomic-{phase}-committed.png"),
                frames);
        }

        private static void WaitForCommittedIdentity(
            Form host,
            HarnessVisualCaptureSession visualCapture,
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayLifecycleHarness.GbayHarnessRouter router,
            string expectedPresentationId,
            string stalePresentationId,
            TimeSpan timeout,
            string screenshotPath,
            ICollection<GbayLifecycleHarness.VisualFrame> frames)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                Application.DoEvents();
                Pump(runtime, broker, router);
                if (!runtime.IsVisible)
                    throw new InvalidOperationException(
                        $"Presentation '{expectedPresentationId}' hid the old visible owner before exact ready.");

                using var image = visualCapture.Capture(host);
                var frame = GbayLifecycleHarness.VisualFrame.Measure(image);
                frames.Add(frame);
                var expectedMarker = MenuPaintIdentityProbe.Contains(
                    image,
                    ProviderSessionGeneration,
                    expectedPresentationId);
                var staleMarker = MenuPaintIdentityProbe.Contains(
                    image,
                    ProviderSessionGeneration,
                    stalePresentationId);
                var exactReady = string.Equals(
                    router.LastAcceptedPresentation,
                    expectedPresentationId,
                    StringComparison.Ordinal);
                if (exactReady && expectedMarker)
                {
                    if (staleMarker)
                    {
                        SaveFailureFrame(image, screenshotPath);
                        throw new InvalidOperationException(
                            $"Presentation '{expectedPresentationId}' committed while the stale paint owner remained visible. " +
                            DescribeFrame(frame, expectedMarker, staleMarker, exactReady));
                    }
                    if (!frame.IsGbay || !frame.SurroundMatchesHost)
                    {
                        SaveFailureFrame(image, screenshotPath);
                        throw new InvalidOperationException(
                            $"Presentation '{expectedPresentationId}' committed a blank, opaque, or intermediate asset frame. " +
                            DescribeFrame(frame, expectedMarker, staleMarker, exactReady));
                    }

                    image.Save(screenshotPath);
                    return;
                }

                // menu.get may now be complete while image decode/fonts/two
                // paint frames are still pending. The old identity remains
                // authoritative through that entire asset-ready interval.
                if (!frame.IsGbay || !frame.SurroundMatchesHost ||
                    !staleMarker || expectedMarker)
                {
                    SaveFailureFrame(image, screenshotPath);
                    throw new InvalidOperationException(
                        $"Presentation '{expectedPresentationId}' exposed a loading, blank, or intermediate asset frame before exact ready. " +
                        DescribeFrame(frame, expectedMarker, staleMarker, exactReady));
                }
                Thread.Sleep(5);
            }

            throw new InvalidOperationException(
                $"Presentation '{expectedPresentationId}' did not become the exact visible paint owner within its budget.");
        }

        private static void SaveFailureFrame(Bitmap image, string screenshotPath)
        {
            var directory = Path.GetDirectoryName(screenshotPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            image.Save(Path.ChangeExtension(screenshotPath, ".failed.png"));
        }

        private static string DescribeFrame(
            GbayLifecycleHarness.VisualFrame frame,
            bool expectedMarker,
            bool staleMarker,
            bool exactReady)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "black={0:F4}; changed={1:F4}; green={2:F4}; blue={3:F4}; surround={4}; isGbay={5}; expectedMarker={6}; staleMarker={7}; exactReady={8}.",
                frame.BlackFraction,
                frame.ChangedFraction,
                frame.GreenFraction,
                frame.BlueFraction,
                frame.SurroundMatchesHost,
                frame.IsGbay,
                expectedMarker,
                staleMarker,
                exactReady);
        }

        private static void PostPresentation(
            IOverlayRuntime runtime,
            GbayLifecycleHarness.GbayHarnessRouter router,
            string presentationId,
            string menuId,
            string menuRevision)
        {
            router.ExpectPresentation(presentationId);
            runtime.PostEvent("host.surface", new JObject { ["mode"] = "none" });
            runtime.PostEvent(
                "menu.presentation",
                new JObject
                {
                    ["extensionId"] = "allin1.gbay",
                    ["menuId"] = menuId,
                    ["presentationId"] = presentationId,
                    ["inputMode"] = "interactive-menu",
                    ["context"] = new JObject
                    {
                        ["route"] = $"gbay/{menuId}",
                        ["presentationStyle"] = "allin1-shell",
                        ["initialSection"] = menuId,
                        ["menuRevision"] = menuRevision,
                    },
                });
        }

        private static void PumpUntil(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayLifecycleHarness.GbayHarnessRouter router,
            Func<bool> condition,
            TimeSpan timeout,
            string error)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                Application.DoEvents();
                Pump(runtime, broker, router);
                if (condition()) return;
                Thread.Sleep(5);
            }
            throw new InvalidOperationException(error);
        }

        private static void PumpFor(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayLifecycleHarness.GbayHarnessRouter router,
            TimeSpan duration)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < duration)
            {
                Application.DoEvents();
                Pump(runtime, broker, router);
                Thread.Sleep(4);
            }
        }

        private static void Pump(
            IOverlayRuntime runtime,
            BridgeBroker broker,
            GbayLifecycleHarness.GbayHarnessRouter router)
        {
            for (var index = 0; index < 64 && broker.TryDequeue(out var request); index++)
            {
                if (request != null && router.TryDispatch(request, out var response))
                    runtime.PostResponse(response);
            }
        }

        internal sealed class Result
        {
            internal Result(
                IReadOnlyList<GbayLifecycleHarness.VisualFrame> frames,
                int additionalMenuGetCount,
                bool crossKeyPreserved,
                bool sameKeyPreserved,
                bool noIntermediateFrame)
            {
                Frames = frames;
                AdditionalMenuGetCount = additionalMenuGetCount;
                CrossKeyPreserved = crossKeyPreserved;
                SameKeyPreserved = sameKeyPreserved;
                NoIntermediateFrame = noIntermediateFrame;
            }

            internal IReadOnlyList<GbayLifecycleHarness.VisualFrame> Frames { get; }
            internal int AdditionalMenuGetCount { get; }
            internal bool CrossKeyPreserved { get; }
            internal bool SameKeyPreserved { get; }
            internal bool NoIntermediateFrame { get; }
        }
    }

    /// <summary>
    /// Independent desktop decoder for the browser's eight-cell menu identity
    /// marker. It intentionally does not trust DOM state or native trace text.
    /// </summary>
    internal static class MenuPaintIdentityProbe
    {
        private const int MinimumStride = 8;
        private const int MaximumStride = 48;

        internal static bool Contains(
            Bitmap image,
            int providerSessionGeneration,
            string presentationId)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var identity = Compute(providerSessionGeneration, presentationId);
            if (identity == 0 || image.Width < 57 || image.Height < 1) return false;

            var scanLeft = Math.Max(0, image.Width - MaximumStride * 8);
            var scanTop = Math.Max(0, image.Height - MaximumStride);
            for (var y = scanTop; y < image.Height; y++)
            {
                for (var x = scanLeft; x < image.Width; x++)
                {
                    // Marker cells have a fixed blue channel and bright nibble
                    // channels. Reject ordinary desktop pixels before testing
                    // candidate strides so the desktop gate remains cheap.
                    var first = image.GetPixel(x, y);
                    if (first.A < 240 || Math.Abs(first.B - 208) > 10 ||
                        first.R < 54 || first.G < 54)
                    {
                        continue;
                    }

                    for (var stride = MinimumStride;
                        stride <= MaximumStride && x + 7 * stride < image.Width;
                        stride++)
                    {
                        var matched = true;
                        for (var byteIndex = 0; byteIndex < 8; byteIndex++)
                        {
                            var pixel = image.GetPixel(x + byteIndex * stride, y);
                            var value = (byte)(identity >> (byteIndex * 8));
                            var red = 64 + ((value >> 4) * 12);
                            var green = 64 + ((value & 0x0F) * 12);
                            if (pixel.A < 240 || Math.Abs(pixel.R - red) > 10 ||
                                Math.Abs(pixel.G - green) > 10 ||
                                Math.Abs(pixel.B - 208) > 10)
                            {
                                matched = false;
                                break;
                            }
                        }
                        if (matched) return true;
                    }
                }
            }
            return false;
        }

        private static ulong Compute(
            int providerSessionGeneration,
            string presentationId)
        {
            if (providerSessionGeneration < 0 || string.IsNullOrWhiteSpace(presentationId))
                return 0;
            var canonical = "reactor-v-paint/v1\0menu\0" +
                providerSessionGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
                "\0" + presentationId;
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var value in System.Text.Encoding.UTF8.GetBytes(canonical))
                {
                    hash ^= value;
                    hash *= 1099511628211UL;
                }
                return hash == 0 ? 1UL : hash;
            }
        }
    }
}
