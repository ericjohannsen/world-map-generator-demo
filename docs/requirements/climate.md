# Stage 3 requirements — climate and rainfall

Stage 3 consumes a settled Stage 2 eroded `Heightmap` (in memory or loaded read-only from
`<prefix>.s2-eroded-height.tif`) and derives surface temperature, zonal prevailing wind transport,
orographic precipitation, rain shadows, potential evapotranspiration, and surface water runoff
(`<prefix>.s3-climate-*`). Stage 3 runs **before** Stage 4 (Hydrology) so that lakes and rivers are driven
by regional rainfall and water balance rather than uniform geometric area.

Implementation: [`TerrainClimate.cs`](../../src/WorldGen.Core/TerrainClimate.cs),
[`ArtifactExporter.cs`](../../src/WorldGen.Core/ArtifactExporter.cs),
[`TerrainPreview.cs`](../../src/WorldGen.Core/TerrainPreview.cs).
Design: [climate](../design/climate.md),
[ADR-016](../design/decisions.md#adr-016--climate-and-orographic-rainfall-as-stage-3-before-hydrology).

## CL-1 — Latitude and elevation temperature field

Temperature **must** depend on latitude and elevation above sea level (`EquatorialTempCelsius` default `+28 °C`,
`PolarTempCelsius` default `-22 °C`, environmental lapse rate `LapseRateCPerKm` default `6.5 °C/km`),
producing polar, temperate, subtropical, and equatorial thermal bands plus cold alpine peaks.

*Acceptance:*
- Equatorial lowlands average `25..30 °C`, high-latitude polar zones fall below `-15 °C`, and mountain ranges
  cool by `6.5 °C` per kilometer of elevation.
- Observed on seed 1337 (`2048x1024`): area-weighted mean land temperature `8.7 °C`.

## CL-2 — Atmospheric circulation and maritime moisture advection

Precipitation **must** reflect planetary circulation bands and distance from upwind oceans:
- wet equatorial convergence (ITCZ, $0\text{–}12^\circ$) and mid-latitude storm tracks ($42\text{–}58^\circ$);
- dry subtropical high-pressure belts (horse latitudes, $20\text{–}33^\circ$) and polar dry zones ($>65^\circ$);
- moisture advection along prevailing winds (easterly trade winds $0\text{–}30^\circ$, westerlies $30\text{–}60^\circ$,
  polar easterlies $60\text{–}90^\circ$) with planetary Rossby-wave undulation and seamless $360^\circ$
  longitude wrap.

*Acceptance:* no antimeridian or mid-map vertical seam; global land area-weighted mean precipitation in
`650..1,150 mm/yr`, arid land (`< 250 mm/yr`) in `18..38%`, and humid land (`>= 1,000 mm/yr`) in `18..38%`.
Observed on seed 1337: land mean `926 mm/yr` (max `6,242 mm/yr`), arid `24.1%`, humid `26.4%`.

## CL-3 — Windward orographic lift and leeward rain shadows

When moist air ascends windward mountain slopes, precipitation **must** increase (`OrographicLiftStrength`),
depleting column moisture; when air descends leeward slopes, adiabatic warming **must** suppress precipitation
(`RainShadowStrength`), creating dry rain-shadow basins downwind of cordilleras.

*Acceptance:* windward mountain flanks receive significantly higher rainfall than leeward interiors at the
same latitude; 2D synoptic diffusion ensures rain shadows form smooth plumes rather than 1-pixel horizontal
streaks.

## CL-4 — Evapotranspiration and surface runoff water balance

Stage 3 **must** compute potential evapotranspiration (`PotentialEvaporationMmPerYear`) and effective surface
runoff (`RunoffMmPerYear`, $P - E$) using a nonlinear Budyko-style water balance so that arid deserts and
rain shadows generate near-zero local runoff while humid belts and alpine snowmelt zones generate high
runoff for Stage 4 hydrology.

*Acceptance:* `RunoffMmPerYear` is non-negative everywhere on land, `< 15 mm/yr` in hyper-arid deserts, and
averaged `433 mm/yr` across all land on seed 1337.

## CL-5 — Stage-tagged immutable artifacts

Stage 3 **must** preserve the Stage 2 `.s2-eroded-height.tif` file unmodified and write:
- `<prefix>.s3-climate-height.tif` (reference float32 elevation GeoTIFF);
- `<prefix>.s3-climate-rainfall.tif` (float32 GeoTIFF of annual precipitation in `mm/yr`);
- `<prefix>.s3-climate-height.png` (16-bit elevation PNG);
- `<prefix>.s3-climate-preview.png` (8-bit RGB precipitation & aridity map over hillshaded terrain);
- `<prefix>.s3-climate-manifest.json` (`"stage": "climate"`, `"sourceHeightGeoTiff"`, and `"climate"` report).
