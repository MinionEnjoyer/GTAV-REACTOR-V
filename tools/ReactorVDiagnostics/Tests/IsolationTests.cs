using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReactorV.Diagnostics;
using Xunit;

namespace ReactorV.Diagnostics.Tests
{
    [CollectionDefinition("Isolation", DisableParallelization = true)]
    public sealed class IsolationCollection { }

    [Collection("Isolation")]
    public sealed class IsolationTests
    {
        [Fact]
        public void Preview_is_read_only_and_lists_only_allowlisted_native_files()
        {
            using var fixture = new Fixture();
            var before = fixture.Read("ReactorV.RenderHook.asi");

            var preview = IsolationService.Preview(fixture.Options, CancellationToken.None, _ => { });

            Assert.Equal(4, ((JArray)preview["actions"]).Count);
            Assert.Equal(before, fixture.Read("ReactorV.RenderHook.asi"));
            Assert.Contains("on-disk absence alone", preview["verification"].ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Apply_requires_consent_and_an_exact_release_manifest()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = false;
            Assert.Throws<InvalidOperationException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));
            Assert.Throws<InvalidOperationException>(() => IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { }));

            fixture.Options.Consent = true;
            fixture.Write("ReactorV.Bootstrap.asi", "changed");
            Assert.Throws<InvalidDataException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));
            Assert.True(File.Exists(fixture.GamePath("ReactorV.Bootstrap.asi")));
        }

        [Fact]
        public void Apply_and_restore_move_only_authenticated_allowlist_and_restore_is_idempotent()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;

            var applied = IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { });

            Assert.Equal("applied", (string)applied["status"]);
            Assert.All(Fixture.Paths, relative => Assert.False(File.Exists(fixture.GamePath(relative))));
            Assert.Equal("unrelated", fixture.Read("unrelated.dll"));
            Assert.True(File.Exists(fixture.Options.IsolationStatePath));

            var restored = IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { });
            Assert.Equal("restored", (string)restored["status"]);
            Assert.All(Fixture.Paths, relative => Assert.True(File.Exists(fixture.GamePath(relative))));
            var again = IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { });
            Assert.Equal("restored", (string)again["status"]);
        }

        [Fact]
        public void Restore_preserves_a_conflicting_destination()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { });
            fixture.Write("ReactorV.RenderHook.asi", "conflict");

            Assert.Throws<IOException>(() => IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { }));
            Assert.Equal("conflict", fixture.Read("ReactorV.RenderHook.asi"));
        }

        [Fact]
        public void Tampered_state_and_reparse_targets_fail_closed()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            fixture.WriteState(new JObject {
                ["schemaVersion"] = 1, ["kind"] = "reactor-native-hook-isolation", ["status"] = "isolated",
                ["gameRoot"] = fixture.Game, ["backupRoot"] = fixture.Backup,
                ["files"] = new JArray(new JObject { ["path"] = "../outside.dll", ["sha256"] = fixture.Hash("x"), ["length"] = 1 })
            });
            Assert.Throws<InvalidDataException>(() => IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { }));

            using var reparse = new Fixture();
            var target = Path.Combine(reparse.Root, "outside-reactor");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "RageWebUI.Native.dll"), "hook-plugins/ReactorV/RageWebUI.Native.dll");
            var link = Path.Combine(reparse.Game, "plugins", "ReactorV");
            Directory.Delete(link, true);
            using var process = Process.Start(new ProcessStartInfo {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            process.WaitForExit();
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, "Unable to create temporary junction: " + output);
            Assert.Throws<IOException>(() => IsolationService.Preview(reparse.Options, CancellationToken.None, _ => { }));
            Directory.Delete(link);
        }

        [Fact]
        public void Existing_journal_is_never_overwritten_and_no_actions_is_not_applied()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            var applied = IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { });
            IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { });
            Assert.Throws<InvalidOperationException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));

            using var absent = new Fixture();
            absent.Options.Consent = true;
            foreach (var path in Fixture.Paths) File.Delete(absent.GamePath(path));
            var noActions = IsolationService.Apply(absent.Options, CancellationToken.None, _ => { });
            Assert.Equal("noActions", (string)noActions["status"]);
            Assert.False((bool)noActions["nativeIsolationApplied"]);
            Assert.False(File.Exists(absent.Options.IsolationStatePath));
        }

        [Fact]
        public void Tampered_backup_root_and_duplicate_journal_records_fail_closed()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { });
            var state = JObject.Parse(File.ReadAllText(fixture.Options.IsolationStatePath));
            state["backupRoot"] = fixture.Backup;
            fixture.WriteState(state);
            Assert.Throws<InvalidDataException>(() => IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { }));

            using var duplicate = new Fixture();
            duplicate.Options.Consent = true;
            IsolationService.Apply(duplicate.Options, CancellationToken.None, _ => { });
            var duplicateState = JObject.Parse(File.ReadAllText(duplicate.Options.IsolationStatePath));
            var files = (JArray)duplicateState["files"];
            files.Add(files[0].DeepClone());
            duplicate.WriteState(duplicateState);
            Assert.Throws<InvalidDataException>(() => IsolationService.Restore(duplicate.Options, CancellationToken.None, _ => { }));
        }

        [Fact]
        public void Cancelled_or_interrupted_transaction_can_only_be_recovered_by_explicit_restore()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => IsolationService.Apply(fixture.Options, cancellation.Token, message => cancellation.Cancel()));
            Assert.Throws<InvalidOperationException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));

            var recovery = IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { });
            Assert.Equal("restored", (string)recovery["status"]);
            Assert.All(Fixture.Paths, relative => Assert.True(File.Exists(fixture.GamePath(relative))));
        }

        [Fact]
        public void Running_game_and_concurrent_mutator_are_rejected()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            IsolationService.GameRunningGuard = _ => true;
            Assert.Throws<InvalidOperationException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));
            IsolationService.GameRunningGuard = _ => false;

            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var calls = 0;
            IsolationService.GameRunningGuard = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
                return false;
            };
            var first = Task.Run(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));
            release.Set();
            Assert.Equal("applied", (string)first.GetAwaiter().GetResult()["status"]);
        }

        [Fact]
        public void Game_starting_mid_apply_stops_before_the_next_move_and_is_recoverable()
        {
            using var fixture = new Fixture();
            fixture.Options.Consent = true;
            var probes = 0;
            IsolationService.GameRunningGuard = _ => Interlocked.Increment(ref probes) >= 4;

            Assert.Throws<InvalidOperationException>(() => IsolationService.Apply(fixture.Options, CancellationToken.None, _ => { }));
            Assert.True(File.Exists(fixture.Options.IsolationStatePath));
            IsolationService.GameRunningGuard = _ => false;
            IsolationService.Restore(fixture.Options, CancellationToken.None, _ => { });
            Assert.All(Fixture.Paths, relative => Assert.True(File.Exists(fixture.GamePath(relative))));
        }

        private sealed class Fixture : IDisposable
        {
            public static readonly string[] Paths = { "ReactorV.RenderHook.asi", "ReactorV.Bootstrap.asi", "ReactorV.ScriptProbe.asi", "plugins/ReactorV/RageWebUI.Native.dll" };
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "ReactorVIsolationTests", Guid.NewGuid().ToString("N"));
            public string Game { get; }
            public string Backup { get; }
            public DiagnosticOptions Options { get; }
            private string Manifest { get; }

            public Fixture()
            {
                Game = Path.Combine(Root, "game"); Backup = Path.Combine(Root, "backups"); Manifest = Path.Combine(Root, "release.json");
                Directory.CreateDirectory(Game); Directory.CreateDirectory(Backup);
                File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "MZ");
                foreach (var path in Paths) Write(path, "hook-" + path);
                Write("unrelated.dll", "unrelated");
                var files = new JArray();
                foreach (var path in Paths) files.Add(new JObject { ["path"] = path, ["sha256"] = Hash(Read(path)), ["length"] = new FileInfo(GamePath(path)).Length });
                File.WriteAllText(Manifest, new JObject { ["schemaVersion"] = 1, ["releaseVersion"] = "0.2.4", ["edition"] = "Enhanced", ["files"] = files }.ToString());
                Options = new DiagnosticOptions { GameDirectory = Game, OutputDirectory = Backup, Edition = "Enhanced", ManifestPath = Manifest, IsolationStatePath = Path.Combine(Backup, "isolation-state.json") };
                IsolationService.GameRunningGuard = _ => false;
            }
            public string GamePath(string relative) => System.IO.Path.Combine(Game, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            public void Write(string relative, string content) { var path = GamePath(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)); File.WriteAllText(path, content); }
            public string Read(string relative) => File.ReadAllText(GamePath(relative));
            public string Hash(string value) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
            public void WriteState(JObject state) => File.WriteAllText(Options.IsolationStatePath, state.ToString());
            public void Dispose()
            {
                IsolationService.GameRunningGuard = _ => false;
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
        }
    }
}
