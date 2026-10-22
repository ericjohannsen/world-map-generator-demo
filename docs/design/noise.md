# Noise

Noise is the raw material, not the model. Stage 1 combines several noise fields into continents, ocean
basins, mountain belts, hills, and fine detail; this document describes the primitives and the numerical
choices that make them stable across zoom levels.

## Why 3D, sampled on the unit sphere

Terrain is evaluated at a point on the unit sphere, so the input to the noise is a 3D position, not a
2D texture coordinate. This removes three failure modes that are unacceptable for a whole planet:

- no seam at the antimeridian, because longitude is just an angle around the sphere;
- no pinch or singularity at the poles, because there is no 2D parameterisation;
- no latitude-dependent stretching of continent shapes.

The cost is that noise is evaluated in 3D and the visual detail near the poles is not "denser" — the
grid is denser there, which is a sampling issue handled by
[spherical-sampling.md](spherical-sampling.md).

See [ADR-002](decisions.md#adr-002--sample-noise-in-3d-on-the-unit-sphere).

## Perlin noise

[`PerlinNoise`](../../PerlinNoise.cs) implements Ken Perlin's improved gradient noise in three dimensions:

- a 256-entry permutation table, duplicated to 512 entries to avoid index wrapping in the inner loop;
- the table is shuffled with `System.Random(seed)` using Fisher–Yates, so a stream is exactly reproducible
  for a given seed and runtime;
- quintic fade `6t⁵ − 15t⁴ + 10t³` for continuous first and second derivatives;
- twelve cube-edge gradient directions selected by the low bits of the hash;
- output in `[-1, 1]`.

Each octave costs eight gradient evaluations plus the fade and interpolation. This is the dominant cost
of a world render.

## Fractal Brownian motion

`Fractal(point, octaves, gain = 0.5, lacunarity = 2)` sums octaves and remaps the result to `[0, 1]`:

```text
total   = Σ_{i=0..n-1} noise(point · f · lacunarity^i) · gain^i
result  = clamp(total · (1 − gain) · 0.5 + 0.5, 0, 1)
```

The normalisation uses the **infinite** geometric series `1 / (1 − gain)`, not the sum of the octaves
actually requested. This is the key property for zoom:

- A render with five octaves and a render with nine octaves have exactly the same first five octaves and
  the same scale factor.
- The finer render simply adds smaller terms. It does not renormalise or shift the low-frequency shape.
- Therefore a coarse sample is a valid "prefix" of the fine sample, which is what makes
  [DG-1](../requirements/diagnostics.md#dg-1--hierarchical-consistency) pass.

With `gain = 0.5` the amplitude of octave *i* is `2^-i` and the infinite sum is 2, hence the `(1 − gain)`
factor. The theoretical range of the sum is `[-2, 2]`; clamping to `[0, 1]` is intentional.

## Ridged multifractal

Mountains need sharp crests, not smooth blobs. `Ridged` uses

```text
signal_i = (1 − |noise_i|)²
result   = clamp(Σ signal_i · gain^i · (1 − gain), 0, 1)
```

`1 − |noise|` is 1 where the underlying noise crosses zero and falls off on both sides; squaring narrows
the crest. Summing octaves creates a hierarchy of ridges. The result is concentrated toward low values
with narrow highs, which is what `MathUtils.SmoothStep` gates turn into mountain chains.

Ridged noise is used only for mountain belts. Hills and detail use plain fBm.

## Domain warping

Round noise blobs do not look like continents. `PlanetTerrain.Warped` displaces the sample point by a
vector-valued noise offset and renormalises back to the sphere:

```csharp
offset = (warp(p + (11.3, 0, 0)),
          warp(p + (0, 23.7, 0)),
          warp(p + (0, 0, 37.1)))
return normalize(p + offset · strength)
```

The three fixed offsets decorrelate the components, so the offset is not a scalar displacement along one
axis. Renormalising is essential: staying on the unit sphere keeps the field seamless and makes the
result independent of the planet's radius.

The continent field is warped twice (frequency 2.4, strength 0.45, then frequency 5.1, strength 0.22).
The second pass adds smaller-scale distortion to coastlines and inland seas. The belt field is warped
once (frequency 3.1, strength 0.35).

Warping is not free: each pass multiplies the number of noise evaluations. It is, however, the cheapest
way to make procedural continents look like they have a history.

## Seeded streams

A single world seed drives seven independent streams:

| Stream | Used for |
| --- | --- |
| 1 | Continent shape |
| 2 | Domain warp |
| 3 | Belts / mid-ocean ridges |
| 4 | Ridged mountains |
| 5 | Hills |
| 6 | Fine detail |
| 7 | Ocean relief (abyssal hills, seamounts) |

`DeriveSeed(seed, stream) = seed · 1_000_003 + stream · 97_409 + 12_345` in unchecked integer
arithmetic. The constants are arbitrary but fixed; changing them changes every planet, so they are part
of the compatibility surface.

The streams are deliberately separate: changing the mountain seed should not move the coastlines, and
vice versa.

## Frequency scales

Noise is evaluated at `point · scale`. Larger scale means smaller features on the sphere. The current
constants in [`PlanetTerrain`](../../PlanetTerrain.cs):

| Field | Scale | Octaves | Character |
| --- | --- | --- | --- |
| Continent | `1.08` | 5 | A few thousand-kilometre landmasses |
| Belt | `2.6` | 3 | Broad orogenic bands |
| Ridge | `5.5` | up to 6 | Individual mountain chains |
| Hill | `12` | up to 5 | Regional roughness |
| Detail | `40` | up to 14 | Fine texture, added only at high resolution |
| Ocean relief | `7` | 4 | Abyssal hills and seamount-scale bumps |

These are tuning constants, not physical wavelengths. Use the observed statistics in
[base-topology.md](../requirements/base-topology.md#bt-4--earth-like-hypsometry) and the preview to
adjust them; do not expect a scale value to correspond to a real geological process.

## Octave selection and aliasing

`OctavesFor(metersPerPixel, baseFrequency, maxOctaves, planetRadius)` stops adding octaves when the
wavelength approaches a few pixels:

```text
finestFrequency = circumference / (4 · metersPerPixel)
octaves         = ceil(log2(finestFrequency / baseFrequency)) + 1
```

The factor of 4 is a conservative Nyquist margin: four samples per wavelength, not two. If the target
resolution cannot represent the base frequency at all, the function returns 0 and that layer is omitted
entirely. `metersPerPixel = ∞` (or non-finite) returns `maxOctaves`.

Consequences:

- A world map at 19.5 km/px omits most detail and hill octaves; the shape is smooth at that scale.
- A regional tile at 3.8 km/px adds them.
- A very fine render approaches the full field but is bounded by the per-layer `maxOctaves`, so it never
  aliases indefinitely or costs unbounded time.

## Limitations

- **No erosion, no flow routing.** The noise has no drainage network; valleys are whatever the sum of
  octaves produces. Erosion is expected to fix this ([ER-2](../requirements/pipeline.md#stage-2--erosion)).
- **No plate causality.** Belts are a warped noise field. There is no subduction, rifting, or hotspot
  model.
- **Anisotropy is not intentional.** Because terrain is isotropic on the sphere, mountain belts do not
  preferentially align with anything; the warped belt field creates apparent trends but no tectonic
  grain.
- **Seed stability is runtime-dependent.** `System.Random` is seeded deterministically, but .NET does not
  promise that its seeded sequence is identical across major runtime versions. Golden hashes should be
  regenerated when the runtime changes, or the permutation shuffle should be replaced with a fixed
  algorithm if cross-version reproducibility becomes a requirement.
