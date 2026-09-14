using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Issue1Isolation
{
    internal sealed class SessionCapture : IDisposable
    {
        private const int Limit = 8 * 1024 * 1024;
        private readonly Isolation isolation;
        private readonly Receipt receipt;
        private readonly Dictionary<string, byte[]> before = new Dictionary<string, byte[]>();
        private readonly HashSet<string> modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> notes = new List<string>();
        private readonly DateTime watchingSince = DateTime.UtcNow;
        private Process? game;
        private int pid;
        private DateTime started;
        private int samples;
        public bool Finished { get; private set; }
        public string Status { get; private set; } = "Ready. Launch one Story Mode test; keep this helper open.";
        private JObject? report;
        private readonly Dictionary<string, string> collected = new Dictionary<string, string>();
        private readonly string[] fixedLogs = {
            "ScriptHookV.log", "ScriptHookVDotNet.log", "scripts/ReactorV/ReactorV.RenderHook.log"
        };

        public SessionCapture(Isolation isolation, Receipt receipt)
        {
            this.isolation = isolation; this.receipt = receipt;
            foreach (var relative in fixedLogs)
                before[relative] = ReadBounded(isolation.Target(relative)) ?? Array.Empty<byte>();
        }

        private static byte[]? ReadBounded(string path)
        {
            Isolation.SafePath(path);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > Limit) throw new IOException("Log exceeds 8 MiB safety limit: " + Path.GetFileName(path));
            using var data = new MemoryStream(); stream.CopyTo(data); return data.ToArray();
        }

        public static void RequireStopped()
        {
            foreach (var name in new[] { "GTA5", "GTA5_Enhanced", "ReactorV.Preloader" })
            {
                var list = Process.GetProcessesByName(name);
                try { if (list.Length != 0) throw new InvalidOperationException("Close GTA and Reactor's preloader first. Still running: " + name); }
                finally { foreach (var p in list) p.Dispose(); }
            }
        }

        public void Poll()
        {
            if (Finished) return;
            if (game == null)
            {
                foreach (var p in Process.GetProcessesByName("GTA5_Enhanced"))
                {
                    bool keep = false;
                    try
                    {
                        if (!string.Equals(p.MainModule?.FileName, isolation.Target("GTA5_Enhanced.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                        if (game != null) throw new InvalidOperationException("Multiple matching GTA processes; capture cannot identify one isolated launch.");
                        if (p.StartTime.ToUniversalTime() < watchingSince.AddSeconds(-2))
                            throw new InvalidOperationException("This process predates the capture; restore and prepare a fresh test.");
                        p.EnableRaisingEvents = true; // Retain a handle so exit code survives process exit.
                        game = p; keep = true; pid = p.Id; started = p.StartTime.ToUniversalTime();
                        isolation.Verify(receipt);
                        Status = "Observing GTA PID " + pid + ". Close the game after checking Story Mode.";
                    }
                    finally { if (!keep) p.Dispose(); }
                }
            }
            if (game == null) return;
            if (game.HasExited) { Complete(); return; }
            try
            {
                game.Refresh();
                foreach (ProcessModule module in game.Modules) modules.Add(module.ModuleName);
                samples++;
            }
            catch (Exception e)
            {
                if (!game.HasExited && notes.Count < 16) notes.Add("Module snapshot unavailable: " + e.GetType().Name);
            }
        }

        internal static byte[] ChangedBytes(byte[] old, byte[] current)
        {
            bool prefix = current.Length >= old.Length;
            for (int i = 0; prefix && i < old.Length; i++) if (current[i] != old[i]) prefix = false;
            return prefix ? current.Skip(old.Length).ToArray() : current;
        }

        internal static bool HasPid(string line, int id) => Regex.IsMatch(line, @"\b(?:pid|target_pid|parent_pid)=" + id + @"\b", RegexOptions.CultureInvariant);

        private void Collect()
        {
            foreach (var relative in fixedLogs)
            {
                try
                {
                    var bytes = ReadBounded(isolation.Target(relative));
                    if (bytes != null)
                        collected["logs/" + Path.GetFileName(relative)] = Encoding.UTF8.GetString(ChangedBytes(before[relative], bytes));
                }
                catch (Exception e) { notes.Add("Not collected: " + relative + ": " + e.GetType().Name); }
            }
            foreach (var name in new[] { "ReactorV.NativeLifecycle.log", "ReactorV.NativeLifecycle.log.1" })
            {
                try
                {
                    var bytes = ReadBounded(isolation.Target("scripts/ReactorV/" + name));
                    if (bytes != null)
                        collected["logs/" + name] = string.Join("\n", Encoding.UTF8.GetString(bytes).Split('\n').Where(line => HasPid(line, pid)));
                }
                catch (Exception e) { notes.Add("Not collected: " + name + ": " + e.GetType().Name); }
            }
            var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReactorV");
            Isolation.SafePath(sessions);
            if (!Directory.Exists(sessions)) return;
            foreach (var path in Directory.GetFiles(sessions, "reactorv-session-*.log", SearchOption.TopDirectoryOnly))
            {
                // Do not traverse WebView2 or collect unrelated sessions/browser profiles.
                if (File.GetLastWriteTimeUtc(path) < watchingSince) continue;
                var name = Regex.Match(Path.GetFileName(path), @"^reactorv-session-(\d{8}T\d{9}Z)-\d+\.log$");
                if (!name.Success || !DateTime.TryParseExact(name.Groups[1].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sessionStart) || sessionStart < watchingSince.AddSeconds(-2)) continue;
                try
                {
                    var bytes = ReadBounded(path); if (bytes == null) continue;
                    var content = Encoding.UTF8.GetString(bytes);
                    if (content.Split('\n').Any(line => HasPid(line, pid))) collected["logs/" + Path.GetFileName(path)] = content;
                }
                catch (Exception e) { notes.Add("Session log skipped: " + Path.GetFileName(path) + ": " + e.GetType().Name); }
            }
        }

        internal static bool EvaluateEvidence(string mode, bool layout, IEnumerable<string> observed, int sampleCount,
            string managed, string shvdn, out string proof)
        {
            var seen = new HashSet<string>(observed, StringComparer.OrdinalIgnoreCase);
            if (mode == Isolation.NativeOff)
            {
                bool forbidden = Isolation.NativeFiles.Any(p => seen.Any(m => m.StartsWith(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)));
                bool windowed = managed.Contains("renderer=WebView2_window") && managed.Contains("stage=diagnostic_first_tick_exit");
                proof = "Requires intact native-off layout, module snapshots without Reactor native components, SHVDN loaded, WebView2_window startup and a completed Reactor tick.";
                return layout && !forbidden && sampleCount > 0 && windowed && seen.Contains("ScriptHookVDotNet.asi");
            }
            if (mode == Isolation.ProvidersOff)
            {
                bool native = Isolation.NativeFiles.All(p => seen.Contains(Path.GetFileName(p)));
                bool providerStarted = shvdn.Contains("Started script ALLIN1.") || shvdn.Contains("Started script RageWebUI.Script.") ||
                    managed.Contains("source=script stage=construction_begin");
                bool shvdnStarted = shvdn.Contains("Loading scripts from");
                proof = "Requires intact provider-off layout, all Reactor native components observed, SHVDN script loading reached, and no Reactor/ALLIN1 provider startup in captured logs. Other managed scripts remain enabled.";
                return layout && sampleCount > 0 && native && !providerStarted && shvdnStarted;
            }
            throw new ArgumentException("Unknown evidence mode.");
        }

        private void Complete()
        {
            int? exit = null;
            try { exit = game!.ExitCode; } catch { notes.Add("Exit code unavailable."); }
            bool layout = true;
            try { isolation.Verify(receipt); } catch (Exception e) { layout = false; notes.Add(e.Message); }
            Collect();
            var managed = string.Join("\n", collected.Where(p => p.Key.Contains("reactorv-session-")).Select(p => p.Value));
            var shvdn = collected.TryGetValue("logs/ScriptHookVDotNet.log", out var text) ? text : "";
            bool valid = EvaluateEvidence(receipt.Mode, layout, modules, samples, managed, shvdn, out var proof);
            report = JObject.FromObject(new {
                tool = receipt.Tool, runId = receipt.Id, mode = receipt.Mode,
                gamePid = pid, processStartedUtc = started, exitObservedUtc = DateTime.UtcNow,
                exitCode = exit, isolationEvidenceSufficient = valid, validationRule = proof,
                fileLayoutIntact = layout, moduleSamples = samples, observedModuleNames = modules.OrderBy(m => m).ToArray(),
                baselineSha256 = receipt.Identity, notes,
                limitations = "Module checks are periodic snapshots. Exit status is not proof that Story Mode loaded. No browser profile, configuration, backups or memory dump is included. Review logs before sharing."
            });
            Finished = true;
            Status = "PID " + pid + " exited (" + (exit?.ToString() ?? "unknown") + "). Isolation evidence: " + (valid ? "confirmed" : "INCONCLUSIVE / changed") + ". Save the result ZIP, then Restore.";
            Isolation.SafePath(isolation.RunDirectory(receipt));
            File.WriteAllText(Path.Combine(isolation.RunDirectory(receipt), "session-summary.json"), report.ToString(Formatting.Indented));
        }

        public void Export(string destination, string observedOutcome)
        {
            if (!Finished || report == null) throw new InvalidOperationException("Wait for the observed game process to exit first.");
            if (File.Exists(destination)) throw new IOException("Choose a new ZIP filename; existing files will not be replaced.");
            Isolation.SafePath(destination);
            report["userObservedOutcome"] = observedOutcome;
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var entry in collected.Concat(new[] { new KeyValuePair<string, string>("session-summary.json", report.ToString(Formatting.Indented)) }))
            {
                using var output = new StreamWriter(zip.CreateEntry(entry.Key).Open(), new UTF8Encoding(false));
                output.Write(entry.Value);
            }
        }

        public void Dispose() { game?.Dispose(); }
    }
}
