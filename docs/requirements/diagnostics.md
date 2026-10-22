# Diagnostics and acceptance

Quality is judged with numbers before images. The generator prints a report with `--check`, and the PNG
encoder is validated by an independent decoder. This exists because "it looks fine" does not survive
changes to octave counts, thresholds, or erosion parameters.

## DG-1 — Hierarchical consistency

**Requirement.** For the same direction, sampling at world-map resolution and at 64x finer resolution
must agree wherever both resolutions can represent the same feature. Differences may come only from
detail octaves that the coarse sample cannot represent.

**Method.** 20,000 directions from a Fibonacci sphere, evaluated at
`metersPerPixel` and `metersPerPixel / 64`; report maximum absolute deviation, RMS, and the share of
samples whose land/ocean classification flips. See
[`TerrainDiagnostics.PrintConsistency`](../../TerrainDiagnostics.cs).

**Threshold.** Max deviation ≤ 100 m (the detail layer's full amplitude is 70 m), RMS ≤ 25 m, coastline
flips ≤ 0.1%.

**Observed** (2048x1024 world, 19.5 km/px vs 305 m/px): max 1 m, RMS 0 m, coastline flips 0.000%.
Regional tile (3.8 km/px vs 59 m/px): max 0 m, flips 0.000%.

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

## DG-3 — Regression suite

**Requirement (planned).** A scripted test run **should** check, per seed:

1. determinism: repeated runs produce identical SHA-256 hashes;
2. PNG validity: `tools/verify_png.py` succeeds, including CRCs and inflated sizes;
3. statistics: land fraction, hypsometry percentiles, and landmass counts inside the documented bands;
4. hierarchy: DG-1 thresholds;
5. no non-finite elevations and no ocean above sea level (and vice versa at the coastline).

**Status.** Checks exist and are run manually; there is no automated suite, no CI, and no golden-image
comparison yet. Golden hashes for a few seeds should be added when the generator stabilises, with
tolerance bands rather than exact pixels for statistics.

## Commands

```powershell
# World map with full report
dotnet run -- --check

# Regional tile with report
dotnet run -- --out tropico --width 1024 --height 1024 --lat 35 5 --lon 120 155 --check

# Independent validation of the saved files
python tools/verify_png.py world-height.png --preview world-preview.png

# Determinism
dotnet run -- --out repeat
(Get-FileHash world-height.png).Hash -eq (Get-FileHash repeat-height.png).Hash
```

## What these checks do not cover

- Visual aesthetics, palette choice, and hillshade quality; those need eyes on the preview.
- Erosion, hydrology, and biome correctness — those stages do not exist yet
  ([roadmap](pipeline.md#stages)).
- Cross-platform float reproducibility ([NFR-8](pipeline.md#non-functional-requirements)).
- Precision loss introduced by the fixed 16-bit range; the float32 store in
  [DF-2](data-formats.md#df-2--precision-and-canonical-store) is intended to remove that class of
  problem from the numeric path.
