# Terrain model

Stage 1 produces one scalar field: elevation in meters above sea level, defined for every direction on
the sphere. This document describes the composite model, its constants, and the behaviour that follows
from them. The primitives are in [noise.md](noise.md); sampling and zoom are in
[spherical-sampling.md](spherical-sampling.md); the plate, province, and landmark layers have their own
note in [terrain-variety.md](terrain-variety.md).

## Shape of the model

```text
c = continent(p) − LandThreshold               // coast is decided only by c
t = TectonicSketch.Sample(p)                   // boundary masks + tangent
q = ProvinceField.Sample(p)                    // blended relief parameters

elevation(p, mpp) = c ≤ 0 ? ocean(p, c, t, q)
                          : land(p, c, t, q, mpp)
elevation         += landmarkField(p)          // sparse additive features
```

Three consequences are worth stating up front:

- **The coastline is decided before any relief is added.** Only the sign of `c` matters, and `c` does not
  depend on resolution, hills, mountains, provinces, or detail. That is why a world map and a zoomed tile
  agree on where the coast is.
- **Land and ocean are separate models.** The ocean uses shelf/slope, abyssal relief, mid-ocean ridges,
  trenches, and arcs; land uses a continental datum, province-modulated continental rise, tectonic
  mountains, rifts, hills, and detail. They meet continuously at `c = 0`, where both return 0 m.
- **Structure is shared across the planet.** Mountain belts, arcs, trenches, rifts, and ridges are masks
  from one plate sketch; provinces vary how each region expresses that structure. They are not independent
  noise blobs.

`continent(p)` is a twice domain-warped fBm field (five octaves, gain 0.52) at scale 1.08. The warps are
frequency 2.4 / strength 0.45, then frequency 5.1 / strength 0.22. The tectonic sketch and province field
are low-frequency analytic fields; their details are in [terrain-variety.md](terrain-variety.md).

## Sea level and land fraction

The sea-level threshold is computed once, in the `PlanetTerrain` constructor, from **32,768 points on a
Fibonacci sphere**:

1. Evaluate the continent field at every point.
2. Sort the values.
3. Take the value at index `round((1 − targetLandFraction) · (N − 1))`.
4. Store it as `LandThreshold`.

Because Fibonacci points are area-uniform on the sphere, selecting a quantile by count selects it by
**surface area**, not by image pixels. The threshold is a property of the seed, not of the render.

Defaults and observed result:

| Quantity | Value |
| --- | --- |
| Target land fraction | 0.29 |
| Threshold (seed 1337) | 0.5480… |
| Observed area-weighted land | 29.7% |

Why not compute sea level from the rendered image? A per-image threshold would move coastlines whenever
the window, resolution, or aspect ratio changed, breaking zoom consistency and making tiles incomparable.
The fixed `LandThreshold` is the reason `--lat/--lon` crops have the same coasts as the world map.

The observed land fraction is slightly above the target because landmarks can raise seamounts above sea
level, creating volcanic islands. The continental coastline itself still follows the fixed threshold.

## Ocean

Let `d = −c` be positive ocean depth parameter, `shelf = smoothstep(0, 0.012, d)` and
`slope = smoothstep(0.012, 0.09, d)`. Then

```text
depth = −170 · shelf − (5900 − 170) · slope
relief = 2 · (oceanFbm(p · 7, 4 octaves) − 0.5)
depth += 380 · relief · slope · (0.7 + 0.6 · q.ContinentalReliefScale)

ridge = t.OceanRidge
depth = lerp(depth, −2500 + 320 · relief, 0.9 · ridge)
depth −= t.Trench · 1800
depth += t.Arc · 2200 · 0.55
```

Reading the terms:

- **Shelf.** From 0 to 0.012 in `d`, the depth falls to −170 m. Starting at exactly 0 keeps the shoreline
  continuous: there is no cliff at the waterline.
- **Slope.** From 0.012 to 0.09, the depth descends to the abyssal level of −5,900 m. The smoothstep makes
  the continental slope a broad sigmoid, not a step.
- **Abyssal relief.** A separate low-amplitude fBm field adds abyssal hills and seamount-scale bumps; it is
  gated by `slope` so the shelf stays smooth. The province scale varies the amplitude slightly.
- **Mid-ocean ridges.** At divergent plate boundaries the floor is pulled toward a shallow crest near
  −2,500 m.
- **Trenches and arcs.** Convergent boundaries add a narrow deep trench and a shallower band on the
  overriding side. The arc shallowing is not usually enough to create an island by itself; landmark
  volcanoes and seamounts do that.

The ocean never rises above 0 m except where a landmark adds a positive feature. The deepest observed
value for the default seed is −7,067 m, below the nominal abyss because relief and trenches are added on
top.

