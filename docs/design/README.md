# Design

This folder explains **how** WorldGen works and why it is built this way. Requirements and acceptance
thresholds are in [../requirements](../requirements/README.md); the decision records behind the larger
choices are in [decisions.md](decisions.md).

## Reading order

| If you want to… | Read |
| --- | --- |
| Understand the whole program | [architecture.md](architecture.md) |
| Understand zoom and tiling | [spherical-sampling.md](spherical-sampling.md) |
| Change the look of a planet | [noise.md](noise.md), then [terrain-model.md](terrain-model.md), then [terrain-variety.md](terrain-variety.md) |
| Understand plates, provinces, and landmarks | [terrain-variety.md](terrain-variety.md) |
| Understand Stage 2 erosion and sediment transport | [erosion.md](erosion.md) |
| Understand Stage 3 climate and orographic rainfall | [climate.md](climate.md) |
| Understand Stage 4 hydrology (lakes, D8 routing, Strahler rivers) | [hydrology.md](hydrology.md) |
| Understand the 3D terrain preview and its fallback | [preview-3d.md](preview-3d.md) |
| Understand preferences, session state, and window restore | [settings.md](settings.md) |
| Understand stage reuse, cache keys, and cleanup | [artifact-cache.md](artifact-cache.md) |
| Understand model versions and released/dev status | [model-versions.md](model-versions.md) |
| Know the tooling policy (no Python) | [no-python.md](no-python.md) |
| Read or write stage-tagged artifacts | [output.md](output.md) |
| Judge whether a change is correct | [verification.md](verification.md) |
| Know why a choice was made | [decisions.md](decisions.md) |

## Module map

| Source file | Responsibility |
| --- | --- |
| [`src/WorldGen.Cli/Program.cs`](../../src/WorldGen.Cli/Program.cs) | CLI orchestration for Stages 1–4, cache-aware full runs, cache list/prune, exit codes |
| [`src/WorldGen.Cli/Options.cs`](../../src/WorldGen.Cli/Options.cs) | CLI argument parsing for terrain stages and cache commands |
| [`src/WorldGen.App/MainWindow.axaml.cs`](../../src/WorldGen.App/MainWindow.axaml.cs) | Avalonia desktop UI for generation, TIFF loading, Stages 2–4, cache reuse, and interactive inspection |
| [`src/WorldGen.Core/PlanetTerrain.cs`](../../src/WorldGen.Core/PlanetTerrain.cs) | Terrain settings and the composite Stage 1 elevation function |
| [`src/WorldGen.Core/TerrainErosion.cs`](../../src/WorldGen.Core/TerrainErosion.cs) | Stage 2 coupled stream-power incision, sediment transport/deposition, Priority-Flood routing, and hillslope diffusion |
| [`src/WorldGen.Core/TerrainClimate.cs`](../../src/WorldGen.Core/TerrainClimate.cs) | Stage 3 temperature, prevailing winds, orographic precipitation lift, rain shadows, and surface runoff |
| [`src/WorldGen.Core/TerrainHydrology.cs`](../../src/WorldGen.Core/TerrainHydrology.cs) | Stage 4 Priority-Flood depression fill/breach, climate-gated lakes, convergent D8 routing, twig pruning, and Strahler river polylines |
| [`src/WorldGen.Core/ModelRegistry.cs`](../../src/WorldGen.Core/ModelRegistry.cs) | Current per-stage model versions, registry loading/validation, released/dev status |
| [`src/WorldGen.Core/models.json`](../../src/WorldGen.Core/models.json) | Checked-in model registry (embedded in Core) |
| [`src/WorldGen.Core/StageKeys.cs`](../../src/WorldGen.Core/StageKeys.cs) | Canonical stage settings and SHA-256 cache keys |
| [`src/WorldGen.Core/StageBundle.cs`](../../src/WorldGen.Core/StageBundle.cs) | Self-describing compressed `.wgc` stage bundle |
| [`src/WorldGen.Core/StageCache.cs`](../../src/WorldGen.Core/StageCache.cs) | Cache directories, sidecars, rebuildable index, list/prune |
| [`src/WorldGen.Core/TerrainStageCodec.cs`](../../src/WorldGen.Core/TerrainStageCodec.cs) | Stage 1–4 result ↔ bundle conversions |
| [`src/WorldGen.Core/TectonicSketch.cs`](../../src/WorldGen.Core/TectonicSketch.cs) | Kinematic plate mosaic, boundary masks, Euler poles, hotspots |
| [`src/WorldGen.Core/ProvinceField.cs`](../../src/WorldGen.Core/ProvinceField.cs) | Blended geomorphic-province parameters (including `Erodibility`) |
| [`src/WorldGen.Core/LandmarkField.cs`](../../src/WorldGen.Core/LandmarkField.cs) | Sparse hotspot tracks, arc volcanoes, massifs, craters, seamounts |
| [`src/WorldGen.Core/PerlinNoise.cs`](../../src/WorldGen.Core/PerlinNoise.cs) | Seeded 3D Perlin noise, fBm, ridged multifractal |
| [`src/WorldGen.Core/Heightmap.cs`](../../src/WorldGen.Core/Heightmap.cs) | `MapView` window, grid storage, area-weighted statistics, renderer |
| [`src/WorldGen.Core/TerrainPreview.cs`](../../src/WorldGen.Core/TerrainPreview.cs) | Diagnostic colour preview with hillshading and Stage 2 erosion/deposition overlay; unshaded albedo for 3D |
| [`src/WorldGen.Core/TerrainMesh.cs`](../../src/WorldGen.Core/TerrainMesh.cs) | Bilinear window mesh (positions, normals, UVs, indices) for the regional 3D preview |
| [`src/WorldGen.Core/TerrainGlobeMesh.cs`](../../src/WorldGen.Core/TerrainGlobeMesh.cs) | Displaced UV-sphere mesh for the globe preview |
| [`src/WorldGen.App/Preview3D/`](../../src/WorldGen.App/Preview3D) | 3D host, orbit/free-flight cameras, OpenGL renderer, software raster fallback, picking |
| [`src/WorldGen.App/Preview3D/PreviewCamera.cs`](../../src/WorldGen.App/Preview3D/PreviewCamera.cs) | `IPreviewCamera` contract and the free-flight camera |
| [`src/WorldGen.App/Settings/AppSettingsStore.cs`](../../src/WorldGen.App/Settings/AppSettingsStore.cs) | Preferences/session models, debounced saving, first-run defaults |
| [`src/WorldGen.App/Settings/SettingsFileStore.cs`](../../src/WorldGen.App/Settings/SettingsFileStore.cs) | Atomic JSON, validation, schema migration, legacy import, corruption quarantine |
| [`src/WorldGen.App/Settings/AppPaths.cs`](../../src/WorldGen.App/Settings/AppPaths.cs) | Per-user settings, cache, and log locations |
| [`src/WorldGen.Core/TerrainDiagnostics.cs`](../../src/WorldGen.Core/TerrainDiagnostics.cs) | Consistency, hypsometry, erosion/deposition, variety, and landmass reporting |
| [`src/WorldGen.Core/GeoTiffWriter.cs`](../../src/WorldGen.Core/GeoTiffWriter.cs) | Dependency-free float32 GeoTIFF encoder |
| [`src/WorldGen.Core/GeoTiffReader.cs`](../../src/WorldGen.Core/GeoTiffReader.cs) | Dependency-free read-only float32 GeoTIFF decoder |
| [`src/WorldGen.Core/StageArtifactPaths.cs`](../../src/WorldGen.Core/StageArtifactPaths.cs) | Stage-tagged naming convention (`.s1-base`, `.s2-eroded`) and source-immutability guard |
| [`src/WorldGen.Core/ArtifactExporter.cs`](../../src/WorldGen.Core/ArtifactExporter.cs) | Stage-tagged artifact bundle writer for Stages 1–4 |
| [`src/WorldGen.Core/PngWriter.cs`](../../src/WorldGen.Core/PngWriter.cs) | Dependency-free 8-bit RGB and 16-bit grayscale PNG encoder |
| [`src/WorldGen.Core/Manifest.cs`](../../src/WorldGen.Core/Manifest.cs) | Manifest records and JSON serialization/deserialization |
| [`src/WorldGen.Core/MathUtils.cs`](../../src/WorldGen.Core/MathUtils.cs) | Smoothstep, lerp, degrees/radians, Fibonacci sphere, `DeterministicRandom` |
| [`tools/make_globe.py`](../../tools/make_globe.py) | **Legacy, unsupported** HTML globe viewer; requires Python and must not be used for new work |
| [`tools/verify_png.py`](../../tools/verify_png.py) | **Legacy, unsupported** PNG decoder; see [no-python.md](no-python.md) |
| [`tools/verify_geotiff.py`](../../tools/verify_geotiff.py) | **Legacy, unsupported** GeoTIFF validator; see [no-python.md](no-python.md) |

