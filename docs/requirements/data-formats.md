# Data format requirements

## DF-1 — Units and datum

- Elevations are **meters relative to sea level**; sea level is exactly 0. Ocean depths are negative.
- The planet radius is stored with the world (default 6,371,000 m) because it converts degrees and
  pixels into ground distance.
- Horizontally, a sample grid is described by an equirectangular window in degrees
  (`north`, `south`, `west`, `east`) plus `width` and `height` in pixels. Longitudes are continuous, so a
  window crossing the antimeridian uses e.g. `170..190`, not `-170..170`.
- Internally the generator uses **unit vectors**, not degrees, and **radians** for trigonometry.

*Acceptance:* no pipeline artifact relies on a normalized 0..1 height or on a per-image min/max.
Verified in the manifest and by the independent validators.

## DF-2 — Precision and canonical store

The canonical elevation store **must** be a single-band **float32 GeoTIFF** in meters. 16-bit and 8-bit
PNGs are derived outputs for viewing and interchange.

| Encoding | Vertical step over a 15.5 km range | Verdict |
| --- | --- | --- |
| 8-bit grayscale | ~60 m | Rejected: visible terracing, unusable for flow routing |
| 16-bit grayscale (fixed range) | ~0.24 m | Acceptable for display and export; not a solver input |
| 24-bit packed RGB | ~0.001 m | Rejected: non-standard interpretation, no tooling support |
| float32 | ~1e-7 relative (sub-millimetre at 10 km) | **Canonical** |

Why 16-bit is not enough for stages 2–4:

- Stream-power incision and flow routing depend on slope. On a 1 km cell, a 1 m rise is a slope of
  0.001, so a ±0.12 m quantization error is a 12–24% slope error. On plains (slopes around 1e-4) the
  quantization error exceeds the real slope, and steepest-descent routing follows rounding noise,
  producing grid-aligned drainage.
- Iterative solvers accumulate bias if they round-trip through a fixed-precision format between
  iterations.
- A fixed range (−8,000..9,000 m) clips or loses resolution on sci-fi worlds with different relief. Float
  carries no baked range.

The canonical file is `<out>-height.tif`: classic TIFF, little-endian, float32, Deflate-compressed strips,
geographic degrees, a user-defined spherical CRS with the planet radius as semi-major axis, area
registration with a north-west corner tiepoint, and metres on a sea-level vertical datum. The 16-bit PNG
is retained because it is universally viewable and convenient for interchange, but it is never an input
to erosion or hydrology.

