# Hydrology model (Stage 4)

Stage 4 (`TerrainHydrology`) takes a Stage 2 eroded `Heightmap` and Stage 3 `ClimateResult` (`RunoffMmPerYear`,
`PrecipitationMmPerYear`, `PotentialEvaporationMmPerYear`) and resolves depressions, climate-gated lakes,
convergent D8 flow routing, volumetric river discharge, headwater twig pruning, Strahler stream orders, and
vector river polylines. It writes its outputs to `<prefix>.s4-hydro-*` without mutating earlier stage files.

## Pipeline algorithm

### 1. Priority-Flood depression analysis

Starting from all ocean cells ($h \le 0$) and non-wrapping map borders, `RunPriorityFlood` executes the
Barnes et al. (2014) Priority-Flood algorithm using a deterministic binary min-heap keyed by
`(elevation, cellIndex)` and a FIFO queue for flats, yielding minimum spill elevation $z_{\text{fill}}(u)$
and outlet path pointer `spillParent[u]` for every subaerial cell.

### 2. Climate-gated sill breaching vs. basin lake extraction

`ExtractLakesAndBreachSills` groups connected depression cells ($z_{\text{fill}}(u) - h(u) > 0.25\text{ m}$)
into candidate basins and evaluates both topographic geometry and **Stage 3 water balance**:

1. **Evaporative water-balance & polar gate:** Using the basin's mean aridity index $\text{AI} = \bar{P}/\overline{\text{PET}}$,
   arid basins ($\text{AI} \le 0.25$) cannot fill against evaporation and are breached into dry basins/wadis;
   semi-arid and high-latitude permafrost basins have their maximum sill fill depth scaled down proportionally
   from `MaxLakeDepthMeters` (default `65 m`).
2. **Resolution-scaled minimum size and depth (`MinLakeCells`, default `12 px` at `1024x512`, `24 px` at `2048x1024`;
   `MinLakeDepthMeters`, default `5.0 m`).**
3. **Isoperimetric compactness (`MinLakeCompactness`, default `0.22`):**
   $$C_{\text{iso}} = \frac{4\pi N_{\text{cells}}}{P_{\text{edges}}^2} \ge 0.22$$
   Rejects narrow 1-pixel valley ribbons in favour of breached river channels.

### 3. Two-pass convergent D8 flow routing

After leveling qualifying lakes and breaching rejected sills, a second Priority-Flood pass establishes the
strictly monotone rank ordering `rank[u]`. To prevent adjacent streams on flat plains from running 1 pixel
apart in parallel:

- **Pass 3a** performs a preliminary steepest-descent area accumulation `prelimArea[u]`.
- **Pass 3b** selects the D8 downslope neighbor (`rank[n] < rank[u]`) maximizing
  $$\text{score}(u \to n) = S(u \to n) \cdot \left(1 + 0.08 \log_{10}\!\left(1 + \frac{\text{prelimArea}[n]}{A_{\text{cell}}(y)}\right)\right)$$
  pulling parallel lowland flow paths together into convergent trunk channels. Inside lakes, cells route
  directly along `spillParent[u]` to the lake spill outlet.

### 4. Volumetric runoff discharge accumulation and twig pruning

Traversing land cells from highest upstream rank to lowest downstream rank:

1. Each cell initializes local volumetric runoff discharge (in $\text{m}^3/\text{s}$) from Stage 3
   `RunoffMmPerYear`:
   $$q_u = \frac{R_u \cdot 10^{-3}\text{ m/mm} \cdot A_{\text{cell}}(y)}{31,557,600\text{ s/yr}}$$
   and routes $Q_u$ downstream to `ReceiverIndex[u]` (with a 1.5% per-cell transmission loss when crossing
   hyper-arid desert cells where $P < 120\text{ mm/yr}$).
2. Candidate river cells must satisfy **both** `DischargeM3s[u] >= effectiveDischargeThreshold` (`220 m³/s`
   at global scale) and `DrainageAreaSqKm[u] >= effectiveAreaThreshold` (`26,000 km²`). Because arid regions
   have $R_u \approx 0$, deserts and deep rain shadows spawn no local headwaters.
3. **Headwater twig pruning (`PruneShortHeadwaterTwigs`):** Any Order-1 headwater branch (starting at a
   channel head with in-degree 0) that reaches a confluence, lake, or coast in fewer than
   `MinHeadwaterReachCells` (`8` pixels) and carries less than $2.8\times$ the base discharge threshold is
   pruned. This eliminates short parallel hillside "comb teeth" while keeping true dendritic river trees.
4. **Strahler stream order (`RiverOrder[u]`) and hydraulic width ($W = 7.2\sqrt{Q}\text{ m}$)** are computed
   on the pruned channel network, and continuous `RiverPolyline` vector reaches are exported to
   `<prefix>.s4-hydro-vectors.json`.
