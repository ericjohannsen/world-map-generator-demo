# Verification

Correctness is checked with numbers and with decoders that do not share code with the writers. "It looks
fine" is not an acceptance test: a change to an octave count or a threshold can look harmless and still
break hypsometry, coastlines, or zoom consistency.

Two layers exist:

1. **In-process diagnostics** (`--check`) measure the terrain model: cross-resolution agreement,
   hypsometry, and landmass structure.
2. **Independent artifact validation** (`tools/*.py`) decodes what was actually written and checks it
   against the manifest and against the other artifact.

## Hierarchy

Requirement: [DG-1](../requirements/diagnostics.md#dg-1--hierarchical-consistency).

**Method.** Draw 20,000 area-uniform directions from a Fibonacci sphere. Evaluate the terrain at the
render's `metersPerPixel` and again at `metersPerPixel / 64`. Report:

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

**Hypsometry.** Elevations are binned at 50 m over the fixed range −6,500 .. 9,000 m. Rows are weighted by
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

## Artifact validation

### PNG

[`tools/verify_png.py`](../../tools/verify_png.py) uses only Python's standard library (`struct`, `zlib`):

- verifies the 8-byte signature and every chunk CRC;
- requires 16-bit, colour type 0, non-interlaced for the heightmap;
- inflates IDAT and checks the exact expected byte count;
- requires filter byte 0 on every row;
- unpacks big-endian samples and reports the observed elevation range;
- optionally checks the RGB preview's dimensions, bit depth, and row filters.

This catches the classic handwritten-PNG failures: wrong CRC, wrong endianness, missing filter byte,
wrong row stride, and a truncated stream.

### GeoTIFF and manifest

[`tools/verify_geotiff.py`](../../tools/verify_geotiff.py) uses `tifffile` and `numpy` (development
dependencies only):

- dtype is float32, one band, all samples finite;
- compression is the expected Deflate variant;
- `ModelPixelScale` and `ModelTiepoint` exist and imply the expected first pixel centre;
- `GTRasterTypeGeoKey == 1` (area), `GeographicTypeGeoKey == 32767` (user-defined), and the semi-major
  axis equals the planet radius;
- the western edge matches the manifest's `view.west`;
- if a PNG is supplied, every elevation is cross-checked against the decoded 16-bit PNG, allowing half a
  quantisation step (0.12 m); observed maximum is **0.119 m**;
- if a manifest is supplied, the raster extremes are within 1 m of `observed.minElevation` /
  `observed.maxElevation`, and the seed/stage/land fraction are printed for review.

Running both validators on the world and on a cropped tile is a stronger check than either alone: it
exercises full-planet sampling, a non-square degree window, and a different pixel scale.

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

## Commands

```powershell
# Build
dotnet build

# World map with the full numeric report
dotnet run -- --out output/world --check

# Regional tile with the full numeric report
dotnet run -- --out output/tropico --width 1024 --height 1024 --lat 35 5 --lon 120 155 --check

# Independent validation
python tools/verify_png.py output/world-height.png --preview output/world-preview.png
python tools/verify_geotiff.py output/world-height.tif --png output/world-height.png --manifest output/world-manifest.json

# The same for the tile
python tools/verify_png.py output/tropico-height.png --preview output/tropico-preview.png
python tools/verify_geotiff.py output/tropico-height.tif --png output/tropico-height.png --manifest output/tropico-manifest.json
```

The Python packages are not required to build or run WorldGen:

```powershell
python -m pip install tifffile numpy
```

## What is not covered

- **No automated suite or CI.** The checks are scripted but run manually. There are no golden hashes and
  no tolerance-based regression runner ([DG-3](../requirements/diagnostics.md#dg-3--regression-suite)).
- **No image comparison.** Palette and hillshade changes are judged by eye.
- **No erosion/hydrology/biome verification.** Those stages do not exist yet.
- **No cross-platform float reproducibility.** Byte equality is only guaranteed on the same runtime and
  platform ([NFR-8](../requirements/pipeline.md#non-functional-requirements)).
- **No test of BigTIFF, tiled access, or streaming.** The writer refuses >4 GB instead of producing a
  broken file.
- **No non-finite check in the writer.** The validator checks for NaN/Inf after the fact; a future change
  that produces them would be caught only if the validator is run.
- **No verification that a GIS reader places the raster correctly.** The GeoKeys and tiepoint are asserted,
  but no GDAL/QGIS round-trip is part of the repeatable process.
