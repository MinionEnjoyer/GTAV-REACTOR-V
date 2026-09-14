using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ReactorV.Diagnostics.Tests
{
    public sealed class ReleaseReferenceTests
    {
        [Fact]
        public void Auto_selects_only_one_exact_bundled_reference_and_marks_drift_unknown()
        {
            var root = Path.Combine(Path.GetTempPath(), "ReactorVReferenceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var old = ReleaseReferenceService.IndexPathProvider;
            try
            {
                var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
                Write(game, "GTA5_Enhanced.exe", "MZ");
                Write(game, "ReactorV.Bootstrap.asi", "bootstrap");
                var hash = DiagnosticIO.Sha256(Path.Combine(game, "ReactorV.Bootstrap.asi"));
                var anchor = new JObject { ["path"] = "ReactorV.Bootstrap.asi", ["sha256"] = hash, ["length"] = 9 };
                var manifest = Path.Combine(root, "0.2.4-enhanced.json"); File.WriteAllText(manifest, new JObject { ["schemaVersion"] = 1, ["releaseVersion"] = "0.2.4", ["edition"] = "Enhanced", ["packageSha256"] = new string('a', 64), ["identityAnchors"] = new JArray(anchor) }.ToString());
                var index = new JObject { ["schemaVersion"] = 1, ["releases"] = new JArray(new JObject {
                    ["releaseVersion"] = "0.2.4", ["edition"] = "Enhanced", ["manifest"] = "0.2.4-enhanced.json", ["artifact"] = "ReactorV-0.2.4-enhanced-live-test.zip", ["packageSha256"] = new string('a', 64),
                    ["identityAnchors"] = new JArray(anchor)
                })};
                var indexPath = Path.Combine(root, "release-index.json"); File.WriteAllText(indexPath, index.ToString());
                ReleaseReferenceService.IndexPathProvider = () => indexPath;
                var options = new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "auto" };
                var selected = ReleaseReferenceService.Resolve(options);
                Assert.Equal("auto-exact-installed-release", (string)selected["status"]);
                Assert.Equal(manifest, options.ManifestPath);
                var manual = ReleaseReferenceService.Resolve(new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "0.2.4" });
                Assert.Equal("manual", (string)manual["mode"]);
                Assert.Equal("manual-pinned", (string)manual["status"]);

                var externalManifest = Path.Combine(root, "external.json");
                File.WriteAllText(externalManifest, new JObject { ["schemaVersion"] = 1, ["edition"] = "Enhanced", ["releaseVersion"] = "0.2.4", ["files"] = new JArray() }.ToString());
                Assert.Throws<InvalidDataException>(() => DependencyService.RunProbe(new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ManifestPath = externalManifest }));

                var secondManifest = Path.Combine(root, "0.2.5-enhanced.json");
                var second = new JObject { ["releaseVersion"] = "0.2.5", ["edition"] = "Enhanced", ["manifest"] = "0.2.5-enhanced.json", ["artifact"] = "ReactorV-0.2.5-enhanced-live-test.zip", ["packageSha256"] = new string('b', 64), ["identityAnchors"] = new JArray(anchor) };
                File.WriteAllText(secondManifest, new JObject { ["schemaVersion"] = 1, ["releaseVersion"] = "0.2.5", ["edition"] = "Enhanced", ["packageSha256"] = new string('b', 64), ["identityAnchors"] = new JArray(anchor) }.ToString());
                ((JArray)index["releases"]!).Add(second); File.WriteAllText(indexPath, index.ToString());
                var selectedOlderOrCurrent = ReleaseReferenceService.Resolve(new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "0.2.5" });
                Assert.Equal("manual", (string)selectedOlderOrCurrent["mode"]);
                Assert.Equal("0.2.5", (string)selectedOlderOrCurrent["releaseVersion"]);
                var ambiguous = ReleaseReferenceService.Resolve(new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "auto" });
                Assert.Equal("ambiguous-installed-reference", (string)ambiguous["status"]);
                ((JArray)index["releases"]!).Remove(second); File.WriteAllText(indexPath, index.ToString());

                File.WriteAllText(indexPath, new JObject { ["schemaVersion"] = 1, ["releases"] = new JArray(JValue.CreateNull()) }.ToString());
                Assert.Throws<InvalidDataException>(() => ReleaseReferenceService.Resolve(new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "auto" }));
                File.WriteAllText(indexPath, index.ToString());

                ((JObject)((JArray)((JObject)index["releases"]![0])["identityAnchors"]!)[0])["length"] = 10;
                File.WriteAllText(indexPath, index.ToString());
                Assert.Throws<InvalidDataException>(() => ReleaseReferenceService.Resolve(new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "auto" }));
                ((JObject)((JArray)((JObject)index["releases"]![0])["identityAnchors"]!)[0])["length"] = 9;
                File.WriteAllText(indexPath, index.ToString());

                File.WriteAllText(Path.Combine(game, "ReactorV.Bootstrap.asi"), "changed");
                options = new DiagnosticOptions { GameDirectory = game, Edition = "Enhanced", ReleaseVersion = "auto" };
                var drift = ReleaseReferenceService.Resolve(options);
                Assert.Equal("unrecognized-or-build-drift", (string)drift["status"]);
                Assert.True(string.IsNullOrEmpty(options.ManifestPath));
                var reports = Path.Combine(root, "reports"); Directory.CreateDirectory(reports);
                var isolation = DiagnosticRunner.Run("isolate-preview", new DiagnosticOptions { GameDirectory = game, OutputDirectory = reports, Edition = "Enhanced", ReleaseVersion = "auto" }, System.Threading.CancellationToken.None, _ => { });
                Assert.Equal("unrecognized-or-build-drift", (string?)isolation["referenceSelection"]?["status"]);
                Assert.Equal("blocked-or-failed", (string)isolation["status"]);
            }
            finally { ReleaseReferenceService.IndexPathProvider = old; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void Write(string root, string relative, string text)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
        }
    }
}
