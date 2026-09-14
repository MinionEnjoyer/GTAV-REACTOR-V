using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using RageWebUI.Core;
using RageWebUI.Runtime;
using ReactorV.BootstrapHost;

// Disposable offline target, never injected into GTA. Uses the actual runtime
// proxy in a secondary AppDomain and the unmodified external production host.
public sealed class ProviderFixtureWorker : MarshalByRefObject
{
    public int Run(string directory, string logs)
    {
        var timer = Stopwatch.StartNew();
        var processId = Process.GetCurrentProcess().Id;
        bool ready = false;
        while (timer.Elapsed.TotalSeconds < 35)
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(BootstrapHostNames.ReadyEvent(processId)))
                    ready = signal.WaitOne(100);
            }
            catch (WaitHandleCannotBeOpenedException) { }
            if (ready) break;
            Thread.Sleep(100);
        }
        if (!ready) throw new TimeoutException("External host readiness was not observed; no fallback attempted.");
        using (var runtime = new OverlayRuntime("windowed", IntPtr.Zero,
            Path.Combine(directory, "ui"), directory, logs, new BridgeBroker(),
            1024, 768, 30, false, false))
        {
            if (!runtime.Start() || runtime.RendererName != "Bootstrap WebView2")
                throw new InvalidOperationException("Fixture did not attach to the external runtime.");
            int generation;
            if (!runtime.TryGetReadyContentGeneration(out generation) || generation <= 0)
                throw new InvalidOperationException("No ready content generation.");
            runtime.SetVisible(false);
            // Rehearsal-only signal for the real controller-owned hang observer.
            // This means the provider attached; it is not a visible-menu claim.
            File.WriteAllText(Path.Combine(logs, "gameplay-ready.log"),
                DateTime.UtcNow.ToString("o") + " session=provider-fixture pid=" + processId +
                " elapsed_ms=1 source=script stage=diagnostic_tick_heartbeat elapsed_ms=1 story_ready=True playable=True browser_ready=True" + Environment.NewLine);
            // Keep the authenticated provider connection alive for observation.
            Thread.Sleep(6000);
            File.WriteAllText(Path.Combine(logs, "fixture-result.txt"),
                "PASS secondary_appdomain=" + (!AppDomain.CurrentDomain.IsDefaultAppDomain()) +
                " renderer=" + runtime.RendererName + " generation=" + generation);
        }
        return 0;
    }
    public override object InitializeLifetimeService() { return null; }
}

public static class ProviderFixture
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        AppDomain domain = null;
        try
        {
            Directory.CreateDirectory(args[1]);
            domain = AppDomain.CreateDomain("Reactor offline provider", null,
                new AppDomainSetup { ApplicationBase = args[0] });
            var worker = (ProviderFixtureWorker)domain.CreateInstanceFromAndUnwrap(
                Assembly.GetExecutingAssembly().Location, typeof(ProviderFixtureWorker).FullName);
            return worker.Run(args[0], args[1]);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (domain != null) AppDomain.Unload(domain); }
    }
}
