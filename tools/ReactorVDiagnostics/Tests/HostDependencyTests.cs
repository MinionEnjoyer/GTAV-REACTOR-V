using System;
using System.IO;
using System.Threading;
using ReactorV.Diagnostics;
using Xunit;

namespace ReactorV.Diagnostics.Tests
{
    public sealed class HostDependencyTests
    {
        [Fact]
        public void Missing_asi_loader_is_unknown_not_an_asserted_failure()
        {
            using var fixture = new Fixture();
            var report = HostDependencyService.Run(fixture.Options, CancellationToken.None, _ => { });
            Assert.Equal("not-observed-alternate-loader-possible", (string)report["asiLoader"]["dinput8"]["status"]);
            Assert.Contains("unknown", (string)report["compatibility"]["edition"]);
        }

        [Fact]
        public void Native_presence_is_observed_without_loading_the_plugin()
        {
            using var fixture = new Fixture();
            File.WriteAllBytes(Path.Combine(fixture.Game, "ScriptHookV.dll"), new byte[] { 1, 2, 3 });
            var report = HostDependencyService.Run(fixture.Options, CancellationToken.None, _ => { });
            Assert.Equal("observed", (string)report["scriptHookV"]["status"]);
            Assert.Equal("unknown-or-not-x64-pe", (string)report["scriptHookV"]["architecture"]);
        }

        [Fact]
        public void Managed_api_is_read_as_assembly_metadata_without_assembly_load()
        {
            using var fixture = new Fixture();
            var destination = Path.Combine(fixture.Game, "ScriptHookVDotNet3.dll");
            File.Copy(typeof(HostDependencyTests).Assembly.Location, destination);
            var report = HostDependencyService.Run(fixture.Options, CancellationToken.None, _ => { });
            Assert.Equal("observed", (string)report["scriptHookVdotNetApi3"]["status"]);
            Assert.NotEqual("unknown", (string)report["scriptHookVdotNetApi3"]["version"]);
            Assert.Equal("unknown-metadata-only", (string)report["scriptHookVdotNetApi3"]["compatibility"]);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "ReactorVHostDependencyTests", Guid.NewGuid().ToString("N"));
            public string Game { get; }
            public DiagnosticOptions Options { get; }
            public Fixture()
            {
                Game = Path.Combine(_root, "game"); Directory.CreateDirectory(Game);
                File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "MZ");
                Options = new DiagnosticOptions { GameDirectory = Game, OutputDirectory = _root, Edition = "Enhanced" };
            }
            public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        }
    }
}
