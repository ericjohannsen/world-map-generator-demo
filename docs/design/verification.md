# Verification

Correctness is checked with numbers and with decoders that do not share code with the writers. "It looks
fine" is not an acceptance test: a change to an octave count or a threshold can look harmless and still
break hypsometry, coastlines, or zoom consistency.

Two layers exist:

1. **In-process diagnostics** (`--check`) measure the terrain model: cross-resolution agreement,
   hypsometry, and landmass structure.
2. **Legacy independent artifact validation** (`tools/*.py`, unsupported; see [no-python.md](no-python.md)) decoded what was actually written and checked it
   against the manifest and against the other artifact.

## Hierarchy

Requirement: [DG-1](../requirements/diagnostics.md#dg-1--hierarchical-consistency).

**Method.** Draw 20,000 area-uniform directions from a Fibonacci sphere. Evaluate the terrain at the
render's `metersPerPixel` and again at `metersPerPixel / 64`. The plate sketch, province blend, and
landmark set are evaluated identically on both sides because they do not depend on `metersPerPixel`, so
they contribute zero deviation by construction. Report:

- maximum absolute elevation difference;
- RMS difference;
- the percentage of directions whose land/ocean classification flips.

**Threshold.** Max ≤ 100 m (the full detail layer is 70 m), RMS ≤ 25 m, coast flips ≤ 0.1%.

**Observed.**

| View | Coarse → fine | Max | RMS | Coast flips |
| --- | --- | --- | --- | --- |
| World 2048x1024 | 19.5 km/px → 305 m/px | 1 m | 0 m | 0.000% |
| Tile 1024² (`35..5 N`, `120..155 E`) | 3.8 km/px → 59 m/px | 0 m | 0 m | 0.000% |

The residual is the detail layer appearing at the finer resolution; it is far below the threshold. A
failure here usually means an octave count was changed without keeping the infinite-series normalisation,
or a layer's frequency was changed in only one place.

Note that this check uses the **world map's** `metersPerPixel` as the coarse side, then evaluates the
same directions at 64× finer. It does not rasterise a tile and compare pixels; that would add grid
alignment as a confounder.

## Statistics

Requirement: [DG-2](../requirements/diagnostics.md#dg-2--terrain-statistics).

**Hypsometry.** Elevations are binned at 50 m over the fixed range −8,000 .. 9,000 m. Rows are weighted by
`cos(latitude)`, because an equirectangular grid oversamples the poles. Land and ocean are accumulated
separately, and percentiles are computed from the weighted bins. The report prints land p50/p90/p99,
ocean p50/p90, and the area-weighted land fraction.

**Landmasses.** A separate 1° grid (360×180, row centres at 89.5°, 88.5°, …, −89.5°) is classified as
land where `Elevation(direction, 111_000 m) > 0`. Connected components use 4-neighbour flood fill with
longitude wrapping. Each component's area is weighted by `cos(latitude)`, and its centroid is the
normalised sum of its member directions. The report prints the total component count, the number larger
than 0.5% of the sphere, and the five largest with approximate centroids.

**Acceptance bands** are in [BT-4](../requirements/base-topology.md#bt-4--earth-like-hypsometry) and
[BT-5](../requirements/base-topology.md#bt-5--landmass-distribution). Observed values for seed 1337 are
recorded in those sections and in [terrain-model.md](terrain-model.md#observed-result-seed-1337-2048x1024).

Landmass analysis is skipped for cropped windows: component counts are a whole-planet property and a
crop would invent a boundary.

## Variety

Requirement: [DG-4](../requirements/diagnostics.md#dg-4--variety-report).

**Method.** Sample 20,000 Fibonacci directions, evaluate the elevation and the tectonic sample at each,
and report plate/hotspot/province/landmark counts, the share of the surface near a boundary
(influence > 0.5), the mean of each tectonic mask, landmark counts by kind, and the land area
represented by each province kind. See
[`TerrainDiagnostics.PrintVariety`../../src/WorldGen.Core/TerrainDiagnostics.cs.

**Thresholds.** In [BT-13](../requirements/base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks):
≥ 5 plates, ≥ 2 hotspots, ≥ 4 provinces, ≥ 10 landmarks; ≥ 10% of the surface near a boundary; at least
three masks with a non-zero mean; ≥ 4 province kinds on land with no kind above 60%.

**Observed** (seed 1337):

| Measure | Value |
| --- | --- |
| Plates / hotspots / provinces / landmarks | 13 / 5 / 9 / 60 |
| Surface with boundary influence > 0.5 | 24.9% |
| Mean orogeny / arc / rift | 0.064 / 0.030 / 0.023 |
| Mean trench / ocean ridge / transform | 0.007 / 0.030 / 0.012 |
| Landmarks by kind | volcano 3, caldera 4, massif 9, crater 11, seamount 33 |
| Land province kinds / largest share | 8 / Plateau 20% |

Because a province kind or a boundary type can silently vanish when a seed or a parameter changes, this
report is the acceptance gate for "the world is varied", not the preview alone.

## Artifact validation

The supported acceptance path is the numeric report (`--check`) plus the C# readers (`GeoTiffReader`, and a
PNG reader when one is added). The old independent decoders (`tools/verify_png.py`,
`tools/verify_geotiff.py`) are **legacy and unsupported**: they require Python, are not part of the build or
test path, and must not be used for new work ([no-python.md](no-python.md)).

They were useful as a specification of what "correct" means: PNG signature and chunk CRCs, 16-bit colour
type, exact inflated size, filter bytes, big-endian samples; GeoTIFF float32/single-band, Deflate,
`ModelPixelScale`, `ModelTiepoint`, GeoKeys, and cross-checks against the PNG and manifest. A future C#
validator should reproduce those checks.

## Determinism

With the same seed, view, size, and **same `--out` base**, every artifact must hash identically across
runs:

```powershell
$files = 'height.tif','height.png','preview.png','manifest.json'
$before = @{}
foreach ($f in $files) { $before[$f] = (Get-FileHash "output/world-$f").Hash }
dotnet run -- --out output/world
foreach ($f in $files) { "$f : $($before[$f] -eq (Get-FileHash "output/world-$f").Hash)" }
```

All four should print `True`. Observed: all `True`.

If the base differs (`output/world` vs `output/repeat`), the rasters are still identical but the manifest
differs because `files.heightGeoTiff`, `files.heightPng`, and `files.previewPng` contain the base names.
That is expected; compare manifests after normalising those fields, or use the same base.

## Cache and model versions

The cache is verified independently of terrain quality:

- **Registry validation.** `ModelRegistry.Default.ValidateCurrent()` fails if a current stage version is
  missing from `models.json`; every current entry is checked to be `dev` with `releasedIn: null`.
- **Key stability.** The same resolved settings produce the same SHA-256 key; changing a seed, a window, or a
  model version produces a different key. UI state is not part of the input.
- **Exact round-trip.** Stage 1–4 results are written to `.wgc` and read back; heights, erosion/deposition,
  drainage, temperature/precipitation/PET/runoff, flow/receiver/discharge/water mask/lake/river order, and
  lake/river vectors are compared exactly (not approximately). Metrics round-trip within floating-point
  tolerance.
- **Corruption behavior.** A malformed index is quarantined and rebuilt by scanning `stage.json` files; an
  empty or stale index recovers entries from their sidecars; a corrupt or schema-incompatible bundle is
  treated as a miss and regenerated.
- **Startup policy.** A launch with no cache leaves Stage 1 unloaded and writes no `s1`; the user can adjust
  parameters and click **Generate Stage 1**, which writes `s1`. A relaunch reports a cache hit and does not
  regenerate. Running Stage 2/3/4 writes their bundles; the next launch loads all four without auto-running a
  missing stage. Clicking a stage, export, or diagnostics action with a missing prerequisite reports the
  missing stage instead of generating it.
- **Manifest provenance.** A stage manifest reports `formatVersion: 4`, the stage `modelVersion`, and the
  settings/cache schema versions.
- **CLI cleanup.** A second identical CLI run prints cache hits for all requested stages. `--cache-list`
  reports status/size; `--cache-prune` previews by default, retains the current key chain, cascades from an
  upstream key to its descendants, and deletes only with `--cache-yes`.

Observed on a 1024×512 test: an explicit **Generate Stage 1** took 0.77 s; the relaunch loaded Stage 1 in
0.24 s; a later relaunch loaded cached Stages 1–4 in 0.26 s. On a cold launch with no cache, the view stayed
empty, the status explained that no cached Stage 1 existed, and no `s1` directory was written until Generate
was clicked. On a 256×128 CLI run, the second full pipeline printed cache hits for `s1`–`s4`.

## Commands

```powershell
# Build
dotnet build

# World map with the full numeric report
dotnet run -- --out output/world --check

# Regional tile with the full numeric report
dotnet run -- --out output/tropico --width 1024 --height 1024 --lat 35 5 --lon 120 155 --check
```

The legacy Python validators are unsupported and must not be used; see [no-python.md](no-python.md).

## What is not covered

- **No automated suite or CI.** The checks are scripted but run manually. There are no golden hashes and
  no tolerance-based regression runner ([DG-3](../requirements/diagnostics.md#dg-3--regression-suite)).
- **No image comparison.** Palette and hillshade changes are judged by eye.
- **No biome verification.** Stage 5 does not exist yet; erosion, climate, and hydrology have numeric
  summaries but not a golden acceptance suite.
- **No model/cache migration coverage.** `settingsSchemaVersion` is 1 and `cacheSchemaVersion` is 1; the
  compatibility-default path and old-bundle migration have not been exercised.
- **No world-aware cleanup test.** Named world files do not exist yet; pruning protects only the current
  settings key chain.
- **No geological-plausibility test of the plate sketch.** DG-4 checks presence, spread, and variety, not
  whether a boundary type or province kind is physically justified at its location.
- **No cross-platform float reproducibility.** Byte equality is only guaranteed on the same runtime and
  platform ([NFR-8](../requirements/pipeline.md#non-functional-requirements)).
- **No test of BigTIFF, tiled access, or streaming.** The writer refuses >4 GB instead of producing a
  broken file.
- **No non-finite check in the writer.** The validator checks for NaN/Inf after the fact; a future change
  that produces them would be caught only if the validator is run.
- **No verification that a GIS reader places the raster correctly.** The GeoKeys and tiepoint are asserted,
  but no GDAL/QGIS round-trip is part of the repeatable process.
