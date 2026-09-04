using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using ReactorV.BootstrapHost;

namespace RageWebUI.Harness
{
    /// <summary>
    /// Bounded visual capture for a real GTA session. Named routes first retain
    /// an asynchronous preloader-owned WebView2 self-capture for renderer
    /// identity, then the harness independently samples the desktop compositor.
    /// Only the latter is allowed to prove that users could see the surface.
    /// </summary>
    internal sealed class LiveAcceptanceWindowCaptureSession
    {
        private readonly LiveAcceptanceWindowBinding _gameWindow;
        private readonly JArray _receipt;
        private readonly string _outputDirectory;
        private readonly string _runId;
        private readonly int _targetProcessId;
        private readonly Dictionary<string, int> _surfaceGenerations =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private int _sequence;

        internal LiveAcceptancePreviewIdentity? LastHostPreviewIdentity { get; private set; }
        internal string? LastDesktopAttemptArtifact { get; private set; }

        internal LiveAcceptanceWindowCaptureSession(
            LiveAcceptanceWindowBinding gameWindow,
            JArray receipt,
            string outputDirectory,
            string runId,
            int targetProcessId)
        {
            _gameWindow = gameWindow;
            _receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
            _outputDirectory = outputDirectory ??
                throw new ArgumentNullException(nameof(outputDirectory));
            _runId = runId ?? throw new ArgumentNullException(nameof(runId));
            if (targetProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetProcessId));
            _targetProcessId = targetProcessId;
        }

        internal void ObserveSurfaceReady(string line)
        {
            if (LiveAcceptanceContract.TryParseSurfaceReady(line, out var surface))
                _surfaceGenerations[surface.Mode] = surface.Generation;
        }

        internal Bitmap Capture(
            string artifact,
            LiveAcceptanceVisualExpectation expectation)
        {
            if (string.IsNullOrWhiteSpace(artifact))
                throw new ArgumentException("An artifact identity is required.", nameof(artifact));
            LastDesktopAttemptArtifact = null;
            var hostPreview =
                LiveAcceptancePreviewCaptureContract.RequiresHostPreview(expectation);
            if (!hostPreview)
            {
                LastHostPreviewIdentity = null;
                return CaptureDesktop(artifact, expectation);
            }

            LastHostPreviewIdentity = null;

            var sequence = Interlocked.Increment(ref _sequence);
            var startedUtc = DateTime.UtcNow;
            var timer = Stopwatch.StartNew();
            var entry = new JObject
            {
                ["sequence"] = sequence,
                ["artifact"] = artifact,
                ["expectation"] = expectation.ToString(),
                ["source"] = "host-webview2-capturepreview",
                ["provesDesktopVisibility"] = false,
                ["startedUtc"] = startedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["timeoutMs"] = LiveAcceptanceVisualCapturePolicy.CaptureTimeout.TotalMilliseconds,
                ["status"] = "running",
                ["attempts"] = new JArray(),
            };
            _receipt.Add(entry);
            return CaptureHostPreview(
                artifact,
                expectation,
                sequence,
                entry,
                timer);
        }

