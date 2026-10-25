# Terrain variety

Stage 1 must produce a planet that reads as **geography**, not as one isotropic noise texture. The original
composite model already separated continents, shelves, mountain belts, hills, and detail, but its mountain
belts were an independent warped noise field: they had no relationship to each other, no boundary type, and
no tectonic grain. This document describes the structural layers added on top of that model:

1. a **kinematic plate sketch** that supplies globally coherent boundaries and boundary types;
2. **geomorphic provinces** that vary relief character across the surface;
3. a small set of **sparse landmarks** (hotspot tracks, arc volcanoes, massifs, craters, seamounts);
4. **anisotropic** ridged texture aligned with tectonic boundaries.

The result is still a sketch. It is not a mantle-convection model, a plate reconstruction, or a claim that
any particular mountain has a physically simulated history
([ADR-010](decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation),
[ADR-011](decisions.md#adr-011--blended-provinces-and-sparse-landmarks-for-variety)).

## Model shape

For a direction `p`:

```text
c  = continent(p) − LandThreshold          // coastline is still decided only by c
t  = TectonicSketch.Sample(p)              // boundary masks and boundary tangent
q  = ProvinceField.Sample(p)               // blended relief parameters
e  = c ≤ 0 ? ocean(p, c, t, q) : land(p, c, t, q, mpp)
e += LandmarkField.Elevation(p)
```

All four layers are functions of direction. None depends on the output grid, so they are identical on the
world map, a crop, and a tile at any resolution.

## Kinematic plate sketch

[`TectonicSketch`../../src/WorldGen.Core/TectonicSketch.cs creates a small spherical plate mosaic:

- Plate seeds are Fibonacci-sphere points with deterministic jitter; a small weight makes cells unequal.
- Each plate has a random Euler axis and angular rate, so relative motion varies across the planet.
- The Voronoi cell boundary between the two nearest plates is evaluated analytically. The sample point is
  domain-warped first, so boundaries are irregular rather than polygonal.
- A boundary is **convergent**, **divergent**, or **transform** according to the component of relative
  velocity normal to the boundary. Relative speed scales the strength.
- Hotspots are fixed points in the mantle frame with a local plate velocity, used later to build tracks.

Default counts are 13 plates and 5 hotspots. The counts are settings, not hard-coded map features.

### Boundary fields

The sketch returns normalised masks, not elevations. `PlanetTerrain` converts them to metres.

| Field | Where it is strong | Used for |
| --- | --- | --- |
| `Orogeny` | broad band on a convergent boundary | continental mountain belts |
| `Arc` | narrow band offset onto the overriding plate | arc volcanoes and shallow arc sea floor |
| `Trench` | narrow convergent boundary | deep subduction trenches |
| `Rift` | divergent boundary | continental rift incision |
| `RiftShoulder` | paired bands beside a rift | shoulder uplift |
| `OceanRidge` | divergent boundary | mid-ocean ridge crest |
| `Transform` | transform boundary | modest strike-slip relief |

Boundary width, offset, and blend are smooth functions of distance. The fields therefore cross the coast
continuously; the land/ocean decision remains `c ≤ 0`, and the masks are gated inland where they must not
move the shoreline.

Trench, arc, rift, and ocean-ridge geometry is additionally modulated along strike by a low-frequency 3D
noise field: the trench centreline meanders by up to ~140 km, band width varies by about ±25 %, and mask
strength varies by roughly 0.55–1.0. That breaks the "uniform ribbon" look a pure distance-to-boundary mask
produces, without introducing a grid seam. Trench and ridge are also gated to the abyssal ocean in
`PlanetTerrain`, so a boundary cannot cut a channel across a shelf.

## Geomorphic provinces

[`ProvinceField`../../src/WorldGen.Core/ProvinceField.cs partitions the sphere into a small number of seeded provinces with
types such as `Craton`, `Plateau`, `Highlands`, `Basin`, `Volcanic`, `Karst`, `Glacial`, and
`PassiveMargin`. A province is **not** a plate: it describes surface character, not crustal motion.

Each type has a preset parameter vector:

| Parameter | Effect |
| --- | --- |
| `ContinentalReliefScale` | scale of the broad continental rise |
| `MountainReliefScale` | scale of tectonic mountains inside the province |
| `HillReliefScale` | medium-scale roughness |
| `DetailReliefScale` | fine texture |
| `RidgeFrequencyScale` | mountain wavelength |
| `BaseOffsetMeters` | plateau uplift or basin depression |
| `Anisotropy` | how strongly ridges follow the local tectonic grain |
| `Roughness` | ridged-noise gain |
| `Erodibility` | reserved for stage 2 erosion |

The two nearest provinces are blended with a smoothstep on their score difference. Near a province boundary
the parameters are 50/50; away from it the nearest province dominates. There is no visible polygon edge, and
the blend depends only on direction, so it is LOD-stable.

The recurring province kinds are not a climate map. A `Glacial` province is rough, high-detail terrain;
whether it is actually ice-covered is a stage-4 climate decision.

## Sparse landmarks

[`LandmarkField`../../src/WorldGen.Core/LandmarkField.cs places individual landforms rather than adding another noise
octave:

- **Hotspot tracks:** a young volcano or caldera at the hotspot, with older seamounts trailing in the
  direction opposite local plate motion.
- **Arc volcanoes:** rejection-sampled on the tectonic arc mask, on land or sea floor.
- **Massifs:** broad domes on land, including a couple of deliberately large ones.
- **Craters:** a rim plus a central depression.
- **Seamounts:** the ocean expression of hotspot and arc features.

Each landmark stores position, radius, height/depth, and kind. Evaluation is additive and O(N); the default
world has roughly 50–80 landmarks. Because they are objects in world space, they survive zoom and can later
be named, moved, or exported as tabletop points of interest. A tall seamount can raise the sea floor above
0 m and create a volcanic island even though the continental coastline is unchanged.

## Anisotropy

Mountains should have a grain. `PlanetTerrain.RidgedAnisotropic` shears the sample point along the local
boundary tangent before evaluating ridged noise:

```text
tangent = normalize(t.BoundaryTangent − p · (p · t.BoundaryTangent))
shear   = anisotropyNoise(p · f) · province.Anisotropy · 0.22
warped  = normalize(p + tangent · shear)
ridges  = ridged(warped · f, octaves, province.Roughness)
```

This stretches crests along the belt without introducing a 2D direction field, a seam, or a resolution
dependence. The shear is deliberately modest: the boundary mask already supplies the large-scale trend.

## Determinism and level of detail

Plate seeds, Euler poles, province seeds, and landmark placement all use [`DeterministicRandom`../../src/WorldGen.Core/MathUtils.cs
seeded through `MathUtils.DeriveSeed` ([ADR-012](decisions.md#adr-012--fixed-prng-for-structural-generation)). The
per-pixel path is pure arithmetic on the direction. There is no cache whose contents depend on row order.

The structural layers are resolution-independent. The only resolution-dependent terms remain the ridged,
hill, and detail octave counts described in [noise.md](noise.md#octave-selection-and-aliasing). As a result,
the same mountain belt, rift, province, and volcano appear at every zoom; a finer render only adds texture.

## Observed evidence

For seed 1337 at 2048×1024, `--check` reports:

| Measure | Value |
| --- | --- |
| Plates / hotspots / provinces / landmarks | 13 / 5 / 9 / 60 |
| Surface with boundary influence > 0.5 | 24.9% |
| Mean orogeny / arc / rift | 0.064 / 0.030 / 0.023 |
| Land provinces represented | 8 of 8 kinds |
| Largest land province share | Plateau 20% |
| Land p50 / p90 / p99 | 425 / 1,625 / 2,625 m |
| Highest peak | 7,247 m |

The full hypsometry and landmass checks are in [BT-4](../requirements/base-topology.md#bt-4--earth-like-hypsometry)
and [BT-5](../requirements/base-topology.md#bt-5--landmass-distribution); the acceptance requirement for the
structural layers is [BT-13](../requirements/base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks).

## Limits

- **Kinematic, not causal.** Plates move on prescribed Euler poles. There is no mantle flow, no crustal
  creation/destruction budget, and no isostasy.
- **Continents are independent of plates.** The continent field is not generated from plate history, so a
  boundary can cross a continent in a way that a real reconstruction would not allow.
- **Province presets are hand-tuned.** They are a vocabulary for variety, not a classification of real
  terranes.
- **Landmarks are sparse and unindexed.** O(N) evaluation is fine at the default count; thousands of
  landmarks would need a spatial index.
- **Land is floored at 0.1 m** until hydrology can represent enclosed basins below sea level. Large rifts
  and basins can therefore contain flat, near-sea-level floors.
- **No erosion.** The tectonic and province fields set up structure, but valleys and sediment are stage 2.
- **Peaks are bounded.** A soft ceiling (`MountainCeilingMeters`) asymptotes the extreme tail so no seed
  clips the 16-bit PNG window; the most extreme peaks are compressed by the ceiling, but typical mountain
  belts are unchanged. The placement stream is fixed across .NET versions by `DeterministicRandom`
  ([ADR-012](decisions.md#adr-012--fixed-prng-for-structural-generation)).

## Related

- [Terrain model](terrain-model.md) — the elevation equation that consumes these fields.
- [Noise](noise.md) — fBm, ridged noise, warping, and octave selection.
- [Spherical sampling](spherical-sampling.md) — why every field is a function of direction.
- [Architecture](architecture.md) — module map and process shape.
- [ADR-010](decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation),
  [ADR-011](decisions.md#adr-011--blended-provinces-and-sparse-landmarks-for-variety).
