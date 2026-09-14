using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Diagnostics
{
    public static class DiagnosticIO
    {
        public static string GameRoot(DiagnosticOptions options)
        {
            options.Validate();
            if (string.IsNullOrWhiteSpace(options.GameDirectory)) throw new ArgumentException("Select the actual GTA installation folder.");
            string root = Path.GetFullPath(options.GameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root.TrimEnd('\\'), Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A drive root is not a game directory.");
            RequireOrdinaryPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Game folder not found.");
            string exe = options.Edition == "Enhanced" ? "GTA5_Enhanced.exe" : "GTA5.exe";
            RequireOrdinaryPath(SafePath(root, exe));
            if (!File.Exists(SafePath(root, exe))) throw new FileNotFoundException("Selected folder does not contain " + exe + ".");
            return root;
        }

        public static bool IsWithin(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string fullPath = Path.GetFullPath(path).TrimEnd('\\', '/');
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static string SafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0)
                throw new InvalidDataException("Expected a relative path without a drive or alternate stream.");
            string normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            foreach (string part in normalized.Split(Path.DirectorySeparatorChar))
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                    throw new InvalidDataException("Ambiguous or traversing relative path rejected.");
            string target = Path.GetFullPath(Path.Combine(root, normalized));
            if (!IsWithin(root, target) || target.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Path escaped the selected directory.");
            RequireOrdinaryPath(target, false);
            return target;
        }

        public static void RequireOrdinaryPath(string path, bool mustExist = true)
        {
            string current = Path.GetFullPath(path);
            if (mustExist && !File.Exists(current) && !Directory.Exists(current)) throw new FileNotFoundException("Required path does not exist.", current);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points/junctions are not accepted: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string? parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
        }

        public static string Sha256(string path)
        {
            RequireOrdinaryPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static void WriteJson(string path, JToken value) => AtomicWriteJson(path, value);

        public static void AtomicWriteJson(string path, JToken value)
        {
            RequireOrdinaryPath(path, false);
            string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
            RequireOrdinaryPath(parent);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(value.ToString(Formatting.Indented));
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static string NewReportDirectory(string outputBase, string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(outputBase)) throw new ArgumentException("Select an output directory.");
            string basePath = Path.GetFullPath(outputBase);
            RequireOrdinaryPath(basePath, false);
            if (IsWithin(gameRoot, basePath)) throw new IOException("Reports and backups must be outside the GTA installation.");
            Directory.CreateDirectory(basePath);
            string result = SafePath(basePath, "ReactorV-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            if (Directory.Exists(result)) throw new IOException("Report directory already exists.");
            Directory.CreateDirectory(result);
            return result;
        }

        public static string Redact(string text, string gameRoot)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string result = text;
            string[] roots = { gameRoot, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            string[] labels = { "<GTA>", "<LOCALAPPDATA>", "<USERPROFILE>" };
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(roots[i])) continue;
                result = Regex.Replace(result, Regex.Escape(roots[i]), _ => labels[i], RegexOptions.IgnoreCase);
                result = Regex.Replace(result, Regex.Escape(roots[i].Replace('\\', '/')), _ => labels[i], RegexOptions.IgnoreCase);
            }
            result = Regex.Replace(result, @"[A-Za-z]:[\\/]Users[\\/][^\\/\r\n\""<>]+", "<USERPROFILE>", RegexOptions.IgnoreCase);
            return result;
        }

        public static JToken RedactJson(JToken source, string gameRoot)
        {
            if (source is JObject obj)
            {
                var result = new JObject();
                foreach (var property in obj.Properties()) result[property.Name] = RedactJson(property.Value, gameRoot);
                return result;
            }
            if (source is JArray array)
            {
                var result = new JArray();
                foreach (var item in array) result.Add(RedactJson(item, gameRoot));
                return result;
            }
            return source.Type == JTokenType.String ? new JValue(Redact((string)source!, gameRoot)) : source.DeepClone();
        }
    }
}
