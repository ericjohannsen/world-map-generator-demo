# Pipeline requirements

## Goals

WorldGen supports tabletop game masters who need a coherent planet they can explore at two scales:

1. **A world map** — continents, oceans, and mountain belts legible as a "view from space", suitable
   for very large campaign maps and for sci-fi settings where the planet is seen from orbit.
2. **A regional map** — a selected area rendered at much finer detail, consistent with the world map,
   for storytelling at the local scale.

Realism is judged by structure, not by texture: where land is, how it is shaped, how water would flow,
and whether the resulting statistics resemble a real planet. Biomes, lakes, and rivers come from later
stages that consume the terrain rather than from cosmetic noise.

The pipeline is spherical from the start because a planet-scale map must not have pole distortion or a
seam where longitude wraps.

## Stages

| Stage | Produces | Status |
| --- | --- | --- |
| 0. Configuration | Seed, planet radius, target land fraction, view window, resolution | Implemented |
| 1. [Base topology](#stage-1--base-topology) | Elevation field in meters, continuous over the sphere | Implemented |
| 2. [Erosion](#stage-2--erosion) | Eroded elevations, incision/deposition fields | Planned |
| 3. [Hydrology](#stage-3--hydrology) | Flow directions, accumulation, lakes, rivers | Planned |
| 4. [Climate and biomes](#stage-4--climate-and-biomes) | Temperature, moisture, biome grid | Planned |
| 5. [Regional zoom](#stage-5--regional-zoom) | Consistent high-detail region renders | Partial |
| 6. [Presentation and export](#stage-6--presentation-and-export) | Heightmaps, previews, metadata, tabletop assets | Partial |

## Stage 1 — Base topology

Requirements are in [base-topology.md](base-topology.md). In short: a sphere, plausible continents and
ocean floor, mountain belts, real units, deterministic seeds, and resolution-independent evaluation so
that stage 5 can zoom in later.

The field is a function of direction on the unit sphere plus a target ground resolution, not a fixed
bitmap. See [ADR-002](../design/decisions.md#adr-002-sample-noise-in-3d-on-the-unit-sphere) and
[ADR-004](../design/decisions.md#adr-004-elevation-as-a-function-of-direction-and-ground-resolution).

## Stage 2 — Erosion

| ID | Requirement |
| --- | --- |
| ER-1 | Erosion **must** run on float32 elevations in meters. Elevations must not be quantized between iterations or between stages. See [DF-2](data-formats.md#df-2--precision-and-canonical-store). |
| ER-2 | Erosion **must** combine a hillslope process (diffusion) with a fluvial process (stream-power incision, `E = K A^m S^n`), with spatially varying erodibility and uplift so that some regions are old and rounded while others are young and rugged. |
| ER-3 | The model **must** track material: incision, transport, and deposition, so that basins, alluvial fans, and deltas can appear rather than only valleys being carved. |
| ER-4 | Sea level stays fixed at 0 m. Erosion **may** modify coastlines through deposition but **must not** change the datum. |
| ER-5 | Erosion **must** be executable for a region with overlap (halo) cells so that tiles agree with each other and with the world-scale result. See [ZM-3](#stage-5--regional-zoom). |
| ER-6 | Erosion **must** be deterministic given the same inputs, including tie-breaking in flow routing. |
| ER-7 | Erosion **must** record enough intermediate data (erosion depth, sediment thickness) for later stages to vary soils, biomes, and rendering. |
| ER-8 | A 2048x1024 world **should** erode in under a minute on a desktop CPU; a 4096x4096 region **should** complete in minutes, with a quality setting trading iterations for time. |

**Known risk.** Drainage divides are inherited from the initial field. If the base topology is left as
isotropic noise, erosion converges to a self-consistent but generic "eroded noise" pattern. This is why
stage 1 has belts and domain-warped continents rather than plain fBm; it is also why erosion parameters
should vary by region (ER-2).

## Stage 3 — Hydrology

| ID | Requirement |
| --- | --- |
| HY-1 | Hydrology **must** treat depressions explicitly: fill or breach them (priority-flood or equivalent) before routing, and preserve the filled surface for lake detection. |
| HY-2 | Flow routing **must** use a documented method (D8 or D∞) with deterministic tie-breaking, and **must** handle flats and the map's longitude wrap. |
| HY-3 | Lakes **must** be derived as filled basins with a surface level and a spill point, not as painted shapes, so that lake extents follow the terrain. |
| HY-4 | Rivers **must** be derived from flow accumulation above a configurable threshold, with an estimated width/order suitable for drawing at both world and regional scale. |
| HY-5 | Outputs **must** include flow direction, accumulation, a water mask, lake identifiers, and river polylines usable by stage 4 and stage 6. |
| HY-6 | Hydrology **must** consume the eroded float terrain and honour the fixed sea level. |

## Stage 4 — Climate and biomes

| ID | Requirement |
| --- | --- |
| BI-1 | Temperature **must** depend on latitude and elevation (and, later, optionally on continentality and ocean currents), producing plausible polar, temperate, and tropical bands. |
| BI-2 | Moisture **must** depend on prevailing winds, orographic lift, and distance from the sea, so that rain shadows and wet coasts emerge from terrain rather than from noise. |
| BI-3 | Biome classification **must** be a deterministic lookup over temperature, moisture, elevation, and water state, driven by a configurable table rather than hard-coded colours. |
| BI-4 | Outputs **must** be separate fields (temperature, moisture, biome id) plus a palette, so that the same planet can be rendered in different visual styles. |
| BI-5 | Lakes and rivers **must** influence local moisture, and coastlines **must** influence temperature where the implementation supports it. |
| BI-6 | Biome boundaries **should** be relatively smooth at world scale while remaining correct at regional scale. |

## Stage 5 — Regional zoom

| ID | Requirement |
| --- | --- |
| ZM-1 | Any latitude/longitude window **must** be selectable from the CLI (implemented with `--lat` / `--lon`). |
| ZM-2 | A zoomed render **must** evaluate the same continuous field at finer ground resolution, adding detail without changing the world-scale shape. Verified for the base topology in [DG-1](diagnostics.md#dg-1--hierarchical-consistency). |
| ZM-3 | Eroded and hydrological detail **must** remain consistent between the world map and a zoomed region. This is not yet solved; expected approaches are hierarchical constraints from the world-scale solution, cached world-scale fields sampled at regional scale, or halo-based tiled erosion. |
| ZM-4 | Adjacent regions **must** match at their shared edge (same function, plus halos for iterative stages). |
| ZM-5 | High-detail regions **must** not require holding the whole planet at that detail in memory; tiling/streaming is expected. |

## Stage 6 — Presentation and export

| ID | Requirement |
| --- | --- |
| PX-1 | Every run **must** write a heightmap artifact that preserves elevations well enough for later numeric stages (see [DF-2](data-formats.md#df-2--precision-and-canonical-store)). Implemented as 16-bit PNG today. |
| PX-2 | Every run **must** write a human-viewable preview for quick review. Implemented as an 8-bit RGB equirectangular image. |
| PX-3 | Artifacts **must** be accompanied by metadata (seed, radius, window, cell size, datum, range, stage). Not started; see [DF-4](data-formats.md#df-4--metadata-manifest). |
| PX-4 | Tabletop export **should** include preview tiles, river/lake overlays, and scale/coordinate references suitable for printing. |
| PX-5 | A globe (orthographic) preview **should** be offered for space views; equirectangular distortion near the poles is currently accepted and must be documented. |

## Non-functional requirements

| ID | Requirement |
| --- | --- |
| NFR-1 | **Determinism.** The same seed and options produce byte-identical artifacts on the same runtime and platform. No wall-clock, ambient randomness, or order-dependent parallelism may affect output. Verified for the current generator. |
| NFR-2 | **Dependency discipline.** Only the .NET base class library; no image libraries, no licence-encumbered packages. See [ADR-006](../design/decisions.md#adr-006-write-png-directly-instead-of-using-an-image-library). |
| NFR-3 | **Cross-platform.** Windows, Linux, and macOS through `dotnet run`. |
| NFR-4 | **Units.** Meters and sea level 0 everywhere inside the pipeline; radians for spherical math; degrees only at the CLI and in file metadata. |
| NFR-5 | **Performance.** A 2048x1024 world map in a few seconds (currently ~5–7 s in a Debug build), with regional tiles proportionally faster. |
| NFR-6 | **Memory.** A 2048x1024 world render should stay well under 1 GB; current peak is tens of megabytes. |
| NFR-7 | **Composability.** Later stages consume and produce explicit fields rather than being folded into the generator, so each can be validated and swapped independently. |
| NFR-8 | **Precision honesty.** Bit-identical results are guaranteed for a given runtime; cross-platform reproducibility is best-effort because floating-point math may differ. |

## Non-goals for now

- Simulating plate tectonics causally. Belts are produced from a warped noise field, not from plate
  motion; the current design does not claim geological causality.
- Importing real-Earth DEM data, or matching a specific real region.
- Atmosphere, clouds, lighting, or orbital rendering. The preview is diagnostic only.
- Full GIS interoperability (GeoTIFF, projections, georeferencing). Planned as a later data format.
- Multi-planet systems, moons, and orbital mechanics.
- Place naming, political boundaries, and cultural features.