The runtime has **no third-party dependencies**. Python is not used for any purpose in this project; the
files above predate that policy and are not part of the build or test path. See
[no-python.md](no-python.md) ([NFR-2](../requirements/pipeline.md#non-functional-requirements)).

## Data flow

```text
args ──▶ Options.Parse ──▶ TerrainSettings
                              │
                              ▼
        PlanetTerrain (plate sketch, provinces, landmarks,
                  land threshold from 32,768
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

The Stage 1 elevation function is the source of terrain truth. Stage 2 consumes its `Heightmap`, Stage 3
consumes the eroded map, and Stage 4 consumes the eroded map plus climate; each stage writes explicit
fields rather than folding work into the generator. The stage cache can resume those results, but it is not a
second source of truth: it stores the exact arrays under keys derived from the same model/settings
identities, and a cache hit is bit-identical to recomputation.

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

Stages 1–4 are implemented: base topology, coupled erosion, climate/rainfall, and hydrology, with stage-tagged
exports, a regional/globe 3D preview, persistent settings, and a content-addressed stage cache. Stage 5
(biomes) is planned; Stage 6 (regional zoom of eroded data) is partial and still tracked by `ZM-*`; see the
[pipeline](../requirements/pipeline.md#stages). The current generator is honest about what it does not
simulate: plate motion is a kinematic sketch, not a geodynamic model, and the erosion model is a coupled
stream-power/hillslope approximation rather than a full sedimentological simulation. Provinces, landmarks,
and boundary-parallel ridge texture supply variety within Stage 1
([terrain-variety.md](terrain-variety.md)); model identity and cache behavior are in
[model-versions.md](model-versions.md) and [artifact-cache.md](artifact-cache.md).

## Keeping the design honest

- If behaviour changes, the requirement and the design note change in the same commit.
- If a design note and the code disagree, the code is wrong until proven otherwise; these notes are
  written from the implementation, not from an earlier plan.
- Thresholds and observed values are recorded together, so a regression is visible as a number rather
  than a feeling.

