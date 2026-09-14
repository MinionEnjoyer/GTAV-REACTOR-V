using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    internal readonly struct DesktopPresentationProbeSample
    {
        internal DesktopPresentationProbeSample(
            double normalizedX,
            double normalizedY,
            Color expected)
        {
            NormalizedX = normalizedX;
            NormalizedY = normalizedY;
            Expected = expected;
        }

        internal double NormalizedX { get; }
        internal double NormalizedY { get; }
        internal Color Expected { get; }
    }

    internal sealed class DesktopPresentationProbeResult
    {
        internal DesktopPresentationProbeResult(
            int readableSampleCount,
            int matchingSampleCount,
            bool isConcrete,
            string source,
            string? error,
            string observedRgb = "unavailable")
        {
            ReadableSampleCount = readableSampleCount;
            MatchingSampleCount = matchingSampleCount;
            IsConcrete = isConcrete;
            Source = source;
            Error = error;
            ObservedRgb = observedRgb;
        }

        internal int ReadableSampleCount { get; }
        internal int MatchingSampleCount { get; }
        internal bool IsConcrete { get; }
        internal string Source { get; }
        internal string? Error { get; }
        internal string ObservedRgb { get; }
        internal int ChildPid { get; private set; }
        internal long ElapsedMilliseconds { get; private set; }
        internal string LastChildStage { get; private set; } = "unobserved";
        internal long ChildStageMilliseconds { get; private set; }
        internal long FirstProgressMilliseconds { get; private set; } = -1;
        internal bool TerminationRequested { get; private set; }
        internal int? ChildExitCode { get; private set; }
        internal int AttemptCount { get; private set; } = 1;
        internal long TotalElapsedMilliseconds { get; private set; }

        internal DesktopPresentationProbeResult WithAttemptSummary(int attempts, long elapsed)
        {
            AttemptCount = attempts;
            TotalElapsedMilliseconds = elapsed;
            return this;
        }

        internal DesktopPresentationProbeResult WithDiagnostics(
            int pid, long elapsed, string stage, long stageMs, long firstProgressMs,
            bool terminationRequested, int? exitCode)
        {
            ChildPid = pid;
            ElapsedMilliseconds = elapsed;
            LastChildStage = stage;
            ChildStageMilliseconds = stageMs;
            FirstProgressMilliseconds = firstProgressMs;
            TerminationRequested = terminationRequested;
            ChildExitCode = exitCode;
            return this;
        }

        internal static DesktopPresentationProbeResult Failed(
            string error,
            string source = "preloader-process") =>
            new DesktopPresentationProbeResult(0, 0, false, source, error);
    }

    /// <summary>
    /// Runs desktop duplication outside the renderer process. A graphics-driver
    /// or duplication stall can therefore be terminated without wedging the
    /// overlay UI thread or its DirectComposition device.
    /// </summary>
    internal static class DesktopPresentationProbeClient
    {
        private const string ChildMode = "--desktop-presentation-probe";
        private const int ChannelTolerance = 56;
        private const int MaximumSamples = 128;
        private const int RequiredIdentitySampleCount = 8;
        private const int ChildStartupReserveMilliseconds = 250;
        private const int GdiAttemptBudgetMilliseconds = 350;
        private const int MinimumGdiProgressMilliseconds = 150;
        private const int MinimumFallbackBudgetMilliseconds = 200;

        internal static async Task<DesktopPresentationProbeResult> VerifyAsync(
            string executablePath,
            Rectangle bounds,
            IReadOnlyList<DesktopPresentationProbeSample> samples,
            int timeoutMilliseconds,
            Action<string, string?>? trace = null)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return DesktopPresentationProbeResult.Failed("missing-executable-path");
            if (!File.Exists(executablePath))
                return DesktopPresentationProbeResult.Failed("preloader-not-found");
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return DesktopPresentationProbeResult.Failed("invalid-bounds");
            if (samples == null ||
                samples.Count != RequiredIdentitySampleCount ||
                samples.Count > MaximumSamples)
                return DesktopPresentationProbeResult.Failed("invalid-sample-count");
            if (timeoutMilliseconds <= 0)
                return DesktopPresentationProbeResult.Failed("invalid-timeout");

            var wireSamples = new List<object>(samples.Count);
            foreach (var sample in samples)
            {
                if (!IsNormalized(sample.NormalizedX) ||
                    !IsNormalized(sample.NormalizedY))
                {
                    return DesktopPresentationProbeResult.Failed(
                        "invalid-sample-coordinate");
                }
                wireSamples.Add(new
                {
                    x = sample.NormalizedX,
                    y = sample.NormalizedY,
                    r = sample.Expected.R,
                    g = sample.Expected.G,
                    b = sample.Expected.B,
                });
            }

            var totalClock = Stopwatch.StartNew();
            var first = await RunAttemptAsync(executablePath, bounds, wireSamples,
                timeoutMilliseconds, "auto", trace)
                .ConfigureAwait(false);
            var remaining = timeoutMilliseconds - (int)totalClock.ElapsedMilliseconds;
            // A hung GDI call never reaches the child's exception fallback.
            // Only after that exact owned child has exited, give DXGI the
            // unused portion of the ORIGINAL budget. No parallel graphics
            // helpers, no longer timeout, no retry of a real pixel mismatch.
            if (first.Error != "hard-timeout" || !first.ChildExitCode.HasValue ||
                !first.LastChildStage.StartsWith("gdi-", StringComparison.Ordinal) ||
                remaining < MinimumFallbackBudgetMilliseconds)
                return first.WithAttemptSummary(1, totalClock.ElapsedMilliseconds);

            trace?.Invoke("webview_desktop_probe_backend_fallback",
                $"previous_child_pid={first.ChildPid} previous_stage={first.LastChildStage} " +
                $"backend=dxgi remaining_ms={remaining} original_budget_ms={timeoutMilliseconds}");
            var fallback = await RunAttemptAsync(executablePath, bounds, wireSamples,
                remaining, "dxgi", trace).ConfigureAwait(false);
            return fallback.WithAttemptSummary(2, totalClock.ElapsedMilliseconds);
        }

        private static async Task<DesktopPresentationProbeResult> RunAttemptAsync(
            string executablePath,
            Rectangle bounds,
            List<object> wireSamples,
            int timeoutMilliseconds,
            string backend,
            Action<string, string?>? trace)
        {
            var clock = Stopwatch.StartNew();
            var childTimeout = Math.Max(
                1,
                timeoutMilliseconds - ChildStartupReserveMilliseconds);
            var request = new
            {
                x = bounds.X,
                y = bounds.Y,
                w = bounds.Width,
                h = bounds.Height,
                s = wireSamples,
                t = ChannelTolerance,
                ms = childTimeout,
                backend,
            };
            var json = JsonConvert.SerializeObject(request, Formatting.None);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = ChildMode + " " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var progressLock = new object();
                var lastStage = "unobserved";
                long lastStageMs = 0;
                long firstProgressMs = -1;
                long firstGdiProgressMs = -1;
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!DesktopProbeProgress.TryParse(args.Data, out var stage, out var stageMs))
                        return;
                    lock (progressLock)
                    {
                        if (firstProgressMs < 0) firstProgressMs = clock.ElapsedMilliseconds;
                        if (firstGdiProgressMs < 0 && stage.StartsWith("gdi-", StringComparison.Ordinal))
                            firstGdiProgressMs = clock.ElapsedMilliseconds;
                        lastStage = stage;
                        lastStageMs = stageMs;
                    }
                };
                try
                {
                    if (!process.Start())
                        return DesktopPresentationProbeResult.Failed("preloader-start-failed");
                }
                catch (Exception error) when (
                    error is InvalidOperationException ||
                    error is System.ComponentModel.Win32Exception)
                {
                    return DesktopPresentationProbeResult.Failed(
                        "preloader-start-failed:" + error.GetType().Name);
                }

                using (var parent = Process.GetCurrentProcess())
                    trace?.Invoke("webview_desktop_probe_child_started",
                        $"child_pid={process.Id} parent_pid={parent.Id} " +
                        $"created_utc={process.StartTime.ToUniversalTime():o} timeout_ms={timeoutMilliseconds} backend={backend}");
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                process.BeginErrorReadLine();
                DesktopPresentationProbeResult Complete(DesktopPresentationProbeResult result, bool terminationRequested)
                {
                    lock (progressLock)
                    {
                        result.WithDiagnostics(process.Id, clock.ElapsedMilliseconds,
                            lastStage, lastStageMs, firstProgressMs, terminationRequested,
                            process.HasExited ? process.ExitCode : (int?)null);
                    }
                    trace?.Invoke("webview_desktop_probe_child_finished",
                        $"child_pid={result.ChildPid} elapsed_ms={result.ElapsedMilliseconds} " +
                        $"first_progress_ms={result.FirstProgressMilliseconds} last_stage={result.LastChildStage} " +
                        $"child_stage_ms={result.ChildStageMilliseconds} termination_requested={result.TerminationRequested} " +
                        $"exit_code={result.ChildExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unobserved"} " +
                        $"error={result.Error ?? "none"}");
                    return result;
                }
                // Do not make the hard deadline depend on a ThreadPool timer.
                // ScriptHookVDotNet secondary domains and a contended WebView2
                // profile can briefly saturate ordinary worker callbacks. A
                // dedicated bounded waiter keeps the desktop witness fail-closed
                // even during that startup pressure.
                var exitedInTime = await Task.Factory.StartNew(
                    () =>
                    {
                        while (true)
                        {
                            var deadline = (long)timeoutMilliseconds;
                            lock (progressLock)
                            {
                                // Shorten only a confirmed GDI wait, never slow
                                // process startup, parsing or the child's DXGI
                                // exception fallback. Late GDI entry gets some
                                // useful work time inside the original budget.
                                if (backend == "auto" && firstGdiProgressMs >= 0 &&
                                    lastStage.StartsWith("gdi-", StringComparison.Ordinal))
                                    deadline = Math.Min(deadline, Math.Max(GdiAttemptBudgetMilliseconds,
                                        firstGdiProgressMs + MinimumGdiProgressMilliseconds));
                            }
                            var remaining = (int)(deadline - clock.ElapsedMilliseconds);
                            if (remaining <= 0) return process.HasExited;
                            if (process.WaitForExit(Math.Min(25, remaining))) return true;
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
                if (!exitedInTime)
                {
                    TryKill(process);
                    process.WaitForExit(250);
                    return Complete(DesktopPresentationProbeResult.Failed("hard-timeout"), true);
                }

                // WaitForExit after the Exited event guarantees redirected
                // stream pumps have observed the final child bytes.
                process.WaitForExit();
                var output = await standardOutput.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    return Complete(DesktopPresentationProbeResult.Failed(
                        "preloader-exit-" +
                        process.ExitCode.ToString(CultureInfo.InvariantCulture)), false);
                }
                return Complete(ParseResult(output, RequiredIdentitySampleCount), false);
            }
        }

        private static DesktopPresentationProbeResult ParseResult(
            string output,
            int expectedSampleCount)
        {
            try
            {
                var json = JObject.Parse(output.Trim());
                var readable = json.Value<int?>("readable");
                var matching = json.Value<int?>("matching");
                var concrete = json.Value<bool?>("concrete");
                var source = json.Value<string>("source");
                var error = json.Value<string>("error");
                if (!readable.HasValue || !matching.HasValue ||
                    !concrete.HasValue || string.IsNullOrWhiteSpace(source) ||
                    readable.Value < 0 || matching.Value < 0 ||
                    matching.Value > readable.Value)
                {
                    return DesktopPresentationProbeResult.Failed("malformed-result");
                }

                // This client carries the complete eight-cell transfer
                // fingerprint, not generic page colours. Require every cell
                // to be readable and a three-quarter identity quorum so an
                // unrelated GTA frame cannot accidentally authorize input.
                var observedRgb = "unavailable";
                var observations = json["observedRgb"];
                if (observations != null && observations.Type != JTokenType.Null)
                {
                    if (!(observations is JArray pixels) || pixels.Count != expectedSampleCount ||
                        pixels.Count > MaximumSamples)
                        return DesktopPresentationProbeResult.Failed("invalid-pixel-diagnostics");
                    var values = new List<string>(pixels.Count);
                    foreach (var pixel in pixels)
                    {
                        if (pixel.Type == JTokenType.Null) { values.Add("missing"); continue; }
                        if (pixel.Type != JTokenType.Integer ||
                            !long.TryParse(pixel.ToString(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var rgb) || rgb < 0 || rgb > 0xffffff)
                            return DesktopPresentationProbeResult.Failed("invalid-pixel-diagnostics");
                        values.Add(rgb.ToString("X6", CultureInfo.InvariantCulture));
                    }
                    observedRgb = string.Join(",", values);
                }
                var independentlyConcrete =
                    readable.Value == expectedSampleCount &&
                    matching.Value >= (expectedSampleCount * 3 + 3) / 4;
                return new DesktopPresentationProbeResult(
                    readable.Value,
                    matching.Value,
                    concrete.Value && independentlyConcrete &&
                        string.IsNullOrEmpty(error),
                    source!,
                    error,
                    observedRgb);
            }
            catch (Exception error) when (
                error is JsonException || error is InvalidOperationException)
            {
                return DesktopPresentationProbeResult.Failed("invalid-json-result");
            }
        }

        private static bool IsNormalized(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) &&
            value >= 0d && value <= 1d;

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is System.ComponentModel.Win32Exception)
            {
                // The result remains fail closed even if the OS reports that
                // the process raced to completion while termination began.
            }
        }
    }
}
