using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class PreloadDataCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ReactorV-PreloadTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void Valid_manifest_produces_portable_atomic_snapshot()
    {
        var fixture = CreateFixture();
        WriteSource(fixture.GtaRoot, "scripts/ALLIN1/catalog.json", "{\"vehicles\":[\"adder\"]}");
        WriteManifest(fixture.GtaRoot, "allin1-core", new JArray
        {
            Entry("catalog", "scripts/ALLIN1/catalog.json", "json", true, 4096),
        });

        var stages = new System.Collections.Generic.List<string>();
        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot,
            (stage, _) => stages.Add(stage));

        Assert.True(result.Complete);
        Assert.Single(result.SnapshotPaths);
        Assert.Equal(1, result.EntryCount);
        var snapshotText = File.ReadAllText(result.SnapshotPaths[0]);
        var snapshot = JObject.Parse(snapshotText);
        Assert.Equal(1, snapshot.Value<int>("schema_version"));
        Assert.Equal("reactorv-preloader", snapshot.Value<string>("producer"));
        Assert.Equal("allin1-core", snapshot.Value<string>("manifest_id"));
        Assert.True(snapshot.Value<bool>("complete"));
        var entry = Assert.IsType<JObject>(snapshot["entries"]![0]);
        Assert.Equal(
            new[] { "content", "id", "kind", "last_write_utc_ticks", "length", "path", "sha256" },
            entry.Properties().Select(property => property.Name).OrderBy(value => value).ToArray());
        Assert.Equal("scripts/ALLIN1/catalog.json", entry.Value<string>("path"));
        Assert.Equal(Sha256(entry.Value<string>("content")!), entry.Value<string>("sha256"));
        Assert.DoesNotContain(fixture.GtaRoot, snapshotText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_length", snapshotText, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(result.SnapshotPaths[0])!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
        Assert.Contains("preload_data_begin", stages);
        Assert.Contains("preload_snapshot_written", stages);
        Assert.Contains("preload_data_complete", stages);
    }

    [Fact]
    public void Traversal_absolute_paths_and_reparse_escapes_fail_closed()
    {
        var fixture = CreateFixture();
        WriteManifest(fixture.GtaRoot, "unsafe", new JArray
        {
            Entry("traversal", "../outside.json", "json", true, 1024),
            Entry("absolute", Path.Combine(_root, "outside.json"), "json", true, 1024),
        });

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        var snapshot = JObject.Parse(File.ReadAllText(Assert.Single(result.SnapshotPaths)));
        Assert.Empty(snapshot["entries"]!);
        Assert.Equal(2, snapshot["errors"]!.Count());
        Assert.All(snapshot["errors"]!, error =>
            Assert.Equal("entry_path_invalid", error!.Value<string>("code")));
        Assert.DoesNotContain(_root, snapshot.ToString(Formatting.None), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Required_missing_is_incomplete_while_optional_missing_is_omitted()
    {
        var fixture = CreateFixture();
        WriteManifest(fixture.GtaRoot, "missing", new JArray
        {
            Entry("required", "scripts/missing-required.json", "json", true, 1024),
            Entry("optional", "scripts/missing-optional.json", "json", false, 1024),
        });

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        var snapshot = JObject.Parse(File.ReadAllText(Assert.Single(result.SnapshotPaths)));
        Assert.Empty(snapshot["entries"]!);
        var error = Assert.Single(snapshot["errors"]!);
        Assert.Equal("required_entry_missing", error!.Value<string>("code"));
        Assert.Equal("required", error.Value<string>("entry_id"));
    }

    [Fact]
    public void Invalid_json_and_entry_bounds_are_reported_without_content()
    {
        var fixture = CreateFixture();
        WriteSource(fixture.GtaRoot, "scripts/bad.json", "{not-json");
        WriteSource(fixture.GtaRoot, "scripts/too-large.txt", "abc");
        WriteManifest(fixture.GtaRoot, "validation", new JArray
        {
            Entry("bad-json", "scripts/bad.json", "json", true, 1024),
            Entry("too-large", "scripts/too-large.txt", "text", true, 2),
            Entry("bad-cap", "scripts/too-large.txt", "text", true, PreloadDataCache.MaximumEntryBytes + 1),
        });

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        var snapshot = JObject.Parse(File.ReadAllText(Assert.Single(result.SnapshotPaths)));
        Assert.Empty(snapshot["entries"]!);
        Assert.Equal(
            new[] { "entry_bounds_invalid", "entry_json_invalid", "entry_read_failed" },
            snapshot["errors"]!
                .Select(error => error!.Value<string>("code"))
                .OrderBy(value => value)
                .ToArray());
    }

    [Fact]
    public void Entry_and_manifest_count_limits_are_enforced()
    {
        var fixture = CreateFixture();
        var entries = new JArray();
        for (var index = 0; index < PreloadDataCache.MaximumEntriesPerManifest + 1; index++)
        {
            var id = "entry-" + index;
            var path = "scripts/data-" + index + ".txt";
            WriteSource(fixture.GtaRoot, path, "x");
            entries.Add(Entry(id, path, "text", true, 1));
        }
        WriteManifest(fixture.GtaRoot, "entry-cap", entries);
        for (var index = 0; index < PreloadDataCache.MaximumManifestCount; index++)
            WriteManifest(fixture.GtaRoot, "extra-" + index.ToString("D2"), new JArray());

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        Assert.Equal(PreloadDataCache.MaximumManifestCount, result.SnapshotPaths.Count);
        Assert.Contains(result.Errors, error => error.StartsWith("manifest_limit_exceeded", StringComparison.Ordinal));
        var limited = result.SnapshotPaths.Single(path => path.EndsWith("entry-cap.snapshot.json", StringComparison.Ordinal));
        var snapshot = JObject.Parse(File.ReadAllText(limited));
        Assert.False(snapshot.Value<bool>("complete"));
        Assert.Equal(PreloadDataCache.MaximumEntriesPerManifest, snapshot["entries"]!.Count());
    }

    [Fact]
    public void Escaped_serialization_overflow_is_replaced_by_bounded_error_snapshot()
    {
        var fixture = CreateFixture();
        var entries = new JArray();
        var content = new string('\n', (int)PreloadDataCache.MaximumEntryBytes);
        for (var index = 0; index < 4; index++)
        {
            var path = "scripts/escaped-" + index + ".txt";
            WriteSource(fixture.GtaRoot, path, content);
            entries.Add(Entry("escaped-" + index, path, "text", true, PreloadDataCache.MaximumEntryBytes));
        }
        WriteManifest(fixture.GtaRoot, "escaped", entries);

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        var snapshotPath = Assert.Single(result.SnapshotPaths);
        Assert.True(new FileInfo(snapshotPath).Length < PreloadDataCache.MaximumSnapshotBytes);
        var snapshot = JObject.Parse(File.ReadAllText(snapshotPath));
        Assert.Empty(snapshot["entries"]!);
        Assert.Contains(snapshot["errors"]!, error =>
            error!.Value<string>("code") == "snapshot_limit_exceeded");
    }

    [Fact]
    public void Process_snapshots_are_isolated_and_event_name_is_process_specific()
    {
        var fixture = CreateFixture();
        WriteSource(fixture.GtaRoot, "scripts/value.txt", "ready");
        WriteManifest(fixture.GtaRoot, "isolation", new JArray
        {
            Entry("value", "scripts/value.txt", "text", true, 16),
        });
        var current = Process.GetCurrentProcess().Id;
        var synthetic = int.MaxValue - 17;

        var first = PreloadDataCache.Build(fixture.GtaRoot, current, fixture.CacheRoot);
        var second = PreloadDataCache.Build(fixture.GtaRoot, synthetic, fixture.CacheRoot);

        Assert.NotEqual(first.ProcessDirectory, second.ProcessDirectory);
        Assert.True(File.Exists(Assert.Single(first.SnapshotPaths)));
        Assert.True(File.Exists(Assert.Single(second.SnapshotPaths)));
        Assert.Equal(
            $@"Local\ReactorV.PreloadDataReady.{current}",
            PreloadDataCache.ReadyEventName(current));
        Assert.Equal(
            PreloadDataCache.ReadyEventName(current),
            PreloadHandoff.PreloadDataReadyEventName(current));
    }

    [Fact]
    public void Stale_cleanup_only_removes_numeric_inactive_non_reparse_directories()
    {
        var cache = Path.Combine(_root, "cleanup");
        Directory.CreateDirectory(Path.Combine(cache, "101"));
        Directory.CreateDirectory(Path.Combine(cache, "202"));
        Directory.CreateDirectory(Path.Combine(cache, "not-a-pid"));

        var deleted = PreloadDataCache.CleanStaleProcessDirectories(
            cache,
            202,
            processId => processId == 303);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(Path.Combine(cache, "101")));
        Assert.True(Directory.Exists(Path.Combine(cache, "202")));
        Assert.True(Directory.Exists(Path.Combine(cache, "not-a-pid")));
    }

    [Fact]
    public void Invalid_manifest_json_and_identifier_never_create_ambiguous_snapshots()
    {
        var fixture = CreateFixture();
        var directory = ManifestDirectory(fixture.GtaRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{");
        File.WriteAllText(
            Path.Combine(directory, "bad-id.json"),
            new JObject
            {
                ["schema_version"] = 1,
                ["id"] = "Bad Id",
                ["entries"] = new JArray(),
            }.ToString(Formatting.None));

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        Assert.Empty(result.SnapshotPaths);
        Assert.Equal(2, result.Errors.Count);
        Assert.Empty(Directory.EnumerateFiles(result.ProcessDirectory));
    }

    [Fact]
    public void Gta_root_is_resolved_only_from_expected_plugin_layout()
    {
        var gta = Path.Combine(_root, "game");
        var plugin = Path.Combine(gta, "plugins", "ReactorV");
        Directory.CreateDirectory(plugin);

        Assert.Equal(
            Path.GetFullPath(gta),
            PreloadDataCache.ResolveGtaRootFromPreloaderDirectory(plugin));
        Assert.Throws<InvalidOperationException>(() =>
            PreloadDataCache.ResolveGtaRootFromPreloaderDirectory(Path.Combine(gta, "scripts")));
    }

    private (string GtaRoot, string CacheRoot) CreateFixture()
    {
        var gtaRoot = Path.Combine(_root, "gta-" + Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gtaRoot);
        Directory.CreateDirectory(cacheRoot);
        return (gtaRoot, cacheRoot);
    }

    private static JObject Entry(
        string id,
        string path,
        string kind,
        bool required,
        long maximumBytes) => new()
    {
        ["id"] = id,
        ["path"] = path,
        ["kind"] = kind,
        ["required"] = required,
        ["max_bytes"] = maximumBytes,
    };

    private static void WriteManifest(string gtaRoot, string id, JArray entries)
    {
        var directory = ManifestDirectory(gtaRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, id + ".json"),
            new JObject
            {
                ["schema_version"] = 1,
                ["id"] = id,
                ["entries"] = entries,
            }.ToString(Formatting.None),
            new UTF8Encoding(false));
    }

    private static string ManifestDirectory(string gtaRoot) =>
        Path.Combine(gtaRoot, "scripts", ".reactorv", "preload");

    private static void WriteSource(string gtaRoot, string portablePath, string content)
    {
        var path = Path.Combine(gtaRoot, portablePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Sha256(string content)
    {
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
