using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ReactorV.Issue1Isolation
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 3 && args[0] == "--windowed-probe") return WindowedProbe.Run(args[1], args[2]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--render-ui")
            {
                Isolation.SafePath(args[1]);
                if (File.Exists(args[1])) return 2;
                using var window = new TestWindow();
                window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(-32000, -32000);
                window.ShowInTaskbar = false;
                window.Show(); window.PerformLayout(); Application.DoEvents();
                using var bitmap = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); return 0;
            }
            Application.Run(new TestWindow()); return 0;
        }
    }

    internal sealed class TestWindow : Form
    {
        private readonly TextBox path = new TextBox { Width = 510, ReadOnly = true };
        private readonly TextBox status = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private readonly ComboBox outcome = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly Timer timer = new Timer { Interval = 500 };
        private Isolation? isolation;
        private SessionCapture? capture;

        public TestWindow()
        {
            Text = "Reactor V — Issue #1 isolation test";
            Size = new Size(780, 510); MinimumSize = new Size(780, 510);
            Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 6, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Diagnostic test, not a crash fix. Requires the issue1 diagnostic patch.\nClose GTA and the Reactor preloader before preparing or restoring.\nKeep this helper open, test one launch, save its ZIP, then Restore. No game launch or upload is automatic." });
            var select = new FlowLayoutPanel { Dock = DockStyle.Fill };
            select.Controls.Add(path); select.Controls.Add(Button("Choose GTA folder…", Choose)); layout.Controls.Add(select);
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill };
            modes.Controls.Add(Button("1 · Managed providers OFF", () => Prepare(Isolation.ProvidersOff)));
            modes.Controls.Add(Button("2 · Reactor native OFF", () => Prepare(Isolation.NativeOff)));
            modes.Controls.Add(Button("Restore originals", Restore)); layout.Controls.Add(modes);
            var export = new FlowLayoutPanel { Dock = DockStyle.Fill };
            outcome.Items.AddRange(new object[] { "Choose observed outcome…", "Crashed", "Story Mode loaded", "Did not reach Story Mode", "Manual exit / other" });
            outcome.SelectedIndex = 0; export.Controls.Add(outcome);
            export.Controls.Add(Button("Save result ZIP…", Export)); layout.Controls.Add(export);
            layout.Controls.Add(status);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Backups stay outside GTA under LocalAppData · No deletion of backups · No Install/Repair during a test" });
            Controls.Add(layout);
            timer.Tick += (_, __) => {
                if (capture == null || capture.Finished) return;
                try { capture.Poll(); status.Text = capture.Status; }
                catch (Exception error) { timer.Stop(); status.Text = "Capture could not be verified: " + error.Message + "\r\nRestore the test when GTA/preloader are closed."; }
            };
            FormClosing += (_, e) => {
                if (isolation == null) return;
                try
                {
                    var r = isolation.Current();
                    if (r != null && r.Status != "Restored" && MessageBox.Show(this,
                        "A test is still active. Closing does NOT restore files. Reopen this helper, choose the same GTA folder, and click Restore. Close anyway?",
                        "Test still active", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
                }
                catch (Exception error) { MessageBox.Show(this, "Retain the backup folder. " + error.Message); }
            };
        }

        private Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 34 };
            button.Click += (_, __) => {
                try { action(); }
                catch (Exception error) { status.Text = error.Message; MessageBox.Show(this, error.Message, "Test not applied / needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            return button;
        }

        private void Choose()
        {
            if (isolation?.Current() is Receipt previous && previous.Status != "Restored")
                throw new InvalidOperationException("Restore the selected installation before selecting another one.");
            using var dialog = new FolderBrowserDialog { Description = "Choose the folder containing GTA5_Enhanced.exe", ShowNewFolderButton = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReactorV-Issue1-Isolation");
            isolation = new Isolation(dialog.SelectedPath, store, SessionCapture.RequireStopped);
            path.Text = isolation.Root;
            var r = isolation.Current();
            status.Text = r != null && r.Status != "Restored"
                ? "Existing test: " + r.Mode + " / " + r.Status + ". Click Restore before preparing a fresh capture.\r\nBackups: " + isolation.RunDirectory(r)
                : "Choose mode 1 first. Other mods and settings remain unchanged. The helper will monitor one game launch after preparation.";
        }

        private void Prepare(string mode)
        {
            if (isolation == null) throw new InvalidOperationException("Choose the GTA Enhanced folder first.");
            var version = FileVersionInfo.GetVersionInfo(isolation.Target("GTA5_Enhanced.exe"));
            if (version.FileMajorPart != 1 || version.FileMinorPart != 0 || version.FileBuildPart != 1158 || version.FilePrivatePart != 13)
                throw new InvalidOperationException("This case-specific helper is qualified only for GTA Enhanced 1.0.1158.13.");
            var description = mode == Isolation.ProvidersOff
                ? "Temporarily move ALLIN1.dll and RageWebUI.Script.dll outside GTA. Reactor's native ASIs, preloader and ScriptHookVDotNet remain enabled. Other managed scripts remain enabled.\n\nThe Reactor/ALLIN1 menus will be unavailable."
                : "Temporarily move ReactorV.Bootstrap.asi, ReactorV.RenderHook.asi, ReactorV.ScriptProbe.asi and RageWebUI.Native.dll outside GTA. Set ReactorV.json's renderer to windowed; preserve and later restore its exact original bytes.\n\nManaged Reactor/ALLIN1 remain enabled. The usual preloader is absent; the test uses the WebView2 window fallback.";
            if (MessageBox.Show(this, description + "\n\nBackups are hash-checked. Prepare this test?", "Confirm diagnostic isolation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var receipt = isolation.Prepare(mode);
            try { capture?.Dispose(); capture = new SessionCapture(isolation, receipt); }
            catch { isolation.Restore(); throw; }
            outcome.SelectedIndex = 0; status.Text = capture.Status + "\r\nBackups: " + isolation.RunDirectory(receipt); timer.Start();
        }

        private void Restore()
        {
            if (isolation == null) throw new InvalidOperationException("Choose the GTA folder used for the test.");
            if (capture != null && !capture.Finished) { capture.Poll(); if (!capture.Finished) SessionCapture.RequireStopped(); }
            isolation.Restore(); timer.Stop();
            status.Text = "Original files restored and SHA-256 verified. Backups retained. " +
                (capture?.Finished == true ? "You can still save this launch's result ZIP." : "Prepare a mode to begin a fresh test.");
        }

        private void Export()
        {
            if (capture?.Finished != true) throw new InvalidOperationException("No completed captured launch yet. Keep the helper open during the game test.");
            if (outcome.SelectedIndex <= 0) throw new InvalidOperationException("Choose what you observed in game first.");
            using var dialog = new SaveFileDialog { Filter = "ZIP archive|*.zip", FileName = "ReactorV-issue1-result-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            capture.Export(dialog.FileName, (string)outcome.SelectedItem);
            status.Text = "Saved " + dialog.FileName + "\r\nReview logs before sharing. No upload occurred. Restore the original files before another mode.";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); capture?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
