# Stage 1 requirements — base topology

Stage 1 produces the elevation field that every later stage consumes. "View from space" is the primary
acceptance lens: a player looking at the world map should see a plausible planet, not noise.

Implementation: [`PlanetTerrain.cs`](../../PlanetTerrain.cs), [`PerlinNoise.cs`](../../PerlinNoise.cs),
[`Heightmap.cs`](../../Heightmap.cs). Design: [terrain model](../design/terrain-model.md),
[noise](../design/noise.md), [spherical sampling](../design/spherical-sampling.md).

## BT-1 — Spherical domain

The terrain **must** be defined over the surface of a sphere with no pole singularity, no seam at the
antimeridian, and no latitude-dependent shape distortion. Sampling in 2D image space and wrapping the
result around the planet is not acceptable.

*Acceptance:* longitude 180 and -180 sample the same direction; the field is continuous through both
poles. Implemented by evaluating 3D noise on unit vectors ([ADR-002](../design/decisions.md#adr-002-sample-noise-in-3d-on-the-unit-sphere)).

## BT-2 — Absolute elevations

Elevations **must** be stored and reported in meters relative to sea level (datum 0), not normalized to
0..1 or to a per-image min/max. Ocean depths are negative. The planet radius **must** be part of the
configuration (default 6,371,000 m) because it translates resolution into ground distance.

*Acceptance:* a run reports an elevation range in meters, and the same direction has the same elevation
regardless of output resolution, apart from resolution-dependent detail octaves.

## BT-3 — Target land fraction

The generator **must** expose a target land fraction (default 0.29) and hit it within about one
percentage point of the **planet's surface area**, not of the pixel count. The sea-level threshold
**must** be derived once from a fixed, area-uniform sample set so that coastlines are identical across
runs, views, and resolutions.

*Acceptance:* area-weighted land fraction within ±1.5 points of the target. Observed: 28.8% for the
default seed.

## BT-4 — Earth-like hypsometry

The area-weighted elevation distribution **should** resemble a real planet: most land low, a long tail
of mountains, ocean dominated by abyssal depths with a minority of shelf.

| Measure | Target band | Observed (seed 1337) |
| --- | --- | --- |
| Land median | 200–600 m | 325 m |
| Land p90 | 1,500–2,600 m | 2,325 m |
| Land p99 | 3,500–5,000 m | 4,475 m |
| Highest peak | > 5,500 m | 6,257 m |
| Ocean median | −4,800 to −3,600 m | −4,125 m |
| Ocean p90 (shallow tenth) | −600 to −100 m | −175 m |

The land p90 sits deliberately at the upper end of the band so mountain belts read at world scale. Tune
through `MountainReliefMeters` and `ContinentalReliefMeters` in [`TerrainSettings`](../../PlanetTerrain.cs).

*Acceptance:* `--check` prints these values and they fall in band. See [DG-2](diagnostics.md#dg-2--terrain-statistics).

## BT-5 — Landmass distribution

A view-from-space world **should** present a handful of recognizable continents rather than one
supercontinent or a scatter of specks.

*Acceptance,* measured at 1° resolution by connected components ([DG-2](diagnostics.md#dg-2--terrain-statistics)):

- Total components: any value; small islands are expected.
- Components larger than 0.5% of the surface: 3–9.
- Largest component: 6–18% of the surface.
- At least two components above 2%.

Observed: 110 components, 5 above 0.5%, sizes 9.3%, 5.3%, 4.4%, 4.3%, 3.8%. The largest is smaller than
Earth's Afro-Eurasia (17%); `ContinentScale` in `PlanetTerrain` trades continent count against size.

## BT-6 — Ocean bathymetry

The ocean **must** contain distinct depth provinces rather than a single noisy basin:

- a shallow **shelf** contiguous with the coast, starting at 0 m depth so the shoreline is continuous;
- a **continental slope** into abyssal depths;
- **abyssal plains/hills** with modest relief;
- **mid-ocean ridges** where the belt field is strongest.

*Acceptance:* ocean p50 and p90 in the bands above; no depth discontinuity at the coastline; shelf depth
default 170 m and abyss default 5,900 m.

## BT-7 — Mountain belts

Mountains **must** be organized into belts rather than spread uniformly, and **must** be suppressed at
the coast and in the ocean, where the same belt field instead produces mid-ocean ridges. Belt cores
**should** be ridged (sharp crests), not rounded blobs.

*Acceptance:* land p90/p99 in the hypsometry bands; mountains visible as continuous chains on the
preview. Implemented with ridged multifractal noise gated by the belt and inland masks
([terrain model](../design/terrain-model.md#land)).

## BT-8 — Hierarchical resolution

The elevation **must** be a pure function of direction and a target ground resolution (meters per
pixel). Finer targets **must** add detail octaves without changing the world-scale shape; no baked
bitmap may be upsampled to satisfy zoom.

*Acceptance:* [DG-1](diagnostics.md#dg-1--hierarchical-consistency) reports near-zero deviation between
world-scale and 64x finer sampling of the same direction. Observed: max 1 m, RMS 0 m, coastline
classification unchanged.

*Limitation:* this guarantee currently covers base topology only. Erosion and hydrology must preserve it
when they are added ([ZM-3](pipeline.md#stage-5--regional-zoom)).

## BT-9 — Determinism

A seed **must** fully determine the planet. Repeated runs with the same options **must** produce
byte-identical files, independent of thread scheduling.

*Acceptance:* SHA-256 of `<out>-height.png` and `<out>-preview.png` matches across repeated runs.
Verified.

## BT-10 — Map windows

The renderer **must** accept any latitude/longitude window (north/south/west/east, degrees) and produce
a properly georeferenced equirectangular sample grid, including windows that cross ±180°.

*Acceptance:* a regional render covers the requested bounds; adjacent windows agree at their shared
edge. Implemented as `--lat` / `--lon`.

## BT-11 — View-from-space preview

Every run **must** produce a diagnostic colour preview with:

- ocean depth tinting, including shelf;
- land colouring by elevation;
- latitude-dependent snow/ice and sea ice;
- hillshading from a consistent light direction.

*Acceptance:* preview exists, is 8-bit RGB, and its coastline matches the heightmap's zero contour.
Biomes and clouds are out of scope ([BT-11](base-topology.md#bt-11--view-from-space-preview) is
presentation only; see [PX-2](pipeline.md#stage-6--presentation-and-export)).

## BT-12 — Performance

A 2048x1024 world render **must** complete in seconds on a desktop CPU, and a 1024x1024 regional tile
**must** be proportionally faster.

*Acceptance:* ≤ 10 s for the world map in a Debug build; observed ~5–7 s, and ~2 s for a 1024² tile.
Rows are rendered in parallel; correctness must not depend on scheduling ([NFR-1](pipeline.md#non-functional-requirements)).
