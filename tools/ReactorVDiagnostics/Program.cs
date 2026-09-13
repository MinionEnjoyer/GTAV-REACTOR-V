using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ReactorV.Diagnostics
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0 || (args.Length == 1 && args[0] == "--gui"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new DiagnosticsForm());
                return 0;
            }
            try
            {
                if (args[0] == "--help" || args[0] == "-h") { Console.WriteLine(Help); return 0; }
                if (args[0] == "--render-ui-test" && (args.Length == 2 || args.Length == 4))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using var form = new DiagnosticsForm();
                    if (args.Length == 4)
                    {
                        var width = int.Parse(args[2]);
                        var height = int.Parse(args[3]);
                        if (width < form.MinimumSize.Width || height < form.MinimumSize.Height)
                            throw new ArgumentException("UI render size is below the supported minimum.");
                        form.Size = new System.Drawing.Size(width, height);
                    }
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new System.Drawing.Point(-32000, -32000);
                    form.ShowInTaskbar = false; form.Opacity = 0;
                    form.Show(); form.PerformLayout(); Application.DoEvents();
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    form.Close();
                    return 0;
                }
                if (args[0] == "--dependency-probe")
                {
                    if (args.Length != 4) throw new ArgumentException("Invalid private probe arguments.");
                    var probe = new DiagnosticOptions { GameDirectory = args[1], ManifestPath = args[2], Edition = args[3] };
                    Console.WriteLine(DependencyService.RunProbe(probe).ToString(Newtonsoft.Json.Formatting.None));
                    return 0;
                }
                var options = Parse(args);
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                var result = DiagnosticRunner.Run(args[0], options, cancellation.Token, Console.WriteLine);
                Console.WriteLine(DiagnosticRunner.Summary(result));
                return (string?)result["status"] == "cancelled" ? 130 : (string?)result["status"] == "collection-complete" ? 0 : 2;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
        }

        internal static DiagnosticOptions Parse(string[] args)
        {
            var result = new DiagnosticOptions {
                OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReactorV-Diagnostics")
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i];
                if (!seen.Add(key)) throw new ArgumentException("Duplicate option: " + key);
                if (key == "--consent") { result.Consent = true; continue; }
                if (key == "--security-events") { result.IncludeSecurityEvents = true; continue; }
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                string value = args[i];
                switch (key)
                {
                    case "--game": result.GameDirectory = value; break;
                    case "--output": result.OutputDirectory = value; break;
                    case "--edition": result.Edition = value; break;
                    case "--manifest": result.ManifestPath = value; break;
                    case "--wait": result.WaitSeconds = int.Parse(value); break;
                    case "--seconds": result.RecordSeconds = int.Parse(value); break;
                    case "--dump": result.DumpMode = value; break;
                    case "--procdump": result.ProcDumpPath = value; break;
                    case "--state": result.IsolationStatePath = value; break;
                    default: throw new ArgumentException("Unknown option: " + key);
                }
            }
            result.Validate();
            if (string.IsNullOrWhiteSpace(result.ManifestPath)) result.ManifestPath = DefaultManifest(result.Edition);
            return result;
        }

        internal static string DefaultManifest(string edition) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manifests", "0.2.4-" + edition.ToLowerInvariant() + ".json");

        internal const string Help = @"Reactor V Diagnostics 0.1.0 (local-only; reference release 0.2.4)
Double-click for the graphical interface, or:
  ReactorV.Diagnostics.exe check --game ""D:\Games\GTA Enhanced""
  ReactorV.Diagnostics.exe record --game ""D:\Games\GTA Enhanced"" --wait 120 --seconds 180
  ReactorV.Diagnostics.exe isolate-preview --game ""D:\Games\GTA Enhanced""
  ReactorV.Diagnostics.exe isolate --game ""D:\Games\GTA Enhanced"" --consent
  ReactorV.Diagnostics.exe restore --game ""D:\Games\GTA Enhanced"" --state ""<original run>\isolation-state.json"" --consent
Options: --edition Enhanced|Legacy; --output <outside-game directory>; --manifest <reference JSON>
         --security-events (related events only); --wait 1..600; --seconds 1..900
         --dump none|mini|full --procdump <Microsoft procdump64.exe> --consent
Capture never launches GTA. Close it, arm capture, then start Story Mode yourself.
Dumps need separate consent, may contain private memory and are excluded from sharing ZIPs.
Isolation moves only four Reactor native files into a journaled backup; expect missing UI.
Keep the original run folder until restored. Nothing uploads or changes security settings.
Exit 0 = collection completed, NOT game healthy; 2 = blocked/error; 130 = cancelled.";
    }
}
