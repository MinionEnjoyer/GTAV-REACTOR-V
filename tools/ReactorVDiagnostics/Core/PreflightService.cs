using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>Read-only, manifest-bounded installation preflight.</summary>
    public static class PreflightService
    {
        private const int MaximumManifestBytes = 1024 * 1024;
        // The release manifest contains Reactor-owned payload only.  Keeping this
        // deliberately narrow prevents a custom manifest from turning preflight
        // into a general-purpose game-file hasher.
        private const int MaximumManifestFiles = 2048;

        public static JObject Run(
            DiagnosticOptions options,
            CancellationToken token,
            Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            token.ThrowIfCancellationRequested();

            var gameRoot = DiagnosticIO.GameRoot(options);
            DiagnosticIO.RequireOrdinaryPath(gameRoot);
            var manifestPath = options.ManifestPath;
            DiagnosticIO.RequireOrdinaryPath(manifestPath);
            progress("Reading the selected release manifest.");
            var manifest = ReadManifest(manifestPath);
            ValidateEdition(manifest, options.Edition);

            var confirmed = new JArray();
            var missing = new JArray();
            var mismatch = new JArray();
            var observations = new JArray();
            var unknown = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in (JArray)manifest["files"]!)
            {
                token.ThrowIfCancellationRequested();
                var relative = RequiredString(item, "path");
                ValidateRelativePath(relative);
                ValidateReactorPackagePath(relative);
                var expectedHash = RequiredHash(item, "sha256");
                var expectedLength = RequiredLength(item, "length");
                if (!seen.Add(relative)) throw new InvalidDataException("The manifest contains duplicate paths.");

                var file = DiagnosticIO.SafePath(gameRoot, relative);
                if (!File.Exists(file))
                {
                    missing.Add(new JObject { ["path"] = relative, ["expectedSha256"] = expectedHash, ["expectedLength"] = expectedLength });
                    continue;
                }
                DiagnosticIO.RequireOrdinaryPath(file);
                var info = new FileInfo(file);
                var actualHash = DiagnosticIO.Sha256(file);
                if (info.Length == expectedLength && string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    confirmed.Add(new JObject { ["path"] = relative, ["sha256"] = actualHash, ["length"] = info.Length });
                }
                else
                {
                    mismatch.Add(new JObject {
                        ["classification"] = "modified-or-replaced",
                        ["path"] = relative, ["expectedSha256"] = expectedHash, ["actualSha256"] = actualHash,
                        ["expectedLength"] = expectedLength, ["actualLength"] = info.Length
                    });
                }
            }

            progress("Recording bounded contextual observations.");
            CollectRootObservations(gameRoot, observations, unknown, token);
            CollectRuntimeDependencyObservations(gameRoot, observations, unknown, token);
            JObject? securityEvidence = null;
            if (options.IncludeSecurityEvents)
            {
                progress("Collecting time-bounded, relevant security evidence.");
                var collectionEndedUtc = DateTime.UtcNow;
                securityEvidence = SecurityEvidence.Collect(collectionEndedUtc.AddHours(-24), collectionEndedUtc, gameRoot, token);
            }
            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["kind"] = "reactor-install-preflight",
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["gameRoot"] = DiagnosticIO.Redact(gameRoot, gameRoot),
                ["manifest"] = new JObject {
                    ["releaseVersion"] = RequiredString(manifest, "releaseVersion"),
                    ["edition"] = RequiredString(manifest, "edition"),
                    ["packageSha256"] = RequiredHash(manifest, "packageSha256")
                },
                ["confirmed"] = confirmed,
                ["missing"] = missing,
                ["mismatch"] = mismatch,
                ["observations"] = observations,
                ["unknown"] = unknown,
                ["securityEvidence"] = securityEvidence ?? new JObject { ["requested"] = false, ["status"] = "not-requested" },
                ["limitations"] = new JArray(
                    "Contextual files are observations only; this preflight does not establish module load, signer trust, compatibility, or causation.",
                    "A hash mismatch means modified-or-replaced content; it is not, by itself, evidence of an invalid installation or user error.",
                    "Only allowlisted, manifest-listed Reactor package files are hashed.")
            };
            DiagnosticIO.RequireOrdinaryPath(options.OutputDirectory);
            var output = DiagnosticIO.SafePath(options.OutputDirectory, "ReactorV-preflight.json");
            DiagnosticIO.AtomicWriteJson(output, report);
            progress("Preflight complete.");
            return report;
        }

        private static JObject ReadManifest(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumManifestBytes) throw new InvalidDataException("Manifest exceeds the size limit.");
            var manifest = JObject.Parse(File.ReadAllText(path));
            if ((int?)manifest["schemaVersion"] != 1) throw new InvalidDataException("Unsupported manifest schema.");
            RequiredString(manifest, "releaseVersion"); RequiredString(manifest, "edition"); RequiredHash(manifest, "packageSha256");
            var files = manifest["files"] as JArray;
            if (files == null || files.Count == 0 || files.Count > MaximumManifestFiles) throw new InvalidDataException("Invalid manifest file list.");
            foreach (var file in files)
            {
                var relative = RequiredString(file, "path");
                ValidateRelativePath(relative);
                ValidateReactorPackagePath(relative);
                RequiredHash(file, "sha256");
                RequiredLength(file, "length");
            }
            return manifest;
        }

        private static void ValidateEdition(JObject manifest, string edition)
        {
            var actual = RequiredString(manifest, "edition");
            if ((actual != "Enhanced" && actual != "Legacy") || !string.Equals(actual, edition, StringComparison.Ordinal))
                throw new InvalidDataException("Manifest edition does not match the selected edition.");
        }

        private static void ValidateRelativePath(string relative)
        {
            if (Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("Manifest path must be relative.");
            var segments = relative.Replace('\\', '/').Split('/');
            if (segments.Length == 0 || segments.Any(s => string.IsNullOrWhiteSpace(s) || s == "." || s == ".."))
                throw new InvalidDataException("Manifest path contains an unsafe segment.");
        }

        private static void ValidateReactorPackagePath(string relative)
        {
            var normalized = relative.Replace('\\', '/');
            if (normalized.StartsWith("plugins/ReactorV/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("scripts/ReactorV/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("ReactorV.RenderHook.asi", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("ReactorV.Bootstrap.asi", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("ReactorV.ScriptProbe.asi", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("Manifest path is outside the Reactor package allowlist.");
        }

        private static void CollectRootObservations(string root, JArray observations, JArray unknown, CancellationToken token)
        {
            int candidateCount = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".asi", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("ScriptHook", StringComparison.OrdinalIgnoreCase)) continue;
                if (candidateCount++ >= 128)
                {
                    observations.Add(new JObject { ["kind"] = "contextual-candidate-summary", ["truncated"] = true, ["maximumCandidates"] = 128 });
                    break;
                }
                try
                {
                    DiagnosticIO.RequireOrdinaryPath(file);
                    var version = FileVersionInfo.GetVersionInfo(file);
                    observations.Add(new JObject {
                        ["path"] = DiagnosticIO.Redact(file, root), ["kind"] = "contextual-candidate",
                        ["length"] = new FileInfo(file).Length, ["fileVersion"] = version.FileVersion ?? "",
                        ["productVersion"] = version.ProductVersion ?? ""
                    });
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                { unknown.Add(new JObject { ["path"] = DiagnosticIO.Redact(file, root), ["reason"] = error.GetType().Name }); }
            }
        }

        private static void CollectRuntimeDependencyObservations(string root, JArray observations, JArray unknown, CancellationToken token)
        {
            var paths = new[] {
                "plugins/ReactorV/RageWebUI.Runtime.dll",
                "plugins/ReactorV/CefSharp.Core.dll",
                "plugins/ReactorV/CefSharp.WinForms.dll",
                "plugins/ReactorV/libcef.dll",
                "plugins/ReactorV/ReactorV.Preloader.exe.config"
            };
            foreach (var relative in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var file = DiagnosticIO.SafePath(root, relative);
                    if (!File.Exists(file)) continue;
                    DiagnosticIO.RequireOrdinaryPath(file);
                    var version = FileVersionInfo.GetVersionInfo(file);
                    observations.Add(new JObject {
                        ["kind"] = "runtime-dependency-presence", ["path"] = DiagnosticIO.Redact(file, root),
                        ["length"] = new FileInfo(file).Length, ["fileVersion"] = version.FileVersion ?? "",
                        ["productVersion"] = version.ProductVersion ?? ""
                    });
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                { unknown.Add(new JObject { ["path"] = relative, ["reason"] = error.GetType().Name }); }
            }
        }

        private static string RequiredString(JToken? token, string name)
        {
            if (token == null) throw new InvalidDataException("Manifest object is required for: " + name);
            var value = (string?)token[name];
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Manifest field is required: " + name);
            return value;
        }
        private static string RequiredHash(JToken token, string name)
        {
            var value = RequiredString(token, name);
            if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid SHA-256: " + name);
            return value;
        }
        private static long RequiredLength(JToken token, string name)
        {
            var value = (long?)token[name];
            if (!value.HasValue || value.Value < 0) throw new InvalidDataException("Invalid length: " + name);
            return value.Value;
        }
    }
}
