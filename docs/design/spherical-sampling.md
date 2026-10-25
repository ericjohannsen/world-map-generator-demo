# Spherical sampling

The terrain model lives on the sphere. A heightmap is a **sample grid over a latitude/longitude
window**, produced by evaluating the model at the direction of each pixel centre. This document defines
that mapping, the resolution conventions, and how zoom stays consistent.

## Direction convention

For latitude `φ` (degrees north) and longitude `λ` (degrees east), the unit direction is

```text
x = cos φ · cos λ
y = sin φ
z = cos φ · sin λ
```

This is the convention used by [`Heightmap.DirectionAt`../../src/WorldGen.Core/Heightmap.cs, the diagnostics, and the
landmass centroid maths. `y` is the polar axis. Longitude increases toward +z.

Because the model only uses the direction, the planet radius is not part of the sampling math; it is only
needed to convert angular resolution into ground meters and to write the CRS.

## The equirectangular grid

`MapView` describes a window as `north`, `south`, `west`, `east` in degrees, with `north > south` and a
longitude span in `(0, 360]`. A `Heightmap` of size `width × height` samples **pixel centres**:

```text
latitude(y)  = north − (y + 0.5) / height · (north − south)
longitude(x) = west  + (x + 0.5) / width  · (east − west)
```

Row 0 is the northernmost row. Column 0 is the westernmost column. No sample lies exactly on a pole or on
the antimeridian when the window covers the whole planet: the first and last row centres are one half-cell
inside ±90°, and the first and last column centres are one half-cell inside ±180°.

That choice matters for file georeferencing: the raster is **area-registered** with its tiepoint on the
north-west corner, and the stored values are samples at cell centres. See
[output.md](output.md#georeferencing).

### Pixel size is anisotropic

```text
north-south:  R · radians(Δφ) / height          (constant for the window)
east-west:    R · radians(Δλ) / width · cos φ   (shrinks toward the poles)
```

For the whole planet at 2048x1024, both are 19.55 km at the equator. For the example tile
(`--lat 35 5 --lon 120 155` at 1024²), north-south is 3.26 km and east-west is 3.80 km at the equator.
The model is asked for detail using the **smaller** of the two, so a non-square window never requests
detail finer than its coarser axis can represent.

Near the poles, the east-west size approaches zero, but the renderer floors the target at 100 m. The grid
also has far more cells per unit area near the poles than an equal-area grid would; statistics compensate
with a `cos(latitude)` weight, but the rendering cost does not.

## Views and tiles

A tile is just a smaller `MapView`. There is no tile cache and no parent image: the generator evaluates
the same function at the tile's directions. Tile boundaries therefore match exactly, because both sides
compute the same direction at the shared edge (up to floating-point rounding in the direction, which is
identical for the same latitude/longitude pair).

### Longitude wrap

`MapView` requires `east > west` and a span at most 360°, so a window that crosses the antimeridian is
expressed with **continuous longitudes**:

```powershell
# 20° wide window centred on 180°E
--lon 170 190
```

This is unambiguous and avoids a special "wrap" flag. `sin`/`cos` make 190° equivalent to −170°, so the
sampling is seamless. Passing `--lon -170 170` instead means the 340°-wide window that goes the other way
around the planet; that is intentional and is not a wrap.

### Poles

A rectangular lat/lon window that reaches a pole includes rows whose centre latitude is just inside ±90°.
Those rows sample a tiny circle around the pole. The terrain function is continuous there, so there is no
singularity or pinch, but the equirectangular grid oversamples the polar cap. Area-weighted statistics
correct for this; a globe renderer will not need the correction.

### Why not sample 2D noise in image space?

That would make terrain a function of pixel coordinates, so a zoomed tile could not agree with the world
map, the antimeridian would need a special blend, and the poles would be distorted by construction.
Sampling 3D noise at unit directions removes all three problems ([ADR-002](decisions.md#adr-002--sample-noise-in-3d-on-the-unit-sphere)).
The equirectangular distortion remains in the *display grid*, not in the model.

## Level of detail

The renderer computes one `metersPerPixel` per row:

```text
mpp = max(100, min(eastWestMetersPerPixel, northSouthMetersPerPixel))
```

It is passed to `PlanetTerrain.Elevation`, where each detail layer decides how many octaves to add based
on the ratio of its base wavelength to the pixel size. The rule and its Nyquist margin are in
[noise.md](noise.md#octave-selection-and-aliasing).

This gives a monotone hierarchy:

- At 19.5 km/px, the detail and hill layers may be absent and the ridge layer uses only a few octaves.
- At 3.8 km/px, more octaves appear.
- At 60 m/px, nearly the full field is present.

Adding octaves only appends terms; it never renormalises the existing ones, so the low-frequency shape is
identical at every level. This is what makes the field *resolution-independent* in the sense required by
[BT-8](../requirements/base-topology.md#bt-8--hierarchical-resolution) and what the consistency check
measures ([DG-1](../requirements/diagnostics.md#dg-1--hierarchical-consistency)).

The plate sketch, province blend, and landmark set are evaluated directly from the direction and do not
depend on `metersPerPixel` at all. A mountain belt, rift, province boundary, or volcano therefore has the
same position and footprint on the world map and on a 3.8 km/px tile; the finer render only adds hill,
ridge, and detail octaves inside that structure. Feature density is a property of the world, not of the
render resolution.

### Resolution is not scale

Note the distinction:

- `--width` / `--height` choose how many samples the grid has.
- The window (`--lat`, `--lon`) chooses which directions are sampled.
- `metersPerPixel` is derived from both and tells the model how much detail is meaningful.

A 512×256 world map and a 4096×2048 world map show the same continents; the finer one has more texture.
A 1024×1024 tile of a 30°×35° window shows the same terrain as the corresponding part of the world map,
with extra detail.

## Georeferencing the grid

The GeoTIFF store uses area registration:

- `ModelPixelScale = (Δλ / width, Δφ / height, 0)` in degrees;
- `ModelTiepoint = (0, 0, 0, west, north, 0)`, i.e. the outer north-west corner;
- `GTRasterTypeGeoKey = 1` (`PixelIsArea`).

A reader that follows the GeoTIFF convention therefore computes the first pixel centre as
`(west + Δλ/(2·width), north − Δφ/(2·height))`, which is exactly the direction the generator sampled.
`PixelIsPoint` is deliberately not used: it invites a half-pixel shift in readers and is ambiguous for
area data. See [output.md](output.md#georeferencing).

## Future: cube-sphere and tiling

The current architecture is lat/lon because it is simple, inspectable, and directly writable as a
geographic GeoTIFF. Two limitations are known and accepted for stage 1:

- **Pole oversampling.** A cube-sphere or HEALPix grid would distribute samples evenly, at the cost of a
  more complex storage and resampling story.
- **Whole-window memory.** A planet-wide 1 km render would need ~3.3 GB of float32. Future work should
  evaluate independent tiles, write them with halo overlap, and stream them to disk
  ([ZM-5](../requirements/pipeline.md#stage-5--regional-zoom)). The GeoTIFF writer already uses strips,
  but it writes a single file per run and does not yet support tiled TIFF or BigTIFF.

A cube-sphere would not invalidate the terrain model: it would change the sampling grid and the metadata,
not the direction-based elevation function.
