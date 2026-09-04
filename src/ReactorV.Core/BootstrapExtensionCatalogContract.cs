using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace RageWebUI.Core
{
    /// <summary>
    /// Produces a bounded, read-only extension summary from data that already
    /// passed the preloader manifest pipeline. This gives the main-menu About
    /// surface useful discovery before SHVDN connects without scanning scripts,
    /// loading an assembly, or granting a package any operation authority.
    /// </summary>
    public static class BootstrapExtensionCatalogContract
    {
        public const string Method = "extensions.list";
        public const string EventName = "host.extensionCatalog";
        public const string Authority = "bootstrap-preload";
        public const int MaximumExtensions = 128;

        private const string RegistryEntryId = "extension-registry";
        private static readonly Regex IdentifierPattern = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryBuildFromSnapshots(
            PreloadDataBuildResult? result,
            out JObject? catalog,
            out string outcome)
        {
            catalog = null;
            outcome = "preload-result-unavailable";
            if (result == null || result.GtaProcessId <= 0)
                return false;
            if (result.SnapshotPaths.Count == 0)
            {
                outcome = "registry-entry-absent";
                return false;
            }
            if (result.SnapshotPaths.Count > PreloadDataCache.MaximumManifestCount)
            {
                outcome = "snapshot-limit-exceeded";
                return false;
            }

            var summaries = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var registryCount = 0;
            try
            {
                foreach (var snapshotPath in result.SnapshotPaths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryReadSnapshot(snapshotPath, result.GtaProcessId, out var snapshot))
                    {
                        outcome = "snapshot-invalid";
                        return false;
                    }

                    var entries = snapshot!["entries"] as JArray;
                    if (entries == null || entries.Count > PreloadDataCache.MaximumEntriesPerManifest)
                    {
                        outcome = "snapshot-entries-invalid";
                        return false;
                    }
                    foreach (var entry in entries.OfType<JObject>())
                    {
                        if (!string.Equals(
                                entry.Value<string>("id"),
                                RegistryEntryId,
                                StringComparison.Ordinal))
                            continue;
                        registryCount++;
                        if (!TryReadRegistryEntry(entry, summaries, out outcome))
                            return false;
                    }
                }
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is JsonException ||
                error is DecoderFallbackException ||
                error is CryptographicException)
            {
                outcome = "snapshot-read-failed";
                return false;
            }

            if (registryCount == 0)
            {
                outcome = "registry-entry-absent";
                return false;
            }

            var items = new JArray(summaries.Values
                .OrderBy(summary => summary.Value<string>("name"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(summary => summary.Value<string>("id"), StringComparer.Ordinal)
                .Select(summary => summary.DeepClone()));
            catalog = new JObject
            {
                ["total"] = items.Count,
                ["items"] = items,
                // The page uses this marker only to label the source. It does
                // not imply that the managed extension is registered or ready.
                ["authority"] = Authority,
            };
            outcome = "ready";
            return true;
        }

        /// <summary>
        /// Handles exactly the read-only extensions.list request while the
        /// managed provider is absent. Once the provider connects the host does
        /// not call this method, preserving the live registry as authority.
        /// </summary>
        public static bool TryCreateLocalResponse(
            string json,
            JObject? catalog,
            out string? responseJson)
        {
            responseJson = null;
            if (!BridgeProtocol.TryParseRequest(json, out var request, out _) ||
                request == null ||
                !string.Equals(request.Method, Method, StringComparison.Ordinal))
                return false;

            BridgeResponse response;
            if (request.Parameters.Count != 0)
            {
                response = BridgeResponse.Failure(
                    request.Id,
                    new BridgeError(
                        "invalid_params",
                        "extensions.list does not accept parameters."),
                    request.ProtocolVersion);
            }
            else if (catalog == null)
            {
                response = BridgeResponse.Failure(
                    request.Id,
                    new BridgeError(
                        "bootstrap_catalog_preparing",
                        "The read-only bootstrap mod catalog is still preparing.",
                        retryable: true),
                    request.ProtocolVersion);
            }
            else
            {
                response = BridgeResponse.Success(
                    request.Id,
                    catalog.DeepClone(),
                    request.ProtocolVersion);
            }
            responseJson = BridgeProtocol.SerializeResponse(response);
            return true;
        }

        public static string SerializeAvailableEvent(JObject catalog) =>
            BridgeProtocol.SerializeEvent(
                EventName,
                new JObject
                {
                    ["available"] = true,
                    ["total"] = catalog?.Value<int?>("total") ?? 0,
                    ["authority"] = Authority,
                },
                BridgeProtocol.CurrentProtocolVersion);

        private static bool TryReadSnapshot(
            string path,
            int processId,
            out JObject? snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > PreloadDataCache.MaximumSnapshotBytes)
                return false;
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            using (var text = new StreamReader(stream, StrictUtf8, false))
            using (var reader = new JsonTextReader(text)
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 32,
            })
            {
                var token = JToken.ReadFrom(
                    reader,
                    new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    });
                if (reader.Read() || token.Type != JTokenType.Object) return false;
                snapshot = (JObject)token;
            }
            return snapshot.Value<int?>("schema_version") == PreloadDataCache.SchemaVersion &&
                string.Equals(
                    snapshot.Value<string>("producer"),
                    "reactorv-preloader",
                    StringComparison.Ordinal) &&
                snapshot.Value<int?>("gta_process_id") == processId;
        }

        private static bool TryReadRegistryEntry(
            JObject entry,
            Dictionary<string, JObject> summaries,
            out string outcome)
        {
            outcome = "registry-entry-invalid";
            if (!string.Equals(entry.Value<string>("kind"), "json", StringComparison.Ordinal) ||
                entry["content"]?.Type != JTokenType.String ||
                entry["length"]?.Type != JTokenType.Integer)
                return false;
            var content = entry.Value<string>("content")!;
            var length = entry.Value<long>("length");
            if (length <= 0 || length > PreloadDataCache.MaximumEntryBytes ||
                StrictUtf8.GetByteCount(content) != length ||
                !string.Equals(
                    entry.Value<string>("sha256"),
                    ComputeSha256(content),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            JObject registry;
            using (var text = new StringReader(content))
            using (var reader = new JsonTextReader(text)
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 32,
            })
            {
                var token = JToken.ReadFrom(
                    reader,
                    new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    });
                if (reader.Read() || token.Type != JTokenType.Object) return false;
                registry = (JObject)token;
            }
            if (registry.Value<int?>("api_version") != 1 ||
                !(registry["extensions"] is JArray extensions) ||
                extensions.Count > MaximumExtensions)
                return false;

            foreach (var candidate in extensions)
            {
                if (!(candidate is JObject extension) ||
                    !TryBoundedString(extension["id"], 64, out var id) ||
                    !IdentifierPattern.IsMatch(id!) ||
                    !TryBoundedString(extension["name"], 120, out var name) ||
                    !TryBoundedString(extension["version"], 64, out var version) ||
                    !TryPositiveInt(extension["api_version"], 64, out var apiVersion) ||
                    extension["enabled"]?.Type != JTokenType.Boolean ||
                    summaries.ContainsKey(id!))
                    return false;
                if (summaries.Count >= MaximumExtensions)
                {
                    outcome = "extension-limit-exceeded";
                    return false;
                }
                summaries.Add(id!, new JObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["version"] = version,
                    ["extensionApiVersion"] = apiVersion,
                    ["actionCount"] = 0,
                    ["eventCount"] = 0,
                    ["menuCount"] = 0,
                });
            }
            outcome = "ready";
            return true;
        }

        private static bool TryBoundedString(
            JToken? token,
            int maximumLength,
            out string? value)
        {
            value = token?.Type == JTokenType.String ? token.Value<string>() : null;
            return !string.IsNullOrEmpty(value) &&
                value!.Length <= maximumLength &&
                string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
                !value.Any(character => char.IsControl(character));
        }

        private static bool TryPositiveInt(JToken? token, int maximum, out int value)
        {
            value = 0;
            if (token?.Type != JTokenType.Integer) return false;
            try { value = token.Value<int>(); }
            catch (Exception error) when (
                error is OverflowException ||
                error is FormatException ||
                error is InvalidCastException)
            {
                return false;
            }
            return value > 0 && value <= maximum;
        }

        private static string ComputeSha256(string content)
        {
            using (var algorithm = SHA256.Create())
            {
                return string.Concat(algorithm
                    .ComputeHash(StrictUtf8.GetBytes(content))
                    .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }
    }
}
