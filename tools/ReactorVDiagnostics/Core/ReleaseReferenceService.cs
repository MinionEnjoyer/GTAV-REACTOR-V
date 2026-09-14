using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>Resolves only bundled, release-pinned manifests; never infers a release from an arbitrary DLL.</summary>
    public static class ReleaseReferenceService
    {
        private static readonly Regex ReleaseVersion = new Regex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant);
        public static Func<string> IndexPathProvider { get; set; } = () => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manifests", "release-index.json");

        public static JObject Resolve(DiagnosticOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var entries = ReadEntries();
            var candidates = entries.Where(x => string.Equals((string)x["edition"], options.Edition, StringComparison.Ordinal)).ToArray();
            if (!string.IsNullOrWhiteSpace(options.ManifestPath))
            {
                var full = Path.GetFullPath(options.ManifestPath);
                var pinned = candidates.FirstOrDefault(x => string.Equals(Path.GetFullPath(ManifestPath(x)), full, StringComparison.OrdinalIgnoreCase));
                if (pinned == null) throw new InvalidDataException("The selected manifest is not a bundled pinned release reference.");
                options.ManifestPath = ManifestPath(pinned); options.ReferenceSelection = "manual-pinned";
                return Result(options, pinned, "manual", "manual-pinned", new JArray());
            }
            if (!string.Equals(options.ReleaseVersion, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var pinned = candidates.SingleOrDefault(x => string.Equals((string)x["releaseVersion"], options.ReleaseVersion, StringComparison.Ordinal));
                if (pinned == null) throw new InvalidDataException("The selected release is not bundled for this edition.");
                options.ManifestPath = ManifestPath(pinned); options.ReferenceSelection = "manual-pinned";
                return Result(options, pinned, "manual", "manual-pinned", new JArray());
            }
            var matches = new List<JObject>(); var drift = new JArray();
            foreach (var candidate in candidates)
            {
                var all = true;
                foreach (var anchor in StrictAnchors(candidate))
                {
                    var relative = Required(anchor, "path"); var expected = RequiredHash(anchor, "sha256"); var expectedLength = RequiredLength(anchor, "length");
                    var path = DiagnosticIO.SafePath(DiagnosticIO.GameRoot(options), relative);
                    var exact = false;
                    if (File.Exists(path)) { DiagnosticIO.RequireOrdinaryPath(path); exact = new FileInfo(path).Length == expectedLength && string.Equals(DiagnosticIO.Sha256(path), expected, StringComparison.OrdinalIgnoreCase); }
                    if (!exact) { all = false; drift.Add(new JObject { ["releaseVersion"] = (string)candidate["releaseVersion"], ["path"] = relative, ["status"] = File.Exists(path) ? "hash-or-length-drift" : "missing" }); }
                }
                if (all) matches.Add(candidate);
            }
            if (matches.Count == 1)
            {
                options.ManifestPath = ManifestPath(matches[0]); options.ReferenceSelection = "auto-exact-installed-release";
                return Result(options, matches[0], "auto", options.ReferenceSelection, drift);
            }
            options.ReferenceSelection = matches.Count == 0 ? "unrecognized-or-build-drift" : "ambiguous-installed-reference";
            return new JObject { ["mode"] = "auto", ["status"] = options.ReferenceSelection, ["edition"] = options.Edition, ["candidates"] = drift,
                ["limitation"] = "No release reference was selected automatically. This is not a successful integrity result; select a bundled release explicitly to compare files." };
        }

        public static string[] BundledReleaseVersions(string edition) => ReadEntries().Where(x => string.Equals((string)x["edition"], edition, StringComparison.Ordinal))
            .Select(x => (string)x["releaseVersion"]!).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();

        private static JObject[] ReadEntries()
        {
            var path = IndexPathProvider(); DiagnosticIO.RequireOrdinaryPath(path);
            var index = JObject.Parse(File.ReadAllText(path));
            var raw = index["releases"] as JArray;
            if ((int?)index["schemaVersion"] != 1 || raw == null || raw.Count == 0) throw new InvalidDataException("Invalid release index.");
            if (raw.Any(x => !(x is JObject))) throw new InvalidDataException("Release index contains a non-object entry.");
            var entries = raw.Cast<JObject>().ToArray(); var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var version = Required(entry, "releaseVersion"); var edition = Required(entry, "edition");
                if (!ReleaseVersion.IsMatch(version) || (edition != "Enhanced" && edition != "Legacy") || !keys.Add(version + "|" + edition)) throw new InvalidDataException("Invalid or duplicate release index entry.");
                var artifact = Required(entry, "artifact");
                if (artifact != "ReactorV-" + version + "-" + edition.ToLowerInvariant() + "-live-test.zip") throw new InvalidDataException("Release index artifact does not match its version and edition.");
                RequiredHash(entry, "packageSha256"); var manifest = ReadManifest(entry);
                if (!string.Equals((string)manifest["releaseVersion"], version, StringComparison.Ordinal) || !string.Equals((string)manifest["edition"], edition, StringComparison.Ordinal) || !string.Equals((string)manifest["packageSha256"], (string)entry["packageSha256"], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release index and manifest disagree.");
                var indexAnchors = StrictAnchors(entry);
                var manifestAnchors = StrictAnchors(manifest);
                if (indexAnchors.Count != manifestAnchors.Count) throw new InvalidDataException("Release index anchor count disagrees with manifest.");
                for (var i = 0; i < indexAnchors.Count; i++)
                {
                    var indexed = indexAnchors[i]; var pinned = manifestAnchors[i];
                    if (!string.Equals(Required(indexed, "path"), Required(pinned, "path"), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(RequiredHash(indexed, "sha256"), RequiredHash(pinned, "sha256"), StringComparison.OrdinalIgnoreCase) ||
                        RequiredLength(indexed, "length") != RequiredLength(pinned, "length"))
                        throw new InvalidDataException("Release index anchors disagree with manifest.");
                }
            }
            return entries;
        }

        private static JObject ReadManifest(JObject entry) { var path = ManifestPath(entry); var manifest = JObject.Parse(File.ReadAllText(path)); if ((int?)manifest["schemaVersion"] != 1) throw new InvalidDataException("Unsupported pinned manifest schema."); return manifest; }
        private static string ManifestPath(JObject entry)
        {
            var name = Required(entry, "manifest");
            if (Path.GetFileName(name) != name || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe release manifest name.");
            var directory = Path.GetDirectoryName(IndexPathProvider()) ?? throw new InvalidDataException("Release index has no directory.");
            var path = Path.Combine(directory, name); DiagnosticIO.RequireOrdinaryPath(path); return path;
        }
        private static List<JObject> StrictAnchors(JObject entry)
        {
            var anchors = entry["identityAnchors"] as JArray;
            if (anchors == null || anchors.Count == 0 || anchors.Any(x => !(x is JObject))) throw new InvalidDataException("Pinned release has invalid identity anchors.");
            var result = anchors.Cast<JObject>().ToList(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var anchor in result) { var path = Required(anchor, "path"); RequiredHash(anchor, "sha256"); RequiredLength(anchor, "length"); if (!paths.Add(path)) throw new InvalidDataException("Pinned release has duplicate identity anchors."); }
            return result;
        }
        private static JObject Result(DiagnosticOptions options, JObject entry, string mode, string status, JArray drift) => new JObject { ["mode"] = mode, ["status"] = status, ["edition"] = options.Edition, ["releaseVersion"] = (string)entry["releaseVersion"], ["manifest"] = (string)entry["manifest"], ["packageSha256"] = (string)entry["packageSha256"], ["candidates"] = drift };
        private static string Required(JToken token, string name) => (string)token[name] ?? throw new InvalidDataException("Release index field is required: " + name);
        private static string RequiredHash(JToken token, string name) { var value = Required(token, name); if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid pinned SHA-256."); return value; }
        private static long RequiredLength(JToken token, string name) { var value = (long?)token[name]; if (!value.HasValue || value.Value < 0) throw new InvalidDataException("Invalid pinned length."); return value.Value; }
    }
}
