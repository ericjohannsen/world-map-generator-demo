# Artifact cache

Requirements are in [../requirements/cache.md](../requirements/cache.md). Model identity is described in
[model-versions.md](model-versions.md).

## Why

The UI used to regenerate Stage 1 on every launch (`MainWindow.Opened` called `GenerateStage1Async`
unconditionally). The parameters were persisted but the artifacts were treated purely as exports. The cache
makes a stage result an input that can be resumed, while keeping exported files under user-controlled names.

## Layout

```
%LOCALAPPDATA%\WorldGen\cache\
  index.json                       derived listing, rebuildable
  s1\<key>\stage.json               self-describing entry
           data.wgc                 compressed stage bundle
  s2\<key>\...
  s3\<key>\...
  s4\<key>\...
```

- `key` is a 64-character lowercase SHA-256 hex string.
- Stage directories are one level deep; Windows path length stays well under the limit.
- `index.json` is a convenience. Deleting or corrupting it triggers `RebuildIndex()`, which scans
  `s*/<key>/stage.json`.

## Stage keys

`StageKeys` builds a canonical string and hashes it:

```
cache=<cacheSchema>;settings=<settingsSchema>;stage=s2;model=<modelVersion>;input=<s1Key>;
settings{AreaExponentM=0.45;CoastalDeltaStrength=0.4;...;};
```

- Settings are reflected from the settings record in ordinal name order. Adding a parameter changes the key
  automatically; this is the conservative choice.
- Floats and doubles use round-trip (`"R"`) invariant formatting, so `0.29` and `0.290` cannot produce
  different keys.
- UI state, export paths, timestamps, and observed statistics are not in the input at all.
- The stage key includes the model version, so a model bump gives a new identity.

## Bundle format (`data.wgc`)

```
"WGC1"                          magic
Deflate stream:
  int32 headerLength
  UTF-8 JSON header
  concatenated array bytes
```

The header contains:

- stage, key, input key, model version, settings schema, cache schema;
- the resolved settings JSON and metrics JSON;
- width, height, map window;
- an ordered array table: name, element type (`float32`, `int32`, `byte`, `sbyte`), count.

Arrays are written in order; the reader uses the table to allocate and copy. The format assumes a
little-endian runtime (all supported .NET targets) and rejects unknown cache schemas. Hydrology lake/river
vectors are serialized to UTF-8 JSON and stored as a `vectors` byte array in the payload, so a large river
network does not inflate the header. Compression is `CompressionLevel.Optimal`; a 256×128 four-stage test
bundle set is well under 1 MB, while an 8192×4096 hydrology bundle can be hundreds of MB before compression.

### Payloads

| Stage | Arrays and metadata |
| --- | --- |
| `s1` | `height`; terrain settings; metrics none |
| `s2` | `height`, `erosionDepth`, `sediment`, `drainage`; erosion metrics |
| `s3` | `temperature`, `precipitation`, `potentialEvaporation`, `runoff`; climate metrics |
| `s4` | `height`, `flow` (sbyte), `receiver` (int), `drainage`, `discharge`, `waterMask` (byte), `lakeId` (int), `riverOrder` (byte), `channelWidth`, `vectors` (UTF-8 JSON byte array); hydrology metrics |

The public height GeoTIFF is **not** the reuse format: downstream stages and diagnostics need the other
arrays.

## Entry sidecar

`stage.json` mirrors the index entry:

```json
{
  "stage": "s2",
  "key": "…",
  "inputKey": "…",
  "modelVersion": 1,
  "settingsSchemaVersion": 1,
  "cacheSchemaVersion": 1,
  "settingsSummary": "settings{AreaExponentM=0.45;…}",
  "createdUtc": "…",
  "lastUsedUtc": "…",
  "sizeBytes": 123456,
  "arrays": ["drainage", "erosionDepth", "height", "sediment"]
}
```

`CacheEntry.Status` is computed from the current registry and is not serialized.

## Write path

1. `StageBundle.Save` writes to `data.wgc.tmp`.
2. The cache atomically replaces `data.wgc`.
3. `stage.json.tmp` is written and replaced.
4. The index is rewritten atomically (`index.json.tmp` → `index.json`).
5. Existing entries with the same stage/key are replaced; different keys coexist.

A corrupt index is quarantined as `index.corrupt-<timestamp>.json` and rebuilt. An empty or stale index is
also reconciled against the stage directories; `TryGet` can recover an entry from its `stage.json` sidecar even
if the index missed it, so a crash between the bundle write and the index write cannot hide a valid stage. A
corrupt bundle is treated as a miss by the typed loader and regenerated on the next run.

## App behaviour

- `GenerateStage1Async` computes the Stage 1 key and tries `TerrainStageCodec.TryLoadStage1`. The explicit
  **Generate Stage 1** action generates and stores on a miss; the startup path calls the same lookup with
  generation disabled, so a miss leaves the view empty. A cache hit regenerates only the preview.
- `TryLoadCachedDownstreamAsync` computes `s2` from the Stage 1 key and the current erosion settings, then
  `s3`, then `s4`, stopping at the first miss. It never runs a missing stage.
- `ApplyStage2/3/4` each require their upstream stage, try the cache first, and on a miss run the stage and
  write the bundle. They do not generate a missing prerequisite.
- Loading an external GeoTIFF clears the stage keys, so the cache is not polluted with a synthetic input.
- The status line reports “Loaded cached Stage 1 (key …)” and the source labels show the cached key and
  model version.

**Startup policy:** load hits, do not generate on a miss. If Stage 1 is not cached, the app leaves the view
empty and tells the user to adjust parameters and click **Generate Stage 1**; it does not start a background
generation just because the app opened. Missing downstream stages are likewise not auto-run; erosion,
climate, and hydrology are explicit actions. Stage, export, and diagnostics actions require their
prerequisites and report the missing stage rather than generating it implicitly.

## CLI

`--cache-list` prints stage, short key, model version/status/compatibility, size, last use, and a settings
summary.

`--cache-prune` is a dry run unless `--cache-yes` is supplied:

- `--cache-stage s2,s3` limits stages;
- `--cache-older-than <days>` filters by `lastUsedUtc`;
- `--cache-include-released` allows pruning released models;
- the current settings' key chain is always retained;
- pruning an upstream key cascades to entries whose `inputKey` depends on it;
- `dev` only by default; `unknown` never.

`--cache-dir` overrides the root. `--no-cache` bypasses reads and writes for a CLI run.

## Concurrency

Unique keys and atomic replaces make concurrent instances safe enough: two writers may race on the index,
and the last writer wins. Stage bundles are immutable for a given key; a regeneration replaces the same key
atomically. No lock files are required.

## Sizes and retention

At 2048×1024, one float32 field is 8 MB; climate (four fields) and hydrology (about seven) dominate. At
8192×4096 the same fields reach hundreds of MB before Deflate. The cache retains until an explicit prune;
there is no silent LRU eviction. `--cache-list` reports total size so retention can be managed deliberately.

## Limits and future work

- Named worlds/projects are not implemented. The file format is planned; until then the current settings
  implicitly define the world.
- World-aware cleanup must protect referenced keys; the CLI currently protects only the current key chain.
- Derived previews and manifests are regenerated from cached results rather than stored, so colormap and
  manifest changes do not invalidate terrain.
- The bundle stores whole arrays; tile-level caching belongs to Stage 6.
