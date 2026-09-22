using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using RageWebUI.Core;

internal static class Program
{
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly Runtime = Assembly.Load("RageWebUI.Runtime");

    [STAThread]
    private static int Main()
    {
        try
        {
            var refs = new WeakReference[12];
            for (var i = 0; i < refs.Length; i++) refs[i] = DisposeWindow(i % 2 == 0);
            for (var i = 0; i < 3; i++)
            {
                Application.DoEvents();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            foreach (var reference in refs)
                if (reference.IsAlive) throw new Exception("Disposed overlay retained by timer/event ownership.");
            DisposeBeforeStart();
            Console.WriteLine("PASS: 12 closed/direct-disposed windows collected; dispose-before-start leaves no UI thread/window.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposeWindow(bool close)
    {
        var type = Runtime.GetType("RageWebUI.Runtime.OverlayWindow", true);
        var hidden = false;
        var window = (Form)Activator.CreateInstance(type, new object[] {
            IntPtr.Zero, (uint)Process.GetCurrentProcess().Id,
            AppDomain.CurrentDomain.BaseDirectory, Path.GetTempPath(), new BridgeBroker(), false, false,
            new Action<string,string>((s,d) => { }), new Action<bool>(v => hidden = !v),
            new Action(() => { }), new Action(() => { }), new Action<Exception>(e => { }), null, null
        });
        var reference = new WeakReference(window);
        if (close) { var handle = window.Handle; window.Close(); }
        else window.Dispose(); // Does not raise FormClosed: historically leaked timers/browser.
        window.Dispose(); // Idempotent.
        if (!hidden || !(bool)type.GetField("_windowResourcesReleased", Fields).GetValue(window))
            throw new Exception("Window resources/visibility were not retired.");
        return reference;
    }

    private static void DisposeBeforeStart()
    {
        var type = Runtime.GetType("RageWebUI.Runtime.WindowedOverlaySession", true);
        var session = Activator.CreateInstance(type, new object[] {
            IntPtr.Zero, AppDomain.CurrentDomain.BaseDirectory, Path.GetTempPath(), new BridgeBroker(), false, false
        });
        ((IDisposable)session).Dispose();
        type.GetMethod("Start").Invoke(session, null);
        var thread = (Thread)type.GetField("_uiThread", Fields).GetValue(session);
        if (!thread.Join(2000) || type.GetField("_window", Fields).GetValue(session) != null)
            throw new Exception("Disposed session created or retained a window.");
    }
}
