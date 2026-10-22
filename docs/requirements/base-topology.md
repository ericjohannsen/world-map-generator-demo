# Stage 1 requirements — base topology

Stage 1 produces the elevation field that every later stage consumes. "View from space" is the primary
acceptance lens: a player looking at the world map should see a plausible planet, not noise.

Implementation: [`PlanetTerrain.cs`](../../PlanetTerrain.cs), [`TectonicSketch.cs`](../../TectonicSketch.cs),
[`ProvinceField.cs`](../../ProvinceField.cs), [`LandmarkField.cs`](../../LandmarkField.cs),
[`PerlinNoise.cs`](../../PerlinNoise.cs), [`Heightmap.cs`](../../Heightmap.cs). Design:
[terrain model](../design/terrain-model.md), [terrain variety](../design/terrain-variety.md),
[noise](../design/noise.md), [spherical sampling](../design/spherical-sampling.md).

## BT-1 — Spherical domain

The terrain **must** be defined over the surface of a sphere with no pole singularity, no seam at the
antimeridian, and no latitude-dependent shape distortion. Sampling in 2D image space and wrapping the
result around the planet is not acceptable.

*Acceptance:* longitude 180 and -180 sample the same direction; the field is continuous through both
poles. Implemented by evaluating 3D noise on unit vectors ([ADR-002](../design/decisions.md#adr-002--sample-noise-in-3d-on-the-unit-sphere)).

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

*Acceptance:* area-weighted land fraction within ±1.5 points of the target. Observed: 29.8% for the
default seed; the +0.8 point offset comes from landmark seamounts that rise above sea level, and the
continental coastline itself still follows the fixed threshold exactly.

## BT-4 — Earth-like hypsometry

The area-weighted elevation distribution **should** resemble a real planet: most land low, a long tail
of mountains, ocean dominated by abyssal depths with a minority of shelf.

| Measure | Target band | Observed (seed 1337, 2048×1024) |
| --- | --- | --- |
| Land median | 200–800 m | 425 m |
| Land p90 | 1,200–2,600 m | 1,625 m |
| Land p99 | 2,400–6,500 m | 2,625 m |
| Highest peak | > 5,500 m | 7,247 m |
| Ocean median | −4,800 to −3,600 m | −4,025 m |
| Ocean p90 (shallow tenth) | −600 to −100 m | −175 m |

**Seed population (defaults, 40 seeds, 512×256):** land p99 min 2,475 / median 3,925 / max 6,425 m;
peaks min 3,362 / max 8,169 m. The band above describes a representative world; a particular seed may fall
in the low tail. The distribution (not a single seed's p99) is the acceptance measure. Peaks are bounded
by the soft ceiling (`MountainCeilingMeters` = 8500) so no seed clips the PNG window; the hard peak may be
slightly compressed in the extreme tail, but normal mountains are unchanged.

*Acceptance:* `--check` prints these values and they fall in band. See [DG-2](diagnostics.md#dg-2--terrain-statistics).

## BT-5 — Landmass distribution

A view-from-space world **should** present a handful of recognizable continents rather than one
supercontinent or a scatter of specks.

*Acceptance,* measured at 1° resolution by connected components ([DG-2](diagnostics.md#dg-2--terrain-statistics)):

- Total components: any value; small islands are expected.
- Components larger than 0.5% of the surface: 3–9.
- Largest component: 6–18% of the surface.
- At least two components above 2%.

Observed: 129 components, 7 above 0.5%, sizes 11.3%, 9.1%, 5.1%, 0.8%, 0.7%, 0.6%, 0.6%. The largest is smaller than
Earth's Afro-Eurasia (17%); `ContinentScale` in `PlanetTerrain` trades continent count against size.

## BT-6 — Ocean bathymetry

The ocean **must** contain distinct depth provinces rather than a single noisy basin:

- a shallow **shelf** contiguous with the coast, starting at 0 m depth so the shoreline is continuous;
- a **continental slope** into abyssal depths;
- **abyssal plains/hills** with modest relief;
- **mid-ocean ridges** along divergent plate boundaries;
- **trenches** along convergent plate boundaries, with a shallower arc band on the overriding side.

*Acceptance:* ocean p50 and p90 in the bands above; no depth discontinuity at the coastline; shelf depth
default 170 m and abyss default 5,900 m. Implemented from the kinematic plate sketch
([terrain variety](../design/terrain-variety.md#boundary-fields)).

## BT-7 — Mountain belts

Mountains **must** be organized into belts rather than spread uniformly, and **must** be suppressed at
the coast and in the ocean, where plate boundaries instead produce mid-ocean ridges and trenches. Belt
cores **should** be ridged (sharp crests), not rounded blobs, and their crests **should** follow the local
tectonic grain rather than being isotropic.

*Acceptance:* land p90/p99 in the hypsometry bands; mountains visible as continuous chains on the preview.
Implemented with ridged multifractal noise gated by the plate-boundary orogeny mask and the inland mask,
and sheared along the boundary tangent ([terrain model](../design/terrain-model.md#land),
[anisotropy](../design/terrain-variety.md#anisotropy)).

## BT-8 — Hierarchical resolution

The elevation **must** be a pure function of direction and a target ground resolution (meters per
pixel). Finer targets **must** add detail octaves without changing the world-scale shape; no baked
bitmap may be upsampled to satisfy zoom.

*Acceptance:* [DG-1](diagnostics.md#dg-1--hierarchical-consistency) reports near-zero deviation between
world-scale and 64x finer sampling of the same direction. Observed: max 1 m, RMS 0 m, coastline
classification unchanged.

The plate sketch, province blend, and landmark set add no resolution-dependent terms; only the ridged,
hill, and detail octave counts vary with `metersPerPixel`. Feature density is therefore a property of the
world, not of the render.

*Limitation:* this guarantee currently covers base topology only. Erosion and hydrology must preserve it
when they are added ([ZM-3](pipeline.md#stage-5--regional-zoom)).

## BT-9 — Determinism

A seed **must** fully determine the planet. Repeated runs with the same options **must** produce
byte-identical files, independent of thread scheduling.

*Acceptance:* SHA-256 of `<out>-height.tif`, `<out>-height.png`, `<out>-preview.png`, and
`<out>-manifest.json` matches across repeated runs with the same output base. Verified.

## BT-10 — Map windows

The renderer **must** accept any latitude/longitude window (north/south/west/east, degrees) and produce
a properly georeferenced equirectangular sample grid, including windows that cross ±180°.

A crossing window uses continuous longitudes (`170..190`), which keeps `east > west` and lets
trigonometry handle the wrap without a special case.

*Acceptance:* a regional render covers the requested bounds; adjacent windows agree at their shared
edge; the window and pixel size are recorded in both the GeoTIFF and the manifest. Implemented as
`--lat` / `--lon`.

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

*Acceptance:* ≤ 20 s for the world map in a Debug build, including artifact writing; observed ~15 s,
and ~12 s for a 1024² tile (a tile adds octaves). Rows are rendered in parallel; correctness must not
depend on scheduling ([NFR-1](pipeline.md#non-functional-requirements)). The structural layers added by
[BT-13](#bt-13--tectonic-structure-provinces-and-landmarks) are the dominant cost; the documented
optimisations (per-row plate/province caching or a landmark spatial index) are not yet needed.

## BT-13 — Tectonic structure, provinces, and landmarks

The planet **must** contain global structural coherence and regional variety, not one uniform relief
recipe:

- mountain belts, trenches, mid-ocean ridges, rifts, and transform boundaries **must** come from one
  kinematic plate sketch with boundary types derived from relative plate motion;
- the surface **must** be partitioned into a small number of geomorphic provinces whose parameters
  (relief scale, wavelength, base offset, anisotropy, roughness) differ, and whose boundaries blend
  smoothly so no polygon edge is visible;
- the world **must** contain a small number of sparse landmarks (hotspot tracks, arc volcanoes, massifs,
  craters, seamounts) placed as world-space objects rather than as extra noise;
- plate boundaries, provinces, and landmarks **must** be seed-determined and reproducible, using separate
  derived seed streams so that changing one layer does not move another;
- ridge texture **should** be anisotropic, aligned with the local boundary tangent;
- all of these layers **must** be functions of direction only, so they are identical at every zoom and
  tile.

*Acceptance,* reported by `--check` ([DG-4](diagnostics.md#dg-4--variety-report)):

- plate, hotspot, province, and landmark counts are reported and non-trivial (≥ 5 plates, ≥ 2 hotspots,
  ≥ 4 provinces, ≥ 10 landmarks for the default seed);
- a meaningful fraction of the surface is near a boundary (observed 24.9% with influence > 0.5) and at
  least three tectonic masks have a non-zero surface mean;
- land is represented by at least four distinct province kinds, and no single kind exceeds 60% of land;
- the structural layers are unchanged between world-scale and 64× finer sampling (they contain no
  resolution-dependent terms).

This is a **sketch**, not a geodynamic simulation; see
[ADR-010](../design/decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation) and
[ADR-011](../design/decisions.md#adr-011--blended-provinces-and-sparse-landmarks-for-variety).
