using System;
using System.Diagnostics;
using System.IO;
using CefSharp;
using CefSharp.OffScreen;
using RageWebUI.Core;

namespace RageWebUI.DirectX.Browser
{
    internal static class CefRuntime
    {
        private static readonly object Sync = new object();
        private static bool _initialized;
        private static GpuAdapterLuid? _initializedAdapterLuid;

        public static void EnsureInitialized(
            string runtimeDirectory,
            string cacheDirectory,
            GpuAdapterLuid? adapterLuid = null)
        {
            lock (Sync)
            {
                if (_initialized || Cef.IsInitialized == true)
                {
                    // CEF's GPU process and ANGLE device are process-global.
                    // Once initialized, silently accepting a different or
                    // unknown adapter would recreate the exact cross-adapter
                    // shared-handle failure this route is designed to avoid.
                    if (adapterLuid.HasValue &&
                        (!_initializedAdapterLuid.HasValue ||
                         _initializedAdapterLuid.Value != adapterLuid.Value))
                    {
                        throw new InvalidOperationException(
                            "CEF was initialized without the authoritative GTA " +
                            "adapter or with a different adapter. The external " +
                            "GPU path cannot be enabled in this process.");
                    }
                    _initialized = true;
                    return;
                }

                Directory.CreateDirectory(cacheDirectory);
                var settings = new CefSettings
                {
                    BrowserSubprocessPath = Path.Combine(runtimeDirectory, "CefSharp.BrowserSubprocess.exe"),
                    ResourcesDirPath = runtimeDirectory,
                    LocalesDirPath = Path.Combine(runtimeDirectory, "locales"),
                    RootCachePath = cacheDirectory,
                    CachePath = Path.Combine(cacheDirectory, "profile"),
                    LogFile = Path.Combine(cacheDirectory, "cef.log"),
                    LogSeverity = LogSeverity.Warning,
                    WindowlessRenderingEnabled = true,
                    MultiThreadedMessageLoop = true,
                    BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0),
                };
                settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
                settings.CefCommandLineArgs["disable-background-networking"] = "1";
                settings.CefCommandLineArgs["disable-component-update"] = "1";
                settings.CefCommandLineArgs["disable-features"] = "Translate,MediaRouter";
                if (adapterLuid.HasValue)
                {
                    settings.CefCommandLineArgs["use-angle"] = "d3d11";
                    settings.CefCommandLineArgs["use-adapter-luid"] =
                        adapterLuid.Value.ToCefCommandLineValue();
                }

                // Cef.Initialize returns after the browser process starts, but before
                // IBrowserProcessHandler.OnContextInitialized is guaranteed to run.
                // Creating a RequestContext/browser during that interval leaves
                // CefSharp to execute CreateBrowser inline from the context callback.
                // Under repeated cold starts that raced inside libcef.dll.  The
                // CefSharp async initializer is the supported context-readiness
                // barrier; wait for it here before OffscreenBrowser creates any CEF
                // object.  MultiThreadedMessageLoop keeps the CEF UI thread separate,
                // so this main-thread wait does not block context initialization.
                var initializationTimer = Stopwatch.StartNew();
                var initialization = Cef.InitializeAsync(
                    settings,
                    performDependencyCheck: true,
                    browserProcessHandler: null);
                if (!initialization.GetAwaiter().GetResult())
                {
                    throw new InvalidOperationException("CEF failed to initialize. See the RageWebUI CEF log for details.");
                }
                // Commit process-global identity before optional trace I/O.
                // CEF is already live at this point and must never later be
                // mistaken for an unpinned runtime if diagnostics fail.
                _initializedAdapterLuid = adapterLuid;
                _initialized = true;
                StartupTrace.Write(
                    Path.GetDirectoryName(cacheDirectory) ?? cacheDirectory,
                    "reactorv-runtime.log",
                    "directx",
                    "cef_context_initialized",
                    $"duration_ms={initializationTimer.Elapsed.TotalMilliseconds:F3}");
            }
        }
    }
}
