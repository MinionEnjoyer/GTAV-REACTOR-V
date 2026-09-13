using System;
using System.IO;
using System.Threading;
using ReactorV.Diagnostics;
using Xunit;

namespace ReactorV.Diagnostics.Tests
{
    public sealed class CaptureTests
    {
        [Theory]
        [InlineData("raw/log-0.txt", true)]
        [InlineData("raw\\log-0.txt", true)]
        [InlineData("../outside.txt", false)]
        [InlineData("raw/../outside.txt", false)]
        [InlineData("C:\\outside.txt", false)]
        [InlineData("", false)]
        public void Relative_artifact_paths_cannot_escape_the_capture_directory(string value, bool expected)
        {
            Assert.Equal(expected, CaptureService.IsSafeRelative(value));
        }

        [Fact]
        public void ProcDump_arguments_are_fixed_and_quote_the_destination()
        {
            var arguments = CaptureService.BuildProcDumpArguments(42, "private-dumps/crash.dmp", "mini");
            Assert.Equal("-mm -e 42 \"private-dumps/crash.dmp\"", arguments);
            Assert.DoesNotContain("-accepteula", arguments, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Target_identity_requires_the_exact_selected_executable_path()
        {
            Assert.True(CaptureService.IsExactExecutablePath("C:\\Games\\GTA\\GTA5_Enhanced.exe", "c:\\games\\gta\\GTA5_Enhanced.exe"));
            Assert.False(CaptureService.IsExactExecutablePath("C:\\Games\\Other\\GTA5_Enhanced.exe", "C:\\Games\\GTA\\GTA5_Enhanced.exe"));
            Assert.False(CaptureService.IsExactExecutablePath("C:\\Games\\GTA\\GTA5.exe", "C:\\Games\\GTA\\GTA5_Enhanced.exe"));
        }

        [Theory]
        [InlineData(0, "private-dumps/a.dmp", "mini")]
        [InlineData(42, "C:\\outside.dmp", "mini")]
        [InlineData(42, "private-dumps/a.dmp", "anything")]
        public void ProcDump_rejects_unbounded_or_injected_inputs(int pid, string path, string mode)
        {
            Assert.ThrowsAny<ArgumentException>(() => CaptureService.BuildProcDumpArguments(pid, path, mode));
        }

        [Theory]
        [InlineData("procdump", true)]
        [InlineData("procdump64", true)]
        [InlineData("procdump64a", true)]
        [InlineData("procdump64b", false)]
        [InlineData("not-procdump", false)]
        public void ProcDump_guard_covers_all_supported_executable_names(string name, bool expected)
        {
            Assert.Equal(expected, CaptureService.IsProcDumpProcessName(name));
        }

        [Fact]
        public void Cancellation_and_timeout_are_distinct_capture_outcomes()
        {
            using (var fixture = new WaitingFixture())
            {
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    var result = new CaptureService().Run(fixture.Options, cancelled.Token, _ => { });
                    Assert.Equal("cancelled-waiting", (string)result["outcome"]);
                }
                var timedOut = new CaptureService().Run(fixture.Options, CancellationToken.None, _ => { });
                Assert.Equal("timeout-waiting", (string)timedOut["outcome"]);
            }
        }

        [Fact]
        public void Rotation_rule_collects_from_zero_instead_of_a_stale_offset()
        {
            Assert.True(CaptureService.IsRotation(400, "old", 20, "old"));
            Assert.True(CaptureService.IsRotation(400, "old", 600, "new"));
            Assert.False(CaptureService.IsRotation(400, "same", 600, "same"));
        }

        private sealed class WaitingFixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "ReactorVDiagnosticsCaptureTests", Guid.NewGuid().ToString("N"));
            public DiagnosticOptions Options { get; }
            public WaitingFixture()
            {
                var game = Path.Combine(_root, "game"); var output = Path.Combine(_root, "output");
                Directory.CreateDirectory(game); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(game, "GTA5_Enhanced.exe"), "synthetic placeholder; never executed");
                Options = new DiagnosticOptions { GameDirectory = game, OutputDirectory = output, WaitSeconds = 1, RecordSeconds = 1 };
            }
            public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        }
    }
}