## Land

Let `c > 0`, `inland = smoothstep(0, 0.05, c)`, and
`uplift = max(t.Orogeny, 0.85 · t.Arc) · smoothstep(0.015, 0.10, c)`. Then

```text
elevation = 375 · smoothstep(0, 0.03, c)
          + 6000 · q.ContinentalReliefScale · c^1.35
          + q.BaseOffsetMeters · smoothstep(0.005, 0.08, c)
          + uplift · (0.20 + 1.05 · ridgedAnisotropic(...))
                   · 9500 · q.MountainReliefScale
          − t.Rift · 850 · inland
          + t.RiftShoulder · 450 · inland
          + t.Transform · 260 · inland · (0.5 + q.Anisotropy)
          + 2 · (hillFbm(...) − 0.5) · 320 · q.HillReliefScale · inland
          + 2 · (detailFbm(...) − 0.5) · 70 · q.DetailReliefScale · inland
```

Reading the terms:

- **Continental datum.** `375 · smoothstep(0, 0.03, c)` gives lowlands a realistic base height. It fades
  to zero at the coast, so it cannot move the shoreline; it is the main reason the land median is not stuck
  near 0 m.
- **Continental rise.** `6000 · scale · c^1.35` is the broad surface. The exponent makes low continentalness
  rise slowly, then steepen inland. Province scale lets cratons and passive margins sit lower than
  highlands.
- **Province offsets.** Plateaus add up to 1,850 m; basins subtract 360 m. Both fade in over the first few
  percent of continentalness.
