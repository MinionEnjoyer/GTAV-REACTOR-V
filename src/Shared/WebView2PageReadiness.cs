using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ReactorV.WebView2Host
{
    /// <summary>
    /// Waits for the bundled UI to publish a concrete first-paint marker.
    /// ExecuteScriptAsync serializes a returned Promise rather than awaiting
    /// it, so browser-side async work must complete before a synchronous marker
    /// is read back by the host.
    /// </summary>
    internal static class WebView2PageReadiness
    {
        private const string Probe = @"
            (() => {
              if (!window.__reactorVPageReady) return null;
              const navigation = performance.getEntriesByType('navigation')[0];
              return {
                readyState: document.readyState,
                performanceNow: Math.round(performance.now()),
                domContentLoaded: Math.round(navigation?.domContentLoadedEventEnd || 0),
                loadEvent: Math.round(navigation?.loadEventEnd || 0),
                imageCount: document.images?.length || 0,
                rootChildren: document.getElementById('root')?.childElementCount || 0,
                resources: performance.getEntriesByType('resource').slice(0, 12).map((entry) => ({
                  name: entry.name.split('/').pop(),
                  start: Math.round(entry.startTime),
                  duration: Math.round(entry.duration),
                  bytes: entry.transferSize || 0
                }))
              };
            })();";

        public static async Task<string> WaitAsync(
            CoreWebView2 core,
            TimeSpan timeout)
        {
            if (core == null)
            {
                throw new ArgumentNullException(nameof(core));
            }
            return await WebView2ReadinessPolicy.WaitForMarkerAsync(
                () => core.ExecuteScriptAsync(Probe),
                timeout,
                TimeSpan.FromMilliseconds(50));
        }
    }
}
