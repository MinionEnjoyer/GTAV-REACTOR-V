using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ReactorV.Diagnostics.Tests
{
    public sealed class IntegrationSafetyTests
    {
        [Fact]
        public void Review_zip_excludes_dumps_and_journals_and_redacts_json_values()
        {
            using var area = new Area();
            var report = new JObject { ["path"] = area.Game + "\\scripts\\ReactorV", ["outside"] = @"C:\Users\someone\private" };
            DiagnosticIO.WriteJson(Path.Combine(area.Output, "report.json"), report);
            File.WriteAllText(Path.Combine(area.Output, "READ-FIRST.txt"), "Game: " + area.Game);
            File.WriteAllText(Path.Combine(area.Output, "isolation-state.json"), "DO NOT SHARE");
            var capture = Directory.CreateDirectory(Path.Combine(area.Output, "capture")).FullName;
            Directory.CreateDirectory(Path.Combine(capture, "private-dumps"));
            File.WriteAllText(Path.Combine(capture, "private-dumps", "secret.dmp"), "MEMORY");
            File.WriteAllText(Path.Combine(capture, "log.txt"), "Loaded from " + area.Game);
            DiagnosticRunner.MakeReviewZip(area.Output, area.Game);
            using var zip = ZipFile.OpenRead(Path.Combine(area.Output, "Review-before-sharing.zip"));
            Assert.Equal(3, zip.Entries.Count);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("dump") || e.FullName.Contains("state"));
            using var reader = new StreamReader(zip.GetEntry("report.json")!.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("someone", text);
            Assert.Contains("<GTA>", text);
            Assert.Contains("<USERPROFILE>", text);
        }

        [Fact]
        public void Human_summary_shows_actual_dependencies_and_never_turns_unknown_into_pass()
        {
            var report = new JObject { ["dependencies"] = new JObject {
                ["os64"] = true, ["process64"] = true, ["execution"] = "completed-child",
                ["probe"] = new JObject { ["webView2"] = new JObject { ["readiness"] = "available", ["version"] = "152.1.2.3" },
                    ["cef"] = new JArray(new JObject { ["path"] = "libcef.dll", ["readiness"] = "loaded-child-only" }) }
            } };
            string summary = DiagnosticRunner.Summary(report);
            Assert.Contains("WebView2: available; version 152.1.2.3", summary);
            Assert.Contains("libcef.dll: loaded-child-only", summary);
            Assert.Contains(".NET Framework >= 4.8 (64-bit registry): unknown / not measured", summary);
            Assert.Contains("Unknown/refused checks are NOT passes", summary);
        }

        [Fact]
        public void Output_refuses_game_tree_and_path_traversal_or_streams()
        {
            using var area = new Area();
            Assert.Throws<IOException>(() => DiagnosticIO.NewReportDirectory(Path.Combine(area.Game, "reports"), area.Game));
            foreach (var relative in new[] { "../bad", "a/../bad", "C:\\bad", "a:secret", "a//b", "a./b" })
                Assert.ThrowsAny<Exception>(() => DiagnosticIO.SafePath(area.Game, relative));
        }

        [Fact]
        public void Options_reject_unbounded_capture_or_unconsented_dump()
        {
            Assert.Throws<ArgumentException>(() => new DiagnosticOptions { RecordSeconds = int.MaxValue }.Validate());
            Assert.Throws<ArgumentException>(() => new DiagnosticOptions { DumpMode = "full", ProcDumpPath = "procdump64.exe" }.Validate());
            Assert.Throws<ArgumentException>(() => new DiagnosticOptions { Edition = "Online" }.Validate());
        }

        [Fact]
        public void Missing_game_stops_before_creating_report_or_mutating_files()
        {
            using var area = new Area();
            File.Delete(Path.Combine(area.Game, "GTA5_Enhanced.exe"));
            Assert.ThrowsAny<Exception>(() => DiagnosticRunner.Run("check", new DiagnosticOptions { GameDirectory = area.Game, OutputDirectory = area.Output }, CancellationToken.None, _ => { }));
            Assert.Empty(Directory.GetFiles(area.Output));
        }

        [Fact]
        public async Task Real_child_output_pipes_are_drained_together_without_deadlock()
        {
            using var process = Process.Start(new ProcessStartInfo(FindFixture(), "--large-output") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            try
            {
                var stdout = DependencyService.DrainBounded(process.StandardOutput);
                var stderr = DependencyService.DrainBounded(process.StandardError);
                var both = Task.WhenAll(stdout, stderr);
                Assert.Same(both, await Task.WhenAny(both, Task.Delay(10000)));
                Assert.True(process.WaitForExit(2000));
                Assert.Equal(0, process.ExitCode);
                Assert.Contains("[output truncated]", await stdout);
                Assert.Contains("[output truncated]", await stderr);
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
        }

        [Fact]
        public async Task Real_synthetic_process_is_correlated_and_its_new_startup_lines_are_retained()
        {
            using var area = new Area();
            string fixture = FindFixture();
            File.Copy(fixture, Path.Combine(area.Game, "GTA5_Enhanced.exe"), true);
            string logs = Directory.CreateDirectory(Path.Combine(area.Game, "scripts", "ReactorV")).FullName;
            File.WriteAllText(Path.Combine(logs, "ReactorV.NativeLifecycle.log"), "OLD SESSION DO NOT ATTRIBUTE\n");
            var armed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = new DiagnosticOptions { GameDirectory = area.Game, OutputDirectory = area.Output, WaitSeconds = 8, RecordSeconds = 8 };
            var capture = Task.Run(() => new CaptureService().Run(options, cancellation.Token, message => { if (message.StartsWith("Armed")) armed.TrySetResult(true); }));
            Assert.Same(armed.Task, await Task.WhenAny(armed.Task, Task.Delay(5000)));
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(area.Game, "GTA5_Enhanced.exe"), "3 -1073741819") { UseShellExecute = false, CreateNoWindow = true })!;
            var result = await capture;
            Assert.Equal("exited", (string?)result["outcome"]);
            Assert.Equal(process.Id, (int?)result["process"]?["pid"]);
            Assert.Equal(-1073741819, (int?)result["process"]?["exitCode"]);
            string all = string.Join("\n", Directory.EnumerateFiles(Path.Combine(area.Output, "capture", "raw"), "*.txt").Select(File.ReadAllText));
            Assert.Contains("synthetic_fixture_tick", all);
            Assert.DoesNotContain("OLD SESSION DO NOT ATTRIBUTE", all);
        }

        private static string FindFixture()
        {
            for (DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            {
                string path = Path.Combine(current.FullName, "fixtures", "FixtureProcess", "bin", "Release", "net48", "GTA5_Enhanced.exe");
                if (File.Exists(path)) return path;
            }
            throw new InvalidOperationException("Build the synthetic FixtureProcess project before running integration tests. It is never packaged.");
        }

        private sealed class Area : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "reactor-diagnostic-integration-" + Guid.NewGuid().ToString("N"));
            public string Game { get; }
            public string Output { get; }
            public Area()
            {
                Game = Directory.CreateDirectory(Path.Combine(Root, "game")).FullName;
                Output = Directory.CreateDirectory(Path.Combine(Root, "reports")).FullName;
                File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "synthetic placeholder");
            }
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }
}
