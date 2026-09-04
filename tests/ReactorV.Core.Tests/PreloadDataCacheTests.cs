using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        Assert.True(PreloadDataCache.IsReadyForHandoff(result.GtaProcessId, result));
        using (var ready = PreloadHandoff.CreatePreloadDataReadyWaitHandle(result.GtaProcessId))
        {
            ready.Reset();
            Assert.True(PreloadHandoff.TrySignalPreloadDataReady(result.GtaProcessId, result));
            Assert.True(ready.WaitOne(0));
        }
        var mismatchedProcessId = result.GtaProcessId == int.MaxValue
            ? result.GtaProcessId - 1
            : result.GtaProcessId + 1;
        using (var mismatched = PreloadHandoff.CreatePreloadDataReadyWaitHandle(mismatchedProcessId))
        {
            mismatched.Reset();
            Assert.False(PreloadDataCache.IsReadyForHandoff(mismatchedProcessId, result));
            Assert.False(PreloadHandoff.TrySignalPreloadDataReady(mismatchedProcessId, result));
            Assert.False(mismatched.WaitOne(0));
        }
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
    public void Snapshot_hash_length_and_timestamp_describe_exact_utf8_source()
    {
        var fixture = CreateFixture();
        const string content = "café 東京\n";
        const string portablePath = "scripts/ALLIN1/labels.txt";
        WriteSource(fixture.GtaRoot, portablePath, content);
        var sourcePath = Path.Combine(
            fixture.GtaRoot,
            portablePath.Replace('/', Path.DirectorySeparatorChar));
        var expectedTicks = new FileInfo(sourcePath).LastWriteTimeUtc.Ticks;
        WriteManifest(fixture.GtaRoot, "utf8", new JArray
        {
            Entry("labels", portablePath, "text", true, 1024),
        });

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.True(PreloadDataCache.IsReadyForHandoff(result.GtaProcessId, result));
        var snapshot = JObject.Parse(File.ReadAllText(Assert.Single(result.SnapshotPaths)));
        var entry = snapshot["entries"]![0]!;
        Assert.Equal(content, entry.Value<string>("content"));
        Assert.Equal(Encoding.UTF8.GetByteCount(content), entry.Value<long>("length"));
        Assert.Equal(Sha256(content), entry.Value<string>("sha256"));
        Assert.Equal(expectedTicks, entry.Value<long>("last_write_utc_ticks"));
    }

    [Fact]
    public async Task Async_build_runs_bounded_file_work_off_the_calling_thread()
    {
        var fixture = CreateFixture();
        WriteSource(fixture.GtaRoot, "scripts/value.txt", "ready");
        WriteManifest(fixture.GtaRoot, "async", new JArray
        {
            Entry("value", "scripts/value.txt", "text", true, 16),
        });
        var callingThread = Thread.CurrentThread.ManagedThreadId;
        var workerThread = 0;
        using var started = new ManualResetEventSlim();

        var build = PreloadDataCache.BuildAsync(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot,
            (stage, _) =>
            {
                if (stage == "preload_data_begin")
                {
                    workerThread = Thread.CurrentThread.ManagedThreadId;
                    started.Set();
                }
            });

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(callingThread, workerThread);
        Assert.True((await build).Complete);
    }

    [Fact]
    public async Task Async_build_honors_cancellation_before_touching_process_cache()
    {
        var fixture = CreateFixture();
        var processId = int.MaxValue - 101;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PreloadDataCache.BuildAsync(
                fixture.GtaRoot,
                processId,
                fixture.CacheRoot,
                cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(fixture.CacheRoot, processId.ToString())));
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
        Assert.False(PreloadDataCache.IsReadyForHandoff(result.GtaProcessId, result));
        using (var ready = PreloadHandoff.CreatePreloadDataReadyWaitHandle(result.GtaProcessId))
        {
            ready.Reset();
            Assert.False(PreloadHandoff.TrySignalPreloadDataReady(result.GtaProcessId, result));
            Assert.False(ready.WaitOne(0));
        }
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
    public void Missing_manifest_directory_is_a_distinct_successful_empty_preload()
    {
        var fixture = CreateFixture();
        var stages = new System.Collections.Generic.List<string>();

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot,
            (stage, _) => stages.Add(stage));

        Assert.True(result.Complete);
        Assert.Empty(result.SnapshotPaths);
        Assert.False(PreloadDataCache.IsReadyForHandoff(result.GtaProcessId, result));
        using (var ready = PreloadHandoff.CreatePreloadDataReadyWaitHandle(result.GtaProcessId))
        {
            ready.Reset();
            Assert.False(PreloadHandoff.TrySignalPreloadDataReady(result.GtaProcessId, result));
            Assert.False(ready.WaitOne(0));
        }
        Assert.Contains("preload_manifest_directory_absent", stages);
        Assert.Contains("preload_data_complete", stages);
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
    public void Aggregate_content_over_sixteen_mib_is_rejected_before_embedding()
    {
        var fixture = CreateFixture();
        var entries = new JArray();
        var fullContent = new string('a', (int)PreloadDataCache.MaximumEntryBytes);
        for (var index = 0; index < 4; index++)
        {
            var portablePath = "scripts/full-" + index + ".txt";
            WriteSource(fixture.GtaRoot, portablePath, fullContent);
            entries.Add(Entry("full-" + index, portablePath, "text", true, PreloadDataCache.MaximumEntryBytes));
        }
        WriteSource(fixture.GtaRoot, "scripts/overflow.txt", "x");
        entries.Add(Entry("overflow", "scripts/overflow.txt", "text", true, 1));
        WriteManifest(fixture.GtaRoot, "aggregate", entries);

        var result = PreloadDataCache.Build(
            fixture.GtaRoot,
            Process.GetCurrentProcess().Id,
            fixture.CacheRoot);

        Assert.False(result.Complete);
        Assert.Equal(PreloadDataCache.MaximumAggregateBytes, result.AggregateBytes);
        var snapshot = JObject.Parse(File.ReadAllText(Assert.Single(result.SnapshotPaths)));
        Assert.Equal(4, snapshot["entries"]!.Count());
        Assert.Contains(snapshot["errors"]!, error =>
            error!.Value<string>("code") == "aggregate_limit_exceeded" &&
            error.Value<string>("entry_id") == "overflow");
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
