# Early data preload

Reactor V can validate and cache inert text or JSON data before GTA's script
threads become available. It does not load assemblies, run scripts, call game
natives, or grant the cached data any authority.

Place up to 16 manifests directly in:

`scripts/.reactorv/preload/*.json`

```json
{
  "schema_version": 1,
  "id": "example.core",
  "entries": [
    {
      "id": "catalog",
      "path": "scripts/ExampleMod/catalog.json",
      "kind": "json",
      "required": true,
      "max_bytes": 4194304
    }
  ]
}
```

Manifest and entry IDs are lowercase identifiers containing letters, numbers,
dots, underscores, or dashes. Entry paths are relative to the GTA root;
absolute paths, traversal, and reparse-point escapes are rejected. Supported
kinds are `text` and `json`.

Limits are 64 entries per manifest, 4 MiB per entry, 16 MiB aggregate source
content, and 24 MiB per serialized snapshot. Optional missing entries are
omitted. A required missing or invalid entry leaves that manifest snapshot
incomplete with structured errors.

Snapshots are atomically published to the current GTA process namespace:

`%LOCALAPPDATA%/ReactorV/Preload/<gta-process-id>/<manifest-id>.snapshot.json`

When every discovered manifest is complete and at least one snapshot was
published, Reactor V signals:

`Local\ReactorV.PreloadDataReady.<gta-process-id>`

The event means usable preload data is ready; it is not a generic completion
notification. Reactor V does **not** signal it when no manifests exist, a
manifest or required entry is incomplete, the bounded build is cancelled, or
an unexpected failure occurs. Those terminal outcomes are recorded in the
preloader trace as `preload_manifest_directory_absent`,
`preload_data_not_ready`, `preload_data_abandoned`, or
`preload_data_failed` as appropriate.

An entry named `extension-registry` may contain a schema-1 package registry
with bounded `id`, `name`, `version`, `api_version`, and `enabled` fields. The
persistent main-menu host projects only those identities into Detected Mods;
descriptions, paths, runtime data, actions, and authored markup are discarded.
The projection is labelled **Installed / awaiting runtime** and is replaced by
the authoritative live extension registry after the managed provider connects.

For an isolated fixture, use the preloader's test-only cache mode with an
explicit GTA root and process ID:

```text
ReactorV.Preloader.exe --cache-only --gta-root <fixture-root> --parent-pid <pid>
```
