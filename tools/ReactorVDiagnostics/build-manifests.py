"""Generate release-pinned diagnostic manifests from explicit ZIPs and sidecars."""
import argparse
import hashlib
import json
import re
from pathlib import Path
import zipfile

ALLOWED_ROOTS = ("plugins/ReactorV/", "scripts/ReactorV/")
ALLOWED_FILES = {"ReactorV.RenderHook.asi", "ReactorV.Bootstrap.asi", "ReactorV.ScriptProbe.asi"}
IDENTITY_PATHS = ("ReactorV.Bootstrap.asi", "ReactorV.RenderHook.asi", "ReactorV.ScriptProbe.asi", "plugins/ReactorV/RageWebUI.Native.dll", "plugins/ReactorV/ReactorV.Preloader.exe", "scripts/ReactorV/ReactorV.contract.json")
SHA256 = re.compile(r"(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])")

def digest(stream):
    sha = hashlib.sha256()
    while block := stream.read(1024 * 1024): sha.update(block)
    return sha.hexdigest()

def sidecar_hash(path):
    matches = SHA256.findall(path.read_text(encoding="ascii"))
    if len(matches) != 1: raise ValueError(f"Malformed SHA-256 sidecar: {path}")
    return matches[0].lower()

def expected_name(version, edition): return f"ReactorV-{version}-{edition.lower()}-live-test.zip"

def inspect(archive_path, sidecar_path, version, edition):
    if archive_path.name != expected_name(version, edition): raise ValueError(f"Wrong artifact name for {edition}: {archive_path.name}")
    expected = sidecar_hash(sidecar_path)
    with archive_path.open("rb") as stream: actual = digest(stream)
    if actual != expected: raise ValueError(f"Archive does not match sidecar: {archive_path}")
    records, seen = [], set()
    with zipfile.ZipFile(archive_path) as archive:
        for entry in archive.infolist():
            if entry.is_dir(): continue
            path = entry.filename.replace("\\", "/")
            parts = path.split("/")
            if any(p in ("", ".", "..") or ":" in p for p in parts): raise ValueError("Unsafe archive entry")
            if path.lower() in seen: raise ValueError("Duplicate case-insensitive path")
            seen.add(path.lower())
            if not (path.startswith(ALLOWED_ROOTS) or path in ALLOWED_FILES): raise ValueError("Unscoped release entry: " + path)
            with archive.open(entry) as stream: sha = digest(stream)
            records.append({"path": path, "sha256": sha, "length": entry.file_size})
        def json_entry(path):
            entry = archive.getinfo(path)
            if entry.file_size > 64 * 1024: raise ValueError("Oversized release identity JSON")
            return json.loads(archive.read(entry).decode("utf-8"))
        contract = json_entry("scripts/ReactorV/ReactorV.contract.json")
        if contract.get("schema_version") != 1 or contract.get("product") != "reactor-v" or contract.get("runtime_version") != version:
            raise ValueError("Release contract does not identify the requested runtime version")
        marker = json_entry(f"plugins/ReactorV/ReactorV.{edition}LiveTest.json")
        expected_exe = "GTA5_Enhanced.exe" if edition == "Enhanced" else "GTA5.exe"
        if marker.get("schema_version") != 1 or marker.get("artifact_kind") != edition.lower() + "-live-test" or marker.get("target_edition") != edition or marker.get("game_executable") != expected_exe or marker.get("public_release") is not False or marker.get("experimental_render_hook") is not True or not isinstance(marker.get("game_version"), str) or not SHA256.fullmatch(str(marker.get("game_sha256", ""))):
            raise ValueError("Release edition marker is invalid or does not match the requested edition")
    by_path = {record["path"]: record for record in records}
    identity_paths = IDENTITY_PATHS + (f"plugins/ReactorV/ReactorV.{edition}LiveTest.json",)
    if any(path not in by_path for path in identity_paths): raise ValueError("Release lacks required identity anchor")
    return actual, sorted(records, key=lambda r: r["path"].lower()), [by_path[path] for path in identity_paths]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-version", required=True)
    parser.add_argument("--enhanced", type=Path, required=True); parser.add_argument("--enhanced-sidecar", type=Path, required=True)
    parser.add_argument("--legacy", type=Path, required=True); parser.add_argument("--legacy-sidecar", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "manifests")
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.release_version): raise ValueError("Release version must be major.minor.patch")
    generated = []
    for edition, archive, sidecar in (("Enhanced", args.enhanced, args.enhanced_sidecar), ("Legacy", args.legacy, args.legacy_sidecar)):
        package_hash, records, anchors = inspect(archive, sidecar, args.release_version, edition)
        name = f"{args.release_version}-{edition.lower()}.json"
        data = {"schemaVersion": 1, "releaseVersion": args.release_version, "edition": edition, "packageSha256": package_hash, "artifact": archive.name, "identityAnchors": anchors, "files": records}
        generated.append((name, data))
    args.output.mkdir(parents=True, exist_ok=True)
    # Both packages validate before writing either reference or index.
    for name, data in generated: (args.output / name).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    index_path = args.output / "release-index.json"
    old = { (x["releaseVersion"], x["edition"]): x for x in json.loads(index_path.read_text(encoding="utf-8")).get("releases", []) } if index_path.exists() else {}
    for name, data in generated:
        old[(data["releaseVersion"], data["edition"])] = {"releaseVersion": data["releaseVersion"], "edition": data["edition"], "manifest": name, "artifact": data["artifact"], "packageSha256": data["packageSha256"], "identityAnchors": data["identityAnchors"]}
    releases = sorted(old.values(), key=lambda x: (tuple(map(int, x["releaseVersion"].split("."))), x["edition"]))
    index_path.write_text(json.dumps({"schemaVersion": 1, "releases": releases}, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {args.release_version}: " + ", ".join(name for name, _ in generated))

if __name__ == "__main__": main()
