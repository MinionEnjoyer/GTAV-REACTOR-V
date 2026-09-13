using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using ReactorV.Diagnostics;
using Xunit;

namespace ReactorV.Diagnostics.Tests
{
    public sealed class PreflightTests
    {
        [Fact]
        public void Good_manifest_confirms_only_the_selected_reactor_file()
        {
            using var fixture = new Fixture();
            fixture.WriteGame("plugins/ReactorV/RageWebUI.Native.dll", "good");
            fixture.WriteGame("unrelated.txt", "do-not-hash");
            fixture.WriteManifest(new JObject { ["path"] = "plugins/ReactorV/RageWebUI.Native.dll", ["sha256"] = fixture.Hash("good"), ["length"] = 4 });

            var result = PreflightService.Run(fixture.Options, CancellationToken.None, _ => { });

            Assert.Single((JArray)result["confirmed"]);
            Assert.Empty((JArray)result["missing"]);
            Assert.Empty((JArray)result["mismatch"]);
            Assert.True(File.Exists(Path.Combine(fixture.Output, "ReactorV-preflight.json")));
        }

        [Fact]
        public void Missing_and_mismatched_files_are_separate()
        {
            using var fixture = new Fixture();
            fixture.WriteGame("plugins/ReactorV/RageWebUI.Native.dll", "actual");
            fixture.WriteManifest(
                new JObject { ["path"] = "plugins/ReactorV/RageWebUI.Native.dll", ["sha256"] = fixture.Hash("wanted"), ["length"] = 6 },
                new JObject { ["path"] = "scripts/ReactorV/RageWebUI.Script.dll", ["sha256"] = fixture.Hash("none"), ["length"] = 4 });

            var result = PreflightService.Run(fixture.Options, CancellationToken.None, _ => { });

            Assert.Single((JArray)result["mismatch"]);
            Assert.Single((JArray)result["missing"]);
        }

        [Theory]
        [InlineData("../escape.dll")]
        [InlineData("plugins/ReactorV/../RageWebUI.Native.dll")]
        public void Traversal_manifest_is_rejected(string path)
        {
            using var fixture = new Fixture();
            fixture.WriteManifest(new JObject { ["path"] = path, ["sha256"] = fixture.Hash("x"), ["length"] = 1 });
            Assert.ThrowsAny<Exception>(() => PreflightService.Run(fixture.Options, CancellationToken.None, _ => { }));
        }

        [Fact]
        public void Duplicate_paths_are_rejected_case_insensitively()
        {
            using var fixture = new Fixture();
            fixture.WriteManifest(
                new JObject { ["path"] = "plugins/ReactorV/a.dll", ["sha256"] = fixture.Hash("a"), ["length"] = 1 },
                new JObject { ["path"] = "plugins/reactorv/A.dll", ["sha256"] = fixture.Hash("a"), ["length"] = 1 });
            Assert.Throws<InvalidDataException>(() => PreflightService.Run(fixture.Options, CancellationToken.None, _ => { }));
        }

        [Fact]
        public void Custom_manifest_cannot_hash_a_non_reactor_game_file()
        {
            using var fixture = new Fixture();
            fixture.WriteGame("GTA5_Enhanced.exe", "MZ-pretend-game");
            fixture.WriteManifest(new JObject { ["path"] = "GTA5_Enhanced.exe", ["sha256"] = fixture.Hash("MZ-pretend-game"), ["length"] = 15 });
            Assert.Throws<InvalidDataException>(() => PreflightService.Run(fixture.Options, CancellationToken.None, _ => { }));
        }

        [Fact]
        public void Junction_manifest_target_is_rejected()
        {
            using var fixture = new Fixture();
            var target = Path.Combine(fixture.Root, "outside");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "target.dll"), "x");
            var junction = Path.Combine(fixture.Game, "plugins", "ReactorV", "linked");
            Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
            var command = new ProcessStartInfo {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /c mklink /J \"" + junction + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(command)!;
            process.WaitForExit();
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, "Unable to create temporary junction: " + output);
            try
            {
                fixture.WriteManifest(new JObject { ["path"] = "plugins/ReactorV/linked/target.dll", ["sha256"] = fixture.Hash("x"), ["length"] = 1 });
                Assert.Throws<IOException>(() => PreflightService.Run(fixture.Options, CancellationToken.None, _ => { }));
            }
            finally { if (Directory.Exists(junction)) Directory.Delete(junction); }
        }

        [Fact]
        public void Invalid_manifest_schema_is_rejected_before_any_game_file_is_read()
        {
            using var fixture = new Fixture();
            File.WriteAllText(fixture.Manifest, "{\"schemaVersion\":99}");
            Assert.Throws<InvalidDataException>(() => PreflightService.Run(fixture.Options, CancellationToken.None, _ => { }));
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"schemaVersion\":1,\"releaseVersion\":\"0.2.4\",\"edition\":\"Enhanced\",\"packageSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"files\":[null]}")]
        public void Malformed_manifest_is_rejected(string contents)
        {
            using var fixture = new Fixture();
            File.WriteAllText(fixture.Manifest, contents);
            Assert.ThrowsAny<Exception>(() => PreflightService.Run(fixture.Options, CancellationToken.None, _ => { }));
        }

        [Fact]
        public void Security_evidence_no_matches_is_not_an_exoneration()
        {
            using var fixture = new Fixture();
            var start = DateTime.UtcNow.AddMinutes(1);
            var result = SecurityEvidence.Collect(start, start.AddMinutes(1), fixture.Game);
            Assert.True((bool?)result["noRelevantEventsFound"] ?? false);
            Assert.Contains("does not establish", (string?)result["limitation"] ?? "");
        }

        [Fact]
        public void Security_evidence_rejects_an_inverted_time_window()
        {
            using var fixture = new Fixture();
            Assert.Throws<ArgumentException>(() => SecurityEvidence.Collect(DateTime.UtcNow, DateTime.UtcNow.AddSeconds(-1), fixture.Game));
        }

        [Fact]
        public void Security_evidence_rejects_a_window_over_24_hours()
        {
            using var fixture = new Fixture();
            var start = DateTime.UtcNow;
            Assert.Throws<ArgumentException>(() => SecurityEvidence.Collect(start, start.AddHours(24).AddTicks(1), fixture.Game));
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "ReactorVDiagnosticsTests", Guid.NewGuid().ToString("N"));
            public string Root => _root;
            public string Game { get; }
            public string Output { get; }
            public string Manifest { get; }
            public DiagnosticOptions Options { get; }

            public Fixture()
            {
                Game = Path.Combine(_root, "game"); Output = Path.Combine(_root, "out"); Manifest = Path.Combine(_root, "manifest.json");
                Directory.CreateDirectory(Game); Directory.CreateDirectory(Output);
                File.WriteAllBytes(Path.Combine(Game, "GTA5_Enhanced.exe"), new byte[] { 0x4d, 0x5a });
                Options = new DiagnosticOptions { GameDirectory = Game, OutputDirectory = Output, Edition = "Enhanced", ManifestPath = Manifest };
            }
            public void WriteGame(string relative, string contents)
            {
                var path = Path.Combine(Game, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, contents);
            }
            public string Hash(string text)
            {
                using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
            public void WriteManifest(params JObject[] files)
            {
                File.WriteAllText(Manifest, new JObject { ["schemaVersion"] = 1, ["releaseVersion"] = "0.2.4", ["edition"] = "Enhanced", ["packageSha256"] = new string('a', 64), ["files"] = new JArray(files) }.ToString());
            }
            public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        }
    }
}
