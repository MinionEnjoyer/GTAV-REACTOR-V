using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>Dependency inventory. Any DLL load occurs only in the explicitly invoked disposable child process.</summary>
    public static class DependencyService
    {
        private const uint LoadLibrarySearchDllLoadDir = 0x100;
        private const uint LoadLibrarySearchSystem32 = 0x800;
        private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const int MaximumManifestBytes = 1024 * 1024;
        private const int MaximumManifestFiles = 512;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

        public static JObject Run(DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            progress = progress ?? (_ => { });
            var staticResult = BuildStatic(options);
            if (IsTestHost()) { staticResult["execution"] = "unknown-test-host-no-child"; return staticResult; }
            if (!(bool?)staticResult["releaseFilesVerified"] ?? false) { staticResult["execution"] = "refused-unverified-release-files"; return staticResult; }
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var start = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "--dependency-probe " + Quote(DiagnosticIO.GameRoot(options)) + " " + Quote(options.ManifestPath) + " " + Quote(options.Edition) };
            using (var child = Process.Start(start))
            {
                if (child == null) { staticResult["execution"] = "failed-child-not-started"; return staticResult; }
                progress("Running dependency loads in an isolated child process.");
                // Start both drains before waiting: a verbose or failed child must
                // never deadlock on either redirected OS pipe.
                var stdoutTask = DrainBounded(child.StandardOutput);
                var stderrTask = DrainBounded(child.StandardError);
                var deadline = DateTime.UtcNow.Add(ProbeTimeout);
                while (!child.WaitForExit(100))
                {
                    if (token.IsCancellationRequested) { StopChild(child); staticResult["execution"] = "cancelled-child-stopped"; break; }
                    if (ProbeTimedOut(deadline, DateTime.UtcNow)) { StopChild(child); staticResult["execution"] = "timeout-child-stopped"; break; }
                }
                if (!child.WaitForExit(2000))
                {
                    staticResult["execution"] = "failed-child-stop";
                    return staticResult;
                }
                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 2000);
                var stdout = stdoutTask.IsCompleted ? stdoutTask.Result : "";
                var stderr = stderrTask.IsCompleted ? stderrTask.Result : "";
                staticResult["childExitCode"] = child.ExitCode;
                if (!string.IsNullOrEmpty(stderr)) staticResult["childStderr"] = stderr;
                if ((string)staticResult["execution"] == "cancelled-child-stopped" || (string)staticResult["execution"] == "timeout-child-stopped") return staticResult;
                if (child.ExitCode != 0) { staticResult["execution"] = "failed-child-exit"; return staticResult; }
                try { var probe = JObject.Parse(stdout); staticResult["probe"] = probe; staticResult["execution"] = "completed-child"; }
                catch { staticResult["execution"] = "failed-child-output"; }
            }
            return staticResult;
        }

        public static JObject RunProbe(DiagnosticOptions options)
        {
            // This private child mode can be invoked directly, so re-resolve here:
            // command-line callers must not turn it into an arbitrary-DLL loader
            // by supplying their own manifest.
            var reference = ReleaseReferenceService.Resolve(options);
            if (!string.Equals((string)reference["status"], "manual-pinned", StringComparison.Ordinal))
                throw new InvalidDataException("Dependency probe requires a bundled pinned release reference.");
            Stage("probe:static-start");
            var report = BuildStatic(options);
            if (!(bool?)report["releaseFilesVerified"] ?? false) { Stage("probe:refused-unverified"); report["execution"] = "refused-unverified-release-files"; return report; }
            var root = DiagnosticIO.GameRoot(options);
            var verified = (JObject)report["verifiedFiles"];
            Stage("probe:webview-start");
            report["webView2"] = ProbeWebView(FindVerified(verified, "WebView2Loader.dll"));
            Stage("probe:libcef-start");
            var cef = ProbeLoad(FindVerified(verified, "libcef.dll"), verified);
            Stage("probe:native-start");
            var native = ProbeLoad(FindVerified(verified, "RageWebUI.Native.dll"), verified);
            report["cef"] = new JArray(cef, native);
            Stage("probe:complete");
            report["execution"] = "completed-child";
            return report;
        }

        internal static bool IsX64Pe(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.ASCII, false))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5a4d) return false;
                    stream.Position = 0x3c; var offset = reader.ReadInt32();
                    if (offset <= 0 || offset > stream.Length - 6) return false;
                    stream.Position = offset;
                    return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
                }
            }
            catch { return false; }
        }

        internal static string ImportClassification(string module)
        {
            if (module.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || module.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase)) return "api-set-contract-not-direct-missing";
            if (module.StartsWith("VCRUNTIME", StringComparison.OrdinalIgnoreCase) || module.StartsWith("MSVCP", StringComparison.OrdinalIgnoreCase) || module.StartsWith("UCRT", StringComparison.OrdinalIgnoreCase)) return "runtime-import";
            return "other-import";
        }

        private static JObject BuildStatic(DiagnosticOptions options)
        {
            var root = DiagnosticIO.GameRoot(options);
            DiagnosticIO.RequireOrdinaryPath(options.ManifestPath);
            if (new FileInfo(options.ManifestPath).Length > MaximumManifestBytes) throw new InvalidDataException("Dependency manifest exceeds the size limit.");
            var manifest = JObject.Parse(File.ReadAllText(options.ManifestPath));
            if ((int?)manifest["schemaVersion"] != 1 || !string.Equals((string)manifest["edition"], options.Edition, StringComparison.Ordinal) || string.IsNullOrWhiteSpace((string)manifest["releaseVersion"]))
                throw new InvalidDataException("Dependency manifest is invalid or has the wrong edition.");
            var files = manifest["files"] as JArray;
            if (files == null || files.Count == 0 || files.Count > MaximumManifestFiles || files.Count != files.OfType<JObject>().Count()) throw new InvalidDataException("Dependency manifest has an invalid file list.");
            var verified = new JObject(); var integrity = new JArray(); var all = true;
            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files.OfType<JObject>())
            {
                var relative = (string)item["path"]; var expected = (string)item["sha256"]; var expectedLength = (long?)item["length"];
                if (String.IsNullOrWhiteSpace(relative) || String.IsNullOrWhiteSpace(expected) || expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)) || !expectedLength.HasValue || expectedLength.Value < 0 || !uniquePaths.Add(relative)) throw new InvalidDataException("Dependency manifest contains an invalid file entry.");
                string path;
                try { path = DiagnosticIO.SafePath(root, relative); }
                catch { all = false; continue; }
                var exists = File.Exists(path); var hash = exists ? DiagnosticIO.Sha256(path) : "missing";
                var good = exists && new FileInfo(path).Length == expectedLength.Value && String.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
                var requiredForLoad = IsNativeModule(relative);
                if (requiredForLoad && !good) all = false;
                integrity.Add(new JObject { ["path"] = relative, ["sha256"] = hash, ["verified"] = good, ["requiredForLoad"] = requiredForLoad });
                if (good && requiredForLoad) verified[Path.GetFileName(relative)] = path;
            }
            // The loader may resolve siblings from its own directory. Refuse a
            // probe if any DLL in a target dependency directory is unlisted or
            // changed; never let an arbitrary sidecar become a transitive load.
            foreach (var name in new[] { "WebView2Loader.dll", "libcef.dll", "RageWebUI.Native.dll" })
            {
                var target = verified.Property(name, StringComparison.OrdinalIgnoreCase)?.Value.Value<string>();
                if (string.IsNullOrEmpty(target)) continue;
                var directory = Path.GetDirectoryName(target);
                if (string.IsNullOrEmpty(directory)) { all = false; continue; }
                DiagnosticIO.RequireOrdinaryPath(directory);
                var siblings = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Take(MaximumManifestFiles + 1).ToArray();
                if (siblings.Length > MaximumManifestFiles) { all = false; continue; }
                foreach (var sibling in siblings)
                {
                    DiagnosticIO.RequireOrdinaryPath(sibling);
                    var relative = MakeRelative(root, sibling);
                    var entry = files.OfType<JObject>().FirstOrDefault(item => SameRelative((string)item["path"], relative));
                    if (entry == null || !string.Equals((string)entry["sha256"], DiagnosticIO.Sha256(sibling), StringComparison.OrdinalIgnoreCase)) all = false;
                }
            }
            return new JObject {
                ["kind"] = "reactor-dependency-inventory", ["generatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["os64"] = Environment.Is64BitOperatingSystem, ["process64"] = Environment.Is64BitProcess,
                ["dotNetFramework48"] = Framework48(), ["releaseFilesVerified"] = all, ["integrity"] = integrity, ["verifiedFiles"] = verified,
                ["limitations"] = new JArray("Observations establish neither hook activation nor fault causation.", "API-set contracts are not reported as direct missing DLLs.")
            };
        }

        private static string MakeRelative(string root, string path)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Dependency path escaped the selected game root.");
            return path.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }
        private static bool SameRelative(string left, string right) =>
            !string.IsNullOrEmpty(left) && string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        private static bool IsNativeModule(string relative)
        {
            var extension = Path.GetExtension(relative);
            return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".asi", StringComparison.OrdinalIgnoreCase);
        }

        internal static async Task<string> DrainBounded(StreamReader reader)
        {
            const int retainedMaximum = 256 * 1024;
            var buffer = new char[4096]; var retained = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var remaining = retainedMaximum - retained.Length;
                if (remaining > 0) retained.Append(buffer, 0, Math.Min(remaining, count));
            }
            if (retained.Length == retainedMaximum) retained.Append("\n[output truncated]");
            return retained.ToString();
        }

        internal static bool IsFramework48Release(int release) => release >= 528040;
        internal static bool ProbeTimedOut(DateTime deadlineUtc, DateTime nowUtc) => nowUtc >= deadlineUtc;

        private static JObject Framework48()
        {
            var registry64 = ReadRelease(RegistryView.Registry64);
            var registry32 = ReadRelease(RegistryView.Registry32);
            return new JObject { ["registry64"] = registry64, ["registry32"] = registry32,
                ["eligible64"] = registry64.Type == JTokenType.Integer ? new JValue(IsFramework48Release((int)registry64)) : new JValue("unknown"),
                ["eligible32"] = registry32.Type == JTokenType.Integer ? new JValue(IsFramework48Release((int)registry32)) : new JValue("unknown") };
        }
        private static JToken ReadRelease(RegistryView view)
        {
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)) using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")) { var r = k == null ? null : k.GetValue("Release"); return r == null ? new JValue("unknown") : new JValue(Convert.ToInt32(r, CultureInfo.InvariantCulture)); } }
            catch { return new JValue("unknown"); }
        }

        private static string FindVerified(JObject files, string name) { return files.Property(name, StringComparison.OrdinalIgnoreCase)?.Value.Value<string>(); }
        private static JObject ProbeWebView(string loader)
        {
            if (String.IsNullOrEmpty(loader)) return new JObject { ["readiness"] = "unknown", ["reason"] = "verified-loader-unavailable" };
            var h = LoadLibraryEx(loader, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (h == IntPtr.Zero) return LoadFailure("failed", "loader", Marshal.GetLastWin32Error(), loader);
            try
            {
                var proc = GetProcAddress(h, "GetAvailableCoreWebView2BrowserVersionString");
                if (proc == IntPtr.Zero) return LoadFailure("failed", "export", Marshal.GetLastWin32Error(), loader);
                var get = (WebViewVersion)Marshal.GetDelegateForFunctionPointer(proc, typeof(WebViewVersion)); IntPtr version;
                var hr = get(null, out version); var output = new JObject { ["hresult"] = "0x" + hr.ToString("X8", CultureInfo.InvariantCulture) };
                if (hr < 0 || version == IntPtr.Zero) { output["readiness"] = "failed"; return output; }
                try
                {
                    var apiVersion = Marshal.PtrToStringUni(version) ?? "unknown";
                    var evergreen = ReadEvergreenRuntime();
                    output["version"] = apiVersion;
                    output["apiRuntime"] = IsPreviewRuntimeLabel(apiVersion) ? "preview-channel" : "available";
                    output["evergreen"] = evergreen;
                    // A numeric API response establishes that some runtime is callable,
                    // not that the production Evergreen channel is installed.
                    output["readiness"] = IsPreviewRuntimeLabel(apiVersion) || !((bool?)evergreen["stablePresent"] ?? false)
                        ? "available-not-confirmed-production-evergreen" : "available-production-evergreen";
                }
                finally { Marshal.FreeCoTaskMem(version); }
                return output;
            }
            finally { FreeLibrary(h); }
        }
        internal static bool IsPreviewRuntimeLabel(string version) =>
            !string.IsNullOrWhiteSpace(version) && (version.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0 || version.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0 || version.IndexOf("dev", StringComparison.OrdinalIgnoreCase) >= 0 || version.IndexOf("canary", StringComparison.OrdinalIgnoreCase) >= 0);

        private static JObject ReadEvergreenRuntime()
        {
            var values = new JArray();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId))
                        {
                            var version = key == null ? null : key.GetValue("pv") as string;
                            if (!string.IsNullOrWhiteSpace(version)) values.Add(new JObject { ["hive"] = hive.ToString(), ["view"] = view.ToString(), ["pv"] = version, ["preview"] = IsPreviewRuntimeLabel(version) });
                        }
                    }
                    catch { }
            return new JObject { ["registrations"] = values, ["stablePresent"] = values.OfType<JObject>().Any(item =>
                !((bool?)item["preview"] ?? false) && Version.TryParse((string)item["pv"], out var version) && version > new Version(0, 0)) };
        }
        private static JObject ProbeLoad(string path, JObject verified)
        {
            if (String.IsNullOrEmpty(path)) return new JObject { ["readiness"] = "unknown", ["reason"] = "verified-file-unavailable" };
            if (!IsX64Pe(path)) return new JObject { ["path"] = Path.GetFileName(path), ["readiness"] = "failed", ["reason"] = "not-x64-pe" };
            var imports = ResolveImports(path, verified);
            var unresolved = imports.OfType<JObject>().Where(item => (string)item["resolution"] == "unresolved").ToArray();
            if (unresolved.Length != 0) return new JObject { ["path"] = Path.GetFileName(path), ["readiness"] = "failed", ["reason"] = "unresolved-imports", ["imports"] = imports, ["unresolvedImports"] = new JArray(unresolved) };
            var h = LoadLibraryEx(path, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (h == IntPtr.Zero) return LoadFailure("failed", Path.GetFileName(path), Marshal.GetLastWin32Error(), path);
            // Some graphics/CEF modules establish process-global lifetime in
            // DllMain. Deliberately do not FreeLibrary them in this disposable
            // probe process; process exit owns teardown and avoids manufacturing
            // an unload crash in the diagnostic harness.
            return new JObject { ["path"] = Path.GetFileName(path), ["readiness"] = "loaded-child-only", ["imports"] = imports, ["fileVersion"] = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "", ["unload"] = "deferred-to-child-process-exit" };
        }

        internal static JArray ParsePeImports(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.ASCII, false))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Not a PE file.");
                    stream.Position = 0x3c; var pe = reader.ReadInt32();
                    if (pe < 0 || pe > stream.Length - 24) throw new InvalidDataException("Invalid PE header offset.");
                    stream.Position = pe; if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Missing PE signature.");
                    reader.ReadUInt16(); var sections = reader.ReadUInt16(); stream.Position += 12; var optionalSize = reader.ReadUInt16(); stream.Position += 2;
                    var optional = stream.Position; if (optionalSize < 120 || optional + optionalSize > stream.Length) throw new InvalidDataException("Invalid optional header.");
                    var magic = reader.ReadUInt16(); var directory = optional + (magic == 0x20b ? 112 : magic == 0x10b ? 96 : throw new InvalidDataException("Unsupported optional header."));
                    if (directory + 16 > optional + optionalSize) return new JArray();
                    stream.Position = directory + 8; var importRva = reader.ReadUInt32(); var importSize = reader.ReadUInt32();
                    if (importRva == 0 || importSize == 0) return new JArray();
                    var table = new List<PeSection>(); stream.Position = optional + optionalSize;
                    for (var i = 0; i < sections; i++)
                    {
                        if (stream.Position + 40 > stream.Length) throw new InvalidDataException("Truncated section table.");
                        stream.Position += 8; var virtualSize = reader.ReadUInt32(); var virtualAddress = reader.ReadUInt32(); var rawSize = reader.ReadUInt32(); var rawOffset = reader.ReadUInt32(); stream.Position += 16;
                        table.Add(new PeSection { VirtualAddress = virtualAddress, Size = Math.Max(virtualSize, rawSize), RawOffset = rawOffset });
                    }
                    var result = new JArray(); var descriptor = RvaOffset(importRva, table, stream.Length);
                    for (var i = 0; i < 2048 && descriptor + 20 <= stream.Length; i++, descriptor += 20)
                    {
                        stream.Position = descriptor; var originalThunk = reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32(); var nameRva = reader.ReadUInt32(); var firstThunk = reader.ReadUInt32();
                        if (originalThunk == 0 && nameRva == 0 && firstThunk == 0) break;
                        result.Add(ReadAscii(stream, RvaOffset(nameRva, table, stream.Length), 260));
                    }
                    return result;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is EndOfStreamException || error is InvalidDataException)
            { return new JArray(new JObject { ["parseError"] = error.Message }); }
        }

        private static JArray ResolveImports(string path, JObject verified)
        {
            var imports = ParsePeImports(path); var directory = Path.GetDirectoryName(path) ?? ""; var result = new JArray();
            foreach (var token in imports)
            {
                if (token.Type != JTokenType.String) { result.Add(token); continue; }
                var module = (string)token;
                var kind = ImportClassification(module);
                if (kind == "api-set-contract-not-direct-missing") { result.Add(new JObject { ["module"] = module, ["resolution"] = "api-set-contract" }); continue; }
                var sibling = verified.Property(module, StringComparison.OrdinalIgnoreCase)?.Value.Value<string>();
                if (!string.IsNullOrEmpty(sibling) && string.Equals(Path.GetDirectoryName(sibling), directory, StringComparison.OrdinalIgnoreCase)) { result.Add(new JObject { ["module"] = module, ["resolution"] = "verified-sibling" }); continue; }
                if (File.Exists(Path.Combine(Environment.SystemDirectory, module))) { result.Add(new JObject { ["module"] = module, ["resolution"] = "system32" }); continue; }
                result.Add(new JObject { ["module"] = module, ["classification"] = kind, ["resolution"] = "unresolved" });
            }
            return result;
        }

        private sealed class PeSection { public uint VirtualAddress; public uint Size; public uint RawOffset; }
        private static long RvaOffset(uint rva, IList<PeSection> sections, long length)
        {
            foreach (var section in sections)
                if (rva >= section.VirtualAddress && (ulong)(rva - section.VirtualAddress) < section.Size)
                {
                    var result = (long)section.RawOffset + (rva - section.VirtualAddress);
                    if (result >= 0 && result < length) return result;
                }
            throw new InvalidDataException("PE RVA is outside its sections.");
        }
        private static string ReadAscii(Stream stream, long offset, int maximum)
        {
            if (offset < 0 || offset >= stream.Length) throw new InvalidDataException("PE import name is outside file.");
            stream.Position = offset; var bytes = new List<byte>();
            for (var i = 0; i < maximum && stream.Position < stream.Length; i++) { var value = stream.ReadByte(); if (value <= 0) break; bytes.Add((byte)value); }
            if (bytes.Count == 0) throw new InvalidDataException("Empty PE import name.");
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
        private static JObject LoadFailure(string readiness, string item, int error, string path = null)
        {
            var result = new JObject { ["readiness"] = readiness, ["item"] = item, ["win32"] = error, ["hresult"] = "0x" + Marshal.GetHRForLastWin32Error().ToString("X8", CultureInfo.InvariantCulture) };
            if (!string.IsNullOrEmpty(path)) result["path"] = path;
            return result;
        }
        private static void Stage(string value)
        {
            try { Console.Error.WriteLine(value); Console.Error.Flush(); }
            catch { }
        }
        private static void StopChild(Process child) { try { if (!child.HasExited) child.Kill(); } catch { } }
        private static bool IsTestHost() => AppDomain.CurrentDomain.FriendlyName.IndexOf("testhost", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "") + "\"";
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WebViewVersion([MarshalAs(UnmanagedType.LPWStr)] string folder, out IntPtr version);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string file, IntPtr h, uint flags);
        [DllImport("kernel32", SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("kernel32", SetLastError = true)] private static extern bool FreeLibrary(IntPtr h);
    }
}
