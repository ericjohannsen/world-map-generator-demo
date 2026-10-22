# Architecture

WorldGen is a single console application. One process, one seed, one output window per invocation. There
is no server, database, cache, or intermediate scene graph; the elevation function is evaluated directly
into a grid and the grid is written out.

## Process shape

```text
WorldGen (net11.0)
├── Program.cs                 entry point, CLI, orchestration
├── PlanetTerrain.cs           elevation model
├── TectonicSketch.cs          plate mosaic, boundary masks, hotspots
├── ProvinceField.cs           blended geomorphic parameters
├── LandmarkField.cs           sparse volcanoes, massifs, craters
├── PerlinNoise.cs             noise primitives
├── Heightmap.cs               grid, window, renderer, statistics
├── TerrainPreview.cs          colour preview
├── TerrainDiagnostics.cs      console report (--check)
├── GeoTiffWriter.cs           canonical numeric output
├── PngWriter.cs               derived image outputs
├── Manifest.cs                metadata sidecar
└── MathUtils.cs               shared math
```

All types are internal to the executable except the public building blocks; there is no plugin system or
configuration file. Options come from the command line and defaults live in code.

## Components

### `Program`

1. Parses arguments into `Options` (width, height, seed, output base, view window, `--check`).
2. Constructs `PlanetTerrain` with `TerrainSettings` (seed only; other settings use defaults).
3. Calls `HeightmapRenderer.Render`, then `ComputeStatistics` once.
4. Writes the GeoTIFF, 16-bit PNG, preview PNG, and manifest, creating parent directories.
5. Optionally prints diagnostics.
6. Returns `0` on success, `1` on a usage error.

The four artifacts are written independently but always together, so a failed run cannot leave a
half-written set behind silently; the first failing write aborts with a non-zero exit and an exception
message.

### `PlanetTerrain`

Holds the immutable settings, eight seeded noise streams, the kinematic plate sketch, the province field,
the landmark set, and one precomputed scalar: `LandThreshold`. Its single public sampling entry point is:

```csharp
float Elevation(Vector3 direction, float metersPerPixel = float.PositiveInfinity)
```

`Infinity` means "give me the fully detailed field", which is what diagnostics use. The renderer passes a
finite ground size so detail stops at the grid's Nyquist limit. Every sample also evaluates the tectonic
masks, province blend, and landmark field; those layers are resolution-independent. See
[terrain-model.md](terrain-model.md), [terrain-variety.md](terrain-variety.md), and [noise.md](noise.md).

### `HeightmapRenderer` and `Heightmap`

`MapView` is a value record describing a latitude/longitude window. `Heightmap` owns a flat
`float[]` of size `width * height`, row-major from north to south, plus the window and helpers that map
pixel indices to latitudes, longitudes, and unit directions.

`HeightmapRenderer.Render` uses `Parallel.For` over rows. Each row computes its latitude once, derives
the local ground size, and evaluates every column. Rows share only the immutable terrain object, so the
result does not depend on thread count or scheduling.

### `TerrainPreview`

Pure function from `Heightmap` to an RGB byte array. It reads neighbouring cells for a central-difference
hillshade and applies the same palette to every tile, so overlapping renders look consistent. It does not
change terrain and is not an input to later stages.

### `TerrainDiagnostics`

Reads the generated map and calls `PlanetTerrain.Elevation` again on 20,000 Fibonacci directions. It
prints consistency, hypsometry, landmass structure, and a variety report (plate/hotspot/province/landmark
counts, boundary influence, tectonic mask means, and the land area represented by each province). It
prints only; it never alters output. Landmass and variety analysis is skipped for cropped windows because
both are whole-planet properties.

### Writers

`GeoTiffWriter` and `PngWriter` are deliberately self-contained encoders. They take arrays and metadata,
perform their own compression, and throw on impossible parameters rather than writing a malformed file.
Both are validated by independent decoders in [verification.md](verification.md).

## Determinism