- **Mountain belts.** `uplift` requires both a tectonic boundary mask and enough distance from the coast.
  The ridge term is multiplied by `(0.20 + 1.05 · ridged)`, so even a weak belt lifts the terrain a little;
  strong belts produce sharp chains. Ridged noise is sheared along the local boundary tangent
  ([terrain-variety.md](terrain-variety.md#anisotropy)).
- **Rifts.** A divergent boundary incises up to 850 m and raises shoulders by up to 450 m. This is
  deliberately modest: rifts should read as structure, not as a second set of mountains.
- **Hills and detail.** Medium-scale fBm (±320 m) and fine fBm (±70 m), both scaled by province and faded
  in from the coast. Detail is only present when the target resolution can represent it.
- **Coastal fade.** Hills, detail, rifts, and mountains are multiplied by `inland` or an inland smoothstep,
  so all added relief vanishes at `c = 0`. The coastline classification stays controlled by the continent
  field.

### Land floor

Until hydrology can represent enclosed basins below sea level, the land branch is floored at **0.1 m**.
Without the floor, a deep rift or basin could produce negative "land", and the elevation-sign coastline
would disagree with the `c`-sign coastline. The cost is a flat, near-sea-level floor in unusually deep
closed basins; it is small at the current parameters and is recorded as a limitation.

## Tectonic and province layers

The plate sketch returns normalised masks: `Orogeny`, `Arc`, `Trench`, `Rift`, `RiftShoulder`,
`OceanRidge`, `Transform`, plus a `BoundaryTangent`. The province field returns blended parameters and the
dominant province kind. Both are described in [terrain-variety.md](terrain-variety.md).

The defaults are 13 plates, 5 hotspots, 9 provinces, and a budget of 20 extra landmarks; the actual count
varies with placement rejection. Plate and province counts are settings, not fixed geography.

## Level of detail

The land model is the only part that depends on resolution:

| Layer | Base scale | Max octaves | Included when |
| --- | --- | --- | --- |
| Ridged mountains | `5.5 · q.RidgeFrequencyScale` | 6 | `OctavesFor(mpp, scale, …) > 0` |
| Hills | 12 | 5 | `OctavesFor(mpp, 12, …) > 0` |
| Detail | 40 | 14 | `OctavesFor(mpp, 40, …) > 0` |

The plate sketch, province blend, and landmark set are resolution-independent. They appear identically on
a coarse world map and a fine tile; only the three octave layers above change.

`OctavesFor` uses the planet's circumference, the requested ground size, and a four-samples-per-wavelength
margin; see [noise.md](noise.md#octave-selection-and-aliasing) and
[ADR-004](decisions.md#adr-004--elevation-as-a-function-of-direction-and-ground-resolution).

Because each layer is a prefix of the same infinite series, adding octaves never changes the
low-frequency shape. The only difference between a coarse and a fine sample is the presence of terms the
coarse grid cannot represent. The measured deviation is reported by
[DG-1](../requirements/diagnostics.md#dg-1--hierarchical-consistency).

The renderer passes `min(east-west ground size, north-south ground size)`, with a 100 m floor. The east-west
size shrinks with `cos(latitude)`, so near the poles the renderer requests *less* detail than the row
spacing would suggest; this avoids aliasing the pole-oversampled grid.

## Parameters

All defaults live in [`TerrainSettings`](../../PlanetTerrain.cs). Change them through code or, eventually,
through configuration; the CLI currently exposes only the seed.

| Parameter | Default | Effect |
| --- | --- | --- |
| `Seed` | 1337 | Selects every noise stream, plate set, province set, and landmark set |
| `PlanetRadiusMeters` | 6,371,000 | Converts degrees/pixels to ground distance; also written to the GeoTIFF |
| `TargetLandFraction` | 0.29 | Quantile used for the sea-level threshold |
| `ShelfDepthMeters` | 170 | Depth of the shelf break |
| `AbyssDepthMeters` | 5,900 | Nominal abyssal depth |
| `ContinentalReliefMeters` | 6,000 | Scale of the broad land surface |
| `LandBaseMeters` | 375 | Continental datum added to lowlands |
| `MountainReliefMeters` | 9,500 | Scale of ridged tectonic belts |
| `HillReliefMeters` | 320 | Amplitude of the hill layer |
| `DetailReliefMeters` | 70 | Amplitude of the finest layer |
| `PlateCount` | 13 | Kinematic plates in the sketch |
| `ProvinceCount` | 9 | Geomorphic provinces |
| `HotspotCount` | 5 | Mantle hotspots used for tracks |
| `LandmarkBudget` | 20 | Budget for non-hotspot, non-arc landmarks |
| `RiftDepthMeters` | 850 | Maximum rift incision |
| `RiftShoulderMeters` | 450 | Rift shoulder uplift |
| `TrenchDepthMeters` | 1,800 | Trench deepening |
| `ArcReliefMeters` | 2,200 | Arc shallowing on the sea floor |
| `ContinentScale` (const) | 1.08 | Continent size; smaller = fewer, larger landmasses |
| `RidgeScale` (const) | 5.5 | Base mountain wavelength |
| `HillScale` (const) | 12 | Hill wavelength |
| `DetailScale` (const) | 40 | Finest detail wavelength |

Tuning notes:

- Raising `MountainReliefMeters` increases land p90/p99 and the peak, but also makes the coastline
  classification noisier only if the coastal gate is weakened; keep `uplift`'s inland gate.
- Raising `LandBaseMeters` raises lowlands without raising peaks much; it is the cleanest way to move the
  land median.
- Raising `TargetLandFraction` moves the threshold down and produces more, smaller continents at the same
  `ContinentScale`.
- Lowering `ContinentScale` produces fewer, larger landmasses; raising it fragments the map.
- More plates mean more boundary influence and generally more mountain belts; more provinces mean finer
  regional character.
- The preview is a useful check, but always read the `--check` statistics as well; a pretty image can hide
  an implausible hypsometry.

## Observed result (seed 1337, 2048x1024)

| Measure | Value |
| --- | --- |
| Land fraction (area-weighted) | 29.7% |
| Elevation range | −7,067 .. 8,157 m |
| Mean elevation (area-weighted) | −2,262 m |
| Land p50 / p90 / p99 | 475 / 1,525 / 3,825 m |
| Ocean p50 / p90 | −4,025 / −175 m |
| Landmasses > 0.5% of surface | 4 (14.3%, 5.5%, 4.6%, 3.9%) |
| Total components at 1° | 95 |
| Plates / hotspots / provinces / landmarks | 13 / 5 / 9 / 52 |

The largest continent is smaller than Earth's Afro-Eurasia; that is a deliberate tabletop choice (several
usable continents rather than one dominant mass). Adjust `ContinentScale` if a different balance is
wanted.

## Limitations

- **No erosion.** Slopes are noise slopes. Valleys do not drain anywhere, and there are no depositional
  plains, fans, or deltas yet.
- **No lakes or rivers.** Any closed basin is merely terrain; hydrology is stage 3.
- **No climate.** Snow in the preview is latitude/elevation tinting, not a temperature model.
- **Kinematic, not causal, tectonics.** The plate sketch supplies coherent boundary masks, but it is not a
  geodynamic simulation and the continents are not generated from plate history
  ([ADR-010](decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation)).
- **Single sea level.** There is no ice volume, tides, or sea-level change.
- **Coastal detail is gated, not carved.** The inland fade keeps coasts clean, but it also means the
  shoreline has less fine structure than an eroded coast would.
- **Land floor.** Closed basins are clamped to 0.1 m until hydrology can fill or breach them.
- **Landmarks are unindexed.** The O(N) loop is fine for dozens of features, not thousands.
