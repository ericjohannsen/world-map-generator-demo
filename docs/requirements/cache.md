# Stage cache requirements

## Goal

Stop regenerating a map on every launch. Reuse stage results when the settings, model versions, and upstream
input are identical, while keeping every variant until the user explicitly cleans up.

## Design summary

Content-addressed stage cache under the per-user application data directory, with a Merkle chain of stage
keys and a self-describing bundle per stage. The public GeoTIFF/PNG exports remain separate.

## Requirements

| ID | Requirement |
| --- | --- |
| CA-1 | The cache **must** live under `LocalApplicationData/WorldGen/cache` (macOS: `~/Library/Application Support/WorldGen/cache`), not in the install or export directory. |
| CA-2 | A stage key **must** be the SHA-256 of a canonical description of: cache schema, settings schema, stage model version, stage name, upstream stage key, and every setting that affects that stage. |
| CA-3 | Keys **must** form a Merkle chain: `s2` includes the `s1` key, `s3` includes `s2`, `s4` includes `s3`. Changing an upstream stage invalidates all descendants. |
| CA-4 | UI state (camera, renderer, grid, window geometry, 3D mode, exaggeration), output prefix, timestamps, and observed statistics **must not** affect a key. |
| CA-5 | A cache hit **must** store the complete stage result needed by downstream stages and diagnostics, not just the public height TIFF. |
| CA-6 | Each entry **must** contain a self-describing `stage.json` and a compressed bundle (`data.wgc`). The `index.json` **must** be derived and rebuildable by scanning the stage directories. |
| CA-7 | Writes **must** be atomic (temporary file then replace). A corrupt or schema-incompatible entry **must** be treated as a miss and regenerated, not crash the app. |
| CA-8 | On launch the app **must** load the cached Stage 1 and any cached downstream stages. If Stage 1 is not cached, it **must not** generate it automatically: the view stays empty and the user is prompted to adjust parameters and click Generate. Missing downstream stages are not auto-run. |
| CA-9 | A stage action (erosion, climate, hydrology) **must** try the cache before computing and store the result after computing. It **must not** implicitly generate a missing prerequisite; it reports which stage is missing. |
| CA-10 | Cache reads and writes **must not** alter canonical artifacts, determinism, or stage math. Exports are written independently. |
| CA-11 | `--cache-list` **must** report stage, key, model version, status, compatibility, size, last use, and a settings summary. |
| CA-12 | `--cache-prune` **must** default to a dry run. Deletion requires `--cache-yes`. Filters: `--cache-stage`, `--cache-older-than`, `--cache-include-released`. |
| CA-13 | Pruning an upstream entry **must** cascade to entries whose `inputKey` chain depends on it. |
| CA-14 | The current settings' key chain **must** be retained by default. `dev` entries may be pruned; `released` entries only with `--cache-include-released`; `unknown` entries are not pruned automatically. |
| CA-15 | There is **no silent eviction**. Entries remain until an explicit prune or a future world-aware cleanup. |
| CA-16 | Named worlds are future work. When they exist they **must** protect every stage key they reference from pruning. |
| CA-17 | Every bundle **must** record the cache schema, settings schema, stage model version, input key, and a settings snapshot. |
| CA-18 | `--no-cache` **must** bypass cache reads and writes for one run. |
| CA-19 | Export and diagnostics actions **must not** implicitly generate a missing stage. They export or inspect what is loaded, or tell the user which stage to run first. |

## Acceptance

- A first full run writes `s1`–`s4`; an identical second run prints cache hits and produces identical exports.
- Relaunching the app after a run loads Stage 1 and the cached downstream stages without recomputing.
- Launching with no cached Stage 1 leaves the view empty and writes no `s1`; clicking **Generate Stage 1** writes it.
- A stage or export action with a missing prerequisite reports the missing stage instead of generating it.
- Changing erosion settings produces a new `s2` key while `s1` remains a hit.
- Deleting `index.json` and relaunching rebuilds the index from `stage.json` files.
- `--cache-prune --cache-stage s2` previews the removal of `s2` and its descendants; `--cache-yes` deletes them.
- A corrupt `data.wgc` is treated as a miss, regenerated, and does not stop the app.
- `--cache-list` shows `model v1 (Dev, initial)` for all current entries.