*Status:* implemented. See [output.md](../design/output.md#geotiff) and
[ADR-007](../design/decisions.md#adr-007--float32-geotiff-as-the-canonical-elevation-store).

**Land floor.** The land branch is currently floored at 0.1 m, because a deep rift or basin would
otherwise produce a negative elevation on a `c > 0` pixel and the elevation-sign coastline would disagree
with the continentalness coastline. This is a numeric convention, not a physical one; hydrology (stage 3)
is expected to replace it with filled or breached basins. Until then, closed basins may contain flat,
near-sea-level floors, and the 16-bit PNG window (−8,000..9,000 m) comfortably contains every observed
value (−7,067..8,157 m for the default seed).

## DF-3 — Artifacts and stage naming convention

Every stage run **must** write all four artifacts tagged with its pipeline stage identifier so that earlier
stage files are never overwritten in place ([ADR-013](../design/decisions.md#adr-013--stage-qualified-immutable-elevation-artifacts)):

| Stage | Stage token | Canonical float32 GeoTIFF | Derived 16-bit PNG | Preview PNG | Metadata Manifest |
| --- | --- | --- | --- | --- | --- |
| Stage 1 (Base topology) | `.s1-base` | `<out>.s1-base-height.tif` | `<out>.s1-base-height.png` | `<out>.s1-base-preview.png` | `<out>.s1-base-manifest.json` |
| Stage 2 (Erosion) | `.s2-eroded` | `<out>.s2-eroded-height.tif` | `<out>.s2-eroded-height.png` | `<out>.s2-eroded-preview.png` | `<out>.s2-eroded-manifest.json` |
| Stage 3 (Climate &amp; rainfall) | `.s3-climate` | `<out>.s3-climate-height.tif` (+ `-rainfall.tif`) | `<out>.s3-climate-height.png` | `<out>.s3-climate-preview.png` | `<out>.s3-climate-manifest.json` |
| Stage 4 (Hydrology) | `.s4-hydro` | `<out>.s4-hydro-height.tif` | `<out>.s4-hydro-height.png` | `<out>.s4-hydro-preview.png` | `<out>.s4-hydro-manifest.json` (+ `-vectors.json`) |
| Stage 5 (Biomes) | `.s5-biome` | `<out>.s5-biome-height.tif` (+ `-ids.tif`) | `<out>.s5-biome-height.png` | `<out>.s5-biome-preview.png` | `<out>.s5-biome-manifest.json` (+ `-ids.png`, `-palette.json`) |

**Source immutability.** When Stage 2 consumes a Stage 1 GeoTIFF (`--erode-from <path>.s1-base-height.tif` or
via the UI's "Load Stage 1 .tif..."), the source `.s1-base-height.tif` is opened strictly read-only and
`StageArtifactPaths.EnsureNotOverwritingSource` guarantees the output is written to `<out>.s2-eroded-height.tif`.

The 16-bit PNG mapping is fixed and shared by **all** tiles:
`sample = round((elevation + 8000) / 17000 * 65535)`, clamped. This makes overlapping renders directly
comparable. Current observed range for the default seed is −7,067..8,157 m, still inside the window;
a world that exceeds it would clip only the derived PNG, never the canonical GeoTIFF. The step over the
17 km window is about 0.26 m.

A run writes exactly these four exports. The in-app 3D preview provides an interactive globe from the active
stage; it is not written by the run and is not listed in the manifest. A stage cache bundle is **not** an
export: it lives under the per-user application data directory, holds fields that are intentionally not
published, and is managed by [cache.md](cache.md); it never changes the canonical artifacts. The legacy
[`tools/make_globe.py`](../../tools/make_globe.py) HTML viewer is unsupported
([no-python.md](../design/no-python.md)).

*Acceptance:*

- the GeoTIFF decodes as float32 with one band, finite samples, and the declared georeferencing;
- the PNG decodes independently, is 16-bit, uses filter 0, and its decoded extremes match the reported
  range within half a quantization step of the GeoTIFF;
- the preview is 8-bit RGB and its dimensions match the heightmap;
- the manifest's observed extremes match the raster.

The numeric diagnostics (`--check`) are the supported verification path. The old independent validators are
legacy and unsupported ([no-python.md](../design/no-python.md)).

*Limit:* classic TIFF cannot exceed 4 GB. BigTIFF and tiled organisation are deferred until high-resolution
global renders need them; the writer throws rather than producing a truncated file
([ADR-009](../design/decisions.md#adr-009--hand-written-classic-geotiff-defer-bigtiff-and-tiles)).

## DF-4 — Metadata manifest

A manifest **must** accompany every numeric artifact so a later stage can reconstruct units, scale, and
provenance without guessing, and so a fictional planet can record parameters that no EPSG code expresses.

| Field | Example | Notes |
| --- | --- | --- |
| `formatVersion` | `4` | Manifest schema version; version 4 adds the three model/cache identifiers below |
| `tool` | `WorldGen` | Producer |
| `stage` | `base-topology` | Pipeline stage that wrote the artifact |
| `seed` | `1337` | Planet seed |
| `planetRadiusMeters` | `6371000` | Also the GeoTIFF semi-major axis |
| `datum` | `sea-level` | Elevation zero |
| `view` | `{ "north": 35, "south": 5, "west": 120, "east": 155 }` | Pixel-centre grid bounds in degrees |
| `width`, `height` | `1024`, `1024` | Grid size |
| `metersPerPixelEquator` | `3800.6` | East-west size at the equator; multiply by `cos(latitude)` elsewhere |
| `metersPerPixelNorthSouth` | `3257.7` | Constant for the window |
| `pngEncodingRange` | `{ "min": -8000, "max": 9000 }` | Fixed 16-bit mapping |
| `encoding` | `height: geotiff-float32-le-deflate; png: grayscale-16` | Artifact encodings |
| `modelVersion` | `1` | Per-stage algorithm identity; see [model-versions.md](model-versions.md) |
| `settingsSchemaVersion` | `1` | Shape/meaning of the settings used to produce the stage |
| `cacheSchemaVersion` | `1` | Stage bundle layout that can be resumed for this result |
| `terrain` | `{ targetLandFraction, landThreshold, shelfDepthMeters, abyssDepthMeters, continentalReliefMeters, landBaseMeters, mountainReliefMeters, hillReliefMeters, detailReliefMeters, riftDepthMeters, riftShoulderMeters, trenchDepthMeters, arcReliefMeters }` | Generator settings needed to reproduce or tune the planet |
| `structure` | `{ model, plates, provinces, hotspots, landmarks }` | Structural-layer model name and counts. `model` states explicitly that plate motion is a sketch, not a geodynamic simulation |
| `files` | `{ heightGeoTiff, heightPng, previewPng }` | File **names**, not paths |
| `observed` | `{ landFraction, minElevation, maxElevation, meanElevation, method }` | Land fraction and mean are area-weighted; min/max are exact raster extremes |

The three version fields are provenance, not cache-key substitutes: the cache key also includes the resolved
stage settings and upstream key. `modelVersion` is the stage's model (`s1` base topology, `s2` erosion,
`s3` climate, `s4` hydrology), so the same manifest schema serves every stage. All current values are `1` and
the registry status is `dev`; see [cache.md](cache.md) and [model-versions.md](model-versions.md).

There is **no timestamp**. A manifest timestamp would make runs non-reproducible for no pipeline benefit.
The manifest is byte-identical for repeated runs with the same `--out` base; different bases change only
the `files` names.

*Status:* implemented.

## DF-5 — Naming and layout

- `--out <base>` names the output **without** extension; suffixes are appended:
  `<base>-height.tif`, `<base>-height.png`, `<base>-preview.png`, `<base>-manifest.json`.
- The default base is `world`, resolved to the current directory.
- Parent directories **are** created automatically.
- One run writes one window. Multiple windows use distinct bases, for example `output/world`,
  `output/tropico`, `output/tropico-2x`.
- Existing files are replaced without a prompt.

## Deferred formats

- **BigTIFF / tiled TIFF**: required for planet-wide high-resolution rasters and for efficient partial
  reads. Same tags, wider offsets. Tracked as the follow-up to ADR-009.
- **Projected or reprojected GeoTIFF**: the pipeline is geographic (degrees) only; arbitrary projections
  and resampling belong to a GIS tool, not the generator.
- **OpenEXR**: useful only for rendering-scale float data; not needed for a heightmap pipeline.
- **NumPy `.npy`**: considered and dropped as the canonical store because it is not self-describing and
  has no georeferencing. A future erosion prototype may still exchange `.npy` as a scratch format.
- **Heightmap image formats with embedded metadata**: avoided; the manifest plus GeoTIFF tags are easier
  to validate and diff.

Cross-reference: [pipeline stages](pipeline.md), [output design](../design/output.md),
[verification design](../design/verification.md).
