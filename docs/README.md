# WorldGen documentation

WorldGen is a procedural planet generator for tabletop game masters. It produces world-scale terrain
that later pipeline stages will erode, hydrate, and classify into biomes, with the option to render a
selected region at much higher detail while staying consistent with the world map.

The terrain is evaluated on the surface of a sphere and stored in meters above sea level.

## Status

| Area | Status |
| --- | --- |
| Stage 1: base topology (continents, ocean floor, mountain belts) | Implemented |
| Stage 1 structure: kinematic plate sketch, provinces, landmarks, anisotropic ridges | Implemented |
| Stage 2: erosion (coupled stream power, sediment, hillslope) | Implemented |
| Stage 3: climate and orographic rainfall | Implemented |
| Stage 4: hydrology (climate-driven discharge, lakes, rivers) | Implemented |
| Stage 5: biomes | Planned |
| Canonical float32 GeoTIFF elevation store (ADR-007) | Implemented |
| Metadata manifest | Implemented |
| In-app 3D terrain preview (OpenGL with CPU fallback; regional + globe, 1×/10×/50×) | Implemented |
| Local preferences, window geometry, and session restore | Implemented |
| Model version registry (per stage; `dev`/`released`; all current models `dev`) | Implemented |
| Stage cache (content-addressed, full bundles, startup reuse, explicit cleanup) | Implemented |
| Interactive 3D globe viewer (HTML) | Legacy, unsupported (Python; see [no-python](design/no-python.md)) |
| Automated regression suite / CI | Not started |

## Documentation map

- **[Requirements](requirements/README.md)** — what the tool must do, with IDs and acceptance criteria.
- **[Design](design/README.md)** — how it works, with module notes and decision records.

## Quick start

```powershell
# World map plus the numeric report, written to output/world.*
dotnet run -- --out output/world --check

# 30x35 degree regional tile at 1024x1024
dotnet run -- --out output/tropico --width 1024 --height 1024 --lat 35 5 --lon 120 155 --check

# Inspect the per-user stage cache, then preview a cleanup
dotnet run --project src/WorldGen.Cli -- --cache-list
dotnet run --project src/WorldGen.Cli -- --cache-prune --cache-stage s2
```

The desktop UI loads cached Stage 1 and any cached downstream stages on launch. If no cached Stage 1 exists,
it does **not** generate one automatically: the view stays empty so parameters can be changed before clicking
**Generate Stage 1**. Missing downstream stages are not auto-run either. See [cache design](design/artifact-cache.md)
and [model versions](design/model-versions.md).

The desktop UI (`dotnet run --project src/WorldGen.App`) adds an in-window **3D** view over the active stage:
switch between a **regional** terrain view and a **globe**, choose **orbit** or **free flight** (W/A/S/D to
fly, drag to look, wheel for speed), pick **1× / 10× / 50×** exaggeration, and choose GPU or the CPU fallback.
See [3D preview design](design/preview-3d.md).

Each run writes four files sharing the base:

| File | Contents |
| --- | --- |
| `<base>-height.tif` | **Canonical** float32 GeoTIFF, metres, geographic CRS |
| `<base>-height.png` | Derived 16-bit grayscale PNG (fixed −8,000..9,000 m mapping) |
| `<base>-preview.png` | 8-bit RGB diagnostic "view from space" |
| `<base>-manifest.json` | Seed, radius, window, cell sizes, parameters, observed statistics |

The default base is `world` in the current directory. Parent directories are created automatically.
See [data formats](requirements/data-formats.md) and [output design](design/output.md).

## Reading paths

- **New to the project:** [pipeline](requirements/pipeline.md) → [base topology requirements](requirements/base-topology.md) → [architecture](design/architecture.md).
- **Before implementing erosion:** [data formats](requirements/data-formats.md), [ADR-007](design/decisions.md#adr-007--float32-geotiff-as-the-canonical-elevation-store), [spherical sampling](design/spherical-sampling.md), and the zoom requirements ([ZM-*](requirements/pipeline.md#stage-6--regional-zoom)).
- **Tuning the look of a planet:** [terrain model](design/terrain-model.md), [terrain variety](design/terrain-variety.md), and [diagnostics](requirements/diagnostics.md).
- **Understanding plates, provinces, and landmarks:** [terrain variety](design/terrain-variety.md) and [ADR-010](design/decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation)/[ADR-011](design/decisions.md#adr-011--blended-provinces-and-sparse-landmarks-for-variety).
- **Checking a change:** [verification](design/verification.md).
- **Understanding cache reuse, model status, or cleanup:** [artifact cache](design/artifact-cache.md), [model versions](design/model-versions.md), [ADR-019](design/decisions.md#adr-019---per-stage-model-versions-with-a-releaseddev-registry), [ADR-020](design/decisions.md#adr-020---content-addressed-stage-cache-with-a-merkle-key-chain).
- **Why something is the way it is:** [decision records](design/decisions.md).

## Conventions

- Elevations are in **meters relative to sea level** (sea level = 0), never normalized to 0..1.
- Positions inside the generator are **unit vectors**; degrees appear only at the CLI and file edges.
- "World scale" means a full equirectangular map of the planet at 2048x1024 or coarser.
- Longitudes in a window are **continuous**: use `170 190` to cross the antimeridian, not `-170 170`.
- Requirements use area-prefixed IDs: `BT` base topology, `DF` data formats, `DG` diagnostics,
  `ER` erosion, `HY` hydrology, `BI` biomes, `ZM` zoom, `PX` presentation, `NFR` non-functional,
  `MV` model versions, `CA` stage cache.
