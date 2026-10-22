# Data format requirements

## DF-1 — Units and datum

- Elevations are **meters relative to sea level**; sea level is exactly 0. Ocean depths are negative.
- The planet radius is stored with the world (default 6,371,000 m) because it converts degrees and
  pixels into ground distance.
- Horizontally, a sample grid is described by an equirectangular window in degrees
  (`north`, `south`, `west`, `east`) plus `width` and `height` in pixels.
- Internally the generator uses **unit vectors**, not degrees, and **radians** for trigonometry.

*Acceptance:* no pipeline artifact relies on a normalized 0..1 height or on a per-image min/max.

## DF-2 — Precision and canonical store

The canonical elevation store **must** be `float32` meters. 16-bit and 8-bit artifacts are derived
outputs for viewing and interchange.

| Encoding | Vertical step over a 15.5 km range | Verdict |
| --- | --- | --- |
| 8-bit grayscale | ~60 m | Rejected: visible terracing, unusable for flow routing |
| 16-bit grayscale (fixed range) | ~0.24 m | Acceptable for display and export; still risky as a solver input |
| 24-bit packed RGB | ~0.001 m | Rejected: non-standard interpretation, no tooling support |
| float32 | ~1e-7 relative (sub-millimetre at 10 km) | **Canonical** |

Why 16-bit is not enough for stages 2–4:

- Stream-power incision and flow routing depend on slope. On a 1 km cell, a 1 m rise is a slope of
  0.001, so a ±0.12 m quantization error is a 12–24% slope error. On plains (slopes around 1e-4) the
  quantization error exceeds the real slope, and steepest-descent routing follows rounding noise,
  producing grid-aligned drainage.
- Iterative solvers accumulate bias if they round-trip through a fixed-precision format between
  iterations.
- A fixed range (−6,500..9,000 m today) clips or loses resolution on sci-fi worlds with different
  relief. Float carries no baked range.

PNG cannot store 32-bit samples, so the canonical file is a sidecar, not a PNG. See
[ADR-007](../design/decisions.md#adr-007-canonical-elevations-as-float32-with-16-bit-png-derived).

*Status:* recommended, not implemented. Current output is 16-bit PNG only.

## DF-3 — Artifacts

Every run **must** write:

| Artifact | Format | Purpose |
| --- | --- | --- |
| `<out>-height.png` | 16-bit grayscale, PNG colour type 0, filter 0, big-endian samples | Numeric heightmap for downstream tools and quick inspection |
| `<out>-preview.png` | 8-bit RGB, PNG colour type 2 | Diagnostic "view from space" colour image |

Planned additions:

| Artifact | Format | Purpose |
| --- | --- | --- |
| `<out>-height.npy` | NumPy `.npy`, float32, little-endian, C order, shape `(height, width)` | Canonical elevation store; directly consumable by erosion prototypes |
| `<out>-manifest.json` | JSON | Metadata, see [DF-4](#df-4--metadata-manifest) |
| `<out>-erosion.npy` etc. | float32 arrays | Later stages' intermediate fields |

The 16-bit PNG mapping is fixed and shared by **all** tiles:
`sample = round((elevation + 6500) / 15500 * 65535)`, clamped. This makes overlapping renders directly
comparable. Current observed range for the default seed is −6,118..6,257 m, well inside the window.

*Acceptance:* the heightmap decodes with an independent decoder, its bit depth is 16, and decoded
extremes match the reported range. Verified by [`tools/verify_png.py`](../../tools/verify_png.py).

## DF-4 — Metadata manifest

A manifest **must** accompany every numeric artifact so a later stage can reconstruct units, scale, and
provenance without guessing. Required fields:

| Field | Example |
| --- | --- |
| `formatVersion` | `1` |
| `tool`, `toolVersion` | `WorldGen`, build/commit identifier |
| `stage` | `base-topology` |
| `seed` | `1337` |
| `planetRadiusMeters` | `6371000` |
| `datum` | `sea-level` |
| `view` | `{ "north": 35, "south": 5, "west": 120, "east": 155 }` |
| `width`, `height` | `1024`, `1024` |
| `metersPerPixelEquator` | `3808.1` |
| `metersPerPixelFormula` | `equator * cos(latitude)`; north-south is constant |
| `elevationRange` | `{ "min": -6118, "max": 6257 }` |
| `encoding` | `npy-float32` or `png-u16` with `{ "min": -6500, "max": 9000 }` |
| `stageParameters` | generator settings, erosion iterations, thresholds, etc. |
| `generatedAt` | ISO 8601 timestamp (informational only; never affects determinism) |

*Status:* not started.

## DF-5 — Naming and layout

- `--out <base>` names the output **without** extension; suffixes are appended:
  `<base>-height.png`, `<base>-preview.png`.
- The default base is `world`, written to the current directory.
- One run writes one window. Multiple windows use distinct bases, for example `world`, `tropico`,
  `tropico-2x`.
- Output directories **should** be created automatically (not yet implemented).

## Deferred formats

- **GeoTIFF** (float32): desirable when GIS tools or real-Earth comparisons enter the pipeline, because
  it carries georeferencing and bands natively.
- **OpenEXR**: useful only for rendering-scale float data; not needed for a heightmap pipeline.
- **Heightmap image formats with embedded metadata**: explicitly avoided; a sidecar manifest is easier
  to validate and diff.

Cross-reference: [pipeline stages](pipeline.md), [output design](../design/output.md),
[verification](../design/verification.md).
