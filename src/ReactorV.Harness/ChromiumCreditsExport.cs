using System;
using System.IO;
using System.Text;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;

namespace RageWebUI.Harness
{
    // Build-only: export notices from the exact CEF binary being redistributed.
    // No game, bridge authority, UI ownership, or consumer content is involved.
    internal static class ChromiumCreditsExport
    {
        public static int Run(string output)
        {
            output = Path.GetFullPath(output);
            var runtime = AppDomain.CurrentDomain.BaseDirectory;
            var cache = Path.Combine(Path.GetTempPath(), "ReactorV-Credits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cache);
            var settings = new CefSettings {
                BrowserSubprocessPath = Path.Combine(runtime, "CefSharp.BrowserSubprocess.exe"),
                ResourcesDirPath = runtime, LocalesDirPath = Path.Combine(runtime, "locales"),
                RootCachePath = cache, CachePath = Path.Combine(cache, "profile"),
                LogFile = Path.Combine(cache, "cef.log"),
                WindowlessRenderingEnabled = true, MultiThreadedMessageLoop = true,
            };
            settings.CefCommandLineArgs["disable-background-networking"] = "1";
            settings.CefCommandLineArgs["disable-component-update"] = "1";
            settings.CefCommandLineArgs["disable-gpu"] = "1";
            try {
                var init = Cef.InitializeAsync(settings, true, null);
                if (!init.Wait(TimeSpan.FromSeconds(30)) || !init.Result)
                    throw new InvalidOperationException("Credits CEF initialization failed.");
                using (var loaded = new ManualResetEventSlim())
                using (var browser = new ChromiumWebBrowser("chrome://credits/", automaticallyCreateBrowser: false)) {
                    browser.FrameLoadEnd += (_, e) => {
                        if (e.Frame.IsMain) { Console.WriteLine("Credits page: " + e.Url); loaded.Set(); }
                    };
                    browser.CreateBrowser();
                    if (!loaded.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Chromium credits did not load.");
                    // textContent includes collapsed license blocks; innerText does not.
                    var read = browser.EvaluateScriptAsync("document.documentElement.textContent");
                    if (!read.Wait(TimeSpan.FromSeconds(15)) || !read.Result.Success || !(read.Result.Result is string text) ||
                        text.Length < 100_000 || !text.Contains("Chromium") || !text.Contains("Redistribution"))
                        throw new InvalidDataException("Chromium credits export was incomplete.");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.WriteAllText(output, text, new UTF8Encoding(false));
                    Console.WriteLine("Chromium bundled credits exported: " + text.Length + " characters.");
                }
                return 0;
            } finally {
                if (Cef.IsInitialized == true) Cef.Shutdown();
                // Only our unique temporary profile; never the user's browser or GTA.
                try { Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
