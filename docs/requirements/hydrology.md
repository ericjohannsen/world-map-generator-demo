# Stage 4 requirements — hydrology

Stage 4 consumes a settled Stage 2 eroded `Heightmap` and Stage 3 `ClimateResult` (`RunoffMmPerYear`,
`PrecipitationMmPerYear`, `PotentialEvaporationMmPerYear`) and derives a hydrologically conditioned elevation
surface, deterministic flow directions, volumetric river discharge, basin lakes, Strahler-ordered river
networks, and vector river polylines (`<prefix>.s4-hydro-*`).

Implementation: [`TerrainHydrology.cs`](../../src/WorldGen.Core/TerrainHydrology.cs),
[`TerrainClimate.cs`](../../src/WorldGen.Core/TerrainClimate.cs),
[`ArtifactExporter.cs`](../../src/WorldGen.Core/ArtifactExporter.cs),
[`TerrainPreview.cs`](../../src/WorldGen.Core/TerrainPreview.cs).
Design: [hydrology](../design/hydrology.md),
[ADR-015](../design/decisions.md#adr-015--selective-sill-breaching-and-compactness-filtered-basin-lakes),
[ADR-016](../design/decisions.md#adr-016--climate-and-orographic-rainfall-as-stage-3-before-hydrology).

## HY-1 — Explicit depression handling (fill, breach, and evaporative water balance)

Hydrology **must** treat closed depressions explicitly using Priority-Flood (Barnes et al., 2014) before
routing flow, and preserve the filled surface for lake detection while breaching minor pits, narrow noise
rills, and arid depressions where potential evaporation exceeds precipitation ($P / \text{PET} < 0.25$).

*Acceptance:*
- No subaerial cell ($h > 0$) is left as an unresolved interior sink (`ReceiverIndex[i] >= 0` for all non-border
  land cells).
- Small pits (`< MinLakeCells` or `< MinLakeDepthMeters`), non-compact rills (`compactness < MinLakeCompactness`),
  and arid desert depressions are breached along their spill paths (`BreachedDepressionCount > 0`).

## HY-2 — Convergent deterministic D8 flow routing with flat handling and longitude wrap

Flow routing **must** use a documented method (convergent D8 routing on the conditioned surface, routed along
the Priority-Flood shortest path across flats and lakes) with deterministic clockwise tie-breaking (`0..7`),
and **must** wrap seamlessly across the antimeridian ($\pm 180^\circ$) on global maps.

*Acceptance:*
- Repeated runs of `--hydro-from` produce byte-identical `.s4-hydro-height.tif`, `.s4-hydro-preview.png`,
  `.s4-hydro-manifest.json`, and `.s4-hydro-vectors.json` artifacts.
- Parallel streams on gentle plains converge into trunk channels rather than running 1 pixel apart.

## HY-3 — Topographically and climatically derived lakes with surface levels and spill points

Lakes **must** be derived as filled topographic basins gated by regional climate aridity, with a uniform water
surface elevation (`SurfaceElevationMeters`), basin depth (`MaxDepthMeters`, `MeanDepthMeters`), area
(`AreaSqKm`), and explicit spill point (`SpillPixelX`, `SpillPixelY`, `SpillLatitude`, `SpillLongitude`).

*Acceptance:*
- Every cell with `LakeId[i] == k` has `HydrologicalHeightmap[i] == lake.SurfaceElevationMeters` and
  `Source[i] < lake.SurfaceElevationMeters`.
- Maximum lake depth is bounded by partial sill breaching (`MaxLakeDepthMeters`, default `65 m`) and attenuated
  in arid and polar-permafrost zones; narrow 1-pixel ribbons are rejected via compactness filtering
  (`MinLakeCompactness`, default `0.22`).
- Observed on seed 1337 (`2048x1024`): `104` lake basins (`2,260,833 km²` total area), with `1,463` minor/arid
  depressions breached.

## HY-4 — Climate-driven river discharge, headwater twig pruning, and Strahler ordering

Rivers **must** be derived from accumulated volumetric runoff discharge (`DischargeM3s`, in $\text{m}^3/\text{s}$,
computed from Stage 3 `RunoffMmPerYear`) above a configurable threshold (`MinRiverDischargeM3s`, default
`220 m³/s`) and minimum catchment area (`RiverThresholdSqKm`, default `26,000 km²`). Short Order-1 hillside
stubs below `MinHeadwaterReachCells` (default `8 px`) **must** be pruned so ridges and coasts do not sprout
parallel comb teeth, and remaining channels **must** carry Strahler stream order (`1..N`) and hydraulic
width ($W \approx 7.2 \sqrt{Q}\text{ m}$).

*Acceptance:*
- Subtropical deserts and leeward rain shadows spawn near-zero local headwaters; wet windward mountains and
  equatorial belts develop branched dendritic networks up to Strahler order `4..5`.
- Observed on seed 1337 (`2048x1024`): `751` river reaches totaling `260,405 km` across `11,585` channel
  cells, with max Strahler order `4`.

## HY-5 — Complete hydrological outputs for downstream stages and export

Stage 4 **must** output:
- `FlowDirection` (`sbyte[]`, D8 index `0..7` or `-1` for ocean);
- `DrainageAreaSqKm` (`float[]`) and `DischargeM3s` (`float[]`);
- `WaterMask` (`WaterBodyKind[]`: `DryLand`, `Ocean`, `Lake`, `River`);
- `LakeId` (`int[]`) and `Lakes` (`IReadOnlyList<LakeFeature>`);
- `RiverOrder` (`byte[]`), `ChannelWidthMeters` (`float[]`), and `Rivers` (`IReadOnlyList<RiverPolyline>`);
- On-disk `<prefix>.s4-hydro-height.tif`, `-height.png`, `-preview.png`, `-manifest.json`, and
  `-vectors.json`.

*Acceptance:* verified by the numeric diagnostics and JSON serialization of
`<prefix>.s4-hydro-vectors.json`. The old Python validators are legacy and unsupported
([no-python.md](../design/no-python.md)).

## HY-6 — Eroded float32 input, source immutability, and fixed sea level

Hydrology **must** consume the Stage 2 eroded `float32` terrain and Stage 3 climate fields without modifying
the source `.s2-eroded-height.tif` or `.s3-climate-height.tif` files, and **must** honour the fixed `0 m`
sea-level datum.
