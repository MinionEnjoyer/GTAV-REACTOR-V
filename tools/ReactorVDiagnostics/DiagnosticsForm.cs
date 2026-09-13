using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReactorV.Diagnostics
{
    internal sealed class DiagnosticsForm : Form
    {
        private readonly TextBox game = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox output = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox edition = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly NumericUpDown wait = new NumericUpDown { Minimum = 1, Maximum = 600, Value = 120, Width = 90 };
        private readonly NumericUpDown duration = new NumericUpDown { Minimum = 1, Maximum = 900, Value = 180, Width = 90 };
        private readonly CheckBox security = new CheckBox { AutoSize = true, Text = "Include related Windows security events (may be unavailable without elevation)" };
        private readonly ComboBox dump = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly TextBox procDump = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, MinimumSize = new Size(0, 100), Font = new Font("Consolas", 10) };
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        private readonly Button stop = new Button { Text = "Stop recording", AutoSize = true, Enabled = false };
        private readonly Button open = new Button { Text = "Open last report", AutoSize = true, Enabled = false };
        private readonly Label status = new Label { Text = "Ready. Select the actual GTA folder, then check the installation.", AutoSize = true, Dock = DockStyle.Fill };
        private CancellationTokenSource? running;
        private string? lastOutput;

        public DiagnosticsForm()
        {
            Text = "Reactor V Diagnostics · 0.1.0";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1040, 800);
            MinimumSize = new Size(860, 680);
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReactorV-Diagnostics");
            edition.Items.AddRange(new object[] { "Enhanced", "Legacy" }); edition.SelectedIndex = 0;
            dump.Items.AddRange(new object[] { "none", "mini", "full" }); dump.SelectedIndex = 0;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            var title = new Label { AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold), Text = "One installation. One test session. Reviewable evidence.", Margin = new Padding(0, 0, 0, 10) };
            layout.Controls.Add(title);
            // Keep the table's minimum preferred width below the supported
            // 860px form width; a fixed 960px label made the right settings
            // column (including Browse) render off-screen.
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), Text = "Local-only · Release reference 0.2.4 · No uploads, automatic repairs or security changes. Dependency loads run in a separate helper. Use Story Mode only.", Margin = new Padding(0, 0, 0, 12) });

            var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 5 };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            for (int row = 0; row < 5; row++) settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AddPath(settings, 0, "GTA folder", game, false);
            AddPath(settings, 1, "Report folder", output, false);
            settings.Controls.Add(new Label { Text = "Edition", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            var timings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            timings.Controls.Add(edition); timings.Controls.Add(new Label { Text = "Wait (sec)", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }); timings.Controls.Add(wait);
            timings.Controls.Add(new Label { Text = "Record (sec)", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }); timings.Controls.Add(duration);
            settings.Controls.Add(timings, 1, 2); settings.SetColumnSpan(timings, 2);
            settings.Controls.Add(new Label { Text = "Crash dump", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
            var dumpRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true };
            dumpRow.Controls.Add(dump); dumpRow.Controls.Add(new Label { Text = "Optional; private memory, never added to the review ZIP", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
            settings.Controls.Add(dumpRow, 1, 3); settings.SetColumnSpan(dumpRow, 2);
            AddPath(settings, 4, "ProcDump EXE", procDump, true);
            layout.Controls.Add(settings);
            layout.Controls.Add(security);

            AddAction("Check", "check");
            AddAction("Record launch", "record");
            AddAction("Preview native-off", "isolate-preview");
            AddAction("Apply native-off…", "isolate");
            AddAction("Restore…", "restore");
            layout.Controls.Add(actions);
            layout.Controls.Add(log);
            var footer = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            footer.Controls.Add(stop); footer.Controls.Add(open);
            stop.Click += (_, __) => { running?.Cancel(); status.Text = "Stopping safely; please wait for the collector to detach."; };
            open.Click += (_, __) => { if (lastOutput != null && Directory.Exists(lastOutput)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lastOutput) { UseShellExecute = true }); };
            layout.Controls.Add(footer);
            layout.Controls.Add(status);
            FormClosing += (_, e) => {
                if (running == null) return;
                running.Cancel(); e.Cancel = true;
                status.Text = "Stopping safely. Close again after recording finishes.";
            };
        }

        private void AddPath(TableLayoutPanel panel, int row, string label, TextBox box, bool file)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            panel.Controls.Add(box, 1, row);
            var browse = new Button { Text = "Browse…", AutoSize = true, Anchor = AnchorStyles.Left };
            browse.Click += (_, __) => {
                if (running != null) return;
                if (file) { using var dialog = new OpenFileDialog { Filter = "ProcDump executable|procdump64.exe;procdump.exe|Executables|*.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; }
                else { using var dialog = new FolderBrowserDialog { SelectedPath = box.Text, Description = label }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; }
            };
            panel.Controls.Add(browse, 2, row);
        }

        private void AddAction(string label, string mode)
        {
            var button = new Button { Text = label, AutoSize = true, Padding = new Padding(4) };
            button.Click += async (_, __) => await Run(mode);
            actions.Controls.Add(button);
        }

        private async Task Run(string mode)
        {
            if (running != null) return;
            var options = new DiagnosticOptions {
                GameDirectory = game.Text, OutputDirectory = output.Text, Edition = edition.Text,
                ManifestPath = Program.DefaultManifest(edition.Text), IncludeSecurityEvents = security.Checked,
                WaitSeconds = (int)wait.Value, RecordSeconds = (int)duration.Value,
                DumpMode = mode == "record" ? dump.Text : "none", ProcDumpPath = procDump.Text
            };
            if (mode == "restore")
            {
                using var dialog = new OpenFileDialog { Title = "Select the ORIGINAL isolation-state.json", Filter = "Restore journal|isolation-state.json|JSON|*.json", CheckFileExists = true };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                options.IsolationStatePath = dialog.FileName;
            }
            if (mode == "isolate" || mode == "restore")
            {
                string prompt = mode == "isolate"
                    ? "Close GTA and any launcher doing installation/repair first. Move ONLY ReactorV.RenderHook.asi, ReactorV.Bootstrap.asi, ReactorV.ScriptProbe.asi and plugins/ReactorV/RageWebUI.Native.dll into an external backup? Reactor UI may stop working. Keep the report folder and use Restore originals afterward."
                    : "Close GTA first. Restore the four original Reactor native files from this journal? Changed destination files will NOT be overwritten.";
                if (MessageBox.Show(this, prompt, "Confirm reversible game-file change", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                options.Consent = true;
            }
            if (options.DumpMode != "none")
            {
                if (MessageBox.Show(this, "Capture one game-process dump with your Microsoft ProcDump? Dumps can contain private memory and full dumps can consume many GB. They stay outside the sharing ZIP. Obtain ProcDump and accept its license yourself before recording. No security settings will be changed.", "Consent to private dump capture", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                options.Consent = true;
            }
            running = new CancellationTokenSource();
            var cancellation = running;
            actions.Enabled = false; stop.Enabled = true; open.Enabled = false; log.Clear();
            try
            {
                var report = await Task.Run(() => DiagnosticRunner.Run(mode, options, cancellation.Token, text => {
                    if (!IsDisposed) BeginInvoke(new Action(() => { log.AppendText(text + Environment.NewLine); status.Text = text; }));
                }));
                lastOutput = options.OutputDirectory;
                log.AppendText(Environment.NewLine + DiagnosticRunner.Summary(report));
                status.Text = "Finished: " + report["status"] + ". Review the report; completion does not mean crash-free.";
            }
            catch (Exception error) { log.AppendText(error.Message + Environment.NewLine); status.Text = "Stopped: " + error.Message; }
            finally { running = null; cancellation.Dispose(); actions.Enabled = true; stop.Enabled = false; open.Enabled = lastOutput != null; }
        }
    }
}
