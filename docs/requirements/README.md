# Requirements

This folder states **what** WorldGen must do and how completion is judged. Implementation details live
in [../design](../design/README.md); the reasons behind major choices live in
[decision records](../design/decisions.md).

## Documents

| Document | Scope | IDs |
| --- | --- | --- |
| [pipeline.md](pipeline.md) | Goals, stage-by-stage requirements, roadmap, non-goals, non-functional requirements | `ER`, `HY`, `BI`, `ZM`, `PX`, `NFR` |
| [base-topology.md](base-topology.md) | Stage 1: planet shape, oceans, mountains, hierarchy, determinism | `BT` |
| [data-formats.md](data-formats.md) | Units, precision, artifacts, metadata | `DF` |
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
| [BT-6](base-topology.md#bt-6--ocean-bathymetry) ocean structure | [terrain model](../design/terrain-model.md#ocean) | Implemented |
| [BT-7](base-topology.md#bt-7--mountain-belts) mountain belts | [terrain model](../design/terrain-model.md#land) | Implemented |
| [BT-8](base-topology.md#bt-8--hierarchical-resolution) hierarchy | [spherical sampling](../design/spherical-sampling.md#level-of-detail), [ADR-004](../design/decisions.md#adr-004-elevation-as-a-function-of-direction-and-ground-resolution) | Implemented |
| [BT-9](base-topology.md#bt-9--determinism) determinism | [architecture](../design/architecture.md#determinism) | Implemented |
| [BT-10](base-topology.md#bt-10--map-windows) map windows | [spherical sampling](../design/spherical-sampling.md#views-and-tiles) | Implemented |
| [BT-11](base-topology.md#bt-11--view-from-space-preview) preview | [output](../design/output.md#preview-rendering) | Implemented |
| [BT-12](base-topology.md#bt-12--performance) performance | [architecture](../design/architecture.md#performance) | Implemented |
| [DF-1](data-formats.md#df-1--units-and-datum) units and datum | [terrain model](../design/terrain-model.md) | Implemented |
| [DF-2](data-formats.md#df-2--precision-and-canonical-store) canonical float32 | [ADR-007](../design/decisions.md#adr-007-canonical-elevations-as-float32-with-16-bit-png-derived) | Recommended |
| [DF-3](data-formats.md#df-3--artifacts) artifacts | [output](../design/output.md) | 16-bit only |
| [DF-4](data-formats.md#df-4--metadata-manifest) metadata | — | Not started |
| [DG-1](diagnostics.md#dg-1--hierarchical-consistency) consistency | [verification](../design/verification.md#hierarchy) | Implemented |
| [DG-2](diagnostics.md#dg-2--terrain-statistics) statistics | [diagnostics](../design/verification.md#statistics) | Implemented |
| [DG-3](diagnostics.md#dg-3--regression-suite) regression suite | — | Not started |
| [ER-*](pipeline.md#stage-2--erosion) erosion | — | Planned |
| [HY-*](pipeline.md#stage-3--hydrology) hydrology | — | Planned |
| [BI-*](pipeline.md#stage-4--climate-and-biomes) biomes | — | Planned |
| [ZM-*](pipeline.md#stage-5--regional-zoom) zoom | partially via [BT-8](base-topology.md#bt-8--hierarchical-resolution) | Partial |

## Keeping this folder honest

Requirements and design are updated in the same change as the code. If a requirement cannot be met,
it is renegotiated here rather than silently dropped; the corresponding decision record should link
back to the requirement.
