# Design

This folder explains **how** WorldGen works and why it is built this way. Requirements and acceptance
thresholds are in [../requirements](../requirements/README.md); the decision records behind the larger
choices are in [decisions.md](decisions.md).

## Reading order

| If you want to… | Read |
| --- | --- |
| Understand the whole program | [architecture.md](architecture.md) |
| Understand zoom and tiling | [spherical-sampling.md](spherical-sampling.md) |
| Change the look of a planet | [noise.md](noise.md), then [terrain-model.md](terrain-model.md) |
| Read or write artifacts | [output.md](output.md) |
| Judge whether a change is correct | [verification.md](verification.md) |
| Know why a choice was made | [decisions.md](decisions.md) |

## Module map

| Source file | Responsibility |
| --- | --- |
| [`Program.cs`](../../Program.cs) | CLI parsing, run orchestration, artifact writing, exit codes |
| [`PlanetTerrain.cs`](../../PlanetTerrain.cs) | Terrain settings and the composite elevation function |
| [`PerlinNoise.cs`](../../PerlinNoise.cs) | Seeded 3D Perlin noise, fBm, ridged multifractal |
| [`Heightmap.cs`](../../Heightmap.cs) | `MapView` window, grid storage, area-weighted statistics, renderer |
| [`TerrainPreview.cs`](../../TerrainPreview.cs) | Diagnostic colour preview with hillshading |
| [`TerrainDiagnostics.cs`](../../TerrainDiagnostics.cs) | Consistency, hypsometry, and landmass reporting |
| [`GeoTiffWriter.cs`](../../GeoTiffWriter.cs) | Dependency-free float32 GeoTIFF encoder |
| [`PngWriter.cs`](../../PngWriter.cs) | Dependency-free 8-bit RGB and 16-bit grayscale PNG encoder |
| [`Manifest.cs`](../../Manifest.cs) | Manifest records and JSON serialization |
| [`MathUtils.cs`](../../MathUtils.cs) | Smoothstep, lerp, degrees/radians, Fibonacci sphere |
| [`tools/verify_png.py`](../../tools/verify_png.py) | Independent PNG decoder (verification only) |
| [`tools/verify_geotiff.py`](../../tools/verify_geotiff.py) | Independent GeoTIFF and manifest validator (verification only) |

The runtime has **no third-party dependencies**. The Python tools require `tifffile` and `numpy`, but
they are development-only and are not needed to build or run WorldGen ([NFR-2](../requirements/pipeline.md#non-functional-requirements)).

## Data flow

```text
args ──▶ Options.Parse ──▶ TerrainSettings
                              │
                              ▼
                       PlanetTerrain
                 (land threshold from 32,768
                  Fibonacci-sphere samples)
                              │
        elevation(direction, metersPerPixel)
                              │
                              ▼
                    HeightmapRenderer
                 (row-parallel equirectangular grid)
                              │
                              ▼
                         Heightmap
          ┌───────────────┬───────────────┬───────────────┐
          ▼               ▼               ▼               ▼
   GeoTiffWriter     PngWriter      TerrainPreview   TerrainDiagnostics
   -height.tif      -height.png       -preview.png      (console)
          │               │               │
          └───────────────┴───────────────┴──▶ ManifestWriter ──▶ -manifest.json
```

The elevation function is the only source of terrain truth. Every artifact is derived from the same
`Heightmap` in one run; nothing reads a previous raster back in.

## Design principles

1. **Sphere first.** Terrain is a function of direction, not of image pixels
   ([ADR-002](decisions.md#adr-002--sample-noise-in-3d-on-the-unit-sphere)). The equirectangular grid is a
   view, not the model.
2. **Real units.** Elevations are meters above sea level and the planet radius is explicit
   ([BT-2](../requirements/base-topology.md#bt-2--absolute-elevations)). No normalized heights.
3. **Pure, deterministic evaluation.** The same seed, direction, and resolution always give the same
   elevation. Rows are rendered in parallel, but no result depends on scheduling
   ([NFR-1](../requirements/pipeline.md#non-functional-requirements)).
4. **LOD by prefix.** Zooming adds higher-frequency octaves without changing lower ones
   ([ADR-004](decisions.md#adr-004--elevation-as-a-function-of-direction-and-ground-resolution)).
5. **Numeric acceptance.** Structure is measured (hypsometry, landmass components, cross-resolution
   deviation), not only eyeballed ([ADR-008](decisions.md#adr-008--numeric-diagnostics-as-acceptance-gates)).
6. **Formats we can verify.** Artifacts are standard (GeoTIFF, PNG, JSON) and each encoder is checked by
   an independently written decoder ([output.md](output.md), [verification.md](verification.md)).

## What is implemented

Stage 1 (base topology) only. Erosion, hydrology, climate, biomes, and regional zoom of *eroded* data are
designed but not built; see the [pipeline](../requirements/pipeline.md#stages). The current generator is
good at planet-scale shape and honest about what it does not simulate: there is no plate-tectonic
causality, and drainage is inherited from noise rather than produced by erosion.

## Keeping the design honest

- If behaviour changes, the requirement and the design note change in the same commit.
- If a design note and the code disagree, the code is wrong until proven otherwise; these notes are
  written from the implementation, not from an earlier plan.
- Thresholds and observed values are recorded together, so a regression is visible as a number rather
  than a feeling.