        /// <summary>
        /// Captures the pixels the desktop compositor presents over GTA. This
        /// is intentionally separate from WebView2 CapturePreview: a browser
        /// can render a correct private frame while DWM shows no overlay.
        /// </summary>
        internal Bitmap CaptureDesktop(
            string artifact,
            LiveAcceptanceVisualExpectation expectation)
        {
            if (string.IsNullOrWhiteSpace(artifact))
                throw new ArgumentException("An artifact identity is required.", nameof(artifact));
            LastDesktopAttemptArtifact = null;
            var sequence = Interlocked.Increment(ref _sequence);
            var timer = Stopwatch.StartNew();
            var entry = new JObject
            {
                ["sequence"] = sequence,
                ["artifact"] = artifact,
                ["expectation"] = expectation.ToString(),
                ["source"] = "desktop-dxgi-duplication-with-bitblt-fallback",
                ["provesDesktopVisibility"] = true,
                ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["timeoutMs"] = LiveAcceptanceVisualCapturePolicy.CaptureTimeout.TotalMilliseconds,
                ["status"] = "running",
                ["attempts"] = new JArray(),
            };
            _receipt.Add(entry);
            Bitmap? previous = null;
            DxgiDesktopCaptureSession? dxgi = null;
            var tracker = new LiveAcceptanceVisualStabilityTracker(expectation);
            try
            {
                RequireClientBounds(_gameWindow.Handle, out var initialBounds);
                try
                {
                    dxgi = new DxgiDesktopCaptureSession(initialBounds);
                    entry["dxgiOutput"] = dxgi.OutputIdentity;
                    entry["dxgiStatus"] = "ready";
                }
                catch (Exception error)
                {
                    entry["dxgiStatus"] = "unavailable";
                    entry["dxgiSetupError"] = error.Message;
                }

                while (timer.Elapsed < LiveAcceptanceVisualCapturePolicy.CaptureTimeout)
                {
                    var remaining = LiveAcceptanceVisualCapturePolicy.CaptureTimeout - timer.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    var attemptStartedUtc = DateTime.UtcNow;
                    var attemptTimer = Stopwatch.StartNew();
                    RequireClientBounds(_gameWindow.Handle, out var currentBounds);
                    var source = "desktop-bitblt-captureblt";
                    string? dxgiFailure = null;
                    Bitmap current;
                    if (dxgi != null)
                    {
                        try
                        {
                            source = "desktop-dxgi-duplication";
                            var frameTimeout = Math.Min(
                                250,
                                Math.Max(1, (int)remaining.TotalMilliseconds));
                            current = RunBounded(
                                () => dxgi.Capture(currentBounds, frameTimeout),
                                remaining,
                                artifact);
                        }
                        catch (Exception error)
                        {
                            dxgiFailure = error.Message;
                            // DXGI_ERROR_WAIT_TIMEOUT means the desktop did not
                            // change. A preceding duplicated frame is therefore
                            // still the current composed desktop image.
                            if (previous != null &&
                                error.HResult == unchecked((int)0x887A0027))
                            {
                                source = "desktop-dxgi-duplication-cached";
                                current = new Bitmap(previous);
                            }
                            else
                            {
                                source = "desktop-bitblt-captureblt-fallback";
                                current = RunBounded(
                                    () => CaptureCompositedClient(_gameWindow.Handle),
                                    remaining,
                                    artifact);
                            }
                        }
                    }
                    else
                    {
                        current = RunBounded(
                            () => CaptureCompositedClient(_gameWindow.Handle),
                            remaining,
                            artifact);
                    }

                    attemptTimer.Stop();
                    var metrics = Measure(current);
                    var changedFraction = previous == null
                        ? 0.0d
                        : ChangedFraction(previous, current);
                    var qualified = LiveAcceptanceVisualCapturePolicy.IsQualified(
                        expectation,
                        metrics);
                    var satisfied = tracker.Observe(metrics, changedFraction);
                    var deadlineExceeded = timer.Elapsed >=
                        LiveAcceptanceVisualCapturePolicy.CaptureTimeout;
                    ((JArray)entry["attempts"]!).Add(new JObject
                    {
                        ["startedUtc"] = attemptStartedUtc.ToString("O", CultureInfo.InvariantCulture),
                        ["completedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ["durationMs"] = attemptTimer.Elapsed.TotalMilliseconds,
                        ["source"] = source,
                        ["dxgiFailure"] = dxgiFailure == null
                            ? JValue.CreateNull()
                            : new JValue(dxgiFailure),
                        ["qualified"] = qualified,
                        ["stable"] = LiveAcceptanceVisualCapturePolicy.IsStableTransition(
                            expectation,
                            changedFraction),
                        ["deadlineExceeded"] = deadlineExceeded,
                        ["consecutiveQualifiedFrames"] = tracker.ConsecutiveQualifiedFrames,
                        ["changedFraction"] = changedFraction,
                        ["metrics"] = ToJson(metrics),
                    });

                    previous?.Dispose();
                    previous = current;
                    if (satisfied && !deadlineExceeded)
                    {
                        timer.Stop();
                        entry["status"] = "passed";
                        entry["completedUtc"] = DateTime.UtcNow.ToString(
                            "O",
                            CultureInfo.InvariantCulture);
                        entry["durationMs"] = timer.Elapsed.TotalMilliseconds;
                        entry["consecutiveQualifiedFrames"] =
                            tracker.ConsecutiveQualifiedFrames;
                        entry["metrics"] = ToJson(metrics);
                        var result = previous;
                        previous = null;
                        return result!;
                    }

                    var settleMilliseconds = Math.Min(
                        75,
                        Math.Max(
                            0,
                            (int)(LiveAcceptanceVisualCapturePolicy.CaptureTimeout -
                                timer.Elapsed).TotalMilliseconds));
                    if (settleMilliseconds > 0) Thread.Sleep(settleMilliseconds);
                }

                throw new TimeoutException(
                    $"Visual capture '{artifact}' did not produce " +
                    $"{LiveAcceptanceVisualCapturePolicy.RequiredConsecutiveFrames(expectation)} " +
                    $"stable {expectation} frame(s) within " +
                    $"{LiveAcceptanceVisualCapturePolicy.CaptureTimeout.TotalSeconds:F0} seconds.");
            }
            catch (Exception error)
            {
                timer.Stop();
                entry["status"] = "failed";
                entry["completedUtc"] = DateTime.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture);
                entry["durationMs"] = timer.Elapsed.TotalMilliseconds;
                entry["consecutiveQualifiedFrames"] =
                    tracker.ConsecutiveQualifiedFrames;
                entry["error"] = error.Message;
                if (previous != null)
                {
                    var rawName = artifact + "-last-desktop-attempt.png";
                    var rawPath = Path.Combine(_outputDirectory, rawName);
                    previous.Save(rawPath, System.Drawing.Imaging.ImageFormat.Png);
                    entry["lastAttemptArtifact"] = rawPath;
                    entry["lastAttemptPreserved"] = true;
                    LastDesktopAttemptArtifact = rawPath;
                }
                throw;
            }
            finally
            {
                previous?.Dispose();
                dxgi?.Dispose();
            }
        }

