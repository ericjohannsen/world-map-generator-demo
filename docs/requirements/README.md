# Requirements

This folder states **what** WorldGen must do and how completion is judged. Implementation details live
in [../design](../design/README.md); the reasons behind major choices live in
[decision records](../design/decisions.md).

## Documents

| Document | Scope | IDs |
| --- | --- | --- |
| [pipeline.md](pipeline.md) | Goals, stage-by-stage requirements, roadmap, non-goals, non-functional requirements | `ER`, `CL`, `HY`, `BI`, `ZM`, `PX`, `UI`, `NFR` |
| [base-topology.md](base-topology.md) | Stage 1: planet shape, oceans, mountains, hierarchy, determinism | `BT` |
| [erosion.md](erosion.md) | Stage 2: coupled fluvial/hillslope erosion, material tracking, immutability | `ER` |
| [climate.md](climate.md) | Stage 3: temperature, prevailing winds, orographic precipitation, rain shadows, runoff | `CL` |
| [hydrology.md](hydrology.md) | Stage 4: depression fill/breach, climate-driven river discharge, lakes, Strahler rivers | `HY` |
| [biomes.md](biomes.md) | Stage 5: climate/elevation/water biome classification, configurable sets, ecotones, palette | `BI` |
| [preview-3d.md](preview-3d.md) | Stage 7: interactive 3D terrain preview, GPU renderer, CPU fallback, camera, exaggeration | `P3D` |
| [settings.md](settings.md) | Local preferences and session state: storage, atomic writes, migration, window geometry | `ST` |
| [model-versions.md](model-versions.md) | Per-stage model identity, registry, released/dev status, compatibility | `MV` |
| [cache.md](cache.md) | Content-addressed stage cache, startup reuse, cleanup, model-aware retention | `CA` |
| [data-formats.md](data-formats.md) | Units, precision, stage-tagged artifacts, metadata | `DF` |
| [diagnostics.md](diagnostics.md) | Acceptance checks and observed results | `DG` |

## Requirement style

- Each requirement is testable from the CLI or from the saved artifacts. Subjective qualities such as
  "realistic" are translated into measurements (hypsometry, landmass count) so they can be reviewed.
- **Must** items are part of the current milestone. **Should** items are expected soon but may be
  deferred. **Planned** items are recorded now because they constrain today's design.
- Acceptance thresholds describe plausible ranges, not exact values: each seed produces a different
  planet and statistical targets vary.

## Traceability

