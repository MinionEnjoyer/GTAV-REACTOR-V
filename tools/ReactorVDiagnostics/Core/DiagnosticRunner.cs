using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    public static class DiagnosticRunner
    {
        public static string ToolVersion => typeof(DiagnosticRunner).Assembly.GetName().Version?.ToString() ?? "unknown";
        public static JObject Run(string mode, DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (mode != "check" && mode != "record" && mode != "isolate-preview" && mode != "isolate" && mode != "restore")
                throw new ArgumentException("Unknown operation: " + mode);
            options.Validate();
            string game = DiagnosticIO.GameRoot(options);
            options.OutputDirectory = DiagnosticIO.NewReportDirectory(options.OutputDirectory, game);
            if (mode == "isolate")
            {
                if (!string.IsNullOrWhiteSpace(options.IsolationStatePath))
                    throw new ArgumentException("Apply always creates a new journal in its new report directory; --state is only for recording/restoring an existing test.");
                options.IsolationStatePath = Path.Combine(options.OutputDirectory, "isolation-state.json");
            }
            var report = new JObject {
                ["schemaVersion"] = 1, ["toolVersion"] = ToolVersion, ["operation"] = mode,
                ["startedUtc"] = DateTime.UtcNow.ToString("o"), ["edition"] = options.Edition,
                ["meaning"] = "Collection completion is not proof of a healthy game or the cause of a crash.",
                ["privacy"] = "Paths are redacted on a best-effort basis. Review all files before sharing. Dumps and restoration files are excluded from the review ZIP."
            };
            try
            {
                if (mode != "restore") report["referenceSelection"] = ReleaseReferenceService.Resolve(options);
                if (mode == "check" || mode == "record")
                {
                    report["preflight"] = PreflightService.Run(options, token, progress);
                    token.ThrowIfCancellationRequested();
                    // This inventory reads only third-party metadata and remains useful
                    // when no exact Reactor release can be selected.
                    report["hostDependencies"] = HostDependencyService.Run(options, token, progress);
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(options.ManifestPath))
                    {
                        report["dependencies"] = DependencyService.Run(options, token, progress);
                        token.ThrowIfCancellationRequested();
                    }
                    else report["dependencies"] = new JObject { ["execution"] = "not-run-unrecognized-or-build-drift", ["releaseFilesVerified"] = false };
                }
                if (mode == "record")
                {
                    report["capture"] = new CaptureService().Run(options, token, progress);
                    report["nativeModuleObservation"] = NativeModuleObservation(report["capture"]!);
                }
                if (mode == "isolate-preview") report["isolation"] = IsolationService.Preview(options, token, progress);
                if (mode == "isolate") report["isolation"] = IsolationService.Apply(options, token, progress);
                if (mode == "restore") report["isolation"] = IsolationService.Restore(options, token, progress);
                report["status"] = token.IsCancellationRequested ? "cancelled" : "collection-complete";
            }
            catch (OperationCanceledException) { report["status"] = "cancelled"; }
            catch (Exception error)
            {
                report["status"] = "blocked-or-failed";
                report["error"] = DiagnosticIO.Redact(error.GetType().Name + ": " + error.Message, game);
                progress("Stopped: " + DiagnosticIO.Redact(error.Message, game));
            }
            report["finishedUtc"] = DateTime.UtcNow.ToString("o");
            report = (JObject)DiagnosticIO.RedactJson(report, game);
            DiagnosticIO.WriteJson(Path.Combine(options.OutputDirectory, "report.json"), report);
            File.WriteAllText(Path.Combine(options.OutputDirectory, "READ-FIRST.txt"), Summary(report), new UTF8Encoding(false));
            MakeReviewZip(options.OutputDirectory, game);
            progress("Saved report and Review-before-sharing.zip in: " + options.OutputDirectory);
            return report;
        }

        public static string Summary(JObject report)
        {
            var text = new StringBuilder();
            text.AppendLine("REACTOR V DIAGNOSTICS " + ToolVersion);
            text.AppendLine("Operation: " + report["operation"] + " | Status: " + report["status"]);
            text.AppendLine("Collection completion DOES NOT mean the game is healthy or a cause is established.");
            if (report["error"] != null) text.AppendLine("STOP: " + report["error"]);
            var selection = report["referenceSelection"];
            if (selection != null) text.AppendLine("Release reference selection: " + Display(selection["status"]));
            var check = report["preflight"];
            if (check != null)
            {
                text.AppendLine("Release reference: " + check["manifest"]?["releaseVersion"] + " " + check["manifest"]?["edition"]);
                foreach (string name in new[] { "confirmed", "missing", "mismatch", "unknown" })
                    text.AppendLine("Installation " + name + ": " + ((check[name] as JArray)?.Count ?? 0));
                text.AppendLine("A modified configuration may be intentional; differences are not an automatic crash diagnosis.");
            }
            var host = report["hostDependencies"];
            if (host != null)
            {
                text.AppendLine();
                text.AppendLine("GAME-HOOK PREREQUISITES (read-only metadata, not loaded by this tool)");
                foreach (var key in new[] { "scriptHookV", "scriptHookVdotNetAsi", "scriptHookVdotNetApi3" })
                {
                    var item = host[key];
                    text.AppendLine(Display(item?["path"]) + ": " + Display(item?["status"]) + "; version " + Display(item?["version"] ?? item?["fileVersion"]));
                }
                text.AppendLine("ASI loader dinput8: " + Display(host["asiLoader"]?["dinput8"]?["status"]));
                text.AppendLine("ASI loader dsound: " + Display(host["asiLoader"]?["dsound"]?["status"]));
                text.AppendLine("Other ASI loaders are possible; observed versions do not establish edition/build compatibility.");
            }
            var dependencies = report["dependencies"];
            if (dependencies != null)
            {
                text.AppendLine();
                text.AppendLine("DEPENDENCY CHECKS");
                text.AppendLine("64-bit Windows / collector: " + Display(dependencies["os64"]) + " / " + Display(dependencies["process64"]));
                var framework = dependencies["dotNetFramework48"];
                text.AppendLine(".NET Framework >= 4.8 (64-bit registry): " + Display(framework?["eligible64"]) + "; release " + Display(framework?["registry64"]));
                text.AppendLine("Release native files verified for loading: " + Display(dependencies["releaseFilesVerified"]));
                text.AppendLine("Isolated load probe: " + Display(dependencies["execution"]));
                if (dependencies["childExitCode"] != null) text.AppendLine("Probe exit code: " + dependencies["childExitCode"]);
                var probe = dependencies["probe"];
                var webView = probe?["webView2"];
                text.AppendLine("WebView2: " + Display(webView?["readiness"]) + "; version " + Display(webView?["version"]));
                if (webView != null) text.AppendLine("WebView2 detail: " + webView.ToString(Newtonsoft.Json.Formatting.None));
                foreach (var library in probe?["cef"] as JArray ?? new JArray())
                {
                    text.AppendLine(Display(library["path"]) + ": " + Display(library["readiness"]) + "; version " + Display(library["fileVersion"]));
                    if (library["reason"] != null || library["win32"] != null)
                        text.AppendLine("  Loader detail: " + Display(library["reason"]) + "; Win32 " + Display(library["win32"]) + "; HRESULT " + Display(library["hresult"]));
                    foreach (var import in library["imports"] as JArray ?? new JArray())
                        text.AppendLine("  Import " + Display(import["module"]) + ": " + Display(import["resolution"]));
                }
                if (probe == null && dependencies["childStderr"] != null)
                    text.AppendLine("Last probe evidence: " + dependencies["childStderr"]);
                text.AppendLine("Unknown/refused checks are NOT passes. A helper failure is not proof of the game's crash cause.");
                text.AppendLine("These loads do not test browser rendering, graphics-driver stability, or hooks inside GTA.");
                text.AppendLine("Full architecture/import/integrity evidence: dependencies in report.json.");
            }
            if (report["capture"] != null)
            {
                text.AppendLine("Capture outcome: " + report["capture"]?["outcome"]);
                text.AppendLine("Recorded game PID: " + report["capture"]?["process"]?["pid"]);
                text.AppendLine("Game exit code: " + report["capture"]?["process"]?["exitCode"]);
            }
            if (report["isolation"] != null)
            {
                text.AppendLine("Isolation/restore result:");
                text.AppendLine(report["isolation"]!.ToString());
                text.AppendLine("Keep the original run directory: its private state/backups are needed to restore.");
                text.AppendLine("Native-off may remove all Reactor UI; it is a crash-isolation test, not a UI acceptance test.");
            }
            text.AppendLine();
            text.AppendLine("Review-before-sharing.zip contains only this summary, the redacted report and captured text diagnostics.");
            text.AppendLine("It excludes crash dumps, binary backups and restore journals. No files were uploaded.");
            text.AppendLine("Logs and dumps can contain private data despite path redaction. Review before sharing; never post full dumps publicly.");
            return text.ToString();
        }

        private static string Display(JToken? value) => value == null || value.Type == JTokenType.Null || string.IsNullOrWhiteSpace(value.ToString())
            ? "unknown / not measured" : value.ToString();

        public static JObject NativeModuleObservation(JToken capture)
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ReactorV.RenderHook.asi", "ReactorV.Bootstrap.asi", "ReactorV.ScriptProbe.asi", "RageWebUI.Native.dll" };
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int sampled = 0; bool unknown = false;
            foreach (var sample in capture["samples"] as JArray ?? new JArray())
            {
                var modules = sample["modules"] as JArray;
                if (modules == null || modules.Count == 0) { unknown = true; continue; }
                sampled++;
                foreach (var module in modules)
                {
                    if ((string?)module["observed"] == "unknown") unknown = true;
                    string? name = (string?)module["name"];
                    if (name != null && expected.Contains(name)) found.Add(name);
                }
            }
            return new JObject { ["expectedNamesObserved"] = new JArray(found), ["samplesWithModuleData"] = sampled,
                ["status"] = found.Count > 0 ? "native-components-observed" : sampled == 0 || unknown ? "unknown" : "expected-native-names-not-observed",
                ["limitation"] = "Sampled module names cannot prove that a renamed, briefly loaded, or manually mapped module never executed. Compare with the on-disk isolation journal." };
        }

        public static void MakeReviewZip(string output, string gameRoot)
        {
            DiagnosticIO.RequireOrdinaryPath(output);
            string archivePath = DiagnosticIO.SafePath(output, "Review-before-sharing.zip");
            if (File.Exists(archivePath)) throw new IOException("Refusing to overwrite an existing review archive.");
            using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            AddText(zip, output, "READ-FIRST.txt", gameRoot);
            AddText(zip, output, "report.json", gameRoot);
            string capture = DiagnosticIO.SafePath(output, "capture");
            if (!Directory.Exists(capture)) return;
            // Allow only immediate text outputs; no directory recursion can include private-dumps or backups.
            int count = 0;
            foreach (string candidate in Directory.EnumerateFiles(capture, "*", SearchOption.TopDirectoryOnly))
            {
                if (++count > 128) break;
                string extension = Path.GetExtension(candidate).ToLowerInvariant();
                if (extension != ".json" && extension != ".txt" && extension != ".log") continue;
                AddText(zip, output, "capture/" + Path.GetFileName(candidate), gameRoot);
            }
            string raw = DiagnosticIO.SafePath(capture, "raw");
            if (!Directory.Exists(raw)) return;
            foreach (string candidate in Directory.EnumerateFiles(raw, "log-*.txt", SearchOption.TopDirectoryOnly))
            {
                if (++count > 128) break;
                AddText(zip, output, "capture/raw/" + Path.GetFileName(candidate), gameRoot);
            }
        }

        private static void AddText(ZipArchive zip, string root, string relative, string gameRoot)
        {
            string path = DiagnosticIO.SafePath(root, relative);
            DiagnosticIO.RequireOrdinaryPath(path);
            if (new FileInfo(path).Length > 32 * 1024 * 1024) return;
            string text = File.ReadAllText(path);
            string safe = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticIO.RedactJson(JToken.Parse(text), gameRoot).ToString()
                : DiagnosticIO.Redact(text, gameRoot);
            using var writer = new StreamWriter(zip.CreateEntry(relative, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write(safe);
        }
    }
}