        private Bitmap CaptureHostPreview(
            string artifact,
            LiveAcceptanceVisualExpectation expectation,
            int sequence,
            JObject entry,
            Stopwatch timer)
        {
            var requestId = string.Format(
                CultureInfo.InvariantCulture,
                "{0:D4}-{1}",
                sequence,
                Guid.NewGuid().ToString("N"));
            var exchange = Path.Combine(_outputDirectory, "capture-exchange");
            Directory.CreateDirectory(exchange);
            var requestPath = Path.Combine(exchange, $"request-{requestId}.json");
            var responsePath = Path.Combine(exchange, $"response-{requestId}.json");
            var expectedMode =
                LiveAcceptancePreviewCaptureContract.ExpectedSurfaceMode(expectation);
            int? expectedGeneration = null;
            if (expectedMode != null &&
                _surfaceGenerations.TryGetValue(expectedMode, out var generation))
                expectedGeneration = generation;
            var request = new JObject
            {
                ["schemaVersion"] = LiveAcceptancePreviewCaptureContract.SchemaVersion,
                ["runId"] = _runId,
                ["requestId"] = requestId,
                ["harnessPid"] = Process.GetCurrentProcess().Id,
                ["expectation"] = expectation.ToString(),
                ["expectedSurfaceMode"] = expectedMode,
                ["expectedSurfaceGeneration"] = expectedGeneration,
                ["createdUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            WriteJsonAtomically(requestPath, request);
            entry["requestId"] = requestId;
            entry["expectedSurfaceMode"] = expectedMode;
            entry["expectedSurfaceGeneration"] = expectedGeneration;
            entry["wake"] = "process-scoped-auto-reset-event";
            if (!LiveAcceptanceCaptureWakeSignal.TrySignal(
                    _targetProcessId,
                    out var wakeFailure))
            {
                throw new InvalidOperationException(
                    "The persistent host could not be notified of the capture request: " +
                    wakeFailure + ".");
            }

            Bitmap? previous = null;
            try
            {
                JObject? response = null;
                while (timer.Elapsed < LiveAcceptanceVisualCapturePolicy.CaptureTimeout)
                {
                    if (File.Exists(responsePath))
                    {
                        var info = new FileInfo(responsePath);
                        if (info.Length > 0 && info.Length <= 131072)
                        {
                            response = JObject.Parse(File.ReadAllText(responsePath));
                            break;
                        }
                    }
                    Thread.Sleep(20);
                }
                if (response == null)
                    throw new TimeoutException(
                        $"Visual capture '{artifact}' exceeded its bounded " +
                        $"{LiveAcceptanceVisualCapturePolicy.CaptureTimeout.TotalSeconds:F0}-second deadline.");
                if (response.Value<int?>("schemaVersion") !=
                        LiveAcceptancePreviewCaptureContract.SchemaVersion ||
                    !string.Equals(response.Value<string>("runId"), _runId, StringComparison.Ordinal) ||
                    !string.Equals(response.Value<string>("requestId"), requestId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The browser capture response was not bound to this acceptance request.");
                if (!string.Equals(response.Value<string>("status"), "passed", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        response.Value<string>("error") ?? "The browser capture failed.");

                var frames = response["frames"] as JArray;
                if (frames == null ||
                    frames.Count != LiveAcceptancePreviewCaptureContract.RequiredFrameCount)
                    throw new InvalidOperationException(
                        "The browser capture did not return two correlated frames.");
                var identities = new List<LiveAcceptancePreviewIdentity>(frames.Count);
                var tracker = new LiveAcceptanceVisualStabilityTracker(expectation);
                LiveAcceptanceVisualFrameMetrics finalMetrics = default;
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index] as JObject ??
                        throw new InvalidOperationException("The browser frame metadata was malformed.");
                    var expectedName = $"{requestId}-frame-{index + 1}.png";
                    if (!string.Equals(
                            frame.Value<string>("file"),
                            expectedName,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "The browser frame referenced an unexpected artifact.");
                    var pngPath = Path.Combine(exchange, expectedName);
                    var pngLength = new FileInfo(pngPath).Length;
                    if (pngLength <= 0 ||
                        pngLength > LiveAcceptancePreviewCaptureContract.MaximumPngBytes)
                        throw new InvalidOperationException(
                            "The browser frame was empty or exceeded its size bound.");
                    var current = LoadBitmap(pngPath);
                    var identity = new LiveAcceptancePreviewIdentity(
                        frame.Value<string>("surfaceMode") ?? string.Empty,
                        frame.Value<int?>("surfaceGeneration") ?? -1,
                        frame.Value<int?>("controllerGeneration") ?? -1,
                        frame.Value<string>("menuPresentationId"));
                    identities.Add(identity);
                    finalMetrics = Measure(current);
                    var changedFraction = previous == null
                        ? 0.0d
                        : ChangedFraction(previous, current);
                    var qualified = LiveAcceptanceVisualCapturePolicy.IsQualified(
                        expectation,
                        finalMetrics);
                    var satisfied = tracker.Observe(finalMetrics, changedFraction);
                    ((JArray)entry["attempts"]!).Add(new JObject
                    {
                        ["frame"] = index + 1,
                        ["startedUtc"] = frame.Value<string>("startedUtc"),
                        ["completedUtc"] = frame.Value<string>("completedUtc"),
                        ["durationMs"] = frame.Value<double?>("durationMs"),
                        ["source"] = "host-webview2-capturepreview",
                        ["qualified"] = qualified,
                        ["stable"] = LiveAcceptanceVisualCapturePolicy.IsStableTransition(
                            expectation,
                            changedFraction),
                        ["consecutiveQualifiedFrames"] = tracker.ConsecutiveQualifiedFrames,
                        ["changedFraction"] = changedFraction,
                        ["surfaceMode"] = identity.SurfaceMode,
                        ["surfaceGeneration"] = identity.SurfaceGeneration,
                        ["controllerGeneration"] = identity.ControllerGeneration,
                        ["menuPresentationId"] = identity.MenuPresentationId,
                        ["metrics"] = ToJson(finalMetrics),
                    });
                    previous?.Dispose();
                    previous = current;
                    if (index + 1 == frames.Count && !satisfied)
                        throw new InvalidOperationException(
                            $"The browser frames did not qualify as stable {expectation} pixels.");
                }
                if (!LiveAcceptancePreviewCaptureContract.TryValidateCorrelatedFrames(
                        expectation,
                        expectedMode,
                        expectedGeneration,
                        identities,
                        out var correlationFailure))
                    throw new InvalidOperationException(correlationFailure);

                LastHostPreviewIdentity = identities[0];

                timer.Stop();
                if (timer.Elapsed > LiveAcceptanceVisualCapturePolicy.CaptureTimeout)
                    throw new TimeoutException(
                        $"Visual capture '{artifact}' exceeded its bounded " +
                        $"{LiveAcceptanceVisualCapturePolicy.CaptureTimeout.TotalSeconds:F0}-second deadline.");
                entry["status"] = "passed";
                entry["completedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                entry["durationMs"] = timer.Elapsed.TotalMilliseconds;
                entry["hostCaptureDurationMs"] = response.Value<double?>("durationMs");
                entry["consecutiveQualifiedFrames"] = tracker.ConsecutiveQualifiedFrames;
                entry["metrics"] = ToJson(finalMetrics);
                var result = previous;
                previous = null;
                return result!;
            }
            catch (Exception error)
            {
                timer.Stop();
                entry["status"] = "failed";
                entry["completedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                entry["durationMs"] = timer.Elapsed.TotalMilliseconds;
                entry["error"] = error.Message;
                throw;
            }
            finally
            {
                previous?.Dispose();
            }
        }

        private static Bitmap LoadBitmap(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var decoded = new Bitmap(stream);
            return new Bitmap(decoded);
        }

        private static void WriteJsonAtomically(string path, JObject value)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value.ToString(Formatting.None));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static Bitmap RunBounded(
            Func<Bitmap> capture,
            TimeSpan timeout,
            string artifact)
        {
            try
            {
                return LiveAcceptanceCaptureDeadline.Execute(
                    capture,
                    timeout,
                    late => late.Dispose());
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Visual capture '{artifact}' exceeded its bounded " +
                    $"{LiveAcceptanceVisualCapturePolicy.CaptureTimeout.TotalSeconds:F0}-second deadline.");
            }
        }

        private static Bitmap CaptureCompositedClient(IntPtr gameWindow)
        {
            RequireClientBounds(gameWindow, out var bounds);
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not expose the desktop composition surface.");
            var memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screen);
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not create the capture device context.");
            }
            var nativeBitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (nativeBitmap == IntPtr.Zero)
            {
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not allocate the capture bitmap.");
            }
            var previous = SelectObject(memory, nativeBitmap);
            try
            {
                if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Windows could not select the capture bitmap.");
                const uint SourceCopy = 0x00CC0020;
                const uint CaptureLayeredWindows = 0x40000000;
                if (!BitBlt(
                        memory,
                        0,
                        0,
                        bounds.Width,
                        bounds.Height,
                        screen,
                        bounds.Left,
                        bounds.Top,
                        SourceCopy | CaptureLayeredWindows))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Windows could not copy the composed GTA client pixels.");
                return Image.FromHbitmap(nativeBitmap);
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1))
                    SelectObject(memory, previous);
                DeleteObject(nativeBitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static LiveAcceptanceVisualFrameMetrics Measure(Bitmap image)
        {
            long samples = 0;
            long content = 0;
            long black = 0;
            long green = 0;
            long blue = 0;
            long white = 0;
            long darkGreen = 0;
            // Route identity is concentrated in Reactor's centered UI region.
            // Sampling that region keeps moving GTA world pixels from
            // inflating the classifier while supporting every client size.
            var left = (int)Math.Round(image.Width * 0.30d);
            var right = (int)Math.Round(image.Width * 0.70d);
            var top = (int)Math.Round(image.Height * 0.12d);
            var bottom = (int)Math.Round(image.Height * 0.88d);
            for (var y = top; y < bottom; y += 4)
            {
                for (var x = left; x < right; x += 4)
                {
                    var pixel = image.GetPixel(x, y);
                    samples++;
                    var isBlack = pixel.R <= 12 && pixel.G <= 12 && pixel.B <= 12;
                    if (isBlack) black++;
                    var isGreen = pixel.G > pixel.R + 15 && pixel.G > pixel.B + 5;
                    var isBlue = pixel.B > pixel.R + 30 && pixel.B > pixel.G + 15;
                    var isWhite = pixel.R > 220 && pixel.G > 220 && pixel.B > 220 &&
                        Math.Abs(pixel.R - pixel.G) < 35 &&
                        Math.Abs(pixel.G - pixel.B) < 35;
                    var isDarkGreen = pixel.R < 70 && pixel.G < 110 && pixel.B < 85 &&
                        pixel.G > pixel.R + 5 && pixel.G > pixel.B + 3;
                    if (isGreen) green++;
                    if (isBlue) blue++;
                    if (isWhite) white++;
                    if (isDarkGreen) darkGreen++;
                    if (isGreen || isBlue || isWhite || isDarkGreen) content++;
                }
            }
            if (samples == 0)
                throw new InvalidOperationException("The visual capture contained no pixels.");
            return new LiveAcceptanceVisualFrameMetrics(
                content / (double)samples,
                black / (double)samples,
                green / (double)samples,
                blue / (double)samples,
                white / (double)samples,
                darkGreen / (double)samples);
        }

        private static double ChangedFraction(Bitmap before, Bitmap after)
        {
            if (before.Width != after.Width || before.Height != after.Height) return 1.0d;
            long changed = 0;
            long samples = 0;
            for (var y = 0; y < before.Height; y += 6)
            {
                for (var x = 0; x < before.Width; x += 6)
                {
                    var left = before.GetPixel(x, y);
                    var right = after.GetPixel(x, y);
                    if (Math.Abs(left.R - right.R) +
                        Math.Abs(left.G - right.G) +
                        Math.Abs(left.B - right.B) > 32) changed++;
                    samples++;
                }
            }
            return samples == 0 ? 1.0d : changed / (double)samples;
        }

        private static JObject ToJson(LiveAcceptanceVisualFrameMetrics metrics) =>
            new JObject
            {
                ["contentFraction"] = metrics.ContentFraction,
                ["blackFraction"] = metrics.BlackFraction,
                ["greenFraction"] = metrics.GreenFraction,
                ["blueFraction"] = metrics.BlueFraction,
                ["whiteFraction"] = metrics.WhiteFraction,
                ["darkGreenFraction"] = metrics.DarkGreenFraction,
            };

        private static void RequireClientBounds(IntPtr window, out Rectangle bounds)
        {
            if (!LiveAcceptanceHarness.TryGetClientBounds(window, out bounds))
                throw new InvalidOperationException(
                    "The bound GTA client no longer has capturable bounds.");
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(
            IntPtr deviceContext,
            int width,
            int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(
            IntPtr destination,
            int destinationX,
            int destinationY,
            int width,
            int height,
            IntPtr source,
            int sourceX,
            int sourceY,
            uint operation);
    }
}
