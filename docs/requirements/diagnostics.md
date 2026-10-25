# Diagnostics and acceptance

Quality is judged with numbers before images. The generator prints a report with `--check`, and both the
PNG and GeoTIFF encoders are validated by independently written decoders. This exists because "it looks
fine" does not survive changes to octave counts, thresholds, or erosion parameters.

## DG-1 — Hierarchical consistency

**Requirement.** For the same direction, sampling at world-map resolution and at 64x finer resolution
must agree wherever both resolutions can represent the same feature. Differences may come only from
detail octaves that the coarse sample cannot represent.

**Method.** 20,000 directions from a Fibonacci sphere, evaluated at
`metersPerPixel` and `metersPerPixel / 64`; report maximum absolute deviation, RMS, and the share of
samples whose land/ocean classification flips. See
[`TerrainDiagnostics.PrintConsistency`../../src/WorldGen.Core/TerrainDiagnostics.cs.

**Threshold.** Max deviation ≤ 100 m (the detail layer's full amplitude is 70 m), RMS ≤ 25 m, coastline
flips ≤ 0.1%.

**Observed** (2048x1024 world, 19.5 km/px vs 305 m/px): max 1 m, RMS 0 m, coastline flips 0.000%.
Regional tile (3.8 km/px vs 59 m/px): max 0 m, flips 0.000%. The structural layers (plates, provinces,
landmarks) contribute exactly zero deviation because they do not depend on `metersPerPixel`.

## DG-2 — Terrain statistics

**Requirement.** Every full-planet run reports, area-weighted (cosine of latitude):

- land fraction (surface area, not pixel count);
- land elevation median, p90, p99, and maximum;
- ocean depth median and p90 (the shallow tenth);
- landmass connected components at 1° resolution, with the largest areas and their centroids.

Bands and observed values are in [BT-4](base-topology.md#bt-4--earth-like-hypsometry) and
[BT-5](base-topology.md#bt-5--landmass-distribution).

**Why area weighting.** An equirectangular grid oversamples the poles by up to a factor of ~1/cos(lat).
Counting pixels would overstate polar land and ice and is the reason an earlier build reported 25.5%
land against a 29% target.

**Why connected components.** "Looks like continents" is measured as a small number of large landmasses
with a reasonable size distribution, not as a vague impression.

## DG-4 — Variety report

**Requirement.** Every full-planet `--check` run **must** report the structural makeup of the world, so
that "every planet looks the same" is visible as a number rather than a feeling:

- plate, hotspot, province, and landmark counts;
- the share of the surface within a plate-boundary influence of 0.5, and the mean of the tectonic masks
  (orogeny, arc, rift, trench, ocean ridge, transform);
- landmark counts by kind (volcano, caldera, massif, crater, seamount);
- the land area represented by each province kind, so a dominant or missing province is visible.

**Method.** Sample 20,000 Fibonacci directions; evaluate the elevation and the tectonic sample at each;
attribute land samples to the dominant province. See
[`TerrainDiagnostics.PrintVariety`../../src/WorldGen.Core/TerrainDiagnostics.cs.

**Acceptance bands** are in [BT-13](base-topology.md#bt-13--tectonic-structure-provinces-and-landmarks):
≥ 5 plates, ≥ 2 hotspots, ≥ 4 provinces, ≥ 10 landmarks; ≥ 10% of the surface near a boundary; at least
three masks with a non-zero mean; ≥ 4 province kinds on land with no kind above 60%.

**Observed** (seed 1337): 13 plates, 5 hotspots, 9 provinces, 52 landmarks; 25.2% near a boundary; mean
orogeny 0.044, arc 0.026, rift 0.021; 8 province kinds on land, largest share 21% (Karst).

**Why.** The variety layers are art-directed structure. Without a report, a tuning change can silently
collapse every region to one province preset or leave a boundary type unused, and the world would still
"look fine" in a thumbnail.

## DG-3 — Regression suite

**Requirement (planned).** A scripted test run **should** check, per seed:

1. determinism: repeated runs produce identical SHA-256 hashes for all four artifacts;
2. PNG validity: signature, chunk CRCs, inflated sizes, and row filters are checked by a decoder;
3. GeoTIFF validity: dtype, band count, GeoKeys, tiepoint, manifest agreement, and the PNG cross-check;
4. statistics: land fraction, hypsometry percentiles, and landmass counts inside the documented bands;
5. hierarchy: DG-1 thresholds;
6. variety: DG-4 counts and province distribution inside the documented bands;
7. no non-finite elevations and no ocean above sea level (and vice versa at the coastline).

**Status.** Checks exist and are run manually; there is no automated suite, no CI, and no golden-image
comparison yet. Golden hashes for a few seeds should be added when the generator stabilises, with
tolerance bands rather than exact pixels for statistics. The original independent decoders were Python
scripts; they are legacy and unsupported ([no-python.md](../design/no-python.md)), and a C# validator is the
planned replacement.

## Commands

```powershell
# World map with full report
dotnet run -- --out output/world --check

# Regional tile with report
dotnet run -- --out output/tropico --width 1024 --height 1024 --lat 35 5 --lon 120 155 --check

# Determinism (same output base)
dotnet run -- --out output/world
```

The legacy Python validators are unsupported and must not be used ([no-python.md](../design/no-python.md)).

## What these checks do not cover

- Visual aesthetics, palette choice, and hillshade quality; those need eyes on the preview.
- Whether the plate sketch is *geologically* plausible; DG-4 checks that the structure is present and
  varied, not that it is physically consistent.
- Biome correctness — `--check` reports erosion, climate, and hydrology statistics, but it does not yet inspect
  the Stage 5 biome grid ([roadmap](pipeline.md#stages)).
- Cross-platform float reproducibility ([NFR-8](pipeline.md#non-functional-requirements)).
- Whether a GIS program places the raster on the globe correctly; the GeoKeys and tiepoint are asserted,
  but no GDAL/QGIS round-trip is automated.
- Precision loss in the derived 16-bit PNG. The canonical float32 GeoTIFF in
  [DF-2](data-formats.md#df-2--precision-and-canonical-store) removes that class of problem from the
  numeric path.
