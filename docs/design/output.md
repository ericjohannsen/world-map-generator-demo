# Output

One stage writes four files sharing an output base and a stage identifier token
(`.s1-base` for Stage 1 base topology, `.s2-eroded` for Stage 2 erosion; see
[ADR-013](decisions.md#adr-013--stage-qualified-immutable-elevation-artifacts)):

| Artifact | Format | Role |
| --- | --- | --- |
| `<base>.<stage>-height.tif` | GeoTIFF, float32, Deflate | **Canonical** numeric elevations |
| `<base>.<stage>-height.png` | PNG, 16-bit grayscale | Derived, fixed-range interchange and quick inspection |
| `<base>.<stage>-preview.png` | PNG, 8-bit RGB | Diagnostic view-from-space image |
| `<base>.<stage>-manifest.json` | JSON | Seed, radius, window, units, stage provenance, parameters, file names |

Only the GeoTIFF is canonical, and a source GeoTIFF is never modified in place by later stages. The PNGs can
be regenerated from the GeoTIFF (or from the seed) and are not inputs to later pipeline stages. Directories
in `--out` are created automatically.

## GeoTIFF

[`GeoTiffWriter`](../../src/WorldGen.Core/GeoTiffWriter.cs) writes a **classic TIFF** (magic 42, not BigTIFF), little-endian,
single band, one IEEE-754 float32 sample per pixel, in metres relative to sea level.

### Storage layout

- Header (8 bytes), then the compressed strips, then one IFD, then the external tag values.
- `RowsPerStrip` is chosen so an uncompressed strip is about 64 KiB (`65536 / (width · 4)` rows, at least
  one). A 2048-pixel-wide image uses 8 rows per strip.
- Each strip is compressed independently with zlib (`Compression = 8`, Adobe Deflate).
- `SampleFormat = 3` (IEEE floating point), `BitsPerSample = 32`,
  `PhotometricInterpretation = 1` (BlackIsZero), `SamplesPerPixel = 1`, `PlanarConfiguration = 1`.
- No predictor is applied. Floating-point predictors exist but add byte-plane shuffling for a modest
  compression gain; they are not needed yet.
- Tag values longer than four bytes live after the IFD, aligned to eight bytes.
- There is no nodata value. Every pixel is a valid elevation; NaN and infinity are never written.

### Georeferencing

| Tag / key | Value |
| --- | --- |
| `ModelPixelScaleTag` (33550) | `(Δλ / width, Δφ / height, 0)` degrees |
| `ModelTiepointTag` (33922) | `(0, 0, 0, west, north, 0)` |
| `GTModelTypeGeoKey` (1024) | `2` — geographic |
| `GTRasterTypeGeoKey` (1025) | `1` — `PixelIsArea` |
| `GeographicTypeGeoKey` (2048) | `32767` — user-defined |
| `GeogCitationGeoKey` (2049) | `WorldGen spherical planet` |
| `GeogAngularUnitsGeoKey` (2054) | `9102` — degree |
| `GeogSemiMajorAxisGeoKey` (2057) | planet radius, metres |
| `GeogInvFlatteningGeoKey` (2059) | `0` — sphere |
| `VerticalUnitsGeoKey` (4099) | `9001` — metre |

The tiepoint is the **outer north-west corner**, because the raster is area-registered. The sample in
pixel `(0, 0)` is at the centre `(west + Δλ/(2·width), north − Δφ/(2·height))`, which is exactly where the
renderer evaluated it. This is the same convention DEM readers expect and it sidesteps the
`PixelIsPoint` half-pixel ambiguity ([ADR-007](decisions.md#adr-007--float32-geotiff-as-the-canonical-elevation-store)).

`GeographicTypeGeoKey = 32767` is deliberate: EPSG:4326 describes Earth's ellipsoid, and this tool can
generate a planet of any radius. The radius is written as the semi-major axis and the flattening is zero,
so the CRS describes a sphere. A fictional-world generator cannot rely on an Earth datum code.

### Limits

- Classic TIFF offsets are 32-bit. The writer throws `NotSupportedException` if the finished file would
  exceed 4 GB. A planet-wide 1 km float32 raster is already ~3.3 GB before compression, so BigTIFF is on
  the critical path for high-resolution global renders ([ADR-009](decisions.md#adr-009--hand-written-classic-geotiff-defer-bigtiff-and-tiles)).
- Strips are not tiles. A GIS reader can window a strip-based raster, but random access is coarser than a
  tiled TIFF. Tiling is expected together with BigTIFF.
- There are no overviews/pyramids; viewers that need them will build them.
- Only a single band and a geographic CRS are supported. No projected CRS, no vertical datum code, no
  colour interpretation.

## 16-bit PNG

`<base>-height.png` is a PNG colour-type 0 image with 16-bit big-endian samples, filter 0 on every row,
and zlib compression. It is a **derived** artifact with a fixed mapping shared by every window and tile:

```text
sample = round((elevation + 8000) / 17000 · 65535)     clamped to [0, 65535]
```

The fixed range (**−8,000 .. 9,000 m**) makes overlapping renders directly comparable and prevents a tile
from silently choosing its own contrast. The cost is a quantisation step of about 0.26 m and clipping
outside the window. With the soft mountain ceiling (`MountainCeilingMeters`) and the fixed PRNG, no seed
clips in normal operation; the window is kept generous for safety, and the validators read
`heightmapMinMeters`/`heightmapMaxMeters` from the manifest and tolerate clipped-but-expected samples.

Do not feed this file to erosion or flow routing: the quantisation is larger than the slopes those
solvers care about ([DF-2](../requirements/data-formats.md#df-2--precision-and-canonical-store)). Use the
GeoTIFF.

## Preview rendering

`<base>-preview.png` is an 8-bit RGB diagnostic, not a biome map and not a final illustration. It exists
so a person can judge structure (continents, shelves, belts, poles) at a glance.

- **Ocean.** Depth is `clamp(−height / 5400, 0, 1)`. Colour blends shelf `(52,121,145)` toward shallow
  `(26,79,124)` over the first 12% of depth, then toward deep `(7,24,58)` with a `depth^0.55` curve.
- **Land.** Bands: beach `(199,186,146)` below 20 m; lowland `(79,105,58)` below 250 m; highland
  `(88,94,52)` below 1,100 m; rock `(116,108,92)` below 2,200 m; bare rock `(150,142,128)` above that,
  smoothly interpolated.
- **Snow and ice.** A snow line at `5200 · cos(latitude)^1.25` metres, fading in over 600 m, covers high
  ground; a polar term reaches 85% snow at ±80° and is modulated by sea ice over the ocean. This is
  latitude/elevation tinting, not a climate simulation.
- **Hillshade.** A central difference in metres (using the local east-west and north-south pixel sizes,
  with a 200 m floor) builds a normal; the light is fixed at normalised `(-0.55, -0.55, 0.85)`, i.e.
  north-west and fairly high. Land is shaded strongly (`0.45 + 0.75 · lambert`); ocean is nearly flat
  (`0.93 + 0.07 · lambert`).
- The palette is shared across tiles, so a mosaic of previews has consistent colours.

Biomes, rivers, lakes, cloud, and a physical atmosphere are deliberately absent from the flat preview
([PX-5](../requirements/pipeline.md#stage-7--presentation-and-export)). The in-app 3D preview provides a
displaced globe; see [preview-3d.md](preview-3d.md). The run itself still writes only the four artifacts above.

## 3D globe preview

The supported globe view is the in-app 3D preview ([preview-3d.md](preview-3d.md)), which displaces the
heightmap and renders in the UI. The old `tools/make_globe.py` HTML viewer is **legacy and unsupported**: it
requires Python, which this project does not use ([no-python.md](no-python.md)); it is not part of a run and
is not listed in the manifest.


## Manifest

The manifest exists because a GeoTIFF can describe *where* and *in what units* but not *which seed*,
*which generator parameters*, or *that the datum is sea level*. It is intentionally small and has no
timestamp, so the whole artifact set is deterministic.

Example (abridged; values are rounded here for readability, while the file stores full round-trip precision):

```json
{
  "formatVersion": 4,
  "tool": "WorldGen",
  "stage": "base-topology",
  "seed": 1337,
  "planetRadiusMeters": 6371000,
  "datum": "sea-level",
  "view": { "north": 90, "south": -90, "west": -180, "east": 180 },
  "width": 2048,
  "height": 1024,
  "metersPerPixelEquator": 19545.983,
  "metersPerPixelNorthSouth": 19545.983,
  "pngEncodingRange": { "min": -8000, "max": 9000 },
  "encoding": "height: geotiff-float32-le-deflate; png: grayscale-16",
  "modelVersion": 1,
  "settingsSchemaVersion": 1,
  "cacheSchemaVersion": 1,
  "terrain": {
    "targetLandFraction": 0.29,
    "landThreshold": 0.54793,
    "shelfDepthMeters": 170,
    "abyssDepthMeters": 5900,
    "continentalReliefMeters": 6000,
    "landBaseMeters": 375,
    "mountainReliefMeters": 11000,
    "hillReliefMeters": 320,
    "detailReliefMeters": 70,
    "riftDepthMeters": 850,
    "riftShoulderMeters": 450,
    "trenchDepthMeters": 1800,
    "arcReliefMeters": 2200
  },
  "structure": {
    "model": "kinematic-plate-sketch + blended-provinces + sparse-landmarks",
    "plates": 13,
    "provinces": 9,
    "hotspots": 5,
    "landmarks": 60
  },
  "files": {
    "heightGeoTiff": "world-height.tif",
    "heightPng": "world-height.png",
    "previewPng": "world-preview.png"
  },
  "observed": {
    "landFraction": 0.298,
    "minElevation": -7211,
    "maxElevation": 7247,
    "meanElevation": -2237,
    "method": "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."
  }
}
```

Field notes:

- `metersPerPixelEquator` is the east-west size at the equator
  (`R · radians(Δλ) / width`); `metersPerPixelNorthSouth` is constant for the window
  (`R · radians(Δφ) / height`). East-west size at latitude φ is the equator value times `cos φ`.
- `observed.landFraction` and `observed.meanElevation` are area-weighted by `cos(latitude)`;
  `minElevation`/`maxElevation` are exact raster extremes over the window, not area-weighted estimates.
- `files` contains **file names**, not full paths, so the manifest is portable. This also means two runs
  with different `--out` bases differ in these fields and nothing else.
- `landThreshold` is the continentalness quantile that produced the coastline; it lets a later stage
  reproduce classification without re-deriving it. Landmarks can raise a seamount above 0 m, so the
  observed land fraction can be slightly above the target even though the coastline itself is fixed.
- `structure.model` is free text but deliberately says **sketch**, not simulation. Consumers (including
  later pipeline stages) must not read plate counts as a physical reconstruction; the numeric fields are
  counts only, not plate polygons or Euler poles.
- `modelVersion` identifies the algorithm for the stage that wrote the artifact; `settingsSchemaVersion`
  and `cacheSchemaVersion` identify the settings and resumable bundle layouts. These are provenance, not a
  replacement for the cache key, which also hashes the resolved settings and upstream key. All current
  values are `1` and the registry status is `dev`. See [model-versions.md](model-versions.md).
- No `generatedAt` field: timestamps would break byte-for-byte determinism for no pipeline benefit.

## Cache

Exports are deliverables, not a resume format. The desktop app and CLI keep full stage results in a
content-addressed cache under the per-user application data directory, with `stage.json` and a compressed
`data.wgc` per entry. Reopening the app loads the cached Stage 1 and any cached downstream stages; if there
is no cached Stage 1, it does not generate one automatically and instead waits for the user to adjust
parameters and click **Generate Stage 1**. Missing downstream stages are not run automatically.
`--cache-list` reports entries and `--cache-prune` removes unreferenced development variants after an
explicit confirmation. Cache behavior never changes the exported numeric artifacts. See
[artifact-cache.md](artifact-cache.md) and [requirements/cache.md](../requirements/cache.md).

## Naming and layout

- `--out <base>` takes a path without extension; the four suffixes are appended.
- The default base is `world`, resolved to the current directory. Parent directories are created.
- One invocation writes one window. Use distinct bases for multiple windows, e.g. `output/world`,
  `output/tropico`, `output/tropico-2x`.
- There is no implicit overwrite protection; an existing file with the same name is replaced.

## Reading the artifacts

| Tool | Notes |
| --- | --- |
| QGIS / GDAL / rasterio | Read the GeoTIFF with correct georeferencing; verify pixel centres before mosaicking |
| Pillow | Can read float32 TIFF, but check its georeferencing support; many viewers ignore GeoKeys |
| Image viewers | 16-bit grayscale PNG is often down-converted to 8-bit for display; that is expected |

The supported verification path is the numeric diagnostics (`--check`). The old Python validators are
**legacy and unsupported** ([no-python.md](no-python.md)); see [verification.md](verification.md).
