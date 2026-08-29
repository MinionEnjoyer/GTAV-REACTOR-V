using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RageWebUI.Core
{
    /// <summary>
    /// Locates the physical REACTOR V installation without trusting a managed
    /// assembly location. ScriptHookVDotNet shadow-copies managed assemblies,
    /// so Assembly.Location can point into the CLR download cache instead of
    /// the GTA scripts directory.
    /// </summary>
    public static class RuntimeDirectoryLocator
    {
        public static string Resolve(params string?[] roots)
        {
            return ResolveWhere(
                "REACTOR V assets",
                candidate => File.Exists(Path.Combine(candidate, "ui", "index.html")),
                ExpandLegacy,
                roots);
        }

        public static string ResolveBootstrap(params string?[] roots)
        {
            return ResolveWhere(
                "REACTOR V bootstrap",
                candidate =>
                    (File.Exists(Path.Combine(candidate, "ReactorV.json")) ||
                     File.Exists(Path.Combine(candidate, "RageWebUI.json"))) &&
                    File.Exists(Path.Combine(candidate, "RageWebUI.Script.dll")),
                ExpandLegacy,
                roots);
        }

        public static string ResolveRenderer(params string?[] roots)
        {
            return ResolveWhere(
                "REACTOR V renderer",
                candidate =>
                    File.Exists(Path.Combine(candidate, "RageWebUI.Runtime.dll")) &&
                    File.Exists(Path.Combine(candidate, "ui", "index.html")),
                ExpandRenderer,
                roots);
        }

        private static string ResolveWhere(
            string description,
            Func<string, bool> matches,
            Func<string, IEnumerable<string>> expand,
            params string?[] roots)
        {
            var checkedDirectories = new List<string>();
            foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                foreach (var candidate in expand(root!))
                {
                    if (checkedDirectories.Any(existing =>
                        string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    checkedDirectories.Add(candidate);
                    if (matches(candidate))
                    {
                        return candidate;
                    }
                }
            }

            var searched = checkedDirectories.Count == 0
                ? "(no candidate directories)"
                : string.Join("', '", checkedDirectories);
            throw new DirectoryNotFoundException(
                $"{description} was not found. Checked '{searched}'.");
        }

        private static IEnumerable<string> ExpandLegacy(string root)
        {
            var fullRoot = Path.GetFullPath(root);
            yield return fullRoot;
            yield return Path.Combine(fullRoot, "ReactorV");
            yield return Path.Combine(fullRoot, "scripts", "ReactorV");
            yield return Path.Combine(fullRoot, "RageWebUI");
            yield return Path.Combine(fullRoot, "scripts", "RageWebUI");
        }

        private static IEnumerable<string> ExpandRenderer(string root)
        {
            var fullRoot = Path.GetFullPath(root);
            yield return fullRoot;
            yield return Path.Combine(fullRoot, "ReactorV");
            yield return Path.Combine(fullRoot, "plugins", "ReactorV");

            var parent = Directory.GetParent(fullRoot);
            if (parent != null)
            {
                yield return Path.Combine(parent.FullName, "ReactorV");
                yield return Path.Combine(parent.FullName, "plugins", "ReactorV");
                if (string.Equals(parent.Name, "scripts", StringComparison.OrdinalIgnoreCase) &&
                    parent.Parent != null)
                {
                    yield return Path.Combine(parent.Parent.FullName, "ReactorV");
                    yield return Path.Combine(parent.Parent.FullName, "plugins", "ReactorV");
                }
            }
        }
    }
}
