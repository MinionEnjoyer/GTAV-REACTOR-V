using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RageWebUI.Core
{
    /// <summary>
    /// Writes a unified, process-relative startup timeline without requiring
    /// exclusive ownership of a log file. Every entry is mirrored to a
    /// per-process session trace so a reader holding the aggregate log cannot
    /// make an entire launch disappear.
    /// </summary>
    public static class StartupTrace
    {
        internal const int MaximumConsoleEntries = 48;
        internal const int MaximumConsoleMessageLength = 240;
        internal const int MaximumRetainedSessionFiles = 48;

        private static readonly int[] RetryDelaysMilliseconds = { 0, 2, 8 };
        private static readonly object Sync = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly DateTime SessionStartedUtc = DateTime.UtcNow;
        private static readonly int ProcessId = ResolveProcessId();
        private static readonly Queue<JObject> ConsoleEntries =
            new Queue<JObject>();
        private static readonly HashSet<string> RetentionScheduledDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex AbsoluteWindowsPath = new Regex(
            @"(?i)(?:[a-z]:[\\/]|\\\\).*?(?=\s+[a-z_][a-z0-9_]*=|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static int _emergencySequence;
        private static long _consoleSequence;
        private static long _droppedConsoleEntries;

        public static string SessionId { get; } =
            $"{SessionStartedUtc:yyyyMMddTHHmmssfffZ}-{ProcessId}";

        public static string SessionFileName { get; } =
            $"reactorv-session-{SessionId}.log";

        /// <summary>
        /// Appends one stage to both the aggregate log and this process's
        /// session trace. Returns true when at least one durable copy was
        /// written. A uniquely named emergency record is attempted if both
        /// normal destinations are unavailable.
        /// </summary>
        public static bool Write(
            string directory,
            string aggregateFileName,
            string source,
            string stage,
            string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(Path.GetTempPath(), "ReactorV");
            }

            var line = FormatLine(source, stage, detail);
            RecordConsoleEntry(source, stage, detail);
            var aggregatePath = Path.Combine(directory, SafeFileName(aggregateFileName, "reactorv-runtime.log"));
            var sessionPath = Path.Combine(directory, SessionFileName);
            var aggregateWritten = TryAppend(aggregatePath, line);
            var sessionWritten = TryAppend(sessionPath, line);
            if (aggregateWritten || sessionWritten)
            {
                ScheduleSessionLogRetention(directory);
                return true;
            }

            var sequence = Interlocked.Increment(ref _emergencySequence);
            var emergencyName =
                $"reactorv-session-{SessionId}-fallback-{sequence.ToString("D4", CultureInfo.InvariantCulture)}.log";
            if (TryAppend(Path.Combine(directory, emergencyName), line))
            {
                return true;
            }

            // Local application data can be unavailable during early process
            // startup or under an unusual policy. Keep one last independent
            // destination so the failed stage still has a chance to survive.
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "ReactorV");
            return TryAppend(Path.Combine(temporaryDirectory, emergencyName), line);
        }

        /// <summary>
        /// Returns a bounded, sanitized copy of this process's recent startup
        /// trace. This intentionally reads no files: it is safe for the early
        /// bootstrap surface even when the managed gameplay provider has not
        /// connected yet.
        /// </summary>
        internal static JObject CreateConsoleSnapshot()
        {
            lock (Sync)
            {
                var entries = new JArray();
                foreach (var entry in ConsoleEntries) entries.Add(entry.DeepClone());
                return new JObject
                {
                    ["maxEntries"] = MaximumConsoleEntries,
                    ["dropped"] = _droppedConsoleEntries,
                    ["entries"] = entries,
                };
            }
        }

        internal static long ConsoleSequence
        {
            get
            {
                lock (Sync) return _consoleSequence;
            }
        }

        private static string FormatLine(string source, string stage, string? detail)
        {
            var suffix = string.IsNullOrWhiteSpace(detail)
                ? string.Empty
                : " " + SingleLine(detail!);
            return
                $"{DateTime.UtcNow:O} session={SessionId} pid={ProcessId} " +
                $"elapsed_ms={Clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"source={Token(source, "unknown")} stage={Token(stage, "unknown")}{suffix}" +
                Environment.NewLine;
        }

        private static bool TryAppend(string path, string line)
        {
            for (var attempt = 0; attempt < RetryDelaysMilliseconds.Length; attempt++)
            {
                if (RetryDelaysMilliseconds[attempt] > 0)
                {
                    Thread.Sleep(RetryDelaysMilliseconds[attempt]);
                }

                try
                {
                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        Directory.CreateDirectory(parent!);
                    }

                    lock (Sync)
                    {
                        using (var stream = new FileStream(
                            path,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        {
                            writer.Write(line);
                            writer.Flush();
                        }
                    }
                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (NotSupportedException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            return false;
        }

        private static void ScheduleSessionLogRetention(string directory)
        {
            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(directory);
            }
            catch
            {
                return;
            }

            lock (Sync)
            {
                if (!RetentionScheduledDirectories.Add(fullDirectory)) return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
                PruneSessionLogs(fullDirectory, DateTime.UtcNow));
        }

        /// <summary>
        /// Removes only old per-process trace files. The newest bounded set and
        /// anything touched during the current startup window are preserved so
        /// concurrent Reactor processes cannot delete one another's evidence.
        /// Aggregate logs and runtime caches are deliberately out of scope.
        /// </summary>
        internal static int PruneSessionLogs(string directory, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(directory)) return 0;
            try
            {
                if (!Directory.Exists(directory)) return 0;
                var activeCutoff = utcNow - TimeSpan.FromMinutes(15);
                var candidates = new DirectoryInfo(directory)
                    .EnumerateFiles("reactorv-session-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();
                var removed = 0;
                for (var index = MaximumRetainedSessionFiles;
                    index < candidates.Length;
                    index++)
                {
                    var candidate = candidates[index];
                    if (candidate.LastWriteTimeUtc >= activeCutoff ||
                        string.Equals(
                            candidate.Name,
                            SessionFileName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try
                    {
                        candidate.Delete();
                        removed++;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
                return removed;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static string SafeFileName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var candidate = Path.GetFileName(value);
            return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
        }

        private static string Token(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return SingleLine(value).Replace(' ', '_').Replace('=', '_');
        }

        private static string SingleLine(string value) =>
            value.Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static void RecordConsoleEntry(string source, string stage, string? detail)
        {
            var safeSource = ConsoleToken(source, "unknown", 48);
            var safeStage = ConsoleToken(stage, "unknown", 96);
            var safeMessage = SanitizeConsoleMessage(detail);
            lock (Sync)
            {
                var sequence = ++_consoleSequence;
                ConsoleEntries.Enqueue(new JObject
                {
                    ["sequence"] = sequence,
                    ["timestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["source"] = safeSource,
                    ["stage"] = safeStage,
                    ["message"] = safeMessage,
                });
                while (ConsoleEntries.Count > MaximumConsoleEntries)
                {
                    ConsoleEntries.Dequeue();
                    _droppedConsoleEntries++;
                }
            }
        }

        internal static string SanitizeConsoleMessage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var result = SingleLine(value!);
            if (result.Length > 4096) result = result.Substring(0, 4096);

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                result = ReplaceOrdinalIgnoreCase(result, profile, "<user-profile>");
            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user))
                result = ReplaceOrdinalIgnoreCase(result, user, "<user>");

            result = AbsoluteWindowsPath.Replace(result, "<local-path>");
            if (result.Length > MaximumConsoleMessageLength)
                result = result.Substring(0, MaximumConsoleMessageLength - 1) + "…";
            return result;
        }

        private static string ConsoleToken(string value, string fallback, int maximumLength)
        {
            var result = Token(value, fallback);
            return result.Length <= maximumLength
                ? result
                : result.Substring(0, maximumLength);
        }

        private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string replacement)
        {
            var start = 0;
            var builder = new StringBuilder(value.Length);
            while (true)
            {
                var found = value.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    builder.Append(value, start, value.Length - start);
                    return builder.ToString();
                }
                builder.Append(value, start, found - start);
                builder.Append(replacement);
                start = found + oldValue.Length;
            }
        }

        private static int ResolveProcessId()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

    }
}
