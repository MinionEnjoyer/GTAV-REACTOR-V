using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal sealed class GameplayHangFixture : Form
{
    protected override bool ShowWithoutActivation { get { return true; } }
    [STAThread]
    static int Main(string[] args)
    {
        try {
            if (args.Length != 2) return 2;
            var mode=args[0]; var log=args[1];
            if (mode != "hang" && mode != "pre-ready-hang" && mode != "responsive" && mode != "transient" && mode != "short") return 3;
            using (var form=new GameplayHangFixture()) {
                form.Text="ReactorV owned hang test (auto-close)";
                form.Width=320; form.Height=90; form.ShowInTaskbar=false;
                form.FormBorderStyle=FormBorderStyle.FixedToolWindow;
                var timer=new System.Windows.Forms.Timer { Interval=4000 };
                form.Shown += delegate {
                    var pid=Process.GetCurrentProcess().Id;
                    File.WriteAllText(log, DateTime.UtcNow.ToString("o")+" session=fixture pid="+pid+" elapsed_ms=1 source=script stage="+
                        (mode=="pre-ready-hang" ? "construction_begin" : "diagnostic_tick_heartbeat elapsed_ms=1 story_ready=True playable=True browser_ready=True")+Environment.NewLine);
                    // No further script heartbeat, even while the UI responds.
                    timer.Start();
                };
                timer.Tick += delegate {
                    timer.Stop();
                    if (mode=="hang" || mode=="pre-ready-hang") {
                        File.AppendAllText(log, "fixture_stage=hang-begin"+Environment.NewLine);
                        Thread.Sleep(mode=="hang" ? 16000 : 7000);
                        File.AppendAllText(log, "fixture_stage=hang-end"+Environment.NewLine);
                        form.Close();
                    } else if (mode=="transient") {
                        Thread.Sleep(1000); timer.Interval=7000;
                        timer.Tick += delegate { form.Close(); }; timer.Start();
                    } else if (mode=="short") { form.Close(); }
                    else { timer.Interval=7000; timer.Tick += delegate { form.Close(); }; timer.Start(); }
                };
                Application.Run(form); timer.Dispose();
            }
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
