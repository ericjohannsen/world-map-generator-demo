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
| 1. [Base topology](#stage-1--base-topology) | Elevation field in meters, continuous over the sphere (`.s1-base-*`) | Implemented |
| 2. [Erosion](#stage-2--erosion) | Eroded elevations, incision/deposition/drainage fields (`.s2-eroded-*`) | Implemented |
| 3. [Climate and rainfall](#stage-3--climate-and-rainfall) | Temperature, prevailing winds, orographic precipitation, rain shadows, runoff (`.s3-climate-*`) | Implemented |
| 4. [Hydrology](#stage-4--hydrology) | Climate-driven river discharge, flow directions, lakes, Strahler rivers, polylines (`.s4-hydro-*`) | Implemented |
| 5. [Biomes](#stage-5--biomes) | Biome classification grid and palette from temperature, moisture, elevation, and water state (`.s5-biome-*`) | Implemented |
| 6. [Regional zoom](#stage-6--regional-zoom) | Consistent high-detail region renders | Partial |
| 7. [Presentation and export](#stage-7--presentation-and-export) | Heightmaps, previews, metadata, tabletop assets | Partial |
| 8. [Interactive UI](#stage-8--interactive-ui-avalonia) | Interactive map viewer with Stage 1–4 controls and exports (Avalonia) | Implemented |

## Stage 1 — Base topology

Requirements are in [base-topology.md](base-topology.md). In short: a sphere, plausible continents and
ocean floor, mountain belts, real units, deterministic seeds, and resolution-independent evaluation so
that stage 5 can zoom in later.

The field is a function of direction on the unit sphere plus a target ground resolution, not a fixed
bitmap. See [ADR-002](../design/decisions.md#adr-002--sample-noise-in-3d-on-the-unit-sphere) and
[ADR-004](../design/decisions.md#adr-004--elevation-as-a-function-of-direction-and-ground-resolution).

Structure comes from a **kinematic plate sketch**, not from an independent belt noise field: mountain
belts, trenches, mid-ocean ridges, rifts, and transforms are masks on one boundary topology, with boundary
types derived from relative plate motion. **Geomorphic provinces** blend relief parameters across the
surface, and a small set of **sparse landmarks** (hotspot tracks, arc volcanoes, massifs, craters,
seamounts) adds memorable features. This is a sketch, not a geodynamic simulation
([ADR-010](../design/decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation),
[ADR-011](../design/decisions.md#adr-011--blended-provinces-and-sparse-landmarks-for-variety),
[terrain variety](../design/terrain-variety.md), [BT-13](base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks)).

## Stage 2 — Erosion

| ID | Requirement |
| --- | --- |
| ER-1 | Erosion **must** run on float32 elevations in meters. Elevations must not be quantized between iterations or between stages. See [DF-2](data-formats.md#df-2--precision-and-canonical-store). |
| ER-2 | Erosion **must** combine a hillslope process (diffusion) with a fluvial process (stream-power incision, `E = K A^m S^n`), with spatially varying erodibility and uplift so that some regions are old and rounded while others are young and rugged. |
| ER-3 | The model **must** track material: incision, transport, and deposition, so that basins, alluvial fans, and deltas can appear rather than only valleys being carved. |
| ER-4 | Sea level stays fixed at 0 m. Erosion **may** modify coastlines through deposition but **must not** change the datum. |
| ER-5 | Erosion **must** be executable for a region with overlap (halo) cells so that tiles agree with each other and with the world-scale result. See [ZM-3](#stage-6--regional-zoom). |
| ER-6 | Erosion **must** be deterministic given the same inputs, including tie-breaking in flow routing. |
| ER-7 | Erosion **must** record enough intermediate data (erosion depth, sediment thickness) for later stages to vary soils, biomes, and rendering. |
| ER-8 | A 2048x1024 world **should** erode in under a minute on a desktop CPU; a 4096x4096 region **should** complete in minutes, with a quality setting trading iterations for time. |

**Known risk.** Drainage divides are inherited from the initial field. If the base topology is left as
isotropic noise, erosion converges to a self-consistent but generic "eroded noise" pattern. This is why
stage 1 has tectonic belts, blended provinces, and domain-warped continents rather than plain fBm; it is
also why erosion parameters should vary by region (ER-2). `ProvinceParameters.Erodibility` is the
intended hook for that variation.

## Stage 3 — Climate and rainfall

Detailed requirements are in [climate.md](climate.md).

| ID | Requirement |
| --- | --- |
| CL-1 | Temperature **must** depend on latitude and elevation (lapse rate), producing polar, temperate, subtropical, and tropical thermal bands. |
| CL-2 | Precipitation **must** depend on atmospheric circulation belts (ITCZ, horse-latitude highs, mid-latitude storm tracks, polar highs) and upwind ocean moisture advection. |
| CL-3 | Orographic lift **must** boost rainfall on windward mountain slopes and adiabatic descent **must** create dry rain shadows on leeward interiors. |
| CL-4 | Effective surface runoff ($P - E$) and potential evapotranspiration **must** be derived so arid zones produce minimal local runoff while wet zones feed Stage 4 rivers. |
| CL-5 | Stage 3 **must** preserve the Stage 2 source `.tif` unmodified and write `.s3-climate-*` artifacts (including `-rainfall.tif`). |

## Stage 4 — Hydrology

Detailed requirements are in [hydrology.md](hydrology.md).

| ID | Requirement |
| --- | --- |
| HY-1 | Hydrology **must** treat depressions explicitly: fill or breach them (Priority-Flood) before routing, and gate lake filling by Stage 3 evaporative water balance. |
| HY-2 | Flow routing **must** use a documented convergent D8 method with deterministic tie-breaking, and **must** handle flats and the map's longitude wrap. |
| HY-3 | Lakes **must** be derived as compact filled basins with a surface level and a spill point, not as painted shapes or 1-pixel ribbons. |
| HY-4 | Rivers **must** be derived from Stage 3 volumetric runoff discharge (`DischargeM3s`) above a configurable threshold, prune short Order-1 hillside twigs, and compute Strahler order and channel width. |
| HY-5 | Outputs **must** include flow direction, drainage area, volumetric discharge, water mask, lake identifiers, and river polylines usable by Stage 5 and Stage 7. |
| HY-6 | Hydrology **must** consume the eroded float terrain and Stage 3 climate fields without modifying earlier stage files, and honour the fixed sea level. |

## Stage 5 — Biomes

| ID | Requirement |
| --- | --- |
| BI-1 | Biome classification **must** be a deterministic lookup over Stage 3 temperature, precipitation/aridity, elevation, and Stage 4 water state, driven by a configurable table rather than hard-coded colours. |
| BI-2 | Outputs **must** be separate fields (biome id) plus a palette, so that the same planet can be rendered in different visual styles. |
| BI-3 | Stage 4 lakes and rivers **must** influence local riparian/wetland moisture, and coastlines **must** influence temperature moderation where supported. |
| BI-4 | Biome boundaries **should** be relatively smooth at world scale while remaining correct at regional scale. |
| BI-5 | Classification **must** be driven by a named, versioned biome set from an embedded `biomes.json`, selectable per world; rules **must** be bounds over a named index vector so new dimensions (for example seasonality) can be added without restructuring the table. |
| BI-6 | Biome boundaries **must** be modulated by a deterministic fractal-noise field sampled on the unit sphere, so ecotones are organic, seamless, and resolution-consistent. |

Detailed requirements, artifacts, and acceptance criteria are in [biomes.md](biomes.md).

## Stage 6 — Regional zoom

| ID | Requirement |
| --- | --- |
| ZM-1 | Any latitude/longitude window **must** be selectable from the CLI (implemented with `--lat` / `--lon`). |
| ZM-2 | A zoomed render **must** evaluate the same continuous field at finer ground resolution, adding detail without changing the world-scale shape. Verified for the base topology in [DG-1](diagnostics.md#dg-1--hierarchical-consistency). |
| ZM-3 | Eroded and hydrological detail **must** remain consistent between the world map and a zoomed region. This is not yet solved; expected approaches are hierarchical constraints from the world-scale solution, cached world-scale fields sampled at regional scale, or halo-based tiled erosion. |
| ZM-4 | Adjacent regions **must** match at their shared edge (same function, plus halos for iterative stages). |
| ZM-5 | High-detail regions **must** not require holding the whole planet at that detail in memory; tiling/streaming is expected. |

## Stage 7 — Presentation and export

| ID | Requirement |
| --- | --- |
| PX-1 | Every run **must** write a heightmap artifact that preserves elevations well enough for later numeric stages (see [DF-2](data-formats.md#df-2--precision-and-canonical-store)). Implemented as a float32 GeoTIFF, with a derived 16-bit PNG. |
| PX-2 | Every run **must** write a human-viewable preview for quick review. Implemented as an 8-bit RGB equirectangular image. |
| PX-3 | Artifacts **must** be accompanied by metadata (seed, radius, window, cell size, datum, range, stage, structural model counts). Implemented as a JSON manifest; see [DF-4](data-formats.md#df-4--metadata-manifest). |
| PX-4 | Tabletop export **should** include preview tiles, river/lake overlays, and scale/coordinate references suitable for printing. |
| PX-5 | A globe (orthographic) preview **should** be offered for space views; equirectangular distortion near the poles is currently accepted and must be documented. The UI includes an in-app displaced globe ([P3D-13](preview-3d.md#requirements)); the legacy Python HTML viewer is unsupported ([no-python.md](../design/no-python.md)). A cube-sphere with per-face tiling is future work. |
| PX-6 | The UI **must** include an interactive 3D terrain preview of the active stage with a GPU renderer and a CPU fallback, in regional and globe modes, plus the position HUD. Requirements are in [preview-3d.md](preview-3d.md) (`P3D-1`…`P3D-26`); the design and its limits are in [preview-3d](../design/preview-3d.md). |

## Stage 8 — Interactive UI (Avalonia)

| ID | Requirement |
| --- | --- |
| UI-1 | An Avalonia UI **must** provide an interactive map viewer (equirectangular/globe) with pan/zoom. |
| UI-2 | The UI **must** expose generation parameters (seed, plate count, resolution, view window, terrain tuning) and regenerate on demand. |
| UI-3 | The UI **must** display elevation/preview, allow exporting artifacts (GeoTIFF/PNG/manifest), and show diagnostics (`--check`). |
| UI-4 | Regional zoom in the UI **must** remain consistent with the world map (same field/function). |
| UI-5 | The UI **should** be cross-platform via Avalonia/.NET. |
| UI-6 | The map viewer **must** switch between the 2D map and the 3D terrain preview without re-running a stage, and **must** keep the inspected stage and terrain identical in both. See [P3D-9](preview-3d.md#requirements). |
| UI-7 | The UI **must** remember window geometry, 3D view mode, orbit/fly control mode, both orbit cameras, both free-flight cameras, renderer preference, grid visibility, and vertical exaggeration between runs. Storage, validation, and migration rules are in [settings.md](settings.md) (`ST-1`…`ST-15`). |
| UI-8 | The UI **must** load cached Stage 1 and any cached downstream stage results on launch. If Stage 1 is not cached, it **must not** generate it automatically; it leaves the view empty so the user can change parameters, then clicks **Generate Stage 1**. Missing downstream stages are not auto-run. It tries the cache before computing a stage and stores the result after computing. See [cache.md](cache.md) (`CA-8`, `CA-9`). |
| UI-9 | The UI and CLI **must** expose the cache's identity and cleanup state: stage model version/status, entry size, and explicit list/prune operations. Cache behavior must never alter canonical artifacts or determinism. See [model-versions.md](model-versions.md) and [cache.md](cache.md). |

## Non-functional requirements

| ID | Requirement |
| --- | --- |
| NFR-1 | **Determinism.** The same seed and options produce byte-identical artifacts on the same runtime and platform. No wall-clock, ambient randomness, or order-dependent parallelism may affect output. Verified for the current generator. |
| NFR-2 | **Dependency discipline.** No licence-encumbered dependencies. The runtime currently uses only the .NET base class library, including hand-written PNG and GeoTIFF writers. Any future dependency must be permissively licensed, actively maintained, and independently verifiable. See [ADR-006](../design/decisions.md#adr-006--write-png-directly-instead-of-using-an-image-library) and [ADR-009](../design/decisions.md#adr-009--hand-written-classic-geotiff-defer-bigtiff-and-tiles). |
| NFR-3 | **Cross-platform.** Windows, Linux, and macOS through `dotnet run`. |
| NFR-4 | **Units.** Meters and sea level 0 everywhere inside the pipeline; radians for spherical math; degrees only at the CLI and in file metadata. |
| NFR-5 | **Performance.** A 2048x1024 world map within ~20 s in a Debug build, including artifact writing (currently ~15 s), with regional tiles proportionally faster (a 1024² tile is ~12 s because it adds detail octaves). Structural layers are the dominant cost; per-row plate/province caching and a landmark spatial index are the documented next optimisations. |
| NFR-6 | **Memory.** A 2048x1024 world render should stay well under 1 GB; current peak is tens of megabytes. |
| NFR-7 | **Composability.** Later stages consume and produce explicit fields rather than being folded into the generator, so each can be validated and swapped independently. |
| NFR-8 | **Precision honesty.** Bit-identical results are guaranteed for a given runtime; cross-platform reproducibility is best-effort because floating-point math may differ. |
| NFR-9 | **No Python.** The project **must not** use Python for any purpose: not at runtime, in the build, in tests, or as a development tool. Use PowerShell or .NET. See [no-python.md](../design/no-python.md). |
| NFR-10 | **Resumability.** Stage results **must** be reusable from a content-addressed cache keyed by model version, settings schema, cache schema, upstream key, and stage settings. The cache **must not** change canonical output, and cleanup **must** be explicit and model-status-aware. See [cache.md](cache.md) and [model-versions.md](model-versions.md). |

## Non-goals for now

- Simulating plate tectonics causally. Stage 1 uses a kinematic plate **sketch**: prescribed Euler poles
  and a boundary classification supply coherent structure, but there is no mantle flow, crustal budget, or
  isostasy, and continents are not generated from plate history. The design claims structural coherence,
  not geological history ([ADR-010](../design/decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation)).
- Importing real-Earth DEM data, or matching a specific real region.
- Atmosphere, clouds, or physically based orbital rendering. The preview and the optional HTML globe are
  diagnostic; their lighting is cosmetic.
- Projected CRSs, reprojection, and GIS analysis. Output is geographic GeoTIFF; arbitrary projections
  belong to a downstream GIS tool. BigTIFF (>4 GB) and tiled/streaming output are deferred, not non-goals.
- Multi-planet systems, moons, and orbital mechanics.
- Place naming, political boundaries, and cultural features.
