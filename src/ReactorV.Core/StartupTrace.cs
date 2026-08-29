using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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
        private static readonly int[] RetryDelaysMilliseconds = { 0, 2, 8 };
        private static readonly object Sync = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly DateTime SessionStartedUtc = DateTime.UtcNow;
        private static readonly int ProcessId = ResolveProcessId();
        private static int _emergencySequence;

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
            var aggregatePath = Path.Combine(directory, SafeFileName(aggregateFileName, "reactorv-runtime.log"));
            var sessionPath = Path.Combine(directory, SessionFileName);
            var aggregateWritten = TryAppend(aggregatePath, line);
            var sessionWritten = TryAppend(sessionPath, line);
            if (aggregateWritten || sessionWritten)
            {
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
