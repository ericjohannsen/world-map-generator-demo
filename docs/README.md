# WorldGen documentation

WorldGen is a procedural planet generator for tabletop game masters. It produces world-scale terrain
that later pipeline stages will erode, hydrate, and classify into biomes, with the option to render a
selected region at much higher detail while staying consistent with the world map.

The terrain is evaluated on the surface of a sphere and stored in meters above sea level.

## Status

| Area | Status |
| --- | --- |
| Stage 1: base topology (continents, ocean floor, mountain belts) | Implemented |
| Stage 2: erosion | Not started |
| Stage 3: hydrology (lakes, rivers) | Not started |
| Stage 4: climate and biomes | Not started |
| Canonical float32 elevation store (ADR-007) | Recommended, not implemented |
| Spherical globe preview | Not started |

## Documentation map

- **[Requirements](requirements/README.md)** — what the tool must do, with IDs and acceptance criteria.
- **[Design](design/README.md)** — how it works, with module notes and decision records.

## Quick start

```powershell
dotnet run -- --check                                # 2048x1024 world map + diagnostics
dotnet run -- --out tropico --width 1024 --height 1024 --lat 35 5 --lon 120 155 --check
```

Each run writes two files: `<out>-height.png` (16-bit grayscale, meters) and `<out>-preview.png`
(8-bit RGB "view from space" diagnostic).

## Reading paths

- **New to the project:** [pipeline](requirements/pipeline.md) → [base topology requirements](requirements/base-topology.md) → [architecture](design/architecture.md).
- **Before implementing erosion:** [data formats](requirements/data-formats.md), [ADR-007](design/decisions.md#adr-007-canonical-elevations-as-float32-with-16-bit-png-derived), [spherical sampling](design/spherical-sampling.md), and the zoom requirements ([ZM-*](requirements/pipeline.md#stage-5--regional-zoom)).
- **Tuning the look of a planet:** [terrain model](design/terrain-model.md) and [diagnostics](requirements/diagnostics.md).
- **Why something is the way it is:** [decision records](design/decisions.md).

## Conventions

- Elevations are in **meters relative to sea level** (sea level = 0), never normalized to 0..1.
- Positions inside the generator are **unit vectors**; degrees appear only at the CLI and file edges.
- "World scale" means a full equirectangular map of the planet at 2048x1024 or coarser.
- Requirements use area-prefixed IDs: `BT` base topology, `DF` data formats, `DG` diagnostics,
  `ER` erosion, `HY` hydrology, `BI` biomes, `ZM` zoom, `PX` presentation, `NFR` non-functional.
