"""Generate bounded reference data from explicitly verified public release archives."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

RELEASES = {
    "Enhanced": "36a54e6d2689e98a232205702b23462b630254260af1b9d048f5ab8abae06de7",
    "Legacy": "186fae1cde3f3e01ab7c2d05b06d208a19fb79d0ffc15104039dd08919aab4b8",
}


def digest(stream):
    sha = hashlib.sha256()
    while block := stream.read(1024 * 1024):
        sha.update(block)
    return sha.hexdigest()


def generate(archive_path, edition, output):
    with archive_path.open("rb") as stream:
        actual = digest(stream)
    if actual != RELEASES[edition]:
        raise ValueError(f"Not the verified public 0.2.4 {edition} archive: {actual}")
    records, seen = [], set()
    with zipfile.ZipFile(archive_path) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            path = entry.filename.replace("\\", "/")
            parts = path.split("/")
            if any(p in ("", ".", "..") or ":" in p for p in parts):
                raise ValueError("Unsafe archive entry")
            if path.lower() in seen:
                raise ValueError("Duplicate case-insensitive path")
            seen.add(path.lower())
            if not (path.startswith(("plugins/ReactorV/", "scripts/ReactorV/")) or path in (
                "ReactorV.RenderHook.asi", "ReactorV.Bootstrap.asi", "ReactorV.ScriptProbe.asi"
            )):
                raise ValueError("Unscoped release entry: " + path)
            with archive.open(entry) as stream:
                sha = digest(stream)
            records.append({"path": path, "sha256": sha, "length": entry.file_size})
    data = {"schemaVersion": 1, "releaseVersion": "0.2.4", "edition": edition,
            "packageSha256": actual, "files": sorted(records, key=lambda r: r["path"].lower())}
    output.mkdir(parents=True, exist_ok=True)
    target = output / f"0.2.4-{edition.lower()}.json"
    target.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    print(f"{edition}: {len(records)} entries -> {target}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--enhanced", type=Path, required=True)
    parser.add_argument("--legacy", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "manifests")
    args = parser.parse_args()
    generate(args.enhanced, "Enhanced", args.output)
    generate(args.legacy, "Legacy", args.output)
