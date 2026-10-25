# Climate and orographic rainfall model (Stage 3)

Stage 3 (`TerrainClimate`) evaluates surface temperature, atmospheric moisture advection, windward orographic
lift, leeward rain shadows, potential evapotranspiration, and surface runoff over a Stage 2 eroded `Heightmap`.
It runs **before** Stage 4 (`TerrainHydrology`) so that river networks and lake levels are governed by
regional water balance rather than uniform geometric catchment area
([ADR-016](decisions.md#adr-016--climate-and-orographic-rainfall-as-stage-3-before-hydrology)).

## 1. Surface temperature and potential evapotranspiration

For each cell at latitude $\phi$ and elevation $h \ge 0\text{ m}$:

$$T_{\text{sea}}(\phi) = T_{\text{eq}} - (T_{\text{eq}} - T_{\text{pole}})\sin^2\phi + 2.2\cos(2\phi)$$

$$T(\phi, h) = T_{\text{sea}}(\phi) - \Gamma \cdot \max(0, h)$$

where $T_{\text{eq}} = 28^\circ\text{C}$, $T_{\text{pole}} = -22^\circ\text{C}$, and $\Gamma = 6.5^\circ\text{C/km}$.
Potential evapotranspiration $\text{PET}$ (in $\text{mm/yr}$) increases with temperature and receives a
subtropical solar-insolation boost centered near $|\phi| \approx 25^\circ$.

## 2. Zonal circulation, Rossby undulation, and orographic moisture sweep

Planetary winds are organized into three latitudinal belts per hemisphere:

- **Trade winds ($0\text{–}30^\circ$):** predominantly easterlies (East $\to$ West).
- **Westerlies ($30\text{–}60^\circ$):** predominantly westerlies (West $\to$ East).
- **Polar easterlies ($60\text{–}90^\circ$):** predominantly easterlies (East $\to$ West).

To avoid rigid latitude bands, the effective circulation latitude $\tilde{\phi}(\phi, \lambda)$ is perturbed
by stationary planetary harmonics ($\Delta\phi \approx 4.2^\circ\sin(2\lambda + 0.7) + 2.3^\circ\cos(3\lambda - 1.1)$).
A zonal convergence multiplier peaks in the equatorial ITCZ ($|\tilde{\phi}| < 12^\circ$) and mid-latitude
frontal storm track ($|\tilde{\phi}| \approx 50^\circ$), and drops in the subtropical horse-latitude ridge
($|\tilde{\phi}| \approx 26.5^\circ$) and polar high ($|\tilde{\phi}| > 65^\circ$).

Along each latitude row (with a full $360^\circ$ spin-up revolution and tail blending across the antimeridian):

1. **Over ocean ($h \le 0$):** column moisture $M$ recharges toward a Clausius-Clapeyron temperature-scaled
   maritime equilibrium with e-folding distance $380\text{ km}$.
2. **Over land ($h > 0$):** using a meridionally averaged barrier height $\bar{h}$, upwind ascent
   ($\Delta\bar{h}/\Delta x > 0$) triggers **orographic condensation lift**, boosting local rainfall and
   accelerating inland moisture depletion; leeward descent ($\Delta\bar{h}/\Delta x < 0$) excites a
   decaying **rain-shadow memory** that suppresses condensation for several hundred kilometers downwind.
3. **2D synoptic diffusion:** a multi-pass separable meridional/zonal smoothing kernel mixes moisture
   across adjacent rows so rain shadows and wind transitions form smooth 2D plumes rather than single-row
   horizontal streaks.

## 3. Budyko surface runoff partitioning

Surface runoff $R$ (in $\text{mm/yr}$) fed to Stage 4 Hydrology is computed from precipitation $P$ and
potential evapotranspiration $\text{PET}$:

$$R = P \cdot \left(\frac{P}{P + \text{PET}}\right)^{1.55}$$

with a snowmelt retention floor on cold alpine peaks ($T < 4^\circ\text{C}$, $P > 250\text{ mm/yr}$). In
subtropical deserts and deep rain shadows ($P \ll \text{PET}$), $R$ falls to near zero, preventing desert
hillsides from spawning local river networks.
