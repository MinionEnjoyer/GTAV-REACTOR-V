using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    /// <summary>
    /// Builds a bounded, inert snapshot of explicitly declared GTA-root files.
    /// It performs no assembly loading, script execution, or game-native work.
    /// </summary>
    public static class PreloadDataCache
    {
        public const int SchemaVersion = 1;
        public const int MaximumManifestCount = 16;
        public const int MaximumEntriesPerManifest = 64;
        public const long MaximumAggregateBytes = 16L * 1024L * 1024L;
        public const long MaximumEntryBytes = 4L * 1024L * 1024L;
        public const long MaximumManifestBytes = 256L * 1024L;
        public const long MaximumSnapshotBytes = 24L * 1024L * 1024L;

        private static readonly Regex IdentifierPattern = new Regex(
            "^[a-z0-9][a-z0-9._-]{0,63}$",
            RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            false,
            true);

        public static string ReadyEventName(int gtaProcessId)
        {
            ValidateProcessId(gtaProcessId);
            return @"Local\ReactorV.PreloadDataReady." +
                gtaProcessId.ToString(CultureInfo.InvariantCulture);
        }

        public static string ResolveGtaRootFromPreloaderDirectory(
            string preloaderDirectory)
        {
            if (string.IsNullOrWhiteSpace(preloaderDirectory))
            {
                throw new ArgumentException(
                    "The preloader directory is required.",
                    nameof(preloaderDirectory));
            }

            var reactorDirectory = new DirectoryInfo(Path.GetFullPath(preloaderDirectory));
            var pluginsDirectory = reactorDirectory.Parent;
            var gtaDirectory = pluginsDirectory?.Parent;
            if (!string.Equals(
                    reactorDirectory.Name,
                    "ReactorV",
                    StringComparison.OrdinalIgnoreCase) ||
                pluginsDirectory == null ||
                !string.Equals(
                    pluginsDirectory.Name,
                    "plugins",
                    StringComparison.OrdinalIgnoreCase) ||
                gtaDirectory == null)
            {
                throw new InvalidOperationException(
                    "The preloader must run from GTA-root\\plugins\\ReactorV.");
            }

            return gtaDirectory.FullName;
        }

        public static PreloadDataBuildResult Build(
            string gtaRoot,
            int gtaProcessId,
            string? cacheRootOverride = null,
            Action<string, string?>? trace = null)
        {
            ValidateProcessId(gtaProcessId);
            if (string.IsNullOrWhiteSpace(gtaRoot))
            {
                throw new ArgumentException("The GTA root is required.", nameof(gtaRoot));
            }

            var resolvedGtaRoot = Path.GetFullPath(gtaRoot);
            if (!Directory.Exists(resolvedGtaRoot))
            {
                throw new DirectoryNotFoundException("The declared GTA root does not exist.");
            }

            var cacheRoot = string.IsNullOrWhiteSpace(cacheRootOverride)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReactorV",
                    "Preload")
                : Path.GetFullPath(cacheRootOverride!);
            Directory.CreateDirectory(cacheRoot);
            CleanStaleProcessDirectories(cacheRoot, gtaProcessId);

            var processDirectory = PrepareProcessDirectory(cacheRoot, gtaProcessId);
            var result = new PreloadDataBuildResult(gtaProcessId, processDirectory);
            var manifestDirectory = Path.Combine(
                resolvedGtaRoot,
                "scripts",
                ".reactorv",
                "preload");
            trace?.Invoke(
                "preload_data_begin",
                $"pid={gtaProcessId.ToString(CultureInfo.InvariantCulture)}");

            if (!Directory.Exists(manifestDirectory))
            {
                trace?.Invoke("preload_manifest_directory_absent", null);
                trace?.Invoke("preload_data_complete", "manifests=0 entries=0 bytes=0 complete=True");
                return result;
            }

            string[] manifestPaths;
            try
            {
                manifestPaths = Directory
                    .EnumerateFiles(manifestDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception error) when (
                error is IOException || error is UnauthorizedAccessException)
            {
                result.AddError("manifest_discovery_failed", "Manifest discovery failed.");
                trace?.Invoke(
                    "preload_manifest_discovery_failed",
                    $"type={error.GetType().Name}");
                return result;
            }

            if (manifestPaths.Length > MaximumManifestCount)
            {
                result.AddError(
                    "manifest_limit_exceeded",
                    $"At most {MaximumManifestCount} manifests are accepted.");
                manifestPaths = manifestPaths.Take(MaximumManifestCount).ToArray();
            }

            var manifestIds = new HashSet<string>(StringComparer.Ordinal);
            long aggregateBytes = 0;
            foreach (var manifestPath in manifestPaths)
            {
                var manifestName = Path.GetFileName(manifestPath);
                trace?.Invoke("preload_manifest_discovered", $"manifest={manifestName}");
                JObject document;
                try
                {
                    var manifestText = ReadUtf8Bounded(
                        manifestPath,
                        MaximumManifestBytes,
                        out _,
                        out _);
                    using (var stringReader = new StringReader(manifestText))
                    using (var jsonReader = new JsonTextReader(stringReader))
                    {
                        document = JObject.Load(
                            jsonReader,
                            new JsonLoadSettings
                            {
                                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                            });
                    }
                }
                catch (Exception error) when (
                    error is IOException ||
                    error is UnauthorizedAccessException ||
                    error is DecoderFallbackException ||
                    error is JsonException ||
                    error is InvalidDataException)
                {
                    result.AddError(
                        "manifest_invalid",
                        $"Manifest '{manifestName}' could not be read as bounded UTF-8 JSON.");
                    trace?.Invoke(
                        "preload_manifest_rejected",
                        $"manifest={manifestName} reason={error.GetType().Name}");
                    continue;
                }

                var manifestId = document.Value<string>("id");
                if (!IsStrictIdentifier(manifestId))
                {
                    result.AddError(
                        "manifest_id_invalid",
                        $"Manifest '{manifestName}' has an invalid id.");
                    trace?.Invoke(
                        "preload_manifest_rejected",
                        $"manifest={manifestName} reason=invalid_id");
                    continue;
                }
                if (!manifestIds.Add(manifestId!))
                {
                    result.AddError(
                        "manifest_id_duplicate",
                        $"Manifest id '{manifestId}' is duplicated.");
                    trace?.Invoke(
                        "preload_manifest_rejected",
                        $"manifest={manifestName} reason=duplicate_id");
                    continue;
                }

                var snapshot = CreateSnapshot(gtaProcessId, manifestId!);
                var snapshotErrors = (JArray)snapshot["errors"]!;
                var snapshotEntries = (JArray)snapshot["entries"]!;
                var manifestComplete = true;

                if (document.Value<int?>("schema_version") != SchemaVersion)
                {
                    AddSnapshotError(
                        snapshotErrors,
                        "schema_version_unsupported",
                        $"schema_version must be {SchemaVersion}.");
                    manifestComplete = false;
                }

                var entries = document["entries"] as JArray;
                if (entries == null)
                {
                    AddSnapshotError(snapshotErrors, "entries_invalid", "entries must be an array.");
                    manifestComplete = false;
                    entries = new JArray();
                }
                if (entries.Count > MaximumEntriesPerManifest)
                {
                    AddSnapshotError(
                        snapshotErrors,
                        "entry_limit_exceeded",
                        $"At most {MaximumEntriesPerManifest} entries are accepted.");
                    manifestComplete = false;
                }

                var entryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entryToken in entries.Take(MaximumEntriesPerManifest))
                {
                    if (!(entryToken is JObject entry))
                    {
                        AddSnapshotError(snapshotErrors, "entry_invalid", "Every entry must be an object.");
                        manifestComplete = false;
                        continue;
                    }

                    var entryId = entry.Value<string>("id");
                    var portablePath = entry.Value<string>("path");
                    var kind = entry.Value<string>("kind");
                    var required = entry.Value<bool?>("required");
                    var maxBytesToken = entry["max_bytes"];
                    if (!IsStrictIdentifier(entryId) || !entryIds.Add(entryId!))
                    {
                        AddSnapshotError(
                            snapshotErrors,
                            "entry_id_invalid",
                            "Entry ids must be unique strict lowercase identifiers.",
                            entryId);
                        manifestComplete = false;
                        continue;
                    }
                    if (!TryNormalizePortablePath(portablePath, out var normalizedPath))
                    {
                        AddSnapshotError(
                            snapshotErrors,
                            "entry_path_invalid",
                            "Entry paths must be relative to the GTA root and may not contain traversal.",
                            entryId);
                        manifestComplete = false;
                        continue;
                    }
                    if (!string.Equals(kind, "text", StringComparison.Ordinal) &&
                        !string.Equals(kind, "json", StringComparison.Ordinal))
                    {
                        AddSnapshotError(
                            snapshotErrors,
                            "entry_kind_invalid",
                            "Entry kind must be text or json.",
                            entryId);
                        manifestComplete = false;
                        continue;
                    }
                    if (!required.HasValue ||
                        maxBytesToken == null ||
                        maxBytesToken.Type != JTokenType.Integer ||
                        !TryGetPositiveInt64(maxBytesToken, out var declaredMaximum) ||
                        declaredMaximum > MaximumEntryBytes)
                    {
                        AddSnapshotError(
                            snapshotErrors,
                            "entry_bounds_invalid",
                            $"max_bytes must be between 1 and {MaximumEntryBytes}.",
                            entryId);
                        manifestComplete = false;
                        continue;
                    }

                    var sourcePath = Path.GetFullPath(Path.Combine(
                        resolvedGtaRoot,
                        normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsContainedPath(resolvedGtaRoot, sourcePath) ||
                        ContainsReparsePoint(resolvedGtaRoot, sourcePath))
                    {
                        AddSnapshotError(
                            snapshotErrors,
                            "entry_path_escaped",
                            "Entry path did not resolve to a regular path beneath the GTA root.",
                            entryId);
                        manifestComplete = false;
                        continue;
                    }

                    if (!File.Exists(sourcePath))
                    {
                        if (required.Value)
                        {
                            AddSnapshotError(
                                snapshotErrors,
                                "required_entry_missing",
                                "A required source file is missing.",
                                entryId);
                            manifestComplete = false;
                        }
                        continue;
                    }

                    try
                    {
                        var content = ReadUtf8Bounded(
                            sourcePath,
                            declaredMaximum,
                            out var sourceLength,
                            out var lastWriteUtcTicks);
                        if (aggregateBytes > MaximumAggregateBytes - sourceLength)
                        {
                            AddSnapshotError(
                                snapshotErrors,
                                "aggregate_limit_exceeded",
                                $"Aggregate preload content may not exceed {MaximumAggregateBytes} bytes.",
                                entryId);
                            manifestComplete = false;
                            continue;
                        }
                        if (string.Equals(kind, "json", StringComparison.Ordinal))
                        {
                            using (var reader = new JsonTextReader(new StringReader(content)))
                            {
                                JToken.ReadFrom(
                                    reader,
                                    new JsonLoadSettings
                                    {
                                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                                    });
                                if (reader.Read())
                                {
                                    throw new JsonReaderException("Unexpected content follows the JSON value.");
                                }
                            }
                        }

                        aggregateBytes += sourceLength;
                        snapshotEntries.Add(new JObject
                        {
                            ["id"] = entryId,
                            ["path"] = normalizedPath,
                            ["kind"] = kind,
                            ["content"] = content,
                            ["sha256"] = ComputeSha256(content),
                            ["length"] = sourceLength,
                            ["last_write_utc_ticks"] = lastWriteUtcTicks,
                        });
                    }
                    catch (Exception error) when (
                        error is IOException ||
                        error is UnauthorizedAccessException ||
                        error is DecoderFallbackException ||
                        error is InvalidDataException ||
                        error is JsonException)
                    {
                        AddSnapshotError(
                            snapshotErrors,
                            string.Equals(kind, "json", StringComparison.Ordinal)
                                ? "entry_json_invalid"
                                : "entry_read_failed",
                            "The source file failed bounded validation.",
                            entryId);
                        manifestComplete = false;
                        trace?.Invoke(
                            "preload_entry_rejected",
                            $"manifest={manifestId} entry={entryId} reason={error.GetType().Name}");
                    }
                }

                snapshot["complete"] = manifestComplete;
                var snapshotPath = Path.Combine(processDirectory, manifestId + ".snapshot.json");
                try
                {
                    WriteAtomicJson(snapshotPath, snapshot);
                }
                catch (InvalidDataException)
                {
                    snapshotEntries.Clear();
                    AddSnapshotError(
                        snapshotErrors,
                        "snapshot_limit_exceeded",
                        $"Serialized snapshots may not exceed {MaximumSnapshotBytes} bytes.");
                    manifestComplete = false;
                    snapshot["complete"] = false;
                    WriteAtomicJson(snapshotPath, snapshot);
                }
                result.AddSnapshot(snapshotPath, manifestComplete, snapshotEntries.Count);
                trace?.Invoke(
                    "preload_snapshot_written",
                    $"manifest={manifestId} entries={snapshotEntries.Count} complete={manifestComplete}");
            }

            result.AggregateBytes = aggregateBytes;
            trace?.Invoke(
                "preload_data_complete",
                $"manifests={result.SnapshotPaths.Count} entries={result.EntryCount} " +
                $"bytes={aggregateBytes} complete={result.Complete}");
            return result;
        }

        public static int CleanStaleProcessDirectories(
            string cacheRoot,
            int currentProcessId,
            Func<int, bool>? processIsAlive = null)
        {
            ValidateProcessId(currentProcessId);
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot))
            {
                return 0;
            }

            processIsAlive = processIsAlive ?? IsProcessAlive;
            var deleted = 0;
            foreach (var directory in Directory.EnumerateDirectories(
                Path.GetFullPath(cacheRoot),
                "*",
                SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (!int.TryParse(
                        name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var processId) ||
                    processId <= 0 ||
                    processId == currentProcessId ||
                    HasReparsePoint(directory) ||
                    processIsAlive(processId))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, true);
                    deleted++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return deleted;
        }

        private static string PrepareProcessDirectory(string cacheRoot, int processId)
        {
            var processDirectory = Path.Combine(
                Path.GetFullPath(cacheRoot),
                processId.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(processDirectory))
            {
                if (HasReparsePoint(processDirectory))
                {
                    throw new InvalidOperationException(
                        "The process preload directory may not be a reparse point.");
                }
                Directory.Delete(processDirectory, true);
            }
            Directory.CreateDirectory(processDirectory);
            return processDirectory;
        }

        private static JObject CreateSnapshot(int processId, string manifestId) => new JObject
        {
            ["schema_version"] = SchemaVersion,
            ["producer"] = "reactorv-preloader",
            ["gta_process_id"] = processId,
            ["manifest_id"] = manifestId,
            ["created_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["entries"] = new JArray(),
            ["errors"] = new JArray(),
            ["complete"] = false,
        };

        private static void AddSnapshotError(
            JArray errors,
            string code,
            string message,
            string? entryId = null)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            };
            if (IsStrictIdentifier(entryId))
            {
                error["entry_id"] = entryId;
            }
            errors.Add(error);
        }

        private static void WriteAtomicJson(string destinationPath, JObject document)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The snapshot destination has no parent directory.");
            }
            Directory.CreateDirectory(directory!);
            string serialized;
            using (var text = new StringWriter(CultureInfo.InvariantCulture))
            using (var jsonWriter = new JsonTextWriter(text) { Formatting = Formatting.Indented })
            {
                document.WriteTo(jsonWriter);
                jsonWriter.Flush();
                serialized = text.ToString();
            }
            if (StrictUtf8.GetByteCount(serialized) > MaximumSnapshotBytes)
            {
                throw new InvalidDataException("The serialized snapshot exceeds its byte limit.");
            }

            var temporaryPath = Path.Combine(
                directory!,
                "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(serialized);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, null);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static string ReadUtf8Bounded(
            string path,
            long maximumBytes,
            out long sourceLength,
            out long lastWriteUtcTicks)
        {
            var before = new FileInfo(path);
            sourceLength = before.Length;
            lastWriteUtcTicks = before.LastWriteTimeUtc.Ticks;
            if (sourceLength < 0 || sourceLength > maximumBytes)
            {
                throw new InvalidDataException("The source exceeds its byte limit.");
            }

            byte[] bytes;
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length != sourceLength)
                {
                    throw new IOException("The source changed before it was read.");
                }
                bytes = new byte[sourceLength];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count <= 0)
                    {
                        throw new EndOfStreamException("The source ended before its declared length.");
                    }
                    offset += count;
                }
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("The source grew past its byte limit.");
                }
            }

            var after = new FileInfo(path);
            if (after.Length != sourceLength || after.LastWriteTimeUtc.Ticks != lastWriteUtcTicks)
            {
                throw new IOException("The source changed while it was read.");
            }
            return StrictUtf8.GetString(bytes);
        }

        private static string ComputeSha256(string content)
        {
            using (var algorithm = SHA256.Create())
            {
                var digest = algorithm.ComputeHash(StrictUtf8.GetBytes(content));
                var builder = new StringBuilder(digest.Length * 2);
                foreach (var value in digest)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private static bool TryNormalizePortablePath(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value) ||
                Path.IsPathRooted(value) ||
                value!.IndexOf('\0') >= 0 ||
                value.Contains(":") ||
                value.StartsWith("/", StringComparison.Ordinal) ||
                value.StartsWith("\\", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = value
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 ||
                segments.Any(segment =>
                    segment == "." ||
                    segment == ".." ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                return false;
            }
            normalized = string.Join("/", segments);
            return true;
        }

        private static bool IsContainedPath(string root, string candidate)
        {
            var rootWithSeparator = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsReparsePoint(string root, string candidate)
        {
            var relative = candidate.Substring(
                Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(root);
            foreach (var segment in relative.Split(new[]
            {
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
            }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current)) && HasReparsePoint(current))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool IsStrictIdentifier(string? value) =>
            !string.IsNullOrWhiteSpace(value) && IdentifierPattern.IsMatch(value!);

        private static bool TryGetPositiveInt64(JToken token, out long value)
        {
            try
            {
                value = token.Value<long>();
                return value > 0;
            }
            catch (Exception error) when (
                error is OverflowException ||
                error is FormatException ||
                error is InvalidCastException)
            {
                value = 0;
                return false;
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true;
            }
        }

        private static void ValidateProcessId(int processId)
        {
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processId),
                    "The GTA process identifier must be positive.");
            }
        }
    }

    public sealed class PreloadDataBuildResult
    {
        private readonly List<string> _snapshotPaths = new List<string>();
        private readonly List<string> _errors = new List<string>();
        private bool _snapshotsComplete = true;

        internal PreloadDataBuildResult(int processId, string processDirectory)
        {
            GtaProcessId = processId;
            ProcessDirectory = processDirectory;
        }

        public int GtaProcessId { get; }
        public string ProcessDirectory { get; }
        public IReadOnlyList<string> SnapshotPaths => _snapshotPaths;
        public IReadOnlyList<string> Errors => _errors;
        public int EntryCount { get; private set; }
        public long AggregateBytes { get; internal set; }
        public bool Complete => _errors.Count == 0 && _snapshotsComplete;

        internal void AddSnapshot(string path, bool complete, int entryCount)
        {
            _snapshotPaths.Add(path);
            EntryCount += entryCount;
            _snapshotsComplete &= complete;
        }

        internal void AddError(string code, string message)
        {
            _errors.Add(code + ": " + message);
        }
    }
}
