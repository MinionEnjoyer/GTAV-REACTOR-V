using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    /// <summary>Read-only observations of documented third-party host prerequisites. No plugin is loaded.</summary>
    public static class HostDependencyService
    {
        public static JObject Run(DiagnosticOptions options, CancellationToken token, Action<string> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            token.ThrowIfCancellationRequested();
            var root = DiagnosticIO.GameRoot(options);
            progress("Observing documented host dependencies without loading third-party plugins.");
            return new JObject {
                ["kind"] = "reactor-host-dependencies",
                ["edition"] = options.Edition,
                ["scriptHookV"] = Native(root, "ScriptHookV.dll"),
                ["scriptHookVdotNetAsi"] = Native(root, "ScriptHookVDotNet.asi"),
                ["scriptHookVdotNetApi3"] = Managed(root, "ScriptHookVDotNet3.dll"),
                ["asiLoader"] = new JObject {
                    ["dinput8"] = Loader(root, "dinput8.dll"),
                    ["dsound"] = Loader(root, "dsound.dll"),
                    ["meaning"] = "Both absent is unknown: another ASI loader may be installed."
                },
                ["compatibility"] = new JObject {
                    ["scriptHookV"] = "unknown-no-version-criteria-in-reactor",
                    ["scriptHookVDotNet"] = "requires-v3; package build target is 3.6.0 but observation does not prove compatibility",
                    ["edition"] = "unknown-third-party-edition-compatibility-not-proven"
                },
                ["limitations"] = new JArray("Presence, architecture, and AssemblyName metadata do not prove load, compatibility, or gameplay readiness.")
            };
        }

        private static JObject Native(string root, string relative)
        {
            var path = DiagnosticIO.SafePath(root, relative);
            if (!File.Exists(path)) return new JObject { ["path"] = relative, ["status"] = "not-observed" };
            DiagnosticIO.RequireOrdinaryPath(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            return new JObject { ["path"] = relative, ["status"] = "observed", ["architecture"] = DependencyService.IsX64Pe(path) ? "x64" : "unknown-or-not-x64-pe", ["length"] = new FileInfo(path).Length, ["fileVersion"] = version.FileVersion ?? "unknown", ["productVersion"] = version.ProductVersion ?? "unknown" };
        }

        private static JObject Managed(string root, string relative)
        {
            var path = DiagnosticIO.SafePath(root, relative);
            if (!File.Exists(path)) return new JObject { ["path"] = relative.Replace('\\', '/'), ["status"] = "not-observed", ["target"] = "3.6.0" };
            try
            {
                DiagnosticIO.RequireOrdinaryPath(path);
                var name = AssemblyName.GetAssemblyName(path);
                return new JObject { ["path"] = relative.Replace('\\', '/'), ["status"] = "observed", ["assemblyName"] = name.Name ?? "unknown", ["version"] = name.Version == null ? "unknown" : name.Version.ToString(), ["target"] = "3.6.0", ["compatibility"] = "unknown-metadata-only" };
            }
            catch (Exception error) when (error is BadImageFormatException || error is FileLoadException || error is IOException || error is UnauthorizedAccessException)
            { return new JObject { ["path"] = relative.Replace('\\', '/'), ["status"] = "unreadable", ["reason"] = error.GetType().Name, ["target"] = "3.6.0" }; }
        }

        private static JObject Loader(string root, string name)
        {
            var path = DiagnosticIO.SafePath(root, name);
            if (!File.Exists(path)) return new JObject { ["path"] = name, ["status"] = "not-observed-alternate-loader-possible" };
            DiagnosticIO.RequireOrdinaryPath(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            return new JObject { ["path"] = name, ["status"] = "observed", ["architecture"] = DependencyService.IsX64Pe(path) ? "x64" : "unknown-or-not-x64-pe", ["fileVersion"] = version.FileVersion ?? "unknown", ["productVersion"] = version.ProductVersion ?? "unknown" };
        }
    }
}
