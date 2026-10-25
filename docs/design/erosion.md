# Erosion model (Stage 2)

Stage 2 (`TerrainErosion`) evolves a Stage 1 base-topology `Heightmap` through coupled fluvial and hillslope
processes on the sphere. It consumes either an in-memory `Heightmap` or a saved `<prefix>.s1-base-height.tif`
loaded read-only via `GeoTiffReader`, and outputs a new `ErosionResult` written to `<prefix>.s2-eroded-*`.

## Immutability and stage-tagged artifacts

The input `Heightmap` and source `.s1-base-height.tif` are never modified in place
([ADR-013](decisions.md#adr-013--stage-qualified-immutable-elevation-artifacts)). `StageArtifactPaths` tags
every artifact quartet with its pipeline stage:

- Stage 1: `<prefix>.s1-base-height.tif`, `-height.png`, `-preview.png`, `-manifest.json`
- Stage 2: `<prefix>.s2-eroded-height.tif`, `-height.png`, `-preview.png`, `-manifest.json`

The Stage 2 manifest records `"stage": "erosion"`, `"sourceHeightGeoTiff": "<name>.s1-base-height.tif"`, and
an `"erosion"` block containing all solver parameters and observed incision/deposition statistics.

## Spherical grid metrics

On an equirectangular grid covering $(\phi_{\text{south}}..\phi_{\text{north}}, \lambda_{\text{west}}..\lambda_{\text{east}})$,
east-west cell spacing shrinks toward the poles as $\cos\phi$:

$$\Delta y = R \Delta\phi, \qquad \Delta x(y) = \max\!\big(200\text{ m},\; R \cos\phi_y \,\Delta\lambda\big), \qquad A_{\text{cell}}(y) = \Delta x(y)\,\Delta y$$

All slopes, Laplacians, catchment areas, and area-weighted statistics use $\Delta x(y)$, $\Delta y$, and
$\cos\phi_y$, and horizontal neighbors wrap seamlessly across the antimeridian when the view spans $360^\circ$
of longitude.

## Solver loop

Each call to `TerrainErosion.Erode` runs $N$ iterations (`Iterations`, default `25`) of three coupled steps:

### 1. Priority-Flood depression resolution and topological ordering

Raw procedural terrain contains closed basins and local noise pits where naive steepest-descent routing would
terminate after a few pixels. Every 5 iterations (and on the first and last iteration), `ResolveDepressions`
runs the Barnes et al. (2014) **Priority-Flood** algorithm:

1. All ocean cells ($h \le 0$) and non-wrapping map borders form the initial boundary; coastal land cells
   adjacent to the ocean are pushed into a deterministic binary min-heap keyed by `(elevation, cellIndex)`.
2. Cells are popped in non-decreasing spill-elevation order. When a neighbor $n$ lies below the current spill
   elevation $z_{\text{spill}}(c)$, its routing spill elevation is raised to $z_{\text{spill}}(c) + \epsilon$
   and it enters a FIFO flat queue, establishing a guaranteed drainage path out of every closed basin.
3. Only subaerial cells ($h > 0$) are appended to `order[]`, so the ~71% of the planet covered by ocean is
   skipped completely during the per-iteration routing and fluvial passes.
4. Iterating `order[]` in reverse visits every land cell in **strict upstream-to-downstream topological
   order** (`rank[receiver] < rank[donor]`).

### 2. Flow routing and drainage accumulation

In upstream-to-downstream order, each land cell $u$ inspects its 8 neighbors (in fixed clockwise D8 order)
among cells with `rank[n] < rank[u]`:

- If downslope neighbors ($h_n < h_u$) exist, flow is partitioned between the steepest neighbor $r_1$ and
  (when within 35% of the primary slope) the second-steepest neighbor $r_2$ proportional to slope. This
  2-receiver partitioning prevents 45°/90° grid-locked incisions while preserving single-pass DAG ordering.
- If $u$ sits inside a flat or depression with no lower neighbor on $h$, it routes along its Priority-Flood
  spill receiver `spillReceiver[u]`.
- Catchment area $A_u$ (in $\text{km}^2$) accumulates downstream into $r_1$ (and $r_2$).

### 3. Coupled stream-power incision, sediment transport, and deposition

For each land cell $u$ in upstream-to-downstream order:

- **Effective catchment and slope:**
  $$\tilde{A}_u = \left(\frac{A_u}{\bar{A}_{\text{cell}}}\right)^m, \qquad \tilde{S}_u = \left(\frac{S_u \sqrt{\Delta x(y) / \Delta x_{\text{ref}}}}{S_0}\right)^n$$
  where $S_0 = 2.5\text{ m/km}$ is the reference channel gradient.
- **Stream power and transport capacity:**
  $$\omega_u = K \, k_e(u) \, \tilde{A}_u \, \tilde{S}_u, \qquad C_u = K_c \, \tilde{A}_u \, \tilde{S}_u^{1.15} \, w_y$$
  where $k_e(u)$ is the blended geomorphic-province erodibility (`ProvinceParameters.Erodibility`) and $w_y = \cos\phi_y$.
- **Tectonic uplift:** Active convergent, volcanic-arc, and rift-shoulder belts receive a small uplift
  increment $\Delta t \, U(u)$ above $150\text{ m}$ elevation so young cordilleras stay sharp while old
  cratons round down.
- **Under-capacity regime ($Q_{\text{in}}(u) < C_u$, net incision):**
  Using a semi-implicit step bounded above the receiver elevation $h_{r_1}$,
  $$\Delta e = \frac{(h_u - h_{r_1}) \cdot \Delta t \, \omega_u \,(1 - Q_{\text{in}}/C_u)}{1 + \Delta t \, \omega_u \,(1 - Q_{\text{in}}/C_u)}$$
  Any previously deposited sediment at $u$ is re-entrained first; remaining incision cuts bedrock and
  increments `ErosionDepthMeters[u]`. The eroded volume is added to the outgoing sediment flux
  $Q_{\text{out}}(u) = Q_{\text{in}}(u) + \Delta e \, w_y$.
- **Over-capacity regime ($Q_{\text{in}}(u) > C_u$, net deposition):**
  When streams exit steep mountains onto gentle plains or basins, transport capacity drops below $Q_{\text{in}}$
  and excess sediment deposits:
  $$\Delta d = \alpha_{\text{dep}} \, \Delta t \, \frac{Q_{\text{in}}(u) - C_u}{w_y}$$
  incrementing `SedimentThicknessMeters[u]` and reducing $Q_{\text{out}}(u)$.
- **Coastal delta progradation:** When a river mouth discharges into a shallow shelf cell
  ($-45\text{ m} < h_{r_1} \le 0\text{ m}$), a fraction of $Q_{\text{out}}(u)$ deposits into $r_1$ (capped at
  $+1.8\text{ m}$ above sea level), allowing large rivers to build coastal deltas without changing the `0 m`
  sea-level datum.

### 4. Spherical hillslope diffusion

After the fluvial pass, a row-parallel spherical Laplacian smooths sharp interfluves and fills small
hillslope hollows:

$$\Delta h_{\text{diff}}(x,y) = \lambda \, k_e(x,y) \, \frac{\frac{h_{x-1,y} - 2h_{x,y} + h_{x+1,y}}{\Delta x(y)^2} + \frac{h_{x,y-1} - 2h_{x,y} + h_{x,y+1}}{\Delta y^2}}{2\left(\Delta x(y)^{-2} + \Delta y^{-2}\right)}$$

with $\lambda \le 0.18$ for unconditional explicit stability. Convex ridge removal ($\Delta h_{\text{diff}} < 0$)
accumulates into `ErosionDepthMeters`; concave hollow filling ($\Delta h_{\text{diff}} > 0$) accumulates into
`SedimentThicknessMeters`.
