using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ReactorV.WebView2Host
{
    /// <summary>
    /// Pure page-marker polling policy. The production adapter supplies the
    /// WebView2 script call; tests supply deterministic marker sequences.
    /// </summary>
    internal static class WebView2ReadinessPolicy
    {
        internal static async Task<string> WaitForMarkerAsync(
            Func<Task<string>> readMarker,
            TimeSpan timeout,
            TimeSpan pollInterval)
        {
            if (readMarker == null)
            {
                throw new ArgumentNullException(nameof(readMarker));
            }
            if (timeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            if (pollInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pollInterval));
            }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                var result = await readMarker();
                if (!string.IsNullOrWhiteSpace(result) &&
                    !string.Equals(result, "null", StringComparison.Ordinal) &&
                    !string.Equals(result, "{}", StringComparison.Ordinal))
                {
                    return result;
                }
                if (pollInterval > TimeSpan.Zero)
                {
                    await Task.Delay(pollInterval);
                }
                else
                {
                    await Task.Yield();
                }
            }

            throw new TimeoutException(
                "The ReactorV page did not publish its ready marker before timeout.");
        }
    }
}
