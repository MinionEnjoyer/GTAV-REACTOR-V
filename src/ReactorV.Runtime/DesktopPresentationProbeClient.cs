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
            string? error)
        {
            ReadableSampleCount = readableSampleCount;
            MatchingSampleCount = matchingSampleCount;
            IsConcrete = isConcrete;
            Source = source;
            Error = error;
        }

        internal int ReadableSampleCount { get; }
        internal int MatchingSampleCount { get; }
        internal bool IsConcrete { get; }
        internal string Source { get; }
        internal string? Error { get; }

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

        internal static async Task<DesktopPresentationProbeResult> VerifyAsync(
            string executablePath,
            Rectangle bounds,
            IReadOnlyList<DesktopPresentationProbeSample> samples,
            int timeoutMilliseconds)
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

                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                // Do not make the hard deadline depend on a ThreadPool timer.
                // ScriptHookVDotNet secondary domains and a contended WebView2
                // profile can briefly saturate ordinary worker callbacks. A
                // dedicated bounded waiter keeps the desktop witness fail-closed
                // even during that startup pressure.
                var exitedInTime = await Task.Factory.StartNew(
                    () => process.WaitForExit(timeoutMilliseconds),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
                if (!exitedInTime)
                {
                    TryKill(process);
                    process.WaitForExit(250);
                    return DesktopPresentationProbeResult.Failed("hard-timeout");
                }

                // WaitForExit after the Exited event guarantees redirected
                // stream pumps have observed the final child bytes.
                process.WaitForExit();
                var output = await standardOutput.ConfigureAwait(false);
                var errorOutput = await standardError.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    return DesktopPresentationProbeResult.Failed(
                        "preloader-exit-" +
                        process.ExitCode.ToString(CultureInfo.InvariantCulture) +
                        NormalizeDetail(errorOutput));
                }
                return ParseResult(output, samples.Count);
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
                var independentlyConcrete =
                    readable.Value == expectedSampleCount &&
                    matching.Value >= (expectedSampleCount * 3 + 3) / 4;
                return new DesktopPresentationProbeResult(
                    readable.Value,
                    matching.Value,
                    concrete.Value && independentlyConcrete &&
                        string.IsNullOrEmpty(error),
                    source!,
                    error);
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

        private static string NormalizeDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return string.Empty;
            var normalized = detail.Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (normalized.Length > 160)
                normalized = normalized.Substring(0, 160);
            return ":" + normalized;
        }

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
