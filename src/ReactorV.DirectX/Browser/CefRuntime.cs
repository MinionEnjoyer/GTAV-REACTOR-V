using System;
using System.IO;
using CefSharp;
using CefSharp.OffScreen;

namespace RageWebUI.DirectX.Browser
{
    internal static class CefRuntime
    {
        private static readonly object Sync = new object();
        private static bool _initialized;

        public static void EnsureInitialized(string runtimeDirectory, string cacheDirectory)
        {
            lock (Sync)
            {
                if (_initialized || Cef.IsInitialized == true)
                {
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

                if (!Cef.Initialize(settings, performDependencyCheck: true))
                {
                    throw new InvalidOperationException("CEF failed to initialize. See the RageWebUI CEF log for details.");
                }
                _initialized = true;
            }
        }
    }
}

