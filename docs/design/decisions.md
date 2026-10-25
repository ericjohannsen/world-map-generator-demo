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
| [ADR-013](#adr-013--stage-qualified-immutable-elevation-artifacts) | Stage-qualified immutable elevation artifacts | Accepted, implemented |
| [ADR-014](#adr-014--coupled-stream-power-sediment-transport-and-hillslope-diffusion) | Coupled stream-power, sediment transport, and hillslope diffusion | Accepted, implemented |
| [ADR-015](#adr-015--selective-sill-breaching-and-compactness-filtered-basin-lakes) | Selective sill breaching and compactness-filtered basin lakes | Accepted, implemented |
| [ADR-016](#adr-016--climate-and-orographic-rainfall-as-stage-3-before-hydrology) | Climate and orographic rainfall as Stage 3 before Hydrology | Accepted, implemented |
| [ADR-017](#adr-017--in-window-opengl-3d-preview-with-a-cpu-raster-fallback) | In-window OpenGL 3D preview with a CPU raster fallback | Accepted, implemented |
| [ADR-018](#adr-018--split-versioned-atomic-json-settings) | Split, versioned, atomic JSON settings | Accepted, implemented |
| [ADR-019](#adr-019---per-stage-model-versions-with-a-releaseddev-registry) | Per-stage model versions with a released/dev registry | Accepted, implemented |
| [ADR-020](#adr-020---content-addressed-stage-cache-with-a-merkle-key-chain) | Content-addressed stage cache with a Merkle key chain | Accepted, implemented (named worlds deferred) |
| [ADR-021](#adr-021--configurable-biome-sets-with-reserved-water-ids) | Configurable biome sets with reserved water ids | Accepted, in progress |
| [ADR-022](#adr-022--deterministic-sphere-noise-ecotones) | Deterministic sphere-noise ecotones | Accepted, in progress |

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
- Boundary masks are modulated along strike, and trenches/ridges are gated to the abyssal ocean, so a
  boundary reads as changing relief rather than a uniform ribbon painted across a shelf.
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
[MathUtils.cs](../../src/WorldGen.Core/MathUtils.cs),
[NFR-8](../requirements/pipeline.md#non-functional-requirements),
[architecture.md](architecture.md#determinism).

## ADR-013 — Stage-qualified immutable elevation artifacts

**Status:** Accepted, implemented.

**Context.** Starting with Stage 2 (erosion), a pipeline stage can consume a previously saved float32 GeoTIFF
from an earlier stage (`--erode-from` or "Load Stage 1 .tif" in the UI). If a downstream stage overwrote its
input `.tif` or used an unqualified `<out>-height.tif` filename, the user would lose the settled Stage 1 base
map whenever they experimented with erosion parameters, and a directory of files would not indicate which
pipeline stage each raster represents.

**Decision.**
1. **Source immutability.** A source GeoTIFF (and its in-memory `Heightmap`) is strictly read-only. Stage 2
   allocates a new `Heightmap` and `StageArtifactPaths.EnsureNotOverwritingSource` throws if the target path
   matches the source path.
2. **Stage-tagged filenames.** Every stage writes its own four-file bundle with an explicit stage token:
   - Stage 1 (base topology): `<prefix>.s1-base-height.tif`, `-height.png`, `-preview.png`, `-manifest.json`
   - Stage 2 (erosion): `<prefix>.s2-eroded-height.tif`, `-height.png`, `-preview.png`, `-manifest.json`
   - Stage 3 (climate &amp; rainfall): `<prefix>.s3-climate-height.tif`, `-rainfall.tif`, `-height.png`, `-preview.png`, `-manifest.json`
   - Stage 4 (hydrology): `<prefix>.s4-hydro-height.tif`, `-height.png`, `-preview.png`, `-manifest.json`, `-vectors.json`
   - Stage 5 (biomes): `<prefix>.s5-biome-...`
3. **Manifest provenance.** Stage 2 manifests (formatVersion 3) record `"stage": "erosion"`,
   `"sourceHeightGeoTiff": "<name>.s1-base-height.tif"`, and an `"erosion"` block with all solver parameters
   and observed incision/deposition metrics.

**Consequences.**
- Users can settle on a Stage 1 `.s1-base-height.tif` and iterate on Stage 2 erosion parameters in the UI or
  CLI without ever mutating or regenerating the Stage 1 file.
- Every file on disk self-identifies its pipeline stage in both its filename and its JSON manifest.

**Related:** [DF-3](../requirements/data-formats.md#df-3--artifacts),
[ER-1](../requirements/erosion.md#er-1--unquantized-float32-elevations-and-source-immutability),
[output.md](output.md).

## ADR-014 — Coupled stream-power, sediment transport, and hillslope diffusion

**Status:** Accepted, implemented.

**Context.** Stage 2 requires an erosion solver that operates on spherical `float32` grids in meters (ER-1),
combines hillslope creep with fluvial stream-power incision modulated by spatial erodibility and uplift (ER-2),
tracks eroded sediment so foothills, basins, and coastal deltas receive deposition rather than only carving
valleys (ER-3, ER-4), runs deterministically (ER-6), and completes a `2048x1024` planet in under a minute (ER-8).

**Decision.** Implement a coupled iterative solver in `TerrainErosion`:
1. **Priority-Flood depression resolution** (Barnes et al., 2014) on subaerial cells ($h > 0$) using a
   deterministic `(elevation, cellIndex)` min-heap and FIFO flat queue, establishing a cycle-free topological
   ordering from highest divides to the coast while skipping ocean cells.
2. **2-receiver slope-partitioned D8 flow routing** on the topological DAG, accumulating cosine-latitude-weighted
   catchment area $A$ in $\text{km}^2$.
3. **Semi-implicit stream-power incision + transport capacity + deposition** in a single upstream-to-downstream
   pass: when incoming sediment flux $Q_{\text{in}} < C$, excess capacity incises bedrock (modulated by
   `ProvinceParameters.Erodibility` and countered by tectonic uplift); when $Q_{\text{in}} > C$, excess
   sediment deposits in basins and alluvial fans; at shallow shelves ($-45\text{ m} < h \le 0\text{ m}$),
   river mouths deposit coastal deltas.
4. **Spherical-metric hillslope diffusion** via a row-parallel Laplacian scaled by $\Delta x(y)^{-2}$ and
   $\Delta y^{-2}$.

**Consequences.**
- Valleys, alluvial fans, basin fills, and coastal deltas emerge self-consistently in a fast, deterministic
  solver (~30 s for 25 steps on `2048x1024`, ~2.5 s on `1024x512`).
- Intermediate fields (`ErosionDepthMeters`, `SedimentThicknessMeters`, `DrainageAreaSqKm`) are preserved for
  Stage 3 (hydrology) and Stage 4 (soils/biomes) and visualized in the UI.

**Related:** [erosion.md](erosion.md), [requirements/erosion.md](../requirements/erosion.md).

## ADR-015 — Selective sill breaching and compactness-filtered basin lakes

**Status:** Accepted, implemented.

**Context.** Stage 3 (hydrology) requires explicit depression handling (HY-1) and topographically derived
lakes with surface levels and spill points (HY-3). If every depression found by Priority-Flood is filled into
a lake, small noise pits become thousands of 1–3 pixel puddles and narrow incised valleys blocked by a single
coarse pixel become long, unnatural worm-like ribbon lakes. Conversely, if every depression is breached, the
planet has no inland lakes at all.

**Decision.** Combine **Priority-Flood depression analysis** with **selective sill breaching and compactness
filtering** in `TerrainHydrology`:
1. Group connected depression cells ($z_{\text{fill}} - h > 0.25\text{ m}$) into candidate basins and cap
   extreme tectonic sill depths at `MaxLakeDepthMeters` (140 m) via partial sill breaching.
2. Evaluate each basin on size (`CellCount >= MinLakeCells`, default 6), depth (`MaxDepth >= MinLakeDepthMeters`,
   default 3 m), and **isoperimetric compactness** ($4\pi A / P^2 \ge 0.14$).
3. Basins that fail any check are **breached** along their Priority-Flood spill path so rivers carve cleanly
   through them; basins that pass are leveled to their water surface elevation $z_{\text{lake}}$ and emitted
   as `LakeFeature` objects.
4. Route D8 flow over the conditioned surface, compute Strahler stream orders (`1..N`) and hydraulic widths
   ($W = 2.2 A^{0.42}\text{ m}$), and export both the conditioned `.s3-hydro-height.tif` raster and the
   `.s3-hydro-vectors.json` sidecar of lake metadata and river polylines.

**Consequences.**
- Genuine topographic basins form realistic lakes while minor pits and narrow ribbons are breached into
  continuous river valleys.
- Stage 4 (biomes) and Stage 6 (tabletop export) receive both per-pixel grids (`WaterMask`, `LakeId`,
  `RiverOrder`, `DrainageAreaSqKm`) and vector polylines (`Rivers`, `Lakes`).

**Related:** [hydrology.md](hydrology.md), [requirements/hydrology.md](../requirements/hydrology.md).

## ADR-016 — Climate and orographic rainfall as Stage 3 before Hydrology

**Status:** Accepted, implemented.

**Context.** In the initial roadmap, Hydrology ran immediately after Erosion assuming uniform rainfall across
every land pixel, while Climate and Biomes were grouped together as Stage 4. Visual inspection of the resulting
world map revealed two major realism problems:
1. Subtropical deserts ($20\text{–}33^\circ$) and leeward rain-shadow basins sprouted the same dense river
   networks and filled lakes as equatorial rainforests.
2. Initiating rivers from raw geometric area at a low pixel threshold spawned short parallel Order-1 hillside
   "comb teeth" along ridges and coasts, and parallel D8 paths on flat plains merged into wide blue smudges.

**Decision.**
1. **Renumber pipeline stages:** Make **Stage 3 — Climate and rainfall** (`.s3-climate`), **Stage 4 — Hydrology**
   (`.s4-hydro`), and **Stage 5 — Biomes** (`.s5-biome`).
2. **Physical moisture & runoff model (`TerrainClimate`):** Compute temperature (latitude + lapse rate),
   3-cell prevailing zonal winds (easterly trade winds, westerlies, polar easterlies) with Rossby-wave
   undulation, maritime moisture advection, windward orographic lift, leeward rain-shadow suppression,
   2D synoptic smoothing, and Budyko surface runoff $R(x, y)$ in $\text{mm/yr}$.
3. **Climate-driven hydrology (`TerrainHydrology`):** Route volumetric river discharge $Q = \sum R \cdot A_{\text{cell}}$
   (in $\text{m}^3/\text{s}$), gate closed-basin lake depth by local aridity ($\bar{P}/\overline{\text{PET}}$),
   use two-pass convergent D8 routing on plains, prune short Order-1 headwater twigs (`MinHeadwaterReachCells = 8`),
   and render rivers as crisp 1-pixel courses scaled by $\log Q$ and Strahler order.

**Consequences.**
- Deserts and leeward rain shadows are naturally dry with few or no local headwaters, while wet windward
  mountain flanks and equatorial convergence belts feed realistic dendritic river basins.
- Parallel hillside comb teeth and lowland blue smudges are eliminated.

**Related:** [climate.md](climate.md), [hydrology.md](hydrology.md),
[requirements/climate.md](../requirements/climate.md),
[requirements/hydrology.md](../requirements/hydrology.md).

## ADR-017 � In-window OpenGL 3D preview with a CPU raster fallback

**Status:** Accepted, implemented.

**Context.** The pipeline produces a planet worth inspecting in relief, but the only 3D output is
`tools/make_globe.py`: a CPU ray-sphere viewer that maps the *shaded preview image* onto a sphere. It cannot
displace terrain, cannot be lit from a 3D sun, and runs in a browser. The UI needs a view that can fly into a
mountain and stay consistent with the 2D map. Candidate substrates were a WebView with a JS renderer, a raw
child window with Silk.NET, a game engine, a first-party OpenGL control, and a pure CPU renderer.

**Decision.** Implement the preview as a **camera-following displaced-mesh view** rendered through
Avalonia's `OpenGlControlBase`, with a **software z-buffered rasteriser** of the same mesh, camera, and albedo
as an automatic fallback.

1. `TerrainMesh.Build` (Core) produces positions, normals, and UVs from the active `Heightmap` at full float
   precision; `TerrainPreview.RenderAlbedo` provides unshaded colour.
2. `Terrain3DGlControl` owns GL state, one interleaved VBO/IBO, and one albedo texture. It relies on the
   depth buffer that `OpenGlControlBaseResources` attaches to the control framebuffer.
3. `Terrain3DSoftwareRenderer` rasterises the same mesh when GL is unavailable or lost, at reduced resolution
   during interaction.
4. Mesh rebuilds run on a worker and are swapped in atomically, so camera motion is not gated on sampling.

**Why not the alternatives.**

- *WebView + three.js*: a third-party runtime (Edge WebView2 or a community control), a second language, an
  IPC path for large textures, doubled GPU memory, and an airspace surface that Avalonia cannot overlap. It
  would have been the fastest prototype and the worst thing to ship.
- *`NativeControlHost` + Silk.NET*: maximum performance, but a child HWND that breaks the visual tree
  (no overlays, manual DPI/input routing, per-platform hosts). The preview is a panel, not an application.
- *Game engine*: a large dependency and content pipeline for one viewport, against NFR-2's discipline.
- *CPU only*: zero risk and perfect integration, but it cannot hold a useful frame rate at a resolution that
  makes mountain relief legible, and it would put the feature's quality ceiling below the data's.

**Consequences.**

- No new runtime dependency; the preview works offline and deterministically (it writes nothing).
- One mesh, one camera, one albedo serve both rasterisers, so the fallback is a genuine reference rather
  than a second implementation that can drift.
- The GL path needs shader variants for GLSL ES and desktop GL, and must degrade when a machine has no
  usable GL context; the CPU path is that degradation.
- No shadows, transparency, or anti-aliasing beyond what the driver provides. These are additive later.
- Sub-cell detail is still bounded by the source map; the window-rebuild seam is where Stage 6 tiling will
  replace the single window with a tile source ([ZM-3](../requirements/pipeline.md#stage-6--regional-zoom)).

**Update — globe view.** The first implementation shipped the regional view only. A planet view was added
afterwards as a second mesh and camera (`TerrainGlobeMesh`, `Terrain3DViewMode.Globe`) that reuses the same
rasterisers, albedo texture, and 1×/10×/50× exaggeration presets. It displaces a UV sphere radially, clamps
oceans to the sea-level sphere so water reads as water, and accepts the usual UV-sphere pole pinch and a
fixed 256×128 resolution for an overview. A cube-sphere with per-face tiling remains the upgrade path.

**Related:** [preview-3d.md](preview-3d.md), [requirements/preview-3d.md](../requirements/preview-3d.md),
[requirements/pipeline.md](../requirements/pipeline.md#stage-7--presentation-and-export).

## ADR-018 � Split, versioned, atomic JSON settings

**Status:** Accepted, implemented.

**Context.** The UI persisted a single `ui-settings.json` written with `File.WriteAllText` on stage runs,
exports, and window close. It mixed durable preferences (renderer, exaggeration, default resolution) with
resumable session state (last seed and tuning, 3D cameras, whether 3D was open), had no schema version, and
could be truncated by a crash. Window geometry was not stored at all, so every launch started from the XAML
size and OS default position.

**Decision.** Replace it with two versioned JSON files under the per-user application data directory:

1. `preferences.json` � window geometry (position, normal size, maximized/normal flag), default resolution,
   renderer, exaggeration preset, and 3D view mode.
2. `session.json` � last generation parameters, export prefix, 3D open state, and both cameras.

Both are written atomically (temporary file + replace, previous copy kept as `.bak`), carry a
`schemaVersion`, are validated and clamped on load, and are saved on change through a 600 ms debounce plus a
flush on close. A corrupt file is quarantined and defaults are used. A minimized window never overwrites the
saved maximized/normal state. The old `ui-settings.json` is imported once and renamed.

**Why not one file.** Preferences and session have different lifetimes and failure costs. A reset or corrupt
session should not cost the user their renderer choice and window layout, and a future project file must be
able to own a world's parameters without inheriting the last-used ones.

**Why not the registry, INI, or SQLite.** The registry is Windows-only and invisible; INI cannot express the
nested window/camera records cleanly; SQLite is a dependency and an operational burden for a few kilobytes of
data. Indented JSON is inspectable, diffable, and already in the BCL.

**Why not save only on close.** Force-kill and crashes are normal in a tool that can run multi-minute
generation jobs. Saving on change with a debounce keeps the loss bounded to well under a second.

**Consequences.**

- Window layout and view state survive restarts, including maximized windows restoring to their previous
  normal size and off-screen rectangles being rejected.
- A bad settings file can no longer prevent startup or silently reset everything without leaving evidence.
- Settings remain strictly outside the pipeline: they are UI state, never an implicit generator input.
- Two instances still race; last writer wins, which is acceptable for a single-user desktop tool.
- A future project/world format will own terrain parameters; the settings split makes that transition
  mechanical instead of a migration of one flat blob.

**Related:** [settings.md](settings.md), [requirements/settings.md](../requirements/settings.md).

## ADR-019 - Per-stage model versions with a released/dev registry

**Status:** Accepted, implemented. All current models are `dev`.

**Context.** Terrain numbers depend on the algorithm, its constants, and the settings schema, but the project
had no record of which algorithm produced an artifact. Cached results and exports could be compared only by
seed and a handful of visible parameters. The project also needs a way to distinguish models that real users
have received from development-only models, so obsolete development variants can eventually be deleted
without touching anything a user's world references.

**Decision.** Track four identities separately:

1. a per-stage integer `modelVersion` (`s1` base topology, `s2` erosion, `s3` climate, `s4` hydrology);
2. `settingsSchemaVersion` for the settings shape and migrations;
3. `cacheSchemaVersion` for the cached bundle layout;
4. a derived result hash for integrity and duplicate detection (planned; not stored yet).

A checked-in `models.json`, embedded in Core, lists every version with `status` (`dev` or `released`),
`compatibility` (`metadata-only`, `performance-only`, `output-changing`, `schema-breaking`), a human reason,
`releasedIn`, and introduced/removed parameter names. Code constants declare the current versions;
`ModelRegistry.ValidateCurrent()` rejects a missing registry entry at startup and in the tests.

Every current model is `dev` and `releasedIn` is null. A future release pipeline flips entries to `released`
and records the release. A model bump is a new identity, not a destructive migration: old cached stages keep
their keys, and a future project file keeps referencing them.

**Why not one global pipeline version.** A hydrology tuning change would invalidate the Stage 1 cache for no
reason, and the artifact would not say which stage changed. Per-stage versions keep invalidation local.

**Why not only inferred versions.** Hashing code or assembly versions cannot express intent. A refactor that
is byte-identical should not invalidate users' worlds, while a constant change that is easy to miss must.
The registry records the *intent* and the golden-hash test proves the *claim*.

**Why JSON in the repository rather than a database or attributes.** It is diffable in review, readable by
the CLI, and visible in the same change as the code bump. A database adds an operational dependency for a
few dozen rows; attributes scatter the history across source files and cannot express `releasedIn` well.

**Consequences.**

- Cache keys include the stage model version and both schema versions. A bump creates a new variant.
- `stage.json`, the bundle header, and the public manifest record the versions.
- The cleanup policy can treat `released` and `dev` differently, and `unknown` versions are never pruned
  automatically.
- A developer must update the registry when bumping; validation catches a missing entry but not a misleading
  reason text.
- All current entries remain `dev` until the release pipeline exists, so no cleanup can assume a user-facing
  model yet.

**Related:** [model-versions.md](model-versions.md), [requirements/model-versions.md](../requirements/model-versions.md),
[artifact-cache.md](artifact-cache.md).

## ADR-020 - Content-addressed stage cache with a Merkle key chain

**Status:** Accepted, implemented (named worlds deferred).

**Context.** The UI regenerated Stage 1 on every launch even though it persisted every parameter. Exported
GeoTIFFs were the only record of a previous run, but they are user-named and incomplete: erosion depth,
sediment, climate PET/runoff, and hydrology flow/lakes are not in the public height file. Regenerating to
recover those fields was slow and made startup feel like the tool had forgotten the world.

**Decision.** Add a per-user content-addressed cache:

- root: `LocalApplicationData/WorldGen/cache` (macOS: `~/Library/Application Support/WorldGen/cache`);
- key: SHA-256 of a canonical description of stage, model version, cache/settings schemas, upstream key,
  and every setting that affects that stage; UI state is excluded;
- chain: `s1` key feeds `s2`, `s2` feeds `s3`, `s3` feeds `s4` (Merkle chain);
- entry: `sN/<key>/stage.json` plus a compressed `data.wgc` full bundle;
- index: `index.json`, derived and rebuildable by scanning `stage.json` files;
- writes: temporary file plus atomic replace; corrupt or incompatible entries behave as misses;
- startup: load Stage 1 and any cached downstream stages, stop at the first miss, and never auto-run a
  missing stage; a missing Stage 1 leaves the view empty until the user changes parameters and clicks
  Generate;
- stage actions: try the cache before computing and store the result after;
- cleanup: `--cache-list` and `--cache-prune` (dry run by default, `--cache-yes` to delete), retaining the
  current key chain, cascading to downstream dependants, pruning `dev` entries by default, and exposing
  stage/age/released filters.

The public exports remain separate and user-named. Named worlds/projects are deferred; until they exist, the
current settings implicitly define the world.

**Why full bundles, not the exported height TIFF.** The public height file is the canonical *output*, not a
resume format. Downstream stages need arrays that are intentionally not exported, and reading a user-named
TIFF back would blur the boundary between exports and working state. A bundle also carries its own model and
schema provenance.

**Why content-addressed directories, not a settings filename.** Two settings sets can produce the same
numbers, one property can be added without changing the others, and model bumps need distinct identities.
Hashing a canonical description gives all three properties and makes duplicate detection free. Reflection
over the settings record ensures a new parameter changes the key automatically.

**Why LocalAppData, not the export directory.** A cache is machine state, not a deliverable. Keeping it out
of the repository avoids accidental commits, keeps exports clean, and lets cleanup be per-user. A future
portable project can copy stage bundles explicitly.

**Why no automatic LRU eviction.** Silently deleting a world's terrain is worse than using disk. The index
reports sizes, and cleanup is explicit. Size pressure is a UI/CLI decision, not a background policy.

**Consequences.**

- Startup is fast when the chain is cached and still deterministic; cache hits do not alter results.
- Disk use grows with every settings variant and model version. `--cache-list` reports total size;
  `--cache-prune` is the deliberate release valve.
- A cache schema bump makes old bundles unreadable. They remain on disk until pruned, but are treated as
  misses; a future migration can read them if needed.
- The CLI can use the same cache (`--cache-dir`, `--no-cache`) while exporting explicitly.
- World-aware reference protection is future work. Until then, the current key chain is the only protected
  set, and users should not prune while a world they care about is not the current settings.
- No new dependency: Deflate, SHA-256, and JSON are BCL.

**Related:** [artifact-cache.md](artifact-cache.md), [requirements/cache.md](../requirements/cache.md),
[model-versions.md](model-versions.md), [ADR-019](#adr-019---per-stage-model-versions-with-a-releaseddev-registry).

## ADR-021 — Configurable biome sets with reserved water ids

**Status:** Accepted, in progress.

**Context.** Stage 5 must classify every cell into a biome from climate, elevation, and water state. The intended
use is a tabletop world generator, so the set of biomes is a presentational and thematic choice, not a fixed law of
nature: a science-fiction world wants exotic biomes and colours. At the same time the classification itself must
be deterministic, cacheable, and re-renderable in more than one style. A hard-coded `if` ladder or a hard-coded
colour array would make both impossible.

**Decision.**

- Classification is driven by a **named, versioned biome set** embedded in `biomes.json`, selected by
  `BiomeSettings.BiomeSetId` / `BiomeSetVersion`. Only `whittaker` ships now; adding a set is a data change.
- Rules are expressed as **bounds over a named index vector** (`{ "temperatureC": { "min": …, "max": … } }`) rather
  than fixed positional thresholds, so new dimensions (seasonality, monsoon index) can be added without changing
  the table structure or the classifier.
- Water is represented **inside the biome-id grid** with a fixed, reserved low-id block
  (`0` unclassified, `1` ocean, `2` lake, `3` river, `4` sea ice, `5…15` reserved, `16…` land). Water ids are a
  deterministic projection of Stage 4's `WaterMask`.
- The **palette is separate** from the id grid and is excluded from the stage cache key, so re-colouring never
  reclassifies (see BI-2).

**Consequences.**

- The same planet can be rendered in several styles with no recompute; a new theme is a JSON entry and a palette.
- The biome id raster is self-contained: one `id → colour` lookup renders land and water, which the 2D preview,
  the 3D albedo, and future consumers all share.
- Water state appears in two places (Stage 4's mask and the biome grid). This is a deliberate, generated
  projection, not duplicated authority: Stage 4 remains the pipeline source of truth, and consumers that need
  river order/discharge/width still read Stage 4.
- The reserved block constrains future water/special classes to ids `5…15`; land biomes start at `16`.
- Changing a set's rules requires bumping `BiomeSetVersion` (or the model version) so cached keys change.

**Alternatives.** Land-only biome ids plus the Stage 4 mask (rejected: every consumer joins two fields and the
export is not self-contained). In-code rules and colours (rejected: not configurable, not a "table"). Per-world
`if` chains (rejected: not deterministic to cache and impossible to theme).

**Related:** [design/biomes.md](biomes.md), [requirements/biomes.md](../requirements/biomes.md),
[ADR-019](#adr-019---per-stage-model-versions-with-a-releaseddev-registry),
[ADR-022](#adr-022--deterministic-sphere-noise-ecotones).

## ADR-022 — Deterministic sphere-noise ecotones

**Status:** Accepted, in progress.

**Context.** A pure thresholds-over-climate lookup produces blocky, latitude-banded biome boundaries with
single-cell speckle. Real ecotones are organic, and the project already treats the sphere as the model surface
(ADR-002), so a trivial raster blur is both ugly and inconsistent at the antimeridian and poles.

**Decision.** Modulate the classification inputs with a fractal Perlin field sampled at the **unit direction**
(the same technique terrain uses), seeded from the world seed, with a configurable amplitude and wavelength. The
modulation perturbs temperature and moisture before the table lookup, so boundaries wobble organically. A short
deterministic majority filter then removes isolated single-cell biomes. Both steps are configurable, can be
disabled exactly (amplitude `0` / passes `0`), and are deterministic.

**Consequences.**

- Boundaries are seamless, pole-free, and identical for a regional tile and the world map, because the field is a
  function of direction rather than of pixel coordinates. This is the same reasoning as ADR-002 for terrain.
- The classification is no longer a pure function of the cell's own climate; it additionally depends on the
  cell's direction and the world seed. This is still deterministic and still pinned by the stage key, since the
  upstream key already carries the seed.
- The majority filter is halo-dependent: a tiled regional run needs `passes × radius` cells of halo. This is
  recorded for the `ZM-3` regional-consistency work.
- Amplitude and wavelength become part of `BiomeSettings`, so they participate in the cache key.

**Alternatives.** A raster Gaussian/majority blur alone (rejected: blocky, halo-hungry, and pixel-grid-dependent).
Per-cell random jitter (rejected: not seamless, not scale-consistent). No smoothing (rejected: BI-4).

**Related:** [design/biomes.md](biomes.md), [requirements/biomes.md](../requirements/biomes.md),
[ADR-002](#adr-002--sample-noise-in-3d-on-the-unit-sphere),
[ADR-021](#adr-021--configurable-biome-sets-with-reserved-water-ids).

