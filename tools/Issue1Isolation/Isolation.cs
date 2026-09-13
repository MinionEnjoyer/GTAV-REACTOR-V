using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Issue1Isolation
{
    internal sealed class Change
    {
        public string Relative = "";
        public string OriginalHash = "";
        public string? AppliedHash;
    }

    internal sealed class Receipt
    {
        public int Schema = 1;
        public string Tool = "issue1-isolation-v1";
        public string Id = "";
        public string Root = "";
        public string Mode = "";
        public string Status = "Preparing";
        public DateTime PreparedUtc;
        public Dictionary<string, string> Identity = new Dictionary<string, string>();
        public List<Change> Changes = new List<Change>();
    }

    internal sealed class Isolation
    {
        public const string ProvidersOff = "managed-providers-off";
        public const string NativeOff = "native-off-windowed";
        public const string Config = "scripts/ReactorV/ReactorV.json";
        public const string Script = "scripts/ReactorV/RageWebUI.Script.dll";
        public const string Native = "plugins/ReactorV/RageWebUI.Native.dll";
        public static readonly string[] NativeFiles = {
            "ReactorV.Bootstrap.asi", "ReactorV.RenderHook.asi", "ReactorV.ScriptProbe.asi", Native
        };
        public static readonly Dictionary<string, string> Candidate = new Dictionary<string, string> {
            [Native] = "58156102796900eee24aac45e2ca3fc8538050ffe52ab12eaa92b73989226fc5",
            ["plugins/ReactorV/RageWebUI.Runtime.dll"] = "54833ddd212545e9fce7c773dba96c00e2c683bbb2a06b1d2e2c51e78d8db1a6",
            ["plugins/ReactorV/ReactorV.Preloader.exe"] = "77d05d87ad12a391911935ad84e43b0c8e996b607291adf2f335a9bafa163db1",
            [Script] = "a9df990bf43700758b1905568159ffd5b6ac592b70fb6027f08c28a05cedac0f"
        };

        public string Root { get; }
        public string Store { get; }
        private readonly Action stopped;
        private readonly IDictionary<string, string> expected;
        private readonly Action<int>? afterChange;

        public Isolation(string root, string store, Action requireStopped,
            IDictionary<string, string>? expectedIdentity = null, Action<int>? faultInjection = null)
        {
            Root = Full(root); Store = Full(store); stopped = requireStopped;
            expected = expectedIdentity ?? Candidate; afterChange = faultInjection;
            if (!Directory.Exists(Root) || Root == Path.GetPathRoot(Root)?.TrimEnd('\\'))
                throw new InvalidOperationException("Select the GTA Enhanced installation folder, not a drive root.");
            SafePath(Root); SafePath(Store);
            if (Within(Store, Root) || Within(Root, Store))
                throw new InvalidOperationException("Backups must be outside the game folder, in a separate directory.");
            if (!File.Exists(Target("GTA5_Enhanced.exe")))
                throw new InvalidOperationException("GTA5_Enhanced.exe is missing from the selected folder.");
        }

        public static string Full(string value) => Path.GetFullPath(value).TrimEnd('\\', '/');
        public static bool Within(string value, string parent) =>
            string.Equals(value, parent, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        public static void SafePath(string path)
        {
            for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) &&
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("A junction/symlink is not supported for this test: " + p);
        }

        public string Target(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split('/', '\\').Any(p => p == ".." || p == "." || p == "") || relative.Contains(':'))
                throw new InvalidOperationException("Unsafe relative path in test receipt.");
            var target = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!Within(target, Root) || target == Root) throw new InvalidOperationException("Target escapes game directory.");
            SafePath(target); return target;
        }

        public static string Hash(string path)
        {
            using var sha = SHA256.Create();
            using var input = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        private static string TextHash(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        private string Key => TextHash(Root.ToUpperInvariant()).Substring(0, 24);
        private string Index => Path.Combine(Store, Key + ".json");
        public string RunDirectory(Receipt receipt)
        {
            if (!Guid.TryParseExact(receipt.Id, "N", out _)) throw new InvalidOperationException("Invalid backup identity.");
            var result = Path.Combine(Store, receipt.Id); SafePath(result); return result;
        }
        private string Backup(Receipt r, int index) => Path.Combine(RunDirectory(r), "originals", index + ".bin");

        public Receipt? Current()
        {
            SafePath(Index);
            if (!File.Exists(Index)) return null;
            var id = File.ReadAllText(Index, Encoding.UTF8);
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid active-test index.");
            var path = Path.Combine(Store, id, "receipt.json"); SafePath(path);
            var r = JsonConvert.DeserializeObject<Receipt>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Invalid receipt.");
            ValidateReceipt(r);
            if (r.Id != id) throw new InvalidOperationException("Backup/index identity mismatch.");
            return r;
        }

        private void ValidateReceipt(Receipt r)
        {
            if (r.Schema != 1 || r.Tool != "issue1-isolation-v1" || !string.Equals(r.Root, Root, StringComparison.OrdinalIgnoreCase) ||
                (r.Mode != ProvidersOff && r.Mode != NativeOff) ||
                !new[] { "Preparing", "Prepared", "Restoring", "Restored" }.Contains(r.Status))
                throw new InvalidOperationException("Receipt belongs to a different test or game directory.");
            RunDirectory(r);
            var paths = r.Changes.Select(c => c.Relative).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidOperationException("Duplicate receipt targets.");
            if (r.Mode == NativeOff && !paths.OrderBy(p => p).SequenceEqual(NativeFiles.Concat(new[] { Config }).OrderBy(p => p)))
                throw new InvalidOperationException("Unexpected native-test targets.");
            if (r.Mode == ProvidersOff && (paths.Length != 2 || !paths.Contains(Script) ||
                paths.Count(p => p.StartsWith("scripts/", StringComparison.Ordinal) && Path.GetFileName(p) == "ALLIN1.dll") != 1))
                throw new InvalidOperationException("Unexpected managed-test targets.");
            foreach (var c in r.Changes)
            {
                Target(c.Relative);
                if (c.OriginalHash.Length != 64 || (c.AppliedHash != null && (c.Relative != Config || c.AppliedHash.Length != 64)))
                    throw new InvalidOperationException("Invalid file identity in receipt.");
            }
            foreach (var path in r.Identity.Keys) Target(path);
        }

        private static void AtomicText(string path, string text)
        {
            SafePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        private void Save(Receipt r) => AtomicText(Path.Combine(RunDirectory(r), "receipt.json"), JsonConvert.SerializeObject(r, Formatting.Indented));

        private List<string> NamedFiles(string directory, string name)
        {
            var result = new List<string>();
            if (!Directory.Exists(directory)) return result;
            SafePath(directory);
            foreach (var file in Directory.GetFiles(directory))
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                { SafePath(file); result.Add(file); }
            foreach (var child in Directory.GetDirectories(directory)) result.AddRange(NamedFiles(child, name));
            return result;
        }

        private List<string> NativeCopies(string directory, string name)
        {
            var result = new List<string>();
            if (!Directory.Exists(directory)) return result;
            SafePath(directory);
            foreach (var file in Directory.GetFiles(directory))
                if (Path.GetFileName(file).StartsWith(name, StringComparison.OrdinalIgnoreCase))
                { SafePath(file); result.Add(file); }
            foreach (var child in Directory.GetDirectories(directory)) result.AddRange(NativeCopies(child, name));
            return result;
        }

        public Dictionary<string, string> Preflight()
        {
            stopped();
            var existing = Current();
            if (existing != null && existing.Status != "Restored") throw new InvalidOperationException("Restore the active test before changing modes.");
            var identity = new Dictionary<string, string>();
            foreach (var e in expected)
            {
                var path = Target(e.Key);
                if (!File.Exists(path) || Hash(path) != e.Value)
                    throw new InvalidOperationException("The tested issue1 diagnostic patch is required. Reapply its four files first. Mismatch: " + e.Key);
                identity[e.Key] = e.Value;
            }
            foreach (var p in NativeFiles.Concat(new[] { "GTA5_Enhanced.exe", "ScriptHookV.dll", "ScriptHookVDotNet.asi", Config }))
            {
                if (!File.Exists(Target(p))) throw new InvalidOperationException("Required file missing: " + p);
                identity[p] = Hash(Target(p));
            }
            foreach (var file in NativeFiles.Concat(new[] { Script }))
            {
                var name = Path.GetFileName(file);
                var native = NativeFiles.Contains(file);
                var found = (native ? NativeCopies(Target("scripts"), name) : NamedFiles(Target("scripts"), name))
                    .Concat(native ? NativeCopies(Target("plugins"), name) : NamedFiles(Target("plugins"), name))
                    .Concat(Directory.GetFiles(Root).Where(p => native ? Path.GetFileName(p).StartsWith(name, StringComparison.OrdinalIgnoreCase) : string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (found.Length != 1 || !string.Equals(found[0], Target(file), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Duplicate or nonstandard Reactor component: " + name);
            }
            return identity;
        }

        public Receipt Prepare(string mode)
        {
            if (mode != ProvidersOff && mode != NativeOff) throw new ArgumentException("Unknown isolation mode.");
            var identity = Preflight();
            var providers = NamedFiles(Target("scripts"), "ALLIN1.dll");
            if (providers.Count != 1) throw new InvalidOperationException("Expected exactly one ALLIN1.dll under scripts; found " + providers.Count + ". No files changed.");
            string allin1 = providers[0].Substring(Root.Length + 1).Replace('\\', '/');
            identity[allin1] = Hash(providers[0]);
            var r = new Receipt { Id = Guid.NewGuid().ToString("N"), Root = Root, Mode = mode, PreparedUtc = DateTime.UtcNow, Identity = identity };
            var targets = mode == ProvidersOff ? new[] { Script, allin1 } : NativeFiles.Concat(new[] { Config }).ToArray();
            string? configText = null;
            if (mode == NativeOff)
            {
                var settings = JObject.Parse(File.ReadAllText(Target(Config)));
                var renderer = settings.Properties().Where(p => string.Equals(p.Name, "renderer", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (renderer.Length > 1) throw new InvalidOperationException("Ambiguous renderer keys in ReactorV.json. No files changed.");
                if (renderer.Length == 1) renderer[0].Value = "windowed"; else settings["Renderer"] = "windowed";
                configText = settings.ToString(Formatting.Indented);
            }
            foreach (var path in targets) r.Changes.Add(new Change { Relative = path, OriginalHash = Hash(Target(path)), AppliedHash = path == Config ? TextHash(configText!) : null });
            ValidateReceipt(r);
            Directory.CreateDirectory(Path.Combine(RunDirectory(r), "originals"));
            Directory.CreateDirectory(Path.Combine(RunDirectory(r), "parked"));
            for (int i = 0; i < r.Changes.Count; i++)
            {
                File.Copy(Target(r.Changes[i].Relative), Backup(r, i), false);
                if (Hash(Backup(r, i)) != r.Changes[i].OriginalHash) throw new IOException("Backup verification failed; game files untouched.");
            }
            Save(r); AtomicText(Index, r.Id);
            try
            {
                stopped();
                for (int i = 0; i < r.Changes.Count; i++)
                {
                    stopped();
                    var c = r.Changes[i]; var target = Target(c.Relative);
                    if (Hash(target) != c.OriginalHash) throw new IOException("File changed after preflight: " + c.Relative);
                    if (c.AppliedHash == null) File.Move(target, Path.Combine(RunDirectory(r), "parked", i + ".bin"));
                    else AtomicText(target, configText!);
                    afterChange?.Invoke(i);
                }
                r.Status = "Prepared"; Save(r); Verify(r); return r;
            }
            catch (Exception error)
            {
                try { Restore(); }
                catch (Exception recovery) { throw new IOException("Preparation interrupted. Backup retained at " + RunDirectory(r) + ". Restore required: " + recovery.Message, error); }
                throw new IOException("Preparation failed; original files restored. " + error.Message, error);
            }
        }

        public void Verify(Receipt r)
        {
            ValidateReceipt(r);
            foreach (var item in r.Identity)
            {
                var change = r.Changes.SingleOrDefault(c => c.Relative == item.Key);
                var expectedHash = change == null ? item.Value : change.AppliedHash;
                var target = Target(item.Key);
                if (expectedHash == null ? File.Exists(target) : !File.Exists(target) || Hash(target) != expectedHash)
                    throw new InvalidOperationException("Isolation was changed or repaired; this run is not valid: " + item.Key);
            }
            if (r.Mode == ProvidersOff)
                foreach (var name in new[] { "ALLIN1.dll", "RageWebUI.Script.dll" })
                    if (NamedFiles(Target("scripts"), name).Count != 0) throw new InvalidOperationException("A disabled provider has reappeared: " + name);
            if (r.Mode == NativeOff)
                foreach (var name in NativeFiles.Select(Path.GetFileName))
                    if (NativeCopies(Target("scripts"), name).Count + NativeCopies(Target("plugins"), name).Count != 0 ||
                        Directory.GetFiles(Root).Any(p => Path.GetFileName(p).StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("A disabled native component has reappeared: " + name);
        }

        public void Restore()
        {
            stopped(); var r = Current() ?? throw new InvalidOperationException("No test backup for this game folder.");
            if (r.Status == "Restored") return;
            // Validate every backup and conflict before changing any target.
            for (int i = 0; i < r.Changes.Count; i++)
            {
                var c = r.Changes[i]; var target = Target(c.Relative); SafePath(Backup(r, i));
                if (!File.Exists(Backup(r, i)) || Hash(Backup(r, i)) != c.OriginalHash)
                    throw new InvalidOperationException("Backup changed; refusing restore: " + c.Relative);
                if (File.Exists(target))
                {
                    var hash = Hash(target);
                    if (hash != c.OriginalHash && hash != c.AppliedHash)
                        throw new InvalidOperationException("File changed during test; restore will not overwrite it: " + c.Relative + ". Originals: " + RunDirectory(r));
                }
            }
            r.Status = "Restoring"; Save(r);
            for (int i = 0; i < r.Changes.Count; i++)
            {
                stopped();
                var c = r.Changes[i]; var target = Target(c.Relative);
                if (File.Exists(target) && Hash(target) == c.OriginalHash) continue;
                var temp = target + ".restore-" + Guid.NewGuid().ToString("N");
                File.Copy(Backup(r, i), temp, false);
                if (Hash(temp) != c.OriginalHash) throw new IOException("Restore staging verification failed.");
                if (File.Exists(target))
                {
                    var current = Hash(target);
                    if (current != c.OriginalHash && current != c.AppliedHash)
                        throw new IOException("File changed while staging restore; refusing overwrite: " + c.Relative);
                }
                if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                if (Hash(target) != c.OriginalHash) throw new IOException("Restored file verification failed.");
            }
            r.Status = "Restored"; Save(r);
        }
    }
}
