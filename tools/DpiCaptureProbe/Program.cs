// Off-screen real WebView2 regression. Does not launch GTA or change display settings.
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

internal sealed class ProbeForm : Form
{
    private readonly string runtimePath, output;
    private readonly StreamWriter log;
    private object? host;
    private Type? hostType;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
    }

    internal ProbeForm(string runtime, string directory)
    {
        runtimePath = runtime;
        output = directory;
        Directory.CreateDirectory(output);
        // Never overwrite earlier evidence.
        log = new StreamWriter(new FileStream(Path.Combine(output, "results.txt"), FileMode.CreateNew))
            { AutoFlush = true };
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ClientSize = new Size(3440, 1440);
        ShowInTaskbar = false;
        Shown += async (_, __) => await RunProbe();
    }

    private object? Call(string method, params object[] args) =>
        hostType!.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(host, args);

    private async Task RunProbe()
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                var name = new AssemblyName(e.Name).Name!;
                if (!name.StartsWith("RageWebUI.", StringComparison.Ordinal) && name != "Newtonsoft.Json") return null;
                var path = Path.Combine(Path.GetDirectoryName(runtimePath)!, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var assembly = Assembly.LoadFrom(runtimePath);
            hostType = assembly.GetType("RageWebUI.Runtime.CompositionWebViewHost", true)!;
            var policy = assembly.GetType("ReactorV.WebView2Host.OverlayPresentationPolicy", true)!
                .GetMethod("CaptureSizeMatchesTarget", BindingFlags.Static | BindingFlags.NonPublic);
            Action<string, string?> trace = (stage, detail) => log.WriteLine(stage + " " + detail);
            var constructor = hostType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(Form), typeof(Action<string, string>) }, null);
            host = constructor != null ? constructor.Invoke(new object[] { this, trace })
                : Activator.CreateInstance(hostType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { this }, CultureInfo.InvariantCulture)!;
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(output, "webview-profile"),
                new CoreWebView2EnvironmentOptions("--disable-gpu --disable-gpu-compositing", "en-US"));
            await (Task)Call("EnsureCoreWebView2Async", environment)!;
            var controller = (CoreWebView2CompositionController)hostType.GetField("_controller",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            log.WriteLine("runtime=" + environment.BrowserVersionString + " reactor=" + assembly.GetName().Version +
                " mvid=" + assembly.ManifestModule.ModuleVersionId + " bounds_mode=" + controller.BoundsMode);
            var navigation = new TaskCompletionSource<bool>();
            controller.CoreWebView2.NavigationCompleted += (_, e) => navigation.TrySetResult(e.IsSuccess);
            controller.CoreWebView2.NavigateToString("<!doctype html><html><head><style>html,body{margin:0;width:100%;height:100%;background:#182c3e;color:white}</style></head><body>Reactor capture regression</body></html>");
            if (await Task.WhenAny(navigation.Task, Task.Delay(10000)) != navigation.Task || !await navigation.Task)
                throw new TimeoutException("Navigation did not finish");
            controller.ShouldDetectMonitorScaleChanges = false;
            int failures = 0, cases = 0;
            foreach (var size in new[] { new Size(3440, 1440), new Size(1919, 1079), new Size(2560, 1440) })
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 3.5 })
            {
                ClientSize = size;
                controller.RasterizationScale = scale;
                Call("SynchronizeBounds");
                Call("RebindRootVisual");
                await Task.Delay(250);
                var pngTask = (Task<byte[]>)Call("CapturePreviewAsync")!;
                if (await Task.WhenAny(pngTask, Task.Delay(5000)) != pngTask)
                    throw new TimeoutException("Capture timed out at scale " + scale);
                using var stream = new MemoryStream(await pngTask);
                using var picture = Image.FromStream(stream);
                var exact = picture.Width == size.Width && picture.Height == size.Height;
                var accepted = policy == null ? exact : (bool)policy.Invoke(null,
                    new object[] { picture.Width, picture.Height, size.Width, size.Height,
                        scale, controller.RasterizationScale })!;
                ++cases;
                if (!accepted) ++failures;
                log.WriteLine("scale=" + scale.ToString(CultureInfo.InvariantCulture) +
                    " target=" + size.Width + "x" + size.Height +
                    " capture=" + picture.Width + "x" + picture.Height +
                    " exact=" + exact + " accepted=" + accepted);
            }
            log.WriteLine("COMPLETE cases=" + cases + " rejected=" + failures);
            if (failures != 0) Environment.ExitCode = 2;
        }
        catch (Exception e) { log.WriteLine("FAILED " + e); Environment.ExitCode = 1; }
        finally
        {
            (host as IDisposable)?.Dispose();
            log.Dispose();
            Close();
        }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) { Environment.ExitCode = 64; return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ProbeForm(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])));
    }
}
