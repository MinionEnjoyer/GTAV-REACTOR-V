using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>Bounded, opt-in collector for the next user-launched GTA process.  It never launches or kills the game.</summary>
    public sealed class CaptureService
    {
        private const int PollMilliseconds = 500;
        private const int MaxTailBytes = 4 * 1024 * 1024;
        private const int MaxLogFiles = 64;
        private const int MaxCollectedLogBytes = 16 * 1024 * 1024;
        private const int MaxModulesPerSample = 1024;
        private const uint ProcessSynchronize = 0x00100000;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint WaitObject0 = 0;

        public JObject Run(DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            progress = progress ?? (_ => { });

            var gameRoot = DiagnosticIO.GameRoot(options);
            var gameExe = Path.GetFullPath(Path.Combine(gameRoot, options.Edition == "Legacy" ? "GTA5.exe" : "GTA5_Enhanced.exe"));
            var captureRoot = DiagnosticIO.SafePath(options.OutputDirectory, "capture");
            Directory.CreateDirectory(captureRoot);

            var report = new JObject {
                ["schema"] = "reactorv-diagnostic-capture-1",
                ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["edition"] = options.Edition ?? "unknown",
                ["gameExecutable"] = DiagnosticIO.Redact(gameExe, gameRoot),
                ["preArmConnectionGap"] = "No process is connected while armed; the collector only observes the next exact executable-path match.",
                ["faultAttribution"] = "not attempted"
            };

            if (HasInaccessibleNamedProcess(gameExe))
            {
                report["outcome"] = "rejected-target-identity-unknown";
                return Finish(captureRoot, report);
            }
            var existing = FindExactGames(gameExe).ToList();
            if (existing.Count != 0)
            {
                report["outcome"] = "rejected-existing-game";
                report["existingPids"] = new JArray(existing.Select(p => p.Id));
                report["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteReport(captureRoot, report);
                return report;
            }

            // This baseline intentionally precedes arming so startup logs are retained as deltas.
            var sources = DiscoverLogSources(gameRoot);
            var baseline = sources.ToDictionary(p => p, Snapshot, StringComparer.OrdinalIgnoreCase);

            progress("Armed for the next user-launched GTA process.");
            var waitSeconds = Math.Max(1, options.WaitSeconds <= 0 ? 120 : options.WaitSeconds);
            var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            Process game = null;
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                var candidates = FindExactGames(gameExe).ToList();
                if (HasInaccessibleNamedProcess(gameExe)) { report["outcome"] = "rejected-target-identity-unknown"; return Finish(captureRoot, report); }
                if (candidates.Count == 1) { game = candidates[0]; break; }
                if (candidates.Count > 1)
                {
                    report["outcome"] = "ambiguous-target";
                    report["candidatePids"] = new JArray(candidates.Select(p => p.Id));
                    return Finish(captureRoot, report);
                }
                token.WaitHandle.WaitOne(PollMilliseconds);
            }
            if (token.IsCancellationRequested) { report["outcome"] = "cancelled-waiting"; return Finish(captureRoot, report); }
            if (game == null) { report["outcome"] = "timeout-waiting"; return Finish(captureRoot, report); }

            DateTime startUtc;
            try { startUtc = game.StartTime.ToUniversalTime(); }
            catch { report["outcome"] = "identity-unavailable"; return Finish(captureRoot, report); }
            report["process"] = new JObject { ["pid"] = game.Id, ["startUtc"] = startUtc.ToString("O", CultureInfo.InvariantCulture) };
            var online = OnlineLaunchState(game.Id);
            report["onlineLaunchFlag"] = online;
            if (online == "present") { report["outcome"] = "rejected-online-flag"; return Finish(captureRoot, report); }
            progress("Exact game process observed; recording bounded diagnostics.");
            var processHandle = OpenProcess(ProcessSynchronize | ProcessQueryLimitedInformation, false, game.Id);
            if (processHandle == IntPtr.Zero) { report["outcome"] = "identity-handle-unavailable"; return Finish(captureRoot, report); }
            DateTime kernelCreationUtc;
            if (!TryGetCreationTimeUtc(processHandle, out kernelCreationUtc) || kernelCreationUtc != startUtc)
            {
                CloseHandle(processHandle); game.Dispose(); report["outcome"] = "identity-recheck-failed"; return Finish(captureRoot, report);
            }
            DumpCollector dump = null;
            try
            {
                dump = StartDumpCollector(options, captureRoot, game.Id);
                report["dump"] = dump.Status;
                var samples = new JArray();
                var recordSeconds = Math.Max(1, options.RecordSeconds <= 0 ? 180 : options.RecordSeconds);
                var recordDeadline = DateTime.UtcNow.AddSeconds(recordSeconds);
                string state = "recording";
                while (DateTime.UtcNow < recordDeadline && !token.IsCancellationRequested)
                {
                    if (WaitForSingleObject(processHandle, 0) == WaitObject0) { state = "exited"; break; }
                    samples.Add(Sample(game, gameRoot));
                    token.WaitHandle.WaitOne(PollMilliseconds);
                }
                if (token.IsCancellationRequested) state = "cancelled";
                report["samples"] = samples;
                report["recordingOutcome"] = state;
                uint exitCode;
                report["process"]["exitCode"] = state == "exited" && GetExitCodeProcess(processHandle, out exitCode) ? unchecked((int)exitCode) : (JToken)"unknown";
                report["logs"] = CollectNewLogContent(captureRoot, baseline, gameRoot, game.Id, startUtc);
                if (options.IncludeSecurityEvents)
                {
                    try { report["securityEvidence"] = SecurityEvidence.Collect(startUtc, DateTime.UtcNow, gameRoot, token); }
                    catch (OperationCanceledException) { report["securityEvidence"] = new JObject { ["requested"] = true, ["status"] = "cancelled" }; }
                    catch (Exception e) { report["securityEvidence"] = new JObject { ["requested"] = true, ["status"] = "unknown", ["reason"] = e.GetType().Name }; }
                }
                report["outcome"] = state == "recording" ? "timeout-recording" : state;
            }
            finally
            {
                if (dump != null) dump.Status["cleanup"] = StopOwnedCollector(dump.Collector, game.Id, options.ProcDumpPath);
                CloseHandle(processHandle);
                game.Dispose();
            }
            if (dump != null && dump.DumpPath != null && File.Exists(dump.DumpPath))
            {
                if ((string)dump.Status["cleanup"] == "already-exited")
                {
                    try { report["dump"]["status"] = "collected-private"; report["dump"]["sha256"] = DiagnosticIO.Sha256(dump.DumpPath); report["dump"]["shareDefault"] = false; }
                    catch (Exception e) { report["dump"]["status"] = "incomplete-private"; report["dump"]["reason"] = e.GetType().Name; }
                }
                else report["dump"]["status"] = "incomplete-private";
            }
            return Finish(captureRoot, report);
        }

        internal static bool IsSafeRelative(string relative)
        {
            return !String.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) &&
                relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).All(s => s != ".." && s.Length != 0);
        }

        internal static bool IsExactExecutablePath(string observed, string expected)
        {
            if (String.IsNullOrWhiteSpace(observed) || String.IsNullOrWhiteSpace(expected)) return false;
            try { return String.Equals(Path.GetFullPath(observed), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal static bool IsRotation(long priorLength, string priorPrefix, long currentLength, string currentPrefix)
        {
            return currentLength < priorLength || !String.Equals(priorPrefix, currentPrefix, StringComparison.Ordinal);
        }

        internal static bool IsProcDumpProcessName(string name)
        {
            return String.Equals(name, "procdump", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "procdump64", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "procdump64a", StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildProcDumpArguments(int pid, string dumpPath, string mode)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
            if (!IsSafeRelative(dumpPath)) throw new ArgumentException("A relative private-dumps destination is required.", nameof(dumpPath));
            if (mode != "mini" && mode != "full") throw new ArgumentException("Only mini or full dumps are supported.", nameof(mode));
            return (mode == "full" ? "-ma" : "-mm") + " -e " + pid.ToString(CultureInfo.InvariantCulture) + " \"" + dumpPath.Replace("\"", "") + "\"";
        }

        private static DumpCollector StartDumpCollector(DiagnosticOptions options, string captureRoot, int pid)
        {
            var mode = options.DumpMode ?? "none";
            var result = new DumpCollector { Status = new JObject { ["mode"] = mode, ["status"] = "disabled" } };
            if (mode == "none") return result;
            if (!options.Consent) { result.Status["status"] = "consent-required"; return result; }
            if (ProcDumpInstances().Any()) { result.Status["status"] = "refused-existing-procdump"; return result; }
            if (!ProcDumpEulaAccepted()) { result.Status["status"] = "eula-not-accepted"; return result; }
            try
            {
                DiagnosticIO.RequireOrdinaryPath(options.ProcDumpPath);
                if (!VerifyMicrosoftSignature(options.ProcDumpPath)) { result.Status["status"] = "signer-not-verified"; return result; }
                var privateDumps = Path.Combine(captureRoot, "private-dumps"); Directory.CreateDirectory(privateDumps);
                var relative = "private-dumps\\game-" + pid.ToString(CultureInfo.InvariantCulture) + ".dmp";
                result.DumpPath = Path.Combine(captureRoot, relative);
                result.Collector = Process.Start(new ProcessStartInfo {
                    FileName = options.ProcDumpPath, Arguments = BuildProcDumpArguments(pid, relative, mode),
                    WorkingDirectory = captureRoot, UseShellExecute = false, CreateNoWindow = true
                });
                result.Status["status"] = result.Collector == null ? "collector-not-started" : "collector-started-unconfirmed";
                result.Status["destination"] = "private-dumps/game-" + pid.ToString(CultureInfo.InvariantCulture) + ".dmp";
            }
            catch (Exception e) { result.Status["status"] = "collector-unavailable"; result.Status["reason"] = e.GetType().Name; }
            return result;
        }

        private static bool VerifyMicrosoftSignature(string executable)
        {
            // Get-AuthenticodeSignature is read-only; no download or EULA acceptance is attempted.
            var quoted = executable.Replace("'", "''");
            var command = "$s=Get-AuthenticodeSignature -LiteralPath '" + quoted + "'; if($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -like '*Microsoft*'){exit 0}else{exit 1}";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            using (var powershell = Process.Start(new ProcessStartInfo {
                FileName = powershellPath, UseShellExecute = false, CreateNoWindow = true,
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded
            }))
            {
                if (powershell == null) return false;
                if (!powershell.WaitForExit(10000)) { try { powershell.Kill(); } catch { } return false; }
                return powershell.ExitCode == 0;
            }
        }

        private static string StopOwnedCollector(Process collector, int targetPid, string procDumpPath)
        {
            if (collector == null) return "not-started";
            try
            {
                if (!collector.HasExited)
                {
                    var others = ProcDumpInstances().Where(p => p.Id != collector.Id).ToList();
                    if (others.Count != 0)
                    {
                        foreach (var other in others) other.Dispose();
                        return "manual-close-needed-other-procdump";
                    }
                    // ProcDump's documented cancellation is used only after rejecting a pre-existing collector.
                    // There is deliberately no Kill fallback: an attached debugger must never be force-terminated here.
                    using (var cancel = Process.Start(new ProcessStartInfo { FileName = procDumpPath, Arguments = "-cancel " + targetPid.ToString(CultureInfo.InvariantCulture), UseShellExecute = false, CreateNoWindow = true }))
                    { if (cancel != null) cancel.WaitForExit(2000); }
                    return collector.WaitForExit(3000) ? "cancelled-owned-collector" : "manual-close-needed";
                }
                return "already-exited";
            }
            catch { return "manual-close-needed"; }
            finally { collector.Dispose(); }
        }

        private static bool ProcDumpEulaAccepted()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Sysinternals\ProcDump")) return Convert.ToInt32(key == null ? 0 : key.GetValue("EulaAccepted", 0), CultureInfo.InvariantCulture) == 1; }
            catch { return false; }
        }

        private static IEnumerable<Process> ProcDumpInstances()
        {
            foreach (var name in new[] { "procdump", "procdump64", "procdump64a" })
                foreach (var process in Process.GetProcessesByName(name)) yield return process;
        }

        private static bool TryGetCreationTimeUtc(IntPtr processHandle, out DateTime creationUtc)
        {
            FILETIME creation, exit, kernel, user;
            if (GetProcessTimes(processHandle, out creation, out exit, out kernel, out user)) { creationUtc = DateTime.FromFileTimeUtc(creation.ToLong()); return true; }
            creationUtc = DateTime.MinValue; return false;
        }

        private static IEnumerable<Process> FindExactGames(string wanted)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(wanted)))
            {
                string actual;
                try { actual = p.MainModule.FileName; }
                catch { p.Dispose(); continue; }
                if (IsExactExecutablePath(actual, wanted)) yield return p;
                else p.Dispose();
            }
        }

        private static bool HasInaccessibleNamedProcess(string wanted)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(wanted)))
            {
                try { var ignored = p.MainModule.FileName; }
                catch { p.Dispose(); return true; }
                p.Dispose();
            }
            return false;
        }

        private static string OnlineLaunchState(int pid)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid.ToString(CultureInfo.InvariantCulture)))
                using (var found = searcher.Get())
                {
                    var command = found.Cast<System.Management.ManagementObject>().Select(x => x["CommandLine"] as string).FirstOrDefault();
                    if (command == null) return "unknown";
                    return command.IndexOf("-online", StringComparison.OrdinalIgnoreCase) >= 0 || command.IndexOf("--online", StringComparison.OrdinalIgnoreCase) >= 0 ? "present" : "absent";
                }
            }
            catch { return "unknown"; }
        }

        private static JObject Sample(Process p, string gameRoot)
        {
            var sample = new JObject { ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["modules"] = new JArray() };
            try
            {
                int count = 0;
                foreach (ProcessModule m in p.Modules)
                {
                    if (count++ >= MaxModulesPerSample) { sample["modulesTruncated"] = true; break; }
                    ((JArray)sample["modules"]).Add(new JObject { ["name"] = m.ModuleName, ["path"] = DiagnosticIO.Redact(m.FileName, gameRoot), ["observed"] = true });
                }
            }
            catch { sample["modules"] = new JArray(new JObject { ["observed"] = "unknown", ["reason"] = "inaccessible" }); }
            try { p.Refresh(); } catch { }
            sample["window"] = WindowSample(p);
            return sample;
        }

        private static JObject WindowSample(Process process)
        {
            try
            {
                var hwnd = process.MainWindowHandle;
                RECT rect;
                if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out rect))
                    return new JObject { ["physicalClient"] = "unknown", ["dpi"] = "unknown" };
                var dpi = GetDpiForWindow(hwnd);
                return new JObject {
                    ["physicalClient"] = new JObject { ["width"] = Math.Max(0, rect.Right - rect.Left), ["height"] = Math.Max(0, rect.Bottom - rect.Top) },
                    ["dpi"] = dpi == 0 ? (JToken)"unknown" : dpi
                };
            }
            catch { return new JObject { ["physicalClient"] = "unknown", ["dpi"] = "unknown" }; }
        }

        private static IEnumerable<string> DiscoverLogSources(string gameRoot)
        {
            var roots = new[] { Path.Combine(gameRoot, "plugins", "ReactorV"), Path.Combine(gameRoot, "scripts", "ReactorV"), gameRoot };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try { DiagnosticIO.RequireOrdinaryPath(root); }
                catch { continue; }
                foreach (var file in Directory.EnumerateFiles(root, "*.log", SearchOption.TopDirectoryOnly).Take(MaxLogFiles))
                {
                    try { DiagnosticIO.RequireOrdinaryPath(file); }
                    catch { continue; }
                    var name = Path.GetFileName(file);
                    if (!String.Equals(root, gameRoot, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("reactorv", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ScriptHook", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("ALLIN1", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "ReShade.log", StringComparison.OrdinalIgnoreCase)) yield return file;
                }
            }
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReactorV");
            if (Directory.Exists(local))
            {
                try { DiagnosticIO.RequireOrdinaryPath(local); }
                catch { yield break; }
                foreach (var file in Directory.EnumerateFiles(local, "*.log", SearchOption.TopDirectoryOnly).Take(MaxLogFiles))
                {
                    try { DiagnosticIO.RequireOrdinaryPath(file); }
                    catch { continue; }
                    yield return file;
                }
            }
        }

        private static LogSnapshot Snapshot(string path)
        {
            try { var f = new FileInfo(path); var n = (int)Math.Min(4096, f.Length); return new LogSnapshot { Length = f.Length, PrefixLength = n, Prefix = PrefixHash(path, n) }; }
            catch { return new LogSnapshot { Length = 0, PrefixLength = 0, Prefix = "unknown" }; }
        }

        private static JArray CollectNewLogContent(string captureRoot, Dictionary<string, LogSnapshot> baseline, string gameRoot, int pid, DateTime startUtc)
        {
            var result = new JArray(); var raw = Path.Combine(captureRoot, "raw"); Directory.CreateDirectory(raw); int remaining = MaxCollectedLogBytes;
            var all = new HashSet<string>(baseline.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var later in DiscoverLogSources(gameRoot)) all.Add(later);
            foreach (var source in all.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(MaxLogFiles))
            {
                if (remaining <= 0) { result.Add(new JObject { ["source"] = DiagnosticIO.Redact(source, gameRoot), ["status"] = "omitted-total-byte-cap" }); continue; }
                try { DiagnosticIO.RequireOrdinaryPath(source); }
                catch { result.Add(new JObject { ["source"] = DiagnosticIO.Redact(source, gameRoot), ["status"] = "unknown-nonordinary-or-inaccessible" }); continue; }
                var hadBaseline = baseline.TryGetValue(source, out var before);
                if (!hadBaseline) before = new LogSnapshot { Length = 0, Prefix = "new" };
                var after = Snapshot(source); var currentPrefix = PrefixHash(source, before.PrefixLength); var rotated = hadBaseline && IsRotation(before.Length, before.Prefix, after.Length, currentPrefix);
                var offset = rotated ? 0 : before.Length;
                var data = ReadTail(source, offset, Math.Min(MaxTailBytes, remaining));
                remaining -= Encoding.UTF8.GetByteCount(data);
                var name = "log-" + result.Count.ToString(CultureInfo.InvariantCulture) + ".txt";
                File.WriteAllText(Path.Combine(raw, name), DiagnosticIO.Redact(data, gameRoot), Encoding.UTF8);
                result.Add(new JObject {
                    ["source"] = DiagnosticIO.Redact(source, gameRoot), ["raw"] = "raw/" + name,
                    ["startOffset"] = before.Length, ["rotationDetected"] = rotated, ["discoveredDuringRecording"] = !hadBaseline,
                    ["sha256"] = DiagnosticIO.Sha256(Path.Combine(raw, name)),
                    ["correlation"] = "Lines are not merged across files; PID/UTC association requires an explicit matching field and is otherwise ambiguous."
                });
            }
            return result;
        }

        private static string ReadTail(string path, long offset, int maximumBytes)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var start = Math.Max(offset, Math.Max(0, stream.Length - maximumBytes)); stream.Seek(start, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
                }
            }
            catch (Exception e) { return "[unavailable: " + e.GetType().Name + "]"; }
        }

        private static string PrefixHash(string path, int count)
        {
            try { using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(new BinaryReader(s).ReadBytes(Math.Max(0, count)))).Replace("-", ""); }
            catch { return "unknown"; }
        }

        private static JObject Finish(string captureRoot, JObject report)
        {
            report["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture); WriteReport(captureRoot, report); return report;
        }
        private static void WriteReport(string captureRoot, JObject report) { DiagnosticIO.AtomicWriteJson(Path.Combine(captureRoot, "report.json"), report); }
        private sealed class LogSnapshot { public long Length; public int PrefixLength; public string Prefix; }
        private sealed class DumpCollector { public JObject Status; public Process Collector; public string DumpPath; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; public long ToLong() => ((long)High << 32) + Low; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
    }
}
