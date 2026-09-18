import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
import sys

SPEC = importlib.util.spec_from_file_location("release_manifests", Path(__file__).with_name("build-manifests.py"))
MODULE = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(MODULE)

class ManifestGeneratorTests(unittest.TestCase):
    def run_main(self, version, enhanced, enhanced_sidecar, legacy, legacy_sidecar, output):
        prior = sys.argv
        try:
            sys.argv = ["build-manifests.py", "--release-version", version, "--enhanced", str(enhanced), "--enhanced-sidecar", str(enhanced_sidecar), "--legacy", str(legacy), "--legacy-sidecar", str(legacy_sidecar), "--output", str(output)]
            MODULE.main()
        finally: sys.argv = prior

    def make_archive(self, root, version, edition, unsafe=None, duplicate=False):
        path = root / f"ReactorV-{version}-{edition.lower()}-live-test.zip"
        marker = {"schema_version": 1, "artifact_kind": edition.lower()+"-live-test", "public_release": False, "target_edition": edition,
                  "game_executable": "GTA5_Enhanced.exe" if edition == "Enhanced" else "GTA5.exe", "game_build_id": edition.lower() + "-test-build", "game_version": "1", "game_sha256": "a"*64, "experimental_render_hook": True}
        files = {"ReactorV.Bootstrap.asi": b"b", "ReactorV.RenderHook.asi": b"r", "ReactorV.ScriptProbe.asi": b"s", "plugins/ReactorV/RageWebUI.Native.dll": b"n", "plugins/ReactorV/ReactorV.Preloader.exe": b"p",
                 "scripts/ReactorV/ReactorV.contract.json": json.dumps({"schema_version":1,"product":"reactor-v","runtime_version":version}).encode(),
                 f"plugins/ReactorV/ReactorV.{edition}LiveTest.json": json.dumps(marker).encode()}
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in files.items(): archive.writestr(name, data)
            if unsafe: archive.writestr(unsafe, b"bad")
            if duplicate: archive.writestr("ReactorV.Bootstrap.asi", b"duplicate")
        sidecar = Path(str(path)+".sha256"); sidecar.write_text(hashlib.sha256(path.read_bytes()).hexdigest()+"  "+path.name, encoding="ascii")
        return path, sidecar

    def test_valid_pair_writes_both_references(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); enhanced, es=self.make_archive(root,"1.2.3","Enhanced"); legacy, ls=self.make_archive(root,"1.2.3","Legacy")
            output=root/"out"; actual, records, anchors=MODULE.inspect(enhanced,es,"1.2.3","Enhanced")
            self.assertEqual(7, len(anchors)); self.assertTrue(records); self.assertTrue(actual)
            self.run_main("1.2.3", enhanced, es, legacy, ls, output)
            self.assertTrue((output/"1.2.3-enhanced.json").exists()); self.assertTrue((output/"1.2.3-legacy.json").exists()); self.assertTrue((output/"release-index.json").exists())

    def test_rejects_renamed_old_contract_wrong_edition_bad_sidecar_unsafe_and_duplicate(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); old, sidecar=self.make_archive(root,"1.2.4","Enhanced")
            renamed=root/"ReactorV-1.2.5-enhanced-live-test.zip"; renamed.write_bytes(old.read_bytes()); renamed_sidecar=Path(str(renamed)+".sha256"); renamed_sidecar.write_text(hashlib.sha256(renamed.read_bytes()).hexdigest(),encoding="ascii")
            with self.assertRaises(ValueError): MODULE.inspect(renamed,renamed_sidecar,"1.2.5","Enhanced")
            output=root/"no-partial"
            legacy, ls=self.make_archive(root,"1.2.5","Legacy")
            with self.assertRaises(ValueError): self.run_main("1.2.5",renamed,renamed_sidecar,legacy,ls,output)
            self.assertFalse(output.exists())
            with self.assertRaises(ValueError): MODULE.inspect(old,sidecar,"1.2.4","Legacy")
            sidecar.write_text("0"*64,encoding="ascii")
            with self.assertRaises(ValueError): MODULE.inspect(old,sidecar,"1.2.4","Enhanced")
            unsafe, us=self.make_archive(root,"1.2.4","Enhanced",unsafe="../escape.dll")
            with self.assertRaises(ValueError): MODULE.inspect(unsafe,us,"1.2.4","Enhanced")
            duplicate, ds=self.make_archive(root,"1.2.4","Enhanced",duplicate=True)
            with self.assertRaises(ValueError): MODULE.inspect(duplicate,ds,"1.2.4","Enhanced")

if __name__ == "__main__": unittest.main()
