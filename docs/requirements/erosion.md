# Stage 2 requirements — erosion

Stage 2 takes a settled Stage 1 base-topology elevation grid (either in memory or loaded read-only from a
`<prefix>.s1-base-height.tif` float32 GeoTIFF) and evolves it through coupled fluvial and hillslope processes
into a Stage 2 eroded elevation grid (`<prefix>.s2-eroded-height.tif`).

Implementation: [`TerrainErosion.cs`](../../src/WorldGen.Core/TerrainErosion.cs),
[`GeoTiffReader.cs`](../../src/WorldGen.Core/GeoTiffReader.cs),
[`StageArtifactPaths.cs`](../../src/WorldGen.Core/StageArtifactPaths.cs),
[`ArtifactExporter.cs`](../../src/WorldGen.Core/ArtifactExporter.cs).
Design: [erosion](../design/erosion.md), [ADR-013](../design/decisions.md#adr-013--stage-qualified-immutable-elevation-artifacts),
[ADR-014](../design/decisions.md#adr-014--coupled-stream-power-sediment-transport-and-hillslope-diffusion).

## ER-1 — Unquantized float32 elevations and source immutability

Erosion **must** operate on `float32` elevations in meters relative to sea level, without quantizing to
integer formats between iterations or stages ([DF-2](data-formats.md#df-2--precision-and-canonical-store)).
Furthermore, the Stage 1 source `Heightmap` and source `.s1-base-height.tif` file **must never be modified
in place**; Stage 2 produces a new `Heightmap` and writes a distinct stage-tagged `.s2-eroded-height.tif`
artifact set ([DF-3](data-formats.md#df-3--artifacts)).

*Acceptance:*
- The source `.s1-base-height.tif` SHA-256 hash is unchanged before and after running `--erode-from`.
- `StageArtifactPaths.EnsureNotOverwritingSource` rejects any attempt to write Stage 2 output over the
  Stage 1 file path.
- The numeric diagnostics confirm `.s2-eroded-height.tif` is single-band `float32` with finite samples
  (the old Python validator is legacy and unsupported; see [no-python.md](../design/no-python.md)).

## ER-2 — Coupled hillslope diffusion, stream-power incision, and spatial variation

Erosion **must** combine:
- a **hillslope process** (spherical-metric Laplacian creep/diffusion $\nabla \cdot (D k_e \nabla h)$);
- a **fluvial process** (stream-power incision $E = K \cdot k_e(\mathbf{d}) \cdot A^m S^n$);
- **spatially varying erodibility** $k_e(\mathbf{d})$ from geomorphic provinces
  (`ProvinceParameters.Erodibility`) and **tectonic uplift** $U(\mathbf{d})$ from active convergent/arc/rift
  boundaries (`TectonicSketch`), so that young mountain belts remain rugged while old cratons and passive
  margins become rounded.

*Acceptance:* tunable parameters (`Iterations`, `StreamPowerK`, `AreaExponentM`, `SlopeExponentN`,
`HillslopeDiffusivity`, `UpliftScale`) are exposed in `ErosionSettings`, CLI flags, and the Avalonia UI.

## ER-3 — Material tracking: incision, transport, and deposition

The solver **must** track eroded material rather than only subtracting elevation:
- compute a sediment transport capacity $C \propto K_c A^m S^{1.15}$ along the routed drainage network;
- incise bedrock/regolith when incoming sediment flux $Q_{\text{in}} < C$, adding the incised volume to the
  downstream sediment flux $Q_{\text{out}}$;
- deposit excess sediment ($\Delta d \propto \alpha_{\text{dep}}(Q_{\text{in}} - C)$) when slope drops in
  foothills, intramontane basins, and alluvial plains ($Q_{\text{in}} > C$).

*Acceptance:* both `MeanErosionMeters` and `MeanDepositionMeters` are strictly positive on land (`> 5 m`
mean incision and `> 2 m` mean deposition on the default seed). Observed on seed 1337 (`2048x1024`, 25 steps):
mean incision `27.4 m` (max `1,886 m`, `28.2%` of land incised $\ge 5\text{ m}$), mean deposition `12.8 m`
(max `607 m`).

## ER-4 — Fixed sea-level datum and coastal delta deposition

Sea level **must** stay fixed at `0 m`. Subaerial erosion operates above sea level ($h > 0$), while rivers
discharging sediment into shallow continental shelf cells ($-45\text{ m} < h \le 0\text{ m}$) **may** build
coastal deltas that prograde the shoreline above `0 m` without altering the sea-level datum.

*Acceptance:* ocean basin depths ($p_{50}$, $p_{90}$) remain invariant; `CoastlineProgradedCells > 0` where
major rivers meet shallow shelves (observed: `1,521` prograded coastal delta cells on seed 1337).

## ER-5 — Regional execution with overlap halo

When eroding a regional window (`MapView != WholePlanet`), the solver **must** support rendering and eroding
with an overlap border (`HaloCells`, default `16 px`) and cropping back to the target window so drainage
entering or exiting the region boundary does not suffer edge starvation.

*Acceptance:* implemented in `TerrainErosion.RenderAndErodeWithHalo` and configurable via `--erosion-halo`.

## ER-6 — Determinism

Given the same input `Heightmap` (or `.s1-base-height.tif`) and `ErosionSettings`, Stage 2 **must** produce
byte-identical `.s2-eroded-*` artifacts across repeated runs, including deterministic tie-breaking in
Priority-Flood depression resolution and D8/2-receiver flow routing.

*Acceptance:* repeated runs of `--erode-from` produce identical SHA-256 hashes for `.s2-eroded-height.tif`,
`.s2-eroded-height.png`, `.s2-eroded-preview.png`, and `.s2-eroded-manifest.json`.

## ER-7 — Intermediate diagnostic fields

Stage 2 **must** record intermediate fields alongside the eroded elevation grid for downstream stages
(hydrology, soils/biomes) and visual inspection:
- `ErosionDepthMeters` (cumulative bedrock incision depth, m);
- `SedimentThicknessMeters` (cumulative deposited sediment thickness, m);
- `DrainageAreaSqKm` (routed catchment area, $\text{km}^2$).

*Acceptance:* fields are populated on `ErosionResult`, summarized in `--check` and `.s2-eroded-manifest.json`,
and viewable in the Avalonia UI via the **Erosion/Deposition Map** view mode and cursor hover readout.

## ER-8 — Performance

A `2048x1024` planet **should** erode in under a minute on a desktop CPU.

*Acceptance:* 25 coupled iterations on `2048x1024` complete in `~29–35 s` in a Debug build (and `< 3 s` at
`1024x512` interactive UI resolution).