Determinism is a first-class requirement ([NFR-1](../requirements/pipeline.md#non-functional-requirements)),
not an accident of the current code.

Mechanisms:

- **Seeded permutation tables.** Each noise stream is derived from the world seed with an explicit
  arithmetic mix; `DeterministicRandom(seed)` (SplitMix64, fixed constants) shuffles each permutation table
  deterministically ([ADR-012](decisions.md#adr-012--fixed-prng-for-structural-generation)).
- **Seeded structural generation.** Plate seeds and Euler poles, province seeds and presets, and landmark
  placement use their own `DeterministicRandom` streams derived by `MathUtils.DeriveSeed`. The resulting
  arrays are fixed before rendering, so the per-pixel path is pure arithmetic on the direction.
- **No ambient state.** There is no wall-clock time, process ID, hash randomization, or ambient
  randomness in the terrain path. The manifest contains no timestamp.
- **No order dependence.** `Parallel.For` writes disjoint rows. All floating-point operations for a given
  pixel occur in a fixed order inside one thread.
- **Fixed string formatting.** JSON uses camelCase property names, two-space indentation, and `\n`
  newlines. Numeric output uses invariant culture (the CLI parses with `CultureInfo.InvariantCulture`).
- **File names are data.** The manifest records artifact file names; therefore two runs with the same
  `--out` base are byte-identical, while runs with different bases differ in those name fields only.

Byte-identical output is guaranteed for the same runtime and platform. Structural placement is fixed across
.NET versions by `DeterministicRandom`; cross-platform bit equality is best-effort because floating-point
math may use different instruction paths ([NFR-8](../requirements/pipeline.md#non-functional-requirements)).

## Performance

The cost is almost entirely `PlanetTerrain.Elevation`: each call evaluates a warped continent field, the
plate sketch and province blend, an O(N) landmark loop, and ridge, hill, and detail octaves. Rendering a
2048x1024 world map takes roughly 15 seconds in a Debug build on a desktop CPU; a 1024x1024 regional tile
takes about 12 seconds because it adds octaves. Compression of the GeoTIFF and PNG adds well under a second
at these sizes. The structural layers are the dominant added cost; if it matters, cache the plate and
province sample per row or add a spatial index to `LandmarkField`.

Memory is dominated by the elevation grid:

| Grid | float32 grid | Approximate run footprint |
| --- | --- | --- |
| 2048x1024 | 8 MB | tens of MB |
| 4096x4096 | 64 MB | ~150 MB |
| 8192x8192 | 256 MB | ~600 MB |

The renderer currently materialises the whole window. Tiling and streaming are required before
planet-wide high-resolution renders ([ZM-5](../requirements/pipeline.md#stage-5--regional-zoom)); the
GeoTIFF writer already emits strips, but it does not yet write a tiled TIFF.

## Concurrency

- Parallelism is limited to rows in `HeightmapRenderer.Render` and the per-pixel loops in the preview.
- A row's `metersPerPixel` is computed once, using latitude at the row centre.
- Writers and diagnostics run after rendering completes; there is no concurrent file access.
- The result must be identical with `Parallel.For` replaced by a serial loop. This is a useful debugging
  check when a rendering change is suspected of being order-dependent.

## CLI

| Option | Default | Meaning |
| --- | --- | --- |
| `--width <px>` | `2048` | Output width |
| `--height <px>` | `1024` | Output height |
| `--seed <int>` | `1337` | Planet seed |
| `--out <path>` | `world` | Output base; four suffixes are appended |
| `--lat <north> <south>` | `90 -90` | Latitude bounds in degrees |
| `--lon <west> <east>` | `-180 180` | Longitude bounds; use continuous longitudes (e.g. `170 190`) across the antimeridian |
| `--check` | off | Print consistency, hypsometry, and landmass report |
| `--help` | — | Usage |

Validation happens before any file is written: latitudes must be within ±90 with north > south, and the
longitude span must be in `(0, 360]`. Bad input prints the usage text and exits `1`.

## Error handling

| Condition | Behaviour |
| --- | --- |
| Unknown or missing argument | Usage error, exit `1` |
| Invalid window | `ArgumentOutOfRangeException` from `MapView.Validate` |
| Non-positive width/height | Usage error, exit `1` |
| Raster would exceed 4 GB | `NotSupportedException` explaining that BigTIFF is required |
| Unwritable output path | I/O exception propagates; non-zero exit |

There is no silent clamping of elevations. The 16-bit PNG clamps to its fixed window by design; the
GeoTIFF never does.

## Extension points

The pipeline is intended to grow without rewriting stage 1:

- **Erosion** consumes `Heightmap.Elevations` (float32 meters) and should produce a new float grid rather
  than editing the base field in place. `ProvinceParameters.Erodibility` is already exposed for it.
- **Hydrology** consumes the eroded grid; it needs flow direction, accumulation, and depression handling.
- **Climate** consumes eroded terrain plus latitude and should write separate temperature/moisture/biome
  fields.
- **Regional zoom** should keep using the same `PlanetTerrain.Elevation` entry point; iterative stages
  need halos or cached world-scale constraints ([ZM-3](../requirements/pipeline.md#stage-5--regional-zoom)).

When those stages arrive, `Heightmap` is the natural place to generalise: today it is both storage and
window bookkeeping. A separate `ScalarField` type (grid + window + units) would let erosion, hydrology,
and climate share the same file and metadata machinery.

## Known architectural limitations

- **Equirectangular only.** Windows are lat/lon rectangles; there is no projection layer.
- **Classic TIFF only.** Files must stay below 4 GB; no BigTIFF, tiles, or overviews yet.
- **No reader.** The tool writes GeoTIFF/PNG but cannot load them back; every run regenerates from the
  seed.
- **Whole-window memory.** Very large regions are limited by RAM, not by disk streaming.
- **No test project.** Verification is scripted externally (Python) rather than run by `dotnet test`.
- **Kinematic plate sketch only.** Boundary masks are coherent, but plate motion is prescribed and
  continents are not generated from plate history ([ADR-010](decisions.md#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation)).
- **Landmarks are unindexed.** `LandmarkField.Elevation` loops every feature; a world with thousands of
  landmarks would need a spatial index.
