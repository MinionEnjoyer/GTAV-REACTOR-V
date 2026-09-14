using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>Reads a small, time-bounded subset of locally relevant Windows event evidence.</summary>
    public static class SecurityEvidence
    {
        private const int MaximumEventsPerLog = 2048;
        private const int MaximumMatches = 128;
        private static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(24);

        public static JObject Collect(DateTime startUtc, DateTime endUtc, string gameRoot) =>
            Collect(startUtc, endUtc, gameRoot, CancellationToken.None);

        public static JObject Collect(DateTime startUtc, DateTime endUtc, string gameRoot, CancellationToken token)
        {
            startUtc = startUtc.ToUniversalTime();
            endUtc = endUtc.ToUniversalTime();
            if (endUtc < startUtc) throw new ArgumentException("End time precedes start time.", nameof(endUtc));
            if (endUtc - startUtc > MaximumWindow) throw new ArgumentException("Security evidence is limited to a 24-hour window.", nameof(endUtc));

            var result = new JObject {
                ["requested"] = true,
                ["startUtc"] = startUtc.ToString("o"),
                ["endUtc"] = endUtc.ToString("o"),
                ["status"] = "completed",
                ["matches"] = new JArray(),
                ["unavailable"] = new JArray(),
                ["maximumEventsPerLog"] = MaximumEventsPerLog,
                ["maximumMatches"] = MaximumMatches
            };
            var matches = (JArray)result["matches"]!;
            var unavailable = (JArray)result["unavailable"]!;
            foreach (var logName in RelevantLogs())
            {
                int examined = 0;
                try
                {
                    var query = new EventLogQuery(logName, PathType.LogName, TimeWindowXPath(startUtc, endUtc)) { ReverseDirection = true };
                    using var reader = new EventLogReader(query);
                    while (examined < MaximumEventsPerLog && matches.Count < MaximumMatches)
                    {
                        token.ThrowIfCancellationRequested();
                        using var record = reader.ReadEvent();
                        if (record == null) break;
                        examined++;
                        var utc = record.TimeCreated?.ToUniversalTime();
                        if (!utc.HasValue) continue;
                        var source = record.ProviderName ?? "";
                        string message;
                        try { message = record.FormatDescription() ?? ""; }
                        catch (EventLogException error)
                        {
                            unavailable.Add(new JObject { ["log"] = logName, ["reason"] = "message-" + error.GetType().Name });
                            continue;
                        }
                        if (!IsRelevant(source, message)) continue;
                        matches.Add(new JObject {
                            ["log"] = logName, ["utc"] = utc.Value.ToString("o"), ["eventId"] = record.Id,
                            ["source"] = DiagnosticIO.Redact(source, gameRoot),
                            ["message"] = DiagnosticIO.Redact(message, gameRoot)
                        });
                    }
                    if (examined >= MaximumEventsPerLog) result["scanCapReached"] = true;
                    if (matches.Count >= MaximumMatches) result["truncated"] = true;
                }
                catch (Exception error) when (error is EventLogException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is PlatformNotSupportedException)
                {
                    unavailable.Add(new JObject { ["log"] = logName, ["reason"] = error.GetType().Name });
                }
            }
            if (matches.Count == 0) result["noRelevantEventsFound"] = true;
            if (unavailable.Count > 0) result["status"] = "completed-with-unavailable-evidence";
            result["limitation"] = "No relevant events found does not establish that no security product or mitigation was involved.";
            return result;
        }

        private static string TimeWindowXPath(DateTime startUtc, DateTime endUtc) =>
            "*[System[TimeCreated[@SystemTime >= '" + startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") +
            "' and @SystemTime <= '" + endUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") + "']]]";

        private static IEnumerable<string> RelevantLogs()
        {
            yield return "Application";
            yield return "Microsoft-Windows-Windows Defender/Operational";
            yield return "Microsoft-Windows-CodeIntegrity/Operational";
            yield return "Microsoft-Windows-Security-Mitigations/KernelMode";
            yield return "Microsoft-Windows-Security-Mitigations/UserMode";
        }

        private static bool IsRelevant(string source, string message)
        {
            var value = source + "\n" + message;
            return value.IndexOf("ReactorV", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("RageWebUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("GTA5_Enhanced", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("GTA5.exe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("ReactorV.RenderHook", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
