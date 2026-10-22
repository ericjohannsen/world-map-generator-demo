# Decision records

Each record is a choice that constrains the code and the documentation. Format: status, context,
decision, consequences, alternatives, related requirements/design.

| ID | Decision | Status |
| --- | --- | --- |
| [ADR-001](#adr-001--a-spherical-planet-not-a-map) | A spherical planet, not a map | Accepted |
| [ADR-002](#adr-002--sample-noise-in-3d-on-the-unit-sphere) | Sample noise in 3D on the unit sphere | Accepted |
| [ADR-003](#adr-003--composite-layered-terrain-instead-of-one-fractal) | Composite layered terrain instead of one fractal | Accepted |
| [ADR-004](#adr-004--elevation-as-a-function-of-direction-and-ground-resolution) | Elevation as a function of direction and ground resolution | Accepted |
| [ADR-005](#adr-005--derive-sea-level-from-an-area-uniform-sample-set) | Derive sea level from an area-uniform sample set | Accepted |
| [ADR-006](#adr-006--write-png-directly-instead-of-using-an-image-library) | Write PNG directly instead of using an image library | Accepted |
| [ADR-007](#adr-007--float32-geotiff-as-the-canonical-elevation-store) | Float32 GeoTIFF as the canonical elevation store | Accepted, implemented |
| [ADR-008](#adr-008--numeric-diagnostics-as-acceptance-gates) | Numeric diagnostics as acceptance gates | Accepted |
| [ADR-009](#adr-009--hand-written-classic-geotiff-defer-bigtiff-and-tiles) | Hand-written classic GeoTIFF; defer BigTIFF and tiles | Accepted, with follow-up |
| [ADR-010](#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation) | Kinematic plate sketch, not a geodynamic simulation | Accepted, implemented |
| [ADR-011](#adr-011--blended-provinces-and-sparse-landmarks-for-variety) | Blended provinces and sparse landmarks for variety | Accepted, implemented |
| [ADR-012](#adr-012--fixed-prng-for-structural-generation) | Fixed PRNG for structural generation | Accepted, implemented |

## ADR-001 — A spherical planet, not a map

**Status:** Accepted.

**Context.** The primary use is a world map viewed at very large scale, often as a planet seen from space.
A flat map with wrapped edges produces a seam at the antimeridian and severe shape distortion near the
poles. Later, a user selects a region for a finer map, which must agree with the world map.

**Decision.** Treat the planet as a sphere. Terrain is defined over directions on the unit sphere; any
2D image is a projected sample of that field.

**Consequences.**

- No seam, no pole singularity, and no latitude-dependent shape distortion in the model.
- The display grid still has equirectangular distortion; that is a rendering/storage concern, not a
  terrain-model concern.
- Storing and viewing the result needs an explicitly geographic format (see
  [ADR-007](#adr-007--float32-geotiff-as-the-canonical-elevation-store)).

**Alternatives.** A wrapped rectangular heightmap (rejected: seam and pole distortion); a cube map
(deferred: more complex storage, better for rendering later); a true spherical mesh (rejected for stage 1:
unnecessary complexity for raster output).

**Related:** [BT-1](../requirements/base-topology.md#bt-1--spherical-domain),
[spherical-sampling.md](spherical-sampling.md).

## ADR-002 — Sample noise in 3D on the unit sphere

**Status:** Accepted.

**Context.** A 2D noise lookup parameterised by longitude/latitude has a discontinuity at ±180° and a
singularity at the poles. Projecting a flat noise image onto the sphere has the same problems.

**Decision.** Evaluate 3D gradient noise at the unit direction. Longitude and latitude exist only at the
edges of the system (CLI, statistics, file metadata).

**Consequences.**

- The field is intrinsically seamless and pole-free.
- Terrain cost is 3D noise cost.
- Domain warping must renormalise back to the sphere, otherwise the field leaves the surface.

**Alternatives.** 2D noise with a longitude blend (rejected: seams are hard to hide at all zooms);
spherical harmonics (rejected: wrong character and hard to art-direct); procedural mesh noise (rejected:
does not fit a raster pipeline).

**Related:** [noise.md](noise.md), [BT-1](../requirements/base-topology.md#bt-1--spherical-domain).

## ADR-003 — Composite layered terrain instead of one fractal

**Status:** Accepted.

**Context.** A single fBm field produces plausible "noise mountains" but no continents, shelves, belts, or
hierarchy of landforms. At world scale it reads as texture, not geography.

**Decision.** Compose several fields with different roles: warped continentalness, tectonic boundary masks
(originally a belt field; see the amendment below), ridged mountains, hills, fine detail, and a separate
ocean model with shelf, slope, abyssal hills, and ridges.

**Consequences.**

- Continents and oceans are structural, and the coastline is controlled separately from relief.
- More parameters to tune; more code than a single fractal.
- Each layer can be validated and adjusted independently.
- The model is still not geological: the belts are fields, not plate boundaries.

> **Amended by [ADR-010](#adr-010--kinematic-plate-sketch-not-a-geodynamic-simulation) and
> [ADR-011](#adr-011--blended-provinces-and-sparse-landmarks-for-variety).** The independent belt field has
> been replaced by a kinematic plate sketch, and province/landmark layers were added. The layered-composition
> decision itself stands.

**Alternatives.** Single fBm (rejected: no continents); a full tectonic simulation (rejected for stage 1:
cost and tuning burden); hand-authored continent masks (rejected: not procedural and not seed-driven).

**Related:** [terrain-model.md](terrain-model.md), [BT-4](../requirements/base-topology.md#bt-4--earth-like-hypsometry),
[BT-6](../requirements/base-topology.md#bt-6--ocean-bathymetry), [BT-7](../requirements/base-topology.md#bt-7--mountain-belts).

## ADR-004 — Elevation as a function of direction and ground resolution

**Status:** Accepted.

**Context.** Zoom must add detail without changing the world-scale shape, and no baked bitmap may be
upsampled to satisfy a regional render.

**Decision.** `Elevation(direction, metersPerPixel)` is a pure function. Each detail layer adds octaves
based on the ratio between its base wavelength and the requested pixel size; the fractal normalisation uses
the infinite geometric series so low-frequency terms do not change when octaves are added.

**Consequences.**

- Coarse and fine samples share the same low-frequency shape; the measured deviation is ~1 m.
- No precomputed world raster is needed to zoom.
- Detail is capped per layer, so very fine renders eventually stop adding octaves.
- The guarantee currently covers base topology only; erosion and hydrology must preserve it
  ([ZM-3](../requirements/pipeline.md#stage-5--regional-zoom)).

**Alternatives.** Upsample a saved world bitmap (rejected: no real detail, violates consistency); generate
each zoom independently at full detail (rejected: world-scale shape depends on resolution); store
precomputed octaves (rejected: memory and cache complexity).

**Related:** [spherical-sampling.md](spherical-sampling.md#level-of-detail),
[BT-8](../requirements/base-topology.md#bt-8--hierarchical-resolution),
[DG-1](../requirements/diagnostics.md#dg-1--hierarchical-consistency).

## ADR-005 — Derive sea level from an area-uniform sample set

**Status:** Accepted.

**Context.** A target land fraction (default 29%) must hold for the planet's surface, and coastlines must
not move between the world map and a zoomed tile. A per-image quantile would make both impossible.

**Decision.** Before rendering, evaluate the continent field at 32,768 Fibonacci-sphere points, sort, and
take the quantile corresponding to the target land fraction. That value is the fixed sea-level threshold
for the seed.

**Consequences.**

- Land fraction is area-based, not pixel-based.
- All windows and resolutions share the same coastline.
- The threshold is computed once per `PlanetTerrain` instance; the finite sample count leaves a small
  quantile error (~0.2 percentage points observed).
- Changing the target fraction changes the threshold and therefore the coasts, not just the water level.

**Alternatives.** Per-image threshold (rejected: inconsistent tiles); a fixed noise value (rejected: land
fraction would vary wildly by seed); iterative fitting on the render (rejected: slow and window-dependent).

**Related:** [BT-3](../requirements/base-topology.md#bt-3--target-land-fraction),
[terrain-model.md](terrain-model.md#sea-level-and-land-fraction).

## ADR-006 — Write PNG directly instead of using an image library

**Status:** Accepted.

**Context.** The first implementation used ImageSharp 4.1.3. Release builds emit a warning that no Six
Labors licence was found and that Release builds require a valid licence. Adding a paid dependency to a
hobby generator is not acceptable, and image libraries otherwise bring a large surface for a narrow need
(16-bit grayscale and 8-bit RGB PNG).

**Decision.** Implement the PNG encoder in `PngWriter` using only the .NET base class library and
`System.IO.Compression`. Validate it with an independently written decoder (`tools/verify_png.py`).

**Consequences.**

- Zero runtime dependencies; one binary and no licence.
- We own the format details: colour type, bit depth, filter 0, big-endian 16-bit samples, chunk CRCs.
- The writer supports only what we need; adding interlacing or palettes would be new work.
- Correctness relies on the independent validator, not on a vendor's decoder.

**Alternatives.** ImageSharp with a licence (rejected: cost); SkiaSharp (rejected: native dependency);
System.Drawing (rejected: platform-specific and no 16-bit grayscale guarantee); writing PPM/TGA (rejected:
poor tool support).

**Related:** [NFR-2](../requirements/pipeline.md#non-functional-requirements),
[output.md](output.md#16-bit-png), [verification.md](verification.md#png).

## ADR-007 — Float32 GeoTIFF as the canonical elevation store

**Status:** Accepted, implemented.

**Context.** Stage 2 (erosion) and stage 3 (hydrology) need elevations with enough precision for slope and
flow routing, and later stages need a self-describing numeric artifact. An earlier plan used
`.npy` + JSON, then 16-bit PNG as the only output. GeoTIFF is the de facto standard for georeferenced
elevation rasters and carries float32, georeferencing, and metadata in one file.

Precision matters because a 1 m error on a 1 km cell is a 0.001 slope, and plains have slopes around 1e-4;
a ±0.12 m 16-bit quantisation error is larger than the real slope and produces grid-aligned drainage.
Fixed-range 16-bit also clips or loses resolution on worlds with different relief.

**Decision.** Make a single-band **float32 GeoTIFF** the canonical numeric artifact. Keep the 16-bit PNG
as a derived display/interchange artifact and keep a small JSON manifest for non-geospatial metadata
(seed, radius, generator parameters, observed statistics). Write the GeoTIFF with a dependency-free
baseline writer validated by `tifffile`.

Georeferencing is geographic degrees, area-registered with a north-west corner tiepoint and a user-defined
spherical CRS whose semi-major axis is the planet radius. Units are metres, vertical datum sea level 0.

**Consequences.**

- One standard file replaces the invented `.npy` + metadata pair.
- Float precision is available to erosion, hydrology, and external tools; no re-quantisation between
  stages.
- GIS tools can read the window and cell size without guessing.
- A fictional planet cannot use EPSG:4326 honestly, so the CRS is user-defined and the manifest remains
  necessary for seed/provenance.
- Classic TIFF limits the file to 4 GB until BigTIFF is implemented
  ([ADR-009](#adr-009--hand-written-classic-geotiff-defer-bigtiff-and-tiles)).
- The 16-bit PNG stays useful for viewers and tabletop work, but it is explicitly not a solver input.

**Alternatives.** `.npy` + JSON (rejected: not self-describing, no GIS interop); PNG-only (rejected:
precision and range); a third-party TIFF/GDAL library (deferred: first dependency; the required subset is
small enough to own); deferring GeoTIFF until erosion (rejected: retrofitting formats later is more
expensive and leaves the precision problem in place).

**Related:** [DF-2](../requirements/data-formats.md#df-2--precision-and-canonical-store),
[output.md](output.md#geotiff), [verification.md](verification.md#geotiff-and-manifest).

## ADR-008 — Numeric diagnostics as acceptance gates

**Status:** Accepted.

**Context.** Terrain "realism" is easy to claim and hard to review. Visual inspection misses regressions in
hypsometry, coastline position, and zoom consistency, especially after parameter changes.

**Decision.** Treat specific measurements as acceptance gates: cross-resolution deviation and coast flips
(DG-1), area-weighted hypsometry percentiles, land fraction, and connected-component landmass statistics
(DG-2). Print them with `--check`; validate artifacts with independent decoders.

**Consequences.**

- Regressions show up as numbers in the run output.
- Thresholds are documented alongside observed values, so drift is visible.
- Some qualities remain unmeasured (aesthetics, hillshade quality, geological plausibility).
- The checks are manual until a regression runner exists ([DG-3](../requirements/diagnostics.md#dg-3--regression-suite)).

**Alternatives.** Golden-image comparison only (rejected: brittle and slow); manual review only (rejected:
regressions hide); unit tests on individual noise octaves (insufficient: does not test the composite field).

**Related:** [diagnostics.md](../requirements/diagnostics.md), [verification.md](verification.md).

## ADR-009 — Hand-written classic GeoTIFF; defer BigTIFF and tiles

**Status:** Accepted, with follow-up.

**Context.** ADR-007 requires float32 GeoTIFF. A third-party library would add the project's first runtime
dependency and a licence to review; the required subset is narrow (single band, float32, Deflate, strips,
geographic CRS). Classic TIFF is well specified and the existing PNG writer already provides zlib
plumbing.

**Decision.** Implement a baseline classic TIFF writer in `GeoTiffWriter`:

- little-endian, magic 42, one IFD at the end;
- float32 samples, Deflate-compressed strips of about 64 KiB;
- GeoKeys for a user-defined spherical geographic CRS, PixelIsArea, NW tiepoint;
- throw `NotSupportedException` rather than write a file that exceeds 4 GB.

Defer BigTIFF, tiled organisation, overviews, and multi-band support until a concrete need (planet-wide
high-resolution renders or streaming) forces them.

**Consequences.**

- No runtime dependency and full control over byte layout.
- The writer is small enough to audit; the independent validator checks the parts most likely to be wrong
  (byte order, strip offsets, GeoKeys, half-pixel placement).
- Files above ~4 GB are impossible; a global 1 km float32 raster is already near that limit.
- Strip-based access is less convenient than tiled TIFF for partial reads.
- BigTIFF is a contained follow-up: header/offset width changes and a wider strip-offset type, with the
  same tag structure.

**Alternatives.** Adopt a permissive TIFF library now (deferred: dependency and licence review; would
solve tiling/BigTIFF sooner); write `.npy` only (rejected in ADR-007); uncompressed TIFF (rejected: files
would be several times larger and slower to move).

**Related:** [output.md](output.md#geotiff), [DF-3](../requirements/data-formats.md#df-3--artifacts),
[ZM-5](../requirements/pipeline.md#stage-5--regional-zoom).

## ADR-010 — Kinematic plate sketch, not a geodynamic simulation

**Status:** Accepted, implemented.

**Context.** The first composite terrain used an independent warped noise field for mountain belts. It
produced plausible-looking chains, but every chain was unrelated to every other chain and to the ocean
floor. There was no answer to "why is there a mountain range here?", and mid-ocean ridges were simply the
same field crossing water. A real geodynamic simulation would supply that coherence but is far outside the
stage-1 budget: it needs mantle flow, rheology, crustal production, and a time-stepped solver, and its output
would be difficult to make deterministic and tunable.

**Decision.** Add a deliberately small **kinematic plate sketch**:

- a spherical Voronoi mosaic of seeded plates with unequal weights;
- a random Euler pole and angular rate per plate;
- per-boundary convergence/divergence/transform classification from relative velocity;
- smooth boundary masks for orogeny, arcs, trenches, rifts, rift shoulders, ocean ridges, and transforms;
- a handful of fixed hotspots with local plate velocity.

The sketch supplies structural masks only. `PlanetTerrain` converts them to metres and continues to decide
the coastline from the independent continent field.

**Consequences.**

- Mountain belts, arcs, trenches, rifts, and mid-ocean ridges are globally coherent and share a boundary
  topology.
- The model is cheap: a few dot products and one warp per sample, plus a tiny generation step.
- The result is *not* a plate reconstruction. Plates are prescribed, continents are not generated from
  plate history, and there is no isostasy or crustal budget.
- Boundary type depends on the local relative velocity, so changing plate rates changes the mix of
  collisions and rifts without moving the noise coastlines.

**Alternatives.** Keep the independent belt field (rejected: no coherence, no tectonic grain); full
mantle/plate dynamics (rejected for stage 1: cost, tuning, and determinism); hand-authored plate polygons
(rejected: not procedural); use real Earth plate data (rejected: the tool generates fictional planets).

**Related:** [terrain-variety.md](terrain-variety.md#kinematic-plate-sketch),
[BT-6](../requirements/base-topology.md#bt-6--ocean-bathymetry),
[BT-7](../requirements/base-topology.md#bt-7--mountain-belts),
[BT-13](../requirements/base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks).

## ADR-011 — Blended provinces and sparse landmarks for variety

**Status:** Accepted, implemented.

**Context.** Even with tectonic belts, a planet can feel uniform: the same ridged-noise amplitude and
wavelength everywhere, with no old cratons, high plateaus, rough volcanic regions, or isolated named
features. Adding more global octaves makes every region noisier rather than more different. The tabletop use
case also benefits from a few memorable landmarks that can be named on a map.

**Decision.** Add two layers on top of the tectonic sketch:

1. **Geomorphic provinces.** A small seeded partition with presets such as craton, plateau, highlands,
   basin, volcanic, karst, glacial, and passive margin. The two nearest provinces blend their parameter
   vectors continuously. Parameters modulate continental relief, mountain relief, hills, detail, ridge
   frequency, base offset, anisotropy, roughness, and (for stage 2) erodibility.
2. **Sparse landmarks.** Hotspot tracks, arc volcanoes, massifs, craters, and seamounts are stored as
   individual world-space objects with position, footprint, and profile. They are additive on top of the
   fields and are not regenerated per pixel from noise.

Ridged mountains are sheared along the local boundary tangent so their crests follow the belt.

**Consequences.**

- Different regions have different relief character at world scale without changing the coastline.
- Province boundaries are soft; there are no polygon seams to hide.
- Landmarks survive zoom and can later be named or exported.
- More parameters and a larger per-sample cost; the current O(N) landmark loop is acceptable for dozens of
  features but not thousands.
- The layers are art-directed structure, not a geological classification.

**Alternatives.** One global relief scale (rejected: uniform); more octaves everywhere (rejected: texture
without geography); a dense scatter of random cones (rejected: noisy and not memorable); full landscape
evolution (deferred to erosion, stage 2).

**Related:** [terrain-variety.md](terrain-variety.md#geomorphic-provinces),
[terrain-model.md](terrain-model.md#land),
[BT-13](../requirements/base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks).

## ADR-012 — Fixed PRNG for structural generation

**Status:** Accepted, implemented.

**Context.** The generator promises that a seed identifies a world: same seed and options produce
byte-identical artifacts ([NFR-8](../requirements/pipeline.md#non-functional-requirements)). Structural
placement — noise permutation tables, plate and hotspot seeding, province jitter, landmark layout — used
`System.Random`. `System.Random` is explicitly documented as not guaranteeing its seeded sequence across
runtime versions: the algorithm is an implementation detail that Microsoft has changed before (for example
in .NET 6) and may change again. A runtime upgrade would therefore silently move every mountain, province,
and island for the same seed, breaking the promise without any code change on our side. The failure is
especially bad because it is invisible until a user compares two runs made weeks apart on different
machines.

**Decision.** Replace `System.Random` in all structural generation with a small, fully specified generator,
`DeterministicRandom`, defined entirely by the arithmetic in `MathUtils.cs`:

- a 64-bit **SplitMix64** stream (fixed constants, increment-then-avalanche);
- `Next(int maxExclusive)` via Lemire's unbiased multiply-shift rejection sampling;
- `Next(int, int)` by offsetting a bounded draw;
- `NextSingle()` from the top 24 bits, matching single precision.

The generator is seeded from the world seed and the per-layer stream through `MathUtils.DeriveSeed`. It is
not cryptographic and is not used for anything security related.

**Consequences.**

- The structural realization is fixed by the source, not by the runtime, so a given seed keeps its plates,
  provinces, and landmarks across .NET versions.
- Its output changed once, when this ADR was implemented: every seed's world shifted to a new (equally
  valid) realization. This is a one-time discontinuity; from here on the sequence is frozen.
- Placement is independently verifiable: `tools/verify_png.py`-style mirror checks can reproduce the stream
  in another language. The sequence was confirmed unbiased and uniform against a Python mirror.
- Cross-platform reproducibility of the *floating-point terrain itself* still depends on the runtime and
  hardware (transcendental and fused operations are not bit-identical everywhere); this ADR only removes the
  runtime-version dependence of the *placement* stream. See [NFR-8](../requirements/pipeline.md#non-functional-requirements).

**Alternatives.** Keep `System.Random` and pin the runtime (rejected: brittle; a runtime bump would silently
change worlds, and the promise is about the seed, not the platform); a checksummed lookup table per stream
(rejected: large, opaque, and no better specified); a third-party PRNG package (rejected: adds a dependency
for something the base class library can express in a dozen lines); a cryptographic generator (rejected:
slower, unnecessary, and its guarantees are irrelevant here).

**Related:** [noise.md](noise.md#seeded-streams),
[MathUtils.cs](../../MathUtils.cs),
[NFR-8](../requirements/pipeline.md#non-functional-requirements),
[architecture.md](architecture.md#determinism).
