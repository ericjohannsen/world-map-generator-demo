# Terrain model

Stage 1 produces one scalar field: elevation in meters above sea level, defined for every direction on
the sphere. This document describes the composite model, its constants, and the behaviour that follows
from them. The primitives are in [noise.md](noise.md); sampling and zoom are in
[spherical-sampling.md](spherical-sampling.md).

## Shape of the model

```text
continentalness c = continent(p) − seaLevelThreshold
belt              = beltField(p)

elevation(p, mpp) = c ≤ 0 ? ocean(p, c, belt)
                          : land(p, c, belt, mpp)
```

Two consequences are worth stating up front:

- **The coastline is decided before any relief is added.** Only the sign of `c` matters, and `c` does not
  depend on resolution, hills, mountains, or detail. That is why a world map and a zoomed tile agree on
  where the coast is.
- **Land and ocean are separate models.** The ocean uses depth provinces and mid-ocean ridges; land uses
  continental rise, mountain belts, hills, and detail. They meet continuously at `c = 0`, where both
  return 0 m.

`continent(p)` is a twice domain-warped fBm field (five octaves, gain 0.52) at scale 1.08. `beltField(p)`
is a warped fBm field (three octaves) at scale 2.6. The warp parameters are in
[noise.md](noise.md#domain-warping).

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
| Observed area-weighted land | 28.8% |

Why not compute sea level from the rendered image? A per-image threshold would move coastlines whenever
the window, resolution, or aspect ratio changed, breaking zoom consistency and making tiles incomparable.
The fixed `LandThreshold` is the reason `--lat/--lon` crops have the same coasts as the world map.

The finite sample set introduces a small quantile error (the observed 28.8% vs 29.0% target). That is
acceptable ([BT-3](../requirements/base-topology.md#bt-3--target-land-fraction)); increasing the sample
count would reduce it at startup cost.

## Ocean

Let `d = −c` be positive ocean depth parameter, `shelf = smoothstep(0, 0.012, d)` and
`slope = smoothstep(0.012, 0.09, d)`. Then

```text
depth = −170 · shelf − (5900 − 170) · slope
relief = 2 · (oceanFbm(p · 7, 4 octaves) − 0.5)
depth += 380 · relief · slope

ridge  = smoothstep(0.74, 0.92, belt)
depth  = lerp(depth, −2500 + 320 · relief, 0.9 · ridge)
```

Reading the terms:

- **Shelf.** From 0 to 0.012 in `d`, the depth falls to −170 m. Starting at exactly 0 keeps the shoreline
  continuous: there is no cliff at the waterline.
- **Slope.** From 0.012 to 0.09, the depth descends to the abyssal level of −5,900 m. The smoothstep makes
  the continental slope a broad sigmoid, not a step.
- **Abyssal relief.** A separate low-amplitude fBm field adds abyssal hills and seamount-scale bumps; it is
  gated by `slope` so the shelf stays smooth.
- **Mid-ocean ridges.** Where the belt field is strongest (above 0.74), the ocean floor is pulled toward a
  shallow crest near −2,500 m. The same belt field that makes mountains on land makes ridges at sea.

The ocean never rises above 0 m and is not clamped from below. The deepest observed value for the default
seed is −6,118 m, slightly below the nominal abyss because relief is added on top.

## Land

Let `c > 0`, `inland = smoothstep(0, 0.05, c)`, and `u = smoothstep(0.30, 0.72, belt) ·
smoothstep(0.015, 0.10, c)`. Then

```text
elevation = 5000 · c^1.35
          + u · (0.2 + 0.8 · ridged(p · 5.5, up to 6 octaves)) · 7400
          + 2 · (hillFbm(p · 12, up to 5 octaves) − 0.5) · 320 · inland
          + 2 · (detailFbm(p · 40, up to 14 octaves) − 0.5) · 70 · inland
```

Reading the terms:

- **Continental rise.** `5000 · c^1.35` is the broad surface. The exponent makes low continentalness
  (near the coast) rise slowly, then steepen inland. With the default threshold, the maximum is around
  1.7 km before mountains.
- **Mountain belts.** `u` requires both a strong belt value and enough distance from the coast. The ridge
  term is multiplied by `(0.2 + 0.8 · ridged)`, so even a weak belt lifts the terrain a little; strong
  belts produce sharp chains up to the full 7,400 m relief.
- **Hills.** Medium-scale fBm, ±320 m, faded in over the first 5% of continentalness.
- **Detail.** ±70 m of fine fBm, only present when the target resolution can represent it, also faded in
  from the coast.
- **Coastal fade.** Hills and detail are multiplied by `inland`, so all added relief vanishes at `c = 0`.
  The residual sub-meter variation of the continental term is the only thing that can cross sea level
  near the coast; in practice this does not change the coastline classification at the tested
  resolutions.

Land is not clamped to a maximum. A seed with unusually strong belts can exceed 7 km; the default seed
peaks at 6,257 m. That is intentional: sci-fi worlds with different relief should not be silently
flattened.

## Level of detail

The land model is the only part that depends on resolution:

| Layer | Base scale | Max octaves | Included when |
| --- | --- | --- | --- |
| Ridged mountains | 5.5 | 6 | `OctavesFor(mpp, 5.5, …) > 0` |
| Hills | 12 | 5 | `OctavesFor(mpp, 12, …) > 0` |
| Detail | 40 | 14 | `OctavesFor(mpp, 40, …) > 0` |

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
| `Seed` | 1337 | Selects all seven noise streams |
| `PlanetRadiusMeters` | 6,371,000 | Converts degrees/pixels to ground distance; also written to the GeoTIFF |
| `TargetLandFraction` | 0.29 | Quantile used for the sea-level threshold |
| `ShelfDepthMeters` | 170 | Depth of the shelf break |
| `AbyssDepthMeters` | 5,900 | Nominal abyssal depth |
| `ContinentalReliefMeters` | 5,000 | Scale of the broad land surface |
| `MountainReliefMeters` | 7,400 | Scale of ridged belts |
| `HillReliefMeters` | 320 | Amplitude of the hill layer |
| `DetailReliefMeters` | 70 | Amplitude of the finest layer |
| `ContinentScale` (const) | 1.08 | Continent size; smaller = fewer, larger landmasses |
| `BeltScale` (const) | 2.6 | Width/spacing of belts and ridges |

Tuning notes:

- Raising `MountainReliefMeters` increases land p90/p99 and the peak, but also makes the coastline
  classification noisier only if the coastal gate is weakened; keep `u`'s inland gate.
- Raising `TargetLandFraction` moves the threshold down and produces more, smaller continents at the same
  `ContinentScale`.
- Lowering `ContinentScale` produces fewer, larger landmasses; raising it fragments the map.
- The preview is a useful check, but always read the `--check` statistics as well; a pretty image can hide
  an implausible hypsometry.

## Observed result (seed 1337, 2048x1024)

| Measure | Value |
| --- | --- |
| Land fraction (area-weighted) | 28.8% |
| Elevation range | −6,118 .. 6,257 m |
| Mean elevation (area-weighted) | −2,277 m |
| Land p50 / p90 / p99 | 325 / 2,325 / 4,475 m |
| Ocean p50 / p90 | −4,125 / −175 m |
| Landmasses > 0.5% of surface | 5 (9.3%, 5.3%, 4.4%, 4.3%, 3.8%) |
| Total components at 1° | 110 |

The largest continent is smaller than Earth's Afro-Eurasia; that is a deliberate tabletop choice (several
usable continents rather than one dominant mass). Adjust `ContinentScale` if a different balance is
wanted.

## Limitations

- **No erosion.** Slopes are noise slopes. Valleys do not drain anywhere, and there are no depositional
  plains, fans, or deltas yet.
- **No lakes or rivers.** Any closed basin is merely terrain; hydrology is stage 3.
- **No climate.** Snow in the preview is latitude/elevation tinting, not a temperature model.
- **No tectonic causality.** Belts are a field, not a simulation. The model does not claim that a
  particular chain is a collision zone.
- **Single sea level.** There is no ice volume, tides, or sea-level change.
- **Coastal detail is gated, not carved.** The inland fade keeps coasts clean, but it also means the
  shoreline has less fine structure than an eroded coast would.
- **Isotropic noise.** Continent and belt shapes inherit no directional bias; warping hides this at
  world scale but not under close inspection.