| Requirement | Implemented in | Status |
| --- | --- | --- |
| [BT-1](base-topology.md#bt-1--spherical-domain) spherical domain | [terrain model](../design/terrain-model.md), [spherical sampling](../design/spherical-sampling.md) | Implemented |
| [BT-2](base-topology.md#bt-2--absolute-elevations) elevations in meters | [terrain model](../design/terrain-model.md) | Implemented |
| [BT-3](base-topology.md#bt-3--target-land-fraction) land fraction | [terrain model](../design/terrain-model.md#sea-level-and-land-fraction) | Implemented |
| [BT-4](base-topology.md#bt-4--earth-like-hypsometry) hypsometry | [terrain model](../design/terrain-model.md), [diagnostics](diagnostics.md) | Implemented |
| [BT-5](base-topology.md#bt-5--landmass-distribution) landmasses | [terrain model](../design/terrain-model.md), [diagnostics](diagnostics.md) | Implemented |
| [BT-6](base-topology.md#bt-6--ocean-bathymetry) ocean structure | [terrain model](../design/terrain-model.md#ocean), [terrain variety](../design/terrain-variety.md#boundary-fields) | Implemented |
| [BT-7](base-topology.md#bt-7--mountain-belts) mountain belts | [terrain model](../design/terrain-model.md#land), [terrain variety](../design/terrain-variety.md#anisotropy) | Implemented |
| [BT-8](base-topology.md#bt-8--hierarchical-resolution) hierarchy | [spherical sampling](../design/spherical-sampling.md#level-of-detail), [ADR-004](../design/decisions.md#adr-004--elevation-as-a-function-of-direction-and-ground-resolution) | Implemented (base topology only) |
| [BT-9](base-topology.md#bt-9--determinism) determinism | [architecture](../design/architecture.md#determinism) | Implemented |
| [BT-10](base-topology.md#bt-10--map-windows) map windows | [spherical sampling](../design/spherical-sampling.md#views-and-tiles) | Implemented |
| [BT-11](base-topology.md#bt-11--view-from-space-preview) preview | [output](../design/output.md#preview-rendering) | Implemented |
| [BT-12](base-topology.md#bt-12--performance) performance | [architecture](../design/architecture.md#performance) | Implemented |
| [BT-13](base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks) tectonic structure, provinces, landmarks | [terrain variety](../design/terrain-variety.md), [ADR-010](../design/decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation), [ADR-011](../design/decisions.md#adr-011--blended-provinces-and-sparse-landmarks-for-variety) | Implemented |
| [DF-1](data-formats.md#df-1--units-and-datum) units and datum | [terrain model](../design/terrain-model.md), [output](../design/output.md) | Implemented |
| [DF-2](data-formats.md#df-2--precision-and-canonical-store) canonical float32 GeoTIFF | [output](../design/output.md#geotiff), [ADR-007](../design/decisions.md#adr-007--float32-geotiff-as-the-canonical-elevation-store) | Implemented |
| [DF-3](data-formats.md#df-3--artifacts) artifacts | [output](../design/output.md) | Implemented |
| [DF-4](data-formats.md#df-4--metadata-manifest) metadata | [output](../design/output.md#manifest) | Implemented |
| [DG-1](diagnostics.md#dg-1--hierarchical-consistency) consistency | [verification](../design/verification.md#hierarchy) | Implemented |
| [DG-2](diagnostics.md#dg-2--terrain-statistics) statistics | [verification](../design/verification.md#statistics) | Implemented |
| [DG-3](diagnostics.md#dg-3--regression-suite) regression suite | [verification](../design/verification.md#what-is-not-covered) | Partial: scripts exist, no CI/golden suite |
| [DG-4](diagnostics.md#dg-4--variety-report) variety report | [verification](../design/verification.md#variety) | Implemented |
| [ER-1..ER-8](erosion.md) Stage 2 erosion | [erosion](../design/erosion.md), [ADR-013](../design/decisions.md#adr-013--stage-qualified-immutable-elevation-artifacts), [ADR-014](../design/decisions.md#adr-014--coupled-stream-power-sediment-transport-and-hillslope-diffusion) | Implemented |
| [CL-1..CL-5](climate.md) Stage 3 climate &amp; rainfall | [climate](../design/climate.md), [ADR-016](../design/decisions.md#adr-016--climate-and-orographic-rainfall-as-stage-3-before-hydrology) | Implemented |
| [HY-1..HY-6](hydrology.md) Stage 4 hydrology | [hydrology](../design/hydrology.md), [ADR-015](../design/decisions.md#adr-015--selective-sill-breaching-and-compactness-filtered-basin-lakes), [ADR-016](../design/decisions.md#adr-016--climate-and-orographic-rainfall-as-stage-3-before-hydrology) | Implemented |
| [BI-*](biomes.md) Stage 5 biomes | [biomes](../design/biomes.md), [ADR-021](../design/decisions.md#adr-021--configurable-biome-sets-with-reserved-water-ids), [ADR-022](../design/decisions.md#adr-022--deterministic-sphere-noise-ecotones) | Implemented |
| [ZM-*](pipeline.md#stage-6--regional-zoom) Stage 6 zoom | [BT-8](base-topology.md#bt-8--hierarchical-resolution), [ER-5](erosion.md#er-5--regional-execution-with-overlap-halo) | Partial |
| [PX-1](pipeline.md#stage-7--presentation-and-export) numeric export | [output](../design/output.md#geotiff) | Implemented |
| [PX-2](pipeline.md#stage-7--presentation-and-export) preview | [output](../design/output.md#preview-rendering) | Implemented |
| [PX-3](pipeline.md#stage-7--presentation-and-export) metadata | [output](../design/output.md#manifest) | Implemented |
| [PX-6](preview-3d.md) 3D terrain preview | [preview-3d](../design/preview-3d.md), [ADR-017](../design/decisions.md#adr-017--in-window-opengl-3d-preview-with-a-cpu-raster-fallback) | Implemented (regional + globe) |
| [MV-1..MV-12](model-versions.md) model versioning and released/dev registry | [model versions](../design/model-versions.md), [ADR-019](../design/decisions.md#adr-019---per-stage-model-versions-with-a-releaseddev-registry), `models.json` | Implemented (all current models `dev`) |
| [CA-1..CA-19](cache.md) content-addressed stage cache and cleanup | [artifact cache](../design/artifact-cache.md), [ADR-020](../design/decisions.md#adr-020---content-addressed-stage-cache-with-a-merkle-key-chain) | Implemented (named worlds deferred) |
| [UI-1..UI-9](pipeline.md#stage-8--interactive-ui-avalonia) interactive Avalonia UI | `src/WorldGen.App` | Implemented |
| [ST-1..ST-15](settings.md) local preferences and session | [settings](../design/settings.md), [ADR-018](../design/decisions.md#adr-018--split-versioned-atomic-json-settings) | Implemented |

## Keeping this folder honest

Requirements and design are updated in the same change as the code. If a requirement cannot be met,
it is renegotiated here rather than silently dropped; the corresponding decision record should link
back to the requirement.
