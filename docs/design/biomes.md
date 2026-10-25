# Stage 5 design — biomes

Stage 5 turns the climate and hydrology fields into a categorical biome map. It is deliberately thin: the
interesting physics already happened in Stage 3 (temperature, precipitation, evapotranspiration) and Stage 4
(water state, lakes, rivers). Stage 5 is a deterministic, configurable classification plus a small amount of
smoothing, and it writes a self-contained biome raster that can be rendered in more than one style.

Requirements: [requirements/biomes.md](../requirements/biomes.md) (`BI-1`…`BI-6`).
Decisions: [ADR-021](decisions.md#adr-021--configurable-biome-sets-with-reserved-water-ids),
[ADR-022](decisions.md#adr-022--deterministic-sphere-noise-ecotones).

## Inputs

| Field | Source |
| --- | --- |
| `TemperatureCelsius` | `HydrologyResult.Climate` (Stage 3) |
| `PrecipitationMmPerYear` | Stage 3 |
| `PotentialEvaporationMmPerYear` | Stage 3 |
| `HydrologicalHeightmap` | Stage 4 (conditioned, lake-levelled surface) |
| `WaterMask`, `LakeId`, `RiverOrder`, `DischargeM3s` | Stage 4 |

Elevation is taken from the **Stage 4 hydrological heightmap**, so the biome map agrees with the terrain the user
sees in the Stage 4 view and the 3D preview. Stage 4 already preserves the unmodified Stage 2 eroded map as
`Source`; nothing earlier is mutated.

## Derived indices

Classification does not read raw precipitation. It builds a small **named index vector** per cell:

- **Moisture index** `MI = P / (P + PET)` in `(0, 1)` — an aridity measure that is independent of the arbitrary
  millimetre scale. `P ≈ 250`, `PET ≈ 1000` gives `0.2` (semi-arid); `P ≈ 2000`, `PET ≈ 600` gives `0.77`.
- **Riparian bonus** `RiparianStrength · exp(−distWaterCells / RiparianScaleCells)`, where `distWaterCells` is a
  multi-source breadth-first distance to the nearest river or lake cell. This is what makes banks and floodplains
  green in dry country (BI-3).
- **Coastal humidity bonus** `CoastalHumidityStrength · exp(−distOceanCells / CoastalScaleCells)`.
- **Coastal temperature moderation**: coastal cells are pulled a small, configurable fraction toward the
  latitude's zonal sea-level temperature, so maritime coasts are less extreme than continental interiors. The
  mean-annual signal is mostly latitude-driven, so this term is intentionally subtle.

```
moisture = clamp(MI + riparian + coastal, 0, 1)
temp     = TemperatureCelsius + coastalModeration
```

Distances are computed once per map with a multi-source BFS over the four-connected grid, wrapping longitude when
the view spans 360°. The result is deterministic and cheap (`O(cells)`).

The vector is named rather than positional: today it carries `temperatureC`, `moisture`, and `elevationM`; a
seasonal model would populate `temperatureSeasonalityC`, `precipitationSeasonality`, `summerMoisture`, and
`winterMoisture` later without changing the table structure (BI-5).

## Classification

Rules live in an embedded `biomes.json` and are matched by descending priority, then file order:

```json
{ "id": 16, "key": "ice_sheet", "name": "Ice sheet", "priority": 100,
  "when": { "temperatureC": { "max": -6 } },
  "color": "#eaf2f8" }
```

- Bounds are inclusive; a rule matches when every referenced index is within its `min`/`max`.
- A rule may reference any subset of named indices; absent dimensions are ignored.
- Water cells bypass the land rules entirely (see below).
- A validation pass flags any land cell that matches no rule as `unclassified`; the tests assert this never
  happens for the shipped set.

The default `whittaker` set is the mean-annual projection of the familiar temperature × moisture diagram:

| Band | Temperature | Classes (moisture splits) |
| --- | --- | --- |
| Polar | `≤ −6 °C` | ice sheet |
| Cold | `−6…2 °C` | tundra |
| Boreal | `2…8 °C` | taiga / cold desert (`≈0.35`) |
| Temperate | `8…18 °C` | temperate rainforest / deciduous forest / grassland / shrubland (`≈0.72 / 0.45 / 0.25`) |
| Tropical | `≥ 18 °C` | tropical rainforest / seasonal forest / savanna / hot desert (same splits) |
| Override | elevation `≥ 2600 m` | alpine |
| Override | very high moisture, low elevation | wetland |

## Water

Water is represented **inside the biome-id grid** with a fixed, reserved low-id block, so the biome raster is
self-contained and every pixel has one palette colour:

| Id | Meaning |
| --- | --- |
| `0` | unclassified (sentinel; should never appear) |
| `1` | ocean |
| `2` | lake |
| `3` | river |
| `4` | sea ice (polar water) |
| `5…15` | reserved for future water/special classes |
| `16…` | land biomes |

Water ids are a **deterministic projection** of Stage 4's `WaterMask`, so Stage 4 remains the source of truth in
the pipeline. The reserved block is stable across every biome set, which keeps palettes compatible and lets a
science-fiction set re-colour water without re-declaring it. Consumers that need river detail (order, discharge,
width) still read Stage 4.

## Ecotones

A crisp table lookup produces blocky, latitude-banded boundaries. Two deterministic steps soften them (BI-4,
BI-6):

1. **Sphere-noise modulation.** A fractal Perlin field is sampled at the unit direction (ADR-002), seeded from
   the world seed, with a configurable wavelength (`≈250–400 km`). Its value perturbs the lookup inputs:
   `temperatureC + EcotoneTempAmplitudeC · n` and `moisture + EcotoneMoistureAmplitude · n`. Because it is a
   function of direction, the boundaries are seamless and pole-free, and a regional tile sees the same field as
   the world map.
2. **Light majority filter.** `SmoothingPasses` (default 1) of a radius-`SmoothingRadiusCells` (default 1) mode
   filter remove isolated single-cell biomes. Ties resolve to the lowest id, so the filter is deterministic.
   Water ids are never changed, and the halo for a regional tile is `passes × radius` cells.

Amplitude `0` and passes `0` reproduce the pure lookup exactly.

## Palette

The biome **id grid is canonical**. A palette maps `id → key → name → colour` and may offer several named styles
(for example `natural` and `tabletop`). Palettes affect only the rendered PNG and the 3D albedo, so they are
excluded from the stage cache key (BI-2). The same classification can therefore be re-rendered without recompute.

## Artifacts

| File | Content |
| --- | --- |
| `.s5-biome-height.tif` | Stage 4 terrain used for classification (float32 reference) |
| `.s5-biome-ids.tif` | Biome ids as float32 (exact for the id range; the GeoTIFF writer is float32-only today) |
| `.s5-biome-ids.png` | 8-bit RGB categorical map from the default palette |
| `.s5-biome-preview.png` | Biome colours over hillshaded terrain |
| `.s5-biome-palette.json` | Palette(s) used, id → key → name → hex |
| `.s5-biome-manifest.json` | Manifest plus a biome report (set id/version, counts, area fractions, ecotone params) |

A categorical byte/int16 GeoTIFF writer is a possible later optimisation; float32 keeps the id raster exact and
avoids touching the hand-written writer.

## Cache and versions

- `ModelVersions.Biomes = 1`; an `s5` entry in `models.json` (dev).
- The stage key is `StageKeys.Biomes(s4Key, BiomeSettings)` and therefore transitively pins Stage 1–4.
- `BiomeSettings` holds `BiomeSetId`/`BiomeSetVersion` plus the scalar knobs above; all are primitives, so the
  reflected key hashing is unchanged.
- The bundle stores `ByteArrays["biomeId"]` and a diagnostic `FloatArrays["moisture"]`; metrics record the
  per-biome cell counts.

## Regional scale

Biome classification is a pure per-cell function of the Stage 3/4 fields, so a regional biome tile is exactly as
consistent as the underlying climate and hydrology. Full regional consistency is the unsolved `ZM-3`; the
smoothing is written to be halo-aware so it does not add a new inconsistency at tile edges. This stage claims
world-scale correctness only.

## Testing

- **Determinism:** identical inputs → byte-identical grid.
- **Coverage:** no `unclassified` land cells across several seeds.
- **Water:** ocean/lake/river ids equal Stage 4's `WaterMask`.
- **Riparian:** cells adjacent to water are wetter than equivalent cells away from it.
- **Ecotone:** amplitude `0` equals the pure lookup; amplitude `> 0` changes some cells; the field is continuous
  across longitude wrap.
- **Smoothing:** isolated single-cell biomes decrease; the filter is deterministic.
- **Cache:** `CreateStage5 → Save → Load` reproduces the grid.
- **Palette independence:** changing the palette changes neither the grid nor the stage key.
