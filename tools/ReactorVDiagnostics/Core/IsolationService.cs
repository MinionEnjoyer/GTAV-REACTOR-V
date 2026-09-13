using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>
    /// Opt-in, manifest-authenticated isolation of the small native loading surface.
    /// This deliberately does not scan, delete, or otherwise alter arbitrary game files.
    /// </summary>
    public static class IsolationService
    {
        private static readonly string[] IsolatedPaths =
        {
            "ReactorV.RenderHook.asi",
            "ReactorV.Bootstrap.asi",
            "ReactorV.ScriptProbe.asi",
            "plugins/ReactorV/RageWebUI.Native.dll"
        };

        // Kept injectable so tests never inspect or affect a real GTA process.
        public static Func<string, bool> GameRunningGuard { get; set; } = DefaultGameRunningGuard;

        public static JObject Preview(DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            token.ThrowIfCancellationRequested();
            var root = RequireSafeRoot(options);
            EnsureGameClosed(root);
            var manifest = ReadAndValidateManifest(options);
            var expected = ExpectedFiles(manifest);
            var actions = new JArray();
            foreach (var relative in IsolatedPaths)
            {
                token.ThrowIfCancellationRequested();
                var destination = DiagnosticIO.SafePath(root, relative);
                if (!File.Exists(destination))
                {
                    actions.Add(Action(relative, "alreadyAbsent"));
                    continue;
                }

                DiagnosticIO.RequireOrdinaryPath(destination);
                var actualHash = DiagnosticIO.Sha256(destination);
                var actualLength = new FileInfo(destination).Length;
                var entry = expected[relative];
                if (entry.Length != actualLength || !SameHash(entry.Hash, actualHash))
                    throw new InvalidDataException("Installed file does not match the selected release manifest: " + relative);
                actions.Add(Action(relative, "moveToExternalBackup", actualHash, actualLength));
            }

            progress("Previewed the explicit native-hook isolation actions; no files were changed.");
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["kind"] = "reactor-native-hook-isolation-preview",
                ["gameRoot"] = DiagnosticIO.Redact(root, root),
                ["edition"] = options.Edition,
                ["releaseVersion"] = (string)manifest["releaseVersion"],
                ["actions"] = actions,
                ["requiresConsent"] = true,
                ["verification"] = VerificationSummary()
            };
        }

        public static JObject Apply(DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            if (!options.Consent) throw new InvalidOperationException("Native-hook isolation requires explicit user consent.");
            var root = RequireSafeRoot(options);
            using (AcquireGameMutex(root))
            {
                EnsureGameClosed(root);
                token.ThrowIfCancellationRequested();
                var statePath = RequireStatePath(options, root);
                RejectExistingState(statePath, root);
                var manifest = ReadAndValidateManifest(options);
                var expected = ExpectedFiles(manifest);
                var transactionId = Guid.NewGuid().ToString("N");
                var backupRoot = BackupRootFor(statePath, transactionId);
                EnsureOutsideGameRoot(root, backupRoot);
                DiagnosticIO.RequireOrdinaryPath(backupRoot, false);
                if (Directory.Exists(backupRoot) || File.Exists(backupRoot)) throw new IOException("Isolation backup path already exists.");

                var files = new JArray();
                foreach (var relative in IsolatedPaths)
                {
                    var destination = DiagnosticIO.SafePath(root, relative);
                    if (!File.Exists(destination)) continue;
                    DiagnosticIO.RequireOrdinaryPath(destination);
                    var actualHash = DiagnosticIO.Sha256(destination);
                    var actualLength = new FileInfo(destination).Length;
                    var entry = expected[relative];
                    if (entry.Length != actualLength || !SameHash(entry.Hash, actualHash))
                        throw new InvalidDataException("Installed file does not match the selected release manifest: " + relative);
                    files.Add(new JObject {
                        ["path"] = relative, ["sha256"] = actualHash, ["length"] = actualLength, ["status"] = "pending"
                    });
                }

                if (files.Count == 0)
                {
                    progress("No authenticated native-hook files are present; no isolation was applied.");
                    return new JObject {
                        ["schemaVersion"] = 1, ["status"] = "noActions", ["nativeIsolationApplied"] = false,
                        ["gameRoot"] = DiagnosticIO.Redact(root, root), ["actions"] = new JArray()
                    };
                }

                var state = new JObject {
                    ["schemaVersion"] = 1, ["kind"] = "reactor-native-hook-isolation",
                    ["status"] = "applying", ["gameRoot"] = root, ["backupRoot"] = backupRoot,
                    ["transactionId"] = transactionId, ["files"] = files
                };
                // The state is durable before the backup directory or any file move.
                DiagnosticIO.AtomicWriteJson(statePath, state);
                Directory.CreateDirectory(backupRoot);
                foreach (var file in files.OfType<JObject>())
                {
                    token.ThrowIfCancellationRequested();
                    EnsureGameClosed(root);
                    file["status"] = "moving";
                    DiagnosticIO.AtomicWriteJson(statePath, state);
                    var relative = (string)file["path"];
                    var source = DiagnosticIO.SafePath(root, relative);
                    var backup = DiagnosticIO.SafePath(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    if (!File.Exists(source)) throw new IOException("Isolation source disappeared: " + relative);
                    DiagnosticIO.RequireOrdinaryPath(source);
                    var freshLength = new FileInfo(source).Length;
                    var freshHash = DiagnosticIO.Sha256(source);
                    if (freshLength != (long)file["length"] || !SameHash(freshHash, (string)file["sha256"]))
                        throw new IOException("Isolation source changed after planning: " + relative);
                    EnsureGameClosed(root);
                    File.Move(source, backup);
                    file["status"] = "isolated";
                    DiagnosticIO.AtomicWriteJson(statePath, state);
                    progress("Isolated " + relative + ".");
                }
                state["status"] = "isolated";
                DiagnosticIO.AtomicWriteJson(statePath, state);
                return Result("applied", root, state, new JArray(files.Select(file => Action((string)file["path"], "isolated"))));
            }
        }

        public static JObject Restore(DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            if (!options.Consent) throw new InvalidOperationException("Restoring native hooks requires explicit user consent.");
            var root = RequireSafeRoot(options);
            using (AcquireGameMutex(root))
            {
                EnsureGameClosed(root);
                var statePath = RequireStatePath(options, root);
                if (!File.Exists(statePath)) return new JObject { ["schemaVersion"] = 1, ["status"] = "nothingToRestore" };
                DiagnosticIO.RequireOrdinaryPath(statePath);
                var state = JObject.Parse(File.ReadAllText(statePath));
                ValidateState(state, root, statePath);
                var backupRoot = (string)state["backupRoot"];
                var actions = new JArray();
                if (!string.Equals((string)state["status"], "restored", StringComparison.Ordinal))
                {
                    state["status"] = "restoring";
                    DiagnosticIO.AtomicWriteJson(statePath, state);
                }
                foreach (var file in ((JArray)state["files"]).OfType<JObject>())
                {
                    token.ThrowIfCancellationRequested();
                    EnsureGameClosed(root);
                    var relative = (string)file["path"];
                    var expectedHash = (string)file["sha256"];
                    var expectedLength = (long)file["length"];
                    var source = DiagnosticIO.SafePath(backupRoot, relative);
                    var destination = DiagnosticIO.SafePath(root, relative);
                    if (!File.Exists(source))
                    {
                        if (File.Exists(destination) && IsIdentical(destination, expectedHash, expectedLength))
                        {
                            file["status"] = "restored";
                            actions.Add(Action(relative, "alreadyRestored"));
                            DiagnosticIO.AtomicWriteJson(statePath, state);
                            continue;
                        }
                        throw new InvalidDataException("Recovery backup is missing for " + relative + ". No file was overwritten.");
                    }
                    DiagnosticIO.RequireOrdinaryPath(source);
                    if (!IsIdentical(source, expectedHash, expectedLength))
                        throw new InvalidDataException("Recovery backup hash does not match the original for " + relative + ".");
                    if (File.Exists(destination))
                    {
                        DiagnosticIO.RequireOrdinaryPath(destination);
                        if (!IsIdentical(destination, expectedHash, expectedLength))
                            throw new IOException("Restore conflict at " + relative + ". Existing file was preserved.");
                        file["status"] = "restored";
                        actions.Add(Action(relative, "alreadyRestored"));
                        DiagnosticIO.AtomicWriteJson(statePath, state);
                        continue;
                    }
                    file["status"] = "restoring";
                    DiagnosticIO.AtomicWriteJson(statePath, state);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    EnsureGameClosed(root);
                    File.Move(source, destination);
                    file["status"] = "restored";
                    DiagnosticIO.AtomicWriteJson(statePath, state);
                    actions.Add(Action(relative, "restored"));
                    progress("Restored " + relative + ".");
                }
                state["status"] = "restored";
                DiagnosticIO.AtomicWriteJson(statePath, state);
                return Result("restored", root, state, actions);
            }
        }

        private static JObject Result(string status, string root, JObject state, JArray actions) => new JObject {
            ["schemaVersion"] = 1, ["status"] = status, ["gameRoot"] = DiagnosticIO.Redact(root, root),
            ["transactionId"] = (string)state["transactionId"], ["actions"] = actions, ["verification"] = VerificationSummary()
        };

        private static JObject Action(string relative, string operation, string sha256 = null, long? length = null)
        {
            var result = new JObject { ["path"] = relative, ["operation"] = operation };
            if (sha256 != null) result["sha256"] = sha256;
            if (length.HasValue) result["length"] = length.Value;
            return result;
        }

        private static JArray VerificationSummary() => new JArray(
            "On-disk absence alone is not proof that a native module was not loaded; use Capture module samples during the diagnostic run.",
            "Native-off isolation can disable or degrade the UI. It is not UI acceptance evidence.");

        private static string RequireSafeRoot(DiagnosticOptions options)
        {
            var root = Path.GetFullPath(DiagnosticIO.GameRoot(options));
            DiagnosticIO.RequireOrdinaryPath(root);
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string RequireStatePath(DiagnosticOptions options, string root)
        {
            if (string.IsNullOrWhiteSpace(options.IsolationStatePath)) throw new InvalidOperationException("An isolation state path is required.");
            var state = Path.GetFullPath(options.IsolationStatePath);
            EnsureOutsideGameRoot(root, state);
            var parent = Path.GetDirectoryName(state);
            DiagnosticIO.RequireOrdinaryPath(parent);
            if (File.Exists(state)) DiagnosticIO.RequireOrdinaryPath(state);
            return state;
        }

        private static void EnsureOutsideGameRoot(string root, string candidate)
        {
            var full = Path.GetFullPath(candidate);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root : root + Path.DirectorySeparatorChar;
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Isolation backups and state must be outside the game directory.");
        }

        private static IDisposable AcquireGameMutex(string root)
        {
            var name = "Local\\ReactorV.Diagnostics.NativeIsolation." + ShortHash(root);
            var mutex = new Mutex(false, name);
            try
            {
                if (!mutex.WaitOne(0)) { mutex.Dispose(); throw new InvalidOperationException("Another isolation operation is already active for this game directory."); }
                return new MutexLease(mutex);
            }
            catch (AbandonedMutexException) { return new MutexLease(mutex); }
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex _mutex;
            public MutexLease(Mutex mutex) { _mutex = mutex; }
            public void Dispose()
            {
                var mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }

        private static string ShortHash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        private static void EnsureGameClosed(string root)
        {
            if ((GameRunningGuard ?? DefaultGameRunningGuard)(root))
                throw new InvalidOperationException("Close GTA before isolating or restoring native hooks.");
        }

        private static bool DefaultGameRunningGuard(string root)
        {
            return System.Diagnostics.Process.GetProcessesByName("GTA5").Any() ||
                   System.Diagnostics.Process.GetProcessesByName("GTA5_Enhanced").Any();
        }

        private sealed class ManifestFile { public string Hash; public long Length; }

        private static JObject ReadAndValidateManifest(DiagnosticOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.ManifestPath)) throw new InvalidOperationException("A release manifest is required.");
            DiagnosticIO.RequireOrdinaryPath(options.ManifestPath);
            var manifest = JObject.Parse(File.ReadAllText(options.ManifestPath));
            if ((int?)manifest["schemaVersion"] != 1 || string.IsNullOrWhiteSpace((string)manifest["releaseVersion"]) ||
                !string.Equals((string)manifest["edition"], options.Edition, StringComparison.Ordinal))
                throw new InvalidDataException("The selected release manifest is invalid or has the wrong edition.");
            if (!(manifest["files"] is JArray)) throw new InvalidDataException("The selected release manifest has no file list.");
            return manifest;
        }

        private static Dictionary<string, ManifestFile> ExpectedFiles(JObject manifest)
        {
            var result = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in ((JArray)manifest["files"]).OfType<JObject>())
            {
                var path = (string)token["path"];
                var hash = (string)token["sha256"];
                var length = (long?)token["length"];
                if (string.IsNullOrWhiteSpace(path) || !IsHash(hash) || !length.HasValue || length.Value < 0 || result.ContainsKey(path))
                    throw new InvalidDataException("The selected release manifest contains an invalid file entry.");
                result.Add(path, new ManifestFile { Hash = hash, Length = length.Value });
            }
            foreach (var relative in IsolatedPaths)
                if (!result.ContainsKey(relative)) throw new InvalidDataException("The selected release manifest does not authenticate " + relative + ".");
            return result;
        }

        private static void RejectExistingState(string statePath, string root)
        {
            if (!File.Exists(statePath)) return;
            DiagnosticIO.RequireOrdinaryPath(statePath);
            var state = JObject.Parse(File.ReadAllText(statePath));
            ValidateState(state, root, statePath);
            throw new InvalidOperationException("An isolation journal already exists. Choose a fresh state path; existing journals are never overwritten.");
        }

        private static void ValidateState(JObject state, string root, string statePath)
        {
            RequireExactProperties(state, "schemaVersion", "kind", "status", "gameRoot", "backupRoot", "transactionId", "files");
            if ((int?)state["schemaVersion"] != 1 || !string.Equals((string)state["kind"], "reactor-native-hook-isolation", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath((string)state["gameRoot"] ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Isolation state does not belong to this game directory.");
            var backupRoot = (string)state["backupRoot"];
            var transactionId = (string)state["transactionId"];
            if (!IsTransactionId(transactionId) || !string.Equals(Path.GetFullPath(backupRoot ?? string.Empty), BackupRootFor(statePath, transactionId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Isolation state has an unowned backup root.");
            EnsureOutsideGameRoot(root, backupRoot!);
            DiagnosticIO.RequireOrdinaryPath(backupRoot!, false);
            var stateStatus = (string)state["status"];
            if (stateStatus != "applying" && stateStatus != "isolated" && stateStatus != "restoring" && stateStatus != "restored")
                throw new InvalidDataException("Isolation state has an invalid status.");
            var files = state["files"] as JArray;
            if (files == null || files.Count == 0 || files.Count > IsolatedPaths.Length) throw new InvalidDataException("Isolation state has invalid file records.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in files)
            {
                var file = token as JObject;
                if (file == null) throw new InvalidDataException("Isolation state contains a non-object file record.");
                RequireExactProperties(file, "path", "sha256", "length", "status");
                var path = (string)file["path"];
                var length = (long?)file["length"];
                var fileStatus = (string)file["status"];
                if (!IsolatedPaths.Contains(path, StringComparer.OrdinalIgnoreCase) || !seen.Add(path) || !IsHash((string)file["sha256"]) || !length.HasValue || length.Value < 0 ||
                    !IsValidFileStatus(stateStatus, fileStatus))
                    throw new InvalidDataException("Isolation state contains an invalid file record.");
                // Derive both locations from the static allowlist; never trust a serialized absolute path.
                DiagnosticIO.SafePath(root, path);
                DiagnosticIO.SafePath(backupRoot, path);
            }
        }

        private static string BackupRootFor(string statePath, string transactionId) =>
            Path.Combine(Path.GetDirectoryName(statePath)!, "ReactorV-native-isolation-" + transactionId);

        private static bool IsTransactionId(string value) => value != null && value.Length == 32 && value.All(Uri.IsHexDigit);

        private static bool IsValidFileStatus(string stateStatus, string fileStatus)
        {
            if (stateStatus == "applying") return fileStatus == "pending" || fileStatus == "moving" || fileStatus == "isolated";
            if (stateStatus == "isolated") return fileStatus == "isolated" || fileStatus == "restoring" || fileStatus == "restored";
            if (stateStatus == "restoring") return fileStatus == "pending" || fileStatus == "moving" || fileStatus == "isolated" || fileStatus == "restoring" || fileStatus == "restored";
            return fileStatus == "restored";
        }

        private static void RequireExactProperties(JObject value, params string[] names)
        {
            var allowed = new HashSet<string>(names, StringComparer.Ordinal);
            if (value.Properties().Any(property => !allowed.Contains(property.Name)) || names.Any(name => value[name] == null))
                throw new InvalidDataException("Isolation journal contains unexpected or missing fields.");
        }

        private static bool IsIdentical(string path, string hash, long length) =>
            File.Exists(path) && new FileInfo(path).Length == length && SameHash(DiagnosticIO.Sha256(path), hash);

        private static bool SameHash(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        private static bool IsHash(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }
}
