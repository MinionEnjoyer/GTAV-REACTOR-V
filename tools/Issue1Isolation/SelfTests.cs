using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Issue1Isolation
{
    internal static class SelfTests
    {
        private sealed class Fixture
        {
            public string Root = "", Store = "";
            public Isolation Tool = null!;
            public Dictionary<string, string> Expected = new Dictionary<string, string>();
            public Dictionary<string, byte[]> Original = new Dictionary<string, byte[]>();
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Throws(Action run)
        {
            bool threw = false; try { run(); } catch { threw = true; }
            Assert(threw, "Expected a fail-closed exception.");
        }
        private static void Write(string path, string text)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

        public static int Run(string output)
        {
            output = Path.GetFullPath(output); Isolation.SafePath(output);
            if (File.Exists(output)) return 2;
            var root = Path.Combine(Path.GetDirectoryName(output)!, "isolation-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var results = new List<object>(); int index = 0, failures = 0;
            Fixture New()
            {
                var f = new Fixture { Root = Path.Combine(root, (++index).ToString(), "game"), Store = Path.Combine(root, index.ToString(), "backups") };
                foreach (var name in Isolation.Candidate.Keys.Concat(Isolation.NativeFiles).Concat(new[] {
                    "GTA5_Enhanced.exe", "ScriptHookV.dll", "ScriptHookVDotNet.asi", Isolation.Config, "scripts/ALLIN1.dll", "scripts/OtherMod.dll"
                }).Distinct())
                {
                    var p = Path.Combine(f.Root, name);
                    Write(p, name == Isolation.Config ? "{ \r\n \"renderer\": \"auto\", \"custom\": {\"keep\": 19}, \"ToggleKey\": \"F8\" }\r\n" : "fixture-" + name);
                    f.Original[name] = File.ReadAllBytes(p);
                }
                foreach (var name in Isolation.Candidate.Keys) f.Expected[name] = Isolation.Hash(Path.Combine(f.Root, name));
                f.Tool = new Isolation(f.Root, f.Store, () => { }, f.Expected); return f;
            }
            void Original(Fixture f)
            { foreach (var item in f.Original) Assert(File.ReadAllBytes(Path.Combine(f.Root, item.Key)).SequenceEqual(item.Value), "Original bytes not restored: " + item.Key); }
            void Test(string name, Action body)
            { try { body(); results.Add(new { name, passed = true }); } catch (Exception e) { failures++; results.Add(new { name, passed = false, error = e.ToString() }); } }

            foreach (var mode in new[] { Isolation.ProvidersOff, Isolation.NativeOff })
                Test(mode + " roundtrip", () => {
                    var f = New(); var r = f.Tool.Prepare(mode); f.Tool.Verify(r);
                    foreach (var c in r.Changes.Where(c => c.AppliedHash == null)) Assert(!File.Exists(f.Tool.Target(c.Relative)), "Disabled binary remains in game.");
                    if (mode == Isolation.NativeOff)
                    {
                        var settings = JObject.Parse(File.ReadAllText(f.Tool.Target(Isolation.Config)));
                        Assert((string?)settings["renderer"] == "windowed" && (int?)settings["custom"]?["keep"] == 19, "Windowed configuration or unknown setting lost.");
                    }
                    Assert(File.Exists(f.Tool.Target("scripts/OtherMod.dll")), "Unrelated script changed.");
                    f.Tool.Restore(); Original(f); f.Tool.Restore(); Original(f);
                });
            Test("reopen backup and restore", () => { var f = New(); f.Tool.Prepare(Isolation.NativeOff); new Isolation(f.Root, f.Store, () => { }, f.Expected).Restore(); Original(f); });
            Test("unknown candidate blocked before mutation", () => { var f = New(); f.Expected[Isolation.Native] = new string('0', 64); Throws(() => f.Tool.Prepare(Isolation.NativeOff)); Original(f); });
            Test("active mode switch blocked", () => { var f = New(); var r = f.Tool.Prepare(Isolation.ProvidersOff); Throws(() => f.Tool.Prepare(Isolation.NativeOff)); f.Tool.Verify(r); f.Tool.Restore(); Original(f); });
            Test("running-game guard blocks preparation", () => { var f = New(); var t = new Isolation(f.Root, f.Store, () => throw new Exception("running"), f.Expected); Throws(() => t.Prepare(Isolation.NativeOff)); Original(f); });
            Test("running-game guard blocks restore", () => { var f = New(); var r = f.Tool.Prepare(Isolation.NativeOff); var t = new Isolation(f.Root, f.Store, () => throw new Exception("running"), f.Expected); Throws(t.Restore); f.Tool.Verify(r); f.Tool.Restore(); Original(f); });
            Test("duplicate managed binary blocked", () => { var f = New(); Write(Path.Combine(f.Root, "scripts/duplicate/RageWebUI.Script.dll"), "duplicate"); Throws(() => f.Tool.Prepare(Isolation.ProvidersOff)); Original(f); });
            Test("duplicate native binary blocked", () => { var f = New(); Write(Path.Combine(f.Root, "scripts/RageWebUI.Native.dll"), "duplicate"); Throws(() => f.Tool.Prepare(Isolation.NativeOff)); Original(f); });
            Test("renamed ASI copy blocked", () => { var f = New(); Write(Path.Combine(f.Root, "ReactorV.RenderHook.asi2"), "renamed"); Throws(() => f.Tool.Prepare(Isolation.NativeOff)); Original(f); });
            Test("duplicate ALLIN1 blocked", () => { var f = New(); Write(Path.Combine(f.Root, "scripts/duplicate/ALLIN1.dll"), "duplicate"); Throws(() => f.Tool.Prepare(Isolation.ProvidersOff)); Original(f); });
            Test("ambiguous renderer config blocked", () => { var f = New(); var p = f.Tool.Target(Isolation.Config); Write(p, "{\"renderer\":\"auto\",\"Renderer\":\"directx\"}"); f.Original[Isolation.Config] = File.ReadAllBytes(p); Throws(() => f.Tool.Prepare(Isolation.NativeOff)); Original(f); });
            Test("partial preparation rolls back", () => { var f = New(); var t = new Isolation(f.Root, f.Store, () => { }, f.Expected, i => { if (i == 1) throw new IOException("simulated interruption"); }); Throws(() => t.Prepare(Isolation.NativeOff)); Original(f); Assert(t.Current()!.Status == "Restored", "Receipt not restored."); });
            Test("changed config blocks all restore writes", () => { var f = New(); var r = f.Tool.Prepare(Isolation.NativeOff); Write(f.Tool.Target(Isolation.Config), "user-edited"); Throws(f.Tool.Restore); foreach (var c in r.Changes.Where(c => c.AppliedHash == null)) Assert(!File.Exists(f.Tool.Target(c.Relative)), "Partial restore occurred before conflict detected."); Assert(File.ReadAllText(f.Tool.Target(Isolation.Config)) == "user-edited", "User edit overwritten."); });
            Test("new parked-path file preserved", () => { var f = New(); f.Tool.Prepare(Isolation.ProvidersOff); Write(f.Tool.Target(Isolation.Script), "new-version"); Throws(f.Tool.Restore); Assert(File.ReadAllText(f.Tool.Target(Isolation.Script)) == "new-version", "New version overwritten."); });
            Test("corrupted backup blocks restore", () => { var f = New(); var r = f.Tool.Prepare(Isolation.NativeOff); Write(Path.Combine(f.Tool.RunDirectory(r), "originals/1.bin"), "corrupt"); Throws(f.Tool.Restore); Assert(!File.Exists(f.Tool.Target(Isolation.NativeFiles[0])), "Restore partially changed game."); });
            Test("repair detected after preparation", () => { var f = New(); var r = f.Tool.Prepare(Isolation.NativeOff); Write(f.Tool.Target(Isolation.Native), "repaired-native"); Throws(() => f.Tool.Verify(r)); });
            Test("new duplicate provider detected", () => { var f = New(); var r = f.Tool.Prepare(Isolation.ProvidersOff); Write(f.Tool.Target("scripts/duplicate/ALLIN1.dll"), "duplicate"); Throws(() => f.Tool.Verify(r)); });
            Test("receipt path traversal refused", () => { var f = New(); var r = f.Tool.Prepare(Isolation.NativeOff); var p = Path.Combine(f.Tool.RunDirectory(r), "receipt.json"); var json = JObject.Parse(File.ReadAllText(p)); json["Changes"]![0]!["Relative"] = "../outside.dll"; File.WriteAllText(p, json.ToString()); Throws(f.Tool.Restore); });
            Test("game root mismatch refused", () => { var f = New(); var r = f.Tool.Prepare(Isolation.NativeOff); var p = Path.Combine(f.Tool.RunDirectory(r), "receipt.json"); var json = JObject.Parse(File.ReadAllText(p)); json["Root"] = f.Store; File.WriteAllText(p, json.ToString()); Throws(f.Tool.Restore); });
            Test("nested backup refused", () => { var f = New(); Throws(() => new Isolation(f.Root, Path.Combine(f.Root, "backup"), () => { }, f.Expected)); });
            Test("traversal and absolute targets refused", () => { var f = New(); foreach (var p in new[] { "../x", "scripts/../../x", "C:/x", "scripts/x:stream", "scripts//x" }) Throws(() => f.Tool.Target(p)); });
            Test("log suffix selection", () => { Assert(SessionCapture.ChangedBytes(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }).SequenceEqual(new byte[] { 3 }), "Old log content included."); Assert(SessionCapture.ChangedBytes(new byte[] { 1, 2 }, new byte[] { 3 }).SequenceEqual(new byte[] { 3 }), "Truncated/replaced log lost."); });
            Test("PID matching rejects substring collisions", () => { Assert(SessionCapture.HasPid("target_pid=29240 stage=x", 29240), "Target missing."); Assert(!SessionCapture.HasPid("pid=292400 stage=x", 29240), "PID substring accepted."); Assert(!SessionCapture.HasPid("pid=129240 stage=x", 29240), "PID suffix accepted."); });
            Test("native-off evidence requires windowed tick and observed SHVDN", () => {
                var seen = new[] { "ScriptHookVDotNet.asi" }; var log = "renderer=WebView2_window stage=diagnostic_first_tick_exit";
                Assert(SessionCapture.EvaluateEvidence(Isolation.NativeOff, true, seen, 1, log, "", out _), "Valid evidence refused.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.NativeOff, true, seen, 1, "renderer=WebView2_window", "", out _), "Missing tick accepted.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.NativeOff, true, Array.Empty<string>(), 1, log, "", out _), "Missing SHVDN accepted.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.NativeOff, true, seen, 0, log, "", out _), "Missing snapshots accepted.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.NativeOff, false, seen, 1, log, "", out _), "Changed layout accepted.");
            });
            Test("native-off evidence rejects loaded native components including renamed copies", () => {
                foreach (var name in Isolation.NativeFiles.Select(Path.GetFileName).Concat(new[] { "ReactorV.RenderHook.asi2" }))
                    Assert(!SessionCapture.EvaluateEvidence(Isolation.NativeOff, true, new[] { "ScriptHookVDotNet.asi", name }, 1,
                        "renderer=WebView2_window stage=diagnostic_first_tick_exit", "", out _), "Native component accepted: " + name);
            });
            Test("provider-off evidence requires native components and script loading", () => {
                var seen = Isolation.NativeFiles.Select(Path.GetFileName).ToArray();
                Assert(SessionCapture.EvaluateEvidence(Isolation.ProvidersOff, true, seen, 1, "", "Loading scripts from", out _), "Valid evidence refused.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.ProvidersOff, true, seen.Skip(1), 1, "", "Loading scripts from", out _), "Missing native accepted.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.ProvidersOff, true, seen, 1, "", "", out _), "No script loading accepted.");
                Assert(!SessionCapture.EvaluateEvidence(Isolation.ProvidersOff, false, seen, 1, "", "Loading scripts from", out _), "Changed layout accepted.");
            });
            Test("provider-off evidence rejects managed provider activation", () => {
                foreach (var active in new[] { "Started script ALLIN1.GbayShop", "Started script RageWebUI.Script.RageWebUiScript", "source=script stage=construction_begin" })
                    Assert(!SessionCapture.EvaluateEvidence(Isolation.ProvidersOff, true, Isolation.NativeFiles.Select(Path.GetFileName), 1,
                        active, "Loading scripts from\n" + active, out _), "Provider activation accepted.");
            });
            Test("unknown evidence mode is refused", () => Throws(() => SessionCapture.EvaluateEvidence("unknown", true, Array.Empty<string>(), 0, "", "", out _)));
            Test("external temp backup roundtrip (cross-volume when test output is on another drive)", () => {
                var f = New(); var store = Path.Combine(Path.GetTempPath(), "ReactorV-isolation-backup-test-" + Guid.NewGuid().ToString("N"));
                var t = new Isolation(f.Root, store, () => { }, f.Expected); t.Prepare(Isolation.NativeOff); t.Restore(); Original(f);
            });
            File.WriteAllText(output, JsonConvert.SerializeObject(new { passed = failures == 0, tests = results.Count, failures,
                helperSha256 = Isolation.Hash(typeof(SelfTests).Assembly.Location), fixtures = root, results }, Formatting.Indented));
            return failures == 0 ? 0 : 1;
        }
    }
}
