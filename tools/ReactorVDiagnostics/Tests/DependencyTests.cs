using System;
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
    public sealed class DependencyTests
    {
        [Fact]
        public void Pe_architecture_and_api_set_imports_are_classified_without_loading()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file, Pe(0x014c));
                Assert.False(DependencyService.IsX64Pe(file));
                File.WriteAllBytes(file, Pe(0x8664));
                Assert.True(DependencyService.IsX64Pe(file));
                Assert.Equal("api-set-contract-not-direct-missing", DependencyService.ImportClassification("api-ms-win-core-file-l1-1-0.dll"));
                Assert.Equal("runtime-import", DependencyService.ImportClassification("VCRUNTIME140.dll"));
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void Pe_import_parser_reads_import_descriptors_without_loading_the_module()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file, PeWithImport("api-ms-win-core-file-l1-1-0.dll"));
                Assert.Contains("api-ms-win-core-file-l1-1-0.dll", DependencyService.ParsePeImports(file).Values<string>());
                File.WriteAllBytes(file, PeWithImport("missing-transitive.dll"));
                Assert.Contains("missing-transitive.dll", DependencyService.ParsePeImports(file).Values<string>());
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void Framework_release_threshold_is_explicit()
        {
            Assert.False(DependencyService.IsFramework48Release(528039));
            Assert.True(DependencyService.IsFramework48Release(528040));
            var deadline = DateTime.UtcNow;
            Assert.True(DependencyService.ProbeTimedOut(deadline, deadline));
            Assert.False(DependencyService.ProbeTimedOut(deadline, deadline.AddMilliseconds(-1)));
        }

        [Fact]
        public async Task Bounded_drain_consumes_large_output_without_retaining_an_unbounded_report()
        {
            var payload = new string('x', 300 * 1024);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var captured = await DependencyService.DrainBounded(reader);
            Assert.Contains("[output truncated]", captured);
            Assert.True(captured.Length < payload.Length);
            Assert.True(captured.Length <= (256 * 1024) + 32);
        }

        [Theory]
        [InlineData("123.4.5.6", false)]
        [InlineData("123.4.5.6 beta", true)]
        [InlineData("dev-channel", true)]
        public void Preview_runtime_labels_do_not_count_as_production_evergreen(string label, bool preview)
        {
            Assert.Equal(preview, DependencyService.IsPreviewRuntimeLabel(label));
        }

        [Fact]
        public void Hash_mismatch_refuses_the_child_probe_in_test_host()
        {
            var root = Path.Combine(Path.GetTempPath(), "ReactorVDependencyTests", Guid.NewGuid().ToString("N"));
            try
            {
                var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
                File.WriteAllText(Path.Combine(game, "GTA5_Enhanced.exe"), "MZ");
                var file = Path.Combine(game, "libcef.dll"); File.WriteAllText(file, "changed");
                var manifest = Path.Combine(root, "release.json");
                File.WriteAllText(manifest, new JObject { ["schemaVersion"] = 1, ["edition"] = "Enhanced", ["releaseVersion"] = "0.2.4", ["files"] = new JArray(new JObject { ["path"] = "libcef.dll", ["sha256"] = Hash("expected"), ["length"] = 8 }) }.ToString());
                var result = DependencyService.Run(new DiagnosticOptions { GameDirectory = game, OutputDirectory = root, ManifestPath = manifest, Edition = "Enhanced" }, CancellationToken.None, _ => { });
                Assert.False((bool)result["releaseFilesVerified"]);
                Assert.Equal("unknown-test-host-no-child", (string)result["execution"]);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Modified_configuration_is_reported_but_does_not_block_a_native_load_gate()
        {
            var root = Path.Combine(Path.GetTempPath(), "ReactorVDependencyTests", Guid.NewGuid().ToString("N"));
            try
            {
                var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
                File.WriteAllText(Path.Combine(game, "GTA5_Enhanced.exe"), "MZ");
                File.WriteAllText(Path.Combine(game, "ReactorV.json"), "customized");
                var manifest = Path.Combine(root, "release.json");
                File.WriteAllText(manifest, new JObject { ["schemaVersion"] = 1, ["edition"] = "Enhanced", ["releaseVersion"] = "0.2.4", ["files"] = new JArray(new JObject { ["path"] = "ReactorV.json", ["sha256"] = Hash("default"), ["length"] = 7 }) }.ToString());
                var result = DependencyService.Run(new DiagnosticOptions { GameDirectory = game, OutputDirectory = root, ManifestPath = manifest, Edition = "Enhanced" }, CancellationToken.None, _ => { });
                Assert.True((bool)result["releaseFilesVerified"]);
                Assert.Equal("unknown-test-host-no-child", (string)result["execution"]);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static byte[] Pe(ushort machine)
        {
            var bytes = new byte[256]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BitConverter.GetBytes(128).CopyTo(bytes, 0x3c); bytes[128] = (byte)'P'; bytes[129] = (byte)'E'; BitConverter.GetBytes(machine).CopyTo(bytes, 132); return bytes;
        }
        private static byte[] PeWithImport(string import)
        {
            var bytes = new byte[0x600]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
            bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84); BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x86); BitConverter.GetBytes((ushort)240).CopyTo(bytes, 0x94);
            BitConverter.GetBytes((ushort)0x20b).CopyTo(bytes, 0x98); BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, 0x110); BitConverter.GetBytes((uint)40).CopyTo(bytes, 0x114);
            BitConverter.GetBytes((uint)0x400).CopyTo(bytes, 0x188 + 8); BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, 0x188 + 12); BitConverter.GetBytes((uint)0x400).CopyTo(bytes, 0x188 + 16); BitConverter.GetBytes((uint)0x400).CopyTo(bytes, 0x188 + 20);
            BitConverter.GetBytes((uint)0x1100).CopyTo(bytes, 0x400 + 12); Encoding.ASCII.GetBytes(import + "\0").CopyTo(bytes, 0x500); return bytes;
        }
        private static string Hash(string value) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
    }
}
