# Stage 5 requirements — biomes

Stage 5 consumes a settled Stage 4 hydrology result (which itself carries the Stage 3 climate fields and the
Stage 2 eroded terrain) and classifies every cell into a **biome** (`<prefix>.s5-biome-*`). Classification is a
deterministic lookup over climate, elevation, and water state, driven by a **named, versioned biome set** rather
than hard-coded colours. The biome id grid is the canonical output; colour is a separate palette so one planet
can be rendered in several styles.

Implementation: [`TerrainBiomes.cs`](../../src/WorldGen.Core/TerrainBiomes.cs),
[`BiomeTable.cs`](../../src/WorldGen.Core/BiomeTable.cs),
[`biomes.json`](../../src/WorldGen.Core/biomes.json),
[`ArtifactExporter.cs`](../../src/WorldGen.Core/ArtifactExporter.cs),
[`TerrainPreview.cs`](../../src/WorldGen.Core/TerrainPreview.cs).
Design: [biomes](../design/biomes.md),
[ADR-021](../design/decisions.md#adr-021--configurable-biome-sets-with-reserved-water-ids),
[ADR-022](../design/decisions.md#adr-022--deterministic-sphere-noise-ecotones).

## BI-1 — Deterministic lookup over climate, elevation, and water

Biome classification **must** be a deterministic lookup over Stage 3 temperature and precipitation/aridity, the
elevation of the Stage 4 hydrological heightmap, and Stage 4 water state. It **must** be driven by a configurable
rule table rather than hard-coded colours or per-cell conditionals. The lookup **must** be a pure function of the
inputs: the same seed, settings, and upstream stages produce a byte-identical biome grid.

*Acceptance:*
- Two runs with identical inputs produce identical `biomeId` arrays.
- No cell is left `unclassified` on land across a sample of seeds.
- The default `whittaker` set contains no colours in code; every biome's colour comes from the set/palette.

## BI-2 — Separate biome id and palette

Outputs **must** separate the biome id (data) from the palette (presentation), so that the same planet can be
rendered in different visual styles without reclassification. The palette **must not** participate in the stage
cache key: changing a palette **must not** change the biome grid or invalidate the cached classification.

*Acceptance:*
- Changing the palette reproduces the same `biomeId` array and the same stage key.
- The palette is exported as `<prefix>.s5-biome-palette.json` and is sufficient to render the id raster.

## BI-3 — Riparian/wetland moisture and coastal moderation

Stage 4 lakes and rivers **must** increase local moisture so riparian and wetland biomes appear along water, and
coastlines **must** moderate temperature and humidity where supported. These effects **must** decay with distance
and **must** be configurable.

*Acceptance:*
- Land cells adjacent to a river or lake are wetter, on average, than the same climate away from water.
- Coastal cells are less arid and less thermally extreme than continental interiors at the same latitude.
- Setting the riparian and coastal strengths to zero reproduces the raw `P / (P + PET)` classification.

## BI-4 — Smooth boundaries at world scale

Biome boundaries **should** be relatively smooth at world scale while remaining correct at regional scale. The
ecotone modulation **must** be a deterministic function of direction (so it is seamless and pole-free), and the
optional de-speckling filter **must** be deterministic with a stable tie-break. Both **must** be configurable and
**must** be able to be disabled.

*Acceptance:*
- With ecotone amplitude `0` and smoothing `0`, the grid equals the pure table lookup.
- With the defaults, isolated single-cell biomes are reduced relative to the pure lookup.
- The ecotone field has no antimeridian or polar discontinuity.

## BI-5 — Configurable, versioned biome sets

Biome classification **must** be driven by a named biome set from an embedded, schema-versioned `biomes.json`,
selected by `BiomeSetId`/`BiomeSetVersion`. Adding a set (for example a science-fiction set) **must not** require
classifier code changes. The set id and version **must** participate in the stage cache key and **must** be
recorded in the manifest. Rules **must** be expressed as bounds over a **named index vector**, so new climate
dimensions (for example seasonality) can be added later without restructuring the table or the classifier.

*Acceptance:*
- Selecting a different set id/version produces a different stage key and a different classification.
- A rule may reference any subset of named indices; unknown or unpopulated indices are ignored.
- Water classes are shared across sets and are not re-declared per set.

## BI-6 — Deterministic sphere-noise ecotones

Biome boundaries **must** be modulated by a deterministic fractal-noise field sampled on the unit sphere
(direction-function), so that ecotones are organic, seamless, and consistent at every resolution. The field
**must** be seeded deterministically (world seed) and **must** have a configurable amplitude and wavelength.

*Acceptance:*
- The same world seed and settings reproduce the same boundary modulation.
- The modulation is continuous across the antimeridian and at the poles.
- Amplitude `0` disables the modulation exactly.

## Artifacts

Stage 5 **must** preserve all earlier stage files unmodified and write:

- `<prefix>.s5-biome-height.tif` (float32 reference elevation used for classification);
- `<prefix>.s5-biome-ids.tif` (float32 raster of biome ids, exact for the id range);
- `<prefix>.s5-biome-ids.png` (8-bit RGB categorical map from the default palette);
- `<prefix>.s5-biome-preview.png` (8-bit RGB biome colours over hillshaded terrain);
- `<prefix>.s5-biome-palette.json` (id → key → name → colour for every named style);
- `<prefix>.s5-biome-manifest.json` (`"stage": "biomes"` with a biome report).
