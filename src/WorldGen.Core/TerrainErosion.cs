using System.Diagnostics;
using System.Numerics;

namespace WorldGen;

/// <summary>
/// Parameters controlling Stage 2 coupled fluvial (stream-power incision + sediment transport/deposition)
/// and hillslope (diffusion) erosion.
/// </summary>
public sealed record ErosionSettings
{
    /// <summary>Seed used to reconstruct spatial erodibility and uplift when eroding a standalone GeoTIFF.</summary>
    public int Seed { get; init; } = 1337;

    /// <summary>Number of coupled fluvial and hillslope solver iterations.</summary>
    public int Iterations { get; init; } = 25;

    /// <summary>Dimensionless timestep per iteration.</summary>
    public float TimeStep { get; init; } = 1.0f;

    /// <summary>Stream-power bedrock erodibility coefficient K in E = K * k_e * A^m * S^n.</summary>
    public float StreamPowerK { get; init; } = 0.032f;

    /// <summary>Drainage area exponent m in the stream-power law (typical range 0.35..0.55).</summary>
    public float AreaExponentM { get; init; } = 0.45f;

    /// <summary>Channel slope exponent n in the stream-power law (typical range 0.8..1.2).</summary>
    public float SlopeExponentN { get; init; } = 1.0f;

    /// <summary>Hillslope creep diffusivity D (smooths ridges and fills small concavities).</summary>
    public float HillslopeDiffusivity { get; init; } = 0.15f;

    /// <summary>Sediment transport capacity coefficient governing transition from incision to deposition.</summary>
    public float SedimentTransportCapacity { get; init; } = 0.55f;

    /// <summary>Fraction of excess sediment load deposited per cell in low-gradient basins and foothills.</summary>
    public float DepositionRate { get; init; } = 0.35f;

    /// <summary>Tectonic uplift compensation on active convergent/arc belts during erosion.</summary>
    public float UpliftScale { get; init; } = 0.28f;

    /// <summary>Coastal delta deposition strength where rivers enter shallow continental shelves.</summary>
    public float CoastalDeltaStrength { get; init; } = 0.40f;

    /// <summary>Overlap border (in pixels) used when eroding regional tiles so boundaries remain consistent (ER-5).</summary>
    public int HaloCells { get; init; } = 16;
}

/// <summary>
/// Output of Stage 2 erosion. The source <see cref="Heightmap"/> is preserved unmodified; <see cref="Eroded"/>
/// is a newly allocated float32 heightmap accompanied by cumulative erosion, sediment thickness, and drainage fields.
/// </summary>
public sealed class ErosionResult
{
    public required Heightmap Source { get; init; }

    public required Heightmap Eroded { get; init; }

    public required float[] ErosionDepthMeters { get; init; }

    public required float[] SedimentThicknessMeters { get; init; }

    public required float[] DrainageAreaSqKm { get; init; }

    public required ErosionSettings Settings { get; init; }

    public double MeanErosionMeters { get; init; }

    public double MaxErosionMeters { get; init; }

    public double MeanDepositionMeters { get; init; }

    public double MaxDepositionMeters { get; init; }

    public int CoastlineProgradedCells { get; init; }

    public double ElapsedSeconds { get; init; }
}

/// <summary>
/// Stage 2 coupled fluvial and hillslope erosion solver on the sphere (ER-1 through ER-8).
/// </summary>
public static class TerrainErosion
{
    // Fixed D8 neighbor offsets (dx, dy) in clockwise order for deterministic tie-breaking.
    private static readonly (int Dx, int Dy)[] D8Offsets =
    [
        (0, -1),  // N
        (1, -1),  // NE
        (1, 0),   // E
        (1, 1),   // SE
        (0, 1),   // S
        (-1, 1),  // SW
        (-1, 0),  // W
        (-1, -1), // NW
    ];

    /// <summary>
    /// Renders a regional or global Stage 1 map with an overlap halo (when regional) and applies Stage 2 erosion,
    /// cropping the result back to the requested <paramref name="view"/> (ER-5).
    /// </summary>
    public static ErosionResult RenderAndErodeWithHalo(
        PlanetTerrain terrain,
        MapView view,
        int width,
        int height,
        ErosionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(settings);

        bool isWholePlanet = view == MapView.WholePlanet ||
                             (view.LongitudeSpan >= 359.99 && view.LatitudeSpan >= 179.99);

        int halo = isWholePlanet ? 0 : Math.Max(0, settings.HaloCells);
        if (halo == 0)
        {
            Heightmap source = HeightmapRenderer.Render(terrain, view, width, height);
            return Erode(source, settings, terrain, terrain.Settings.PlanetRadiusMeters);
        }

        double degPerPxLon = view.LongitudeSpan / width;
        double degPerPxLat = view.LatitudeSpan / height;

        int padWest = halo;
        int padEast = halo;
        double west = view.WestLongitude - padWest * degPerPxLon;
        double east = view.EastLongitude + padEast * degPerPxLon;

        int padNorth = (int)Math.Min(halo, Math.Floor((90.0 - view.NorthLatitude) / degPerPxLat));
        int padSouth = (int)Math.Min(halo, Math.Floor((view.SouthLatitude - (-90.0)) / degPerPxLat));

        double north = view.NorthLatitude + padNorth * degPerPxLat;
        double south = view.SouthLatitude - padSouth * degPerPxLat;

        int paddedWidth = width + padWest + padEast;
        int paddedHeight = height + padNorth + padSouth;
        var paddedView = new MapView(north, south, west, east);

        Heightmap paddedSource = HeightmapRenderer.Render(terrain, paddedView, paddedWidth, paddedHeight);
        ErosionResult paddedResult = Erode(paddedSource, settings, terrain, terrain.Settings.PlanetRadiusMeters);

        return CropResult(paddedResult, view, width, height, padWest, padNorth);
    }

    /// <summary>
    /// Applies Stage 2 erosion to <paramref name="source"/> without modifying <paramref name="source"/>.
    /// Returns a new <see cref="ErosionResult"/> containing the eroded float32 <see cref="Heightmap"/> and
    /// diagnostic intermediate fields.
    /// </summary>
    public static ErosionResult Erode(
        Heightmap source,
        ErosionSettings settings,
        PlanetTerrain? terrain = null,
        float planetRadiusMeters = 6_371_000f,
        ParallelProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var stopwatch = Stopwatch.StartNew();

        int width = source.Width;
        int height = source.Height;
        int totalCells = width * height;
        bool wrapsLongitude = source.View.LongitudeSpan >= 359.999;

        // Copy source elevations into working buffer; source.Elevations is never mutated.
        var h = new float[totalCells];
        source.Elevations.CopyTo(h);

        var erosionDepth = new float[totalCells];
        var sedimentThickness = new float[totalCells];
        var drainageAreaSqKm = new float[totalCells];

        int steps = Math.Max(1, settings.Iterations);
        float dt = Math.Max(0.01f, settings.TimeStep);

        // Precompute spherical row metrics.
        double dLatRad = MathUtils.ToRadians(source.View.LatitudeSpan / height);
        double dLonRad = MathUtils.ToRadians(source.View.LongitudeSpan / width);
        float dyMeters = (float)Math.Max(100.0, planetRadiusMeters * dLatRad);

        var dxMeters = new float[height];
        var dDiagMeters = new float[height];
        var cellAreaSqKm = new float[height];
        var cosWeight = new float[height];
        double sumCellAreaSqKm = 0.0;

        for (int y = 0; y < height; y++)
        {
            double latRad = MathUtils.ToRadians(source.LatitudeAt(y));
            double cosLat = Math.Max(0.05, Math.Cos(latRad));
            cosWeight[y] = (float)cosLat;
            dxMeters[y] = (float)Math.Max(200.0, planetRadiusMeters * cosLat * dLonRad);
            dDiagMeters[y] = MathF.Sqrt(dxMeters[y] * dxMeters[y] + dyMeters * dyMeters);
            cellAreaSqKm[y] = (dxMeters[y] * dyMeters) * 1e-6f;
            sumCellAreaSqKm += cellAreaSqKm[y];
        }

        float meanCellAreaSqKm = (float)(sumCellAreaSqKm / height);
        // Reference horizontal step used to normalize slopes across resolutions.
        float refStepMeters = MathF.Min(dyMeters, 6_000f);

        // Precompute spatial erodibility and uplift from provinces & tectonic sketch on a 4x4 coarse grid and interpolate.
        var erodibility = new float[totalCells];
        var uplift = new float[totalCells];
        BuildSpatialFields(source, settings.Seed, terrain, erodibility, uplift);

        // Working buffers for Priority-Flood, topological ordering, routing, and sediment flux.
        var spillElev = new float[totalCells];
        var spillReceiver = new int[totalCells];
        var order = new int[totalCells];
        var rank = new int[totalCells];
        var receiver = new int[totalCells];
        var secondReceiver = new int[totalCells];
        var secondWeight = new float[totalCells];
        var slope = new float[totalCells];
        var sedFlux = new float[totalCells];
        var diffDelta = new float[totalCells];
        var visited = new bool[totalCells];
        var heap = new MinHeap(totalCells / 2 + 1024);
        var flatQueue = new Queue<int>(4096);

        int orderCount = 0;

        for (int step = 0; step < steps; step++)
        {
            float stepBaseFrac = (float)step / steps;
            float stepEndFrac = (float)(step + 1) / steps;

            // Re-run Priority-Flood depression resolution every 8 iterations (and on first/last step).
            if (step % 8 == 0 || step == steps - 1)
            {
                orderCount = ResolveDepressions(
                    h, width, height, wrapsLongitude,
                    spillElev, spillReceiver, order, rank, visited, heap, flatQueue);
            }

            // Initialize drainage area with local cell area, clear sediment flux, and find steepest receivers in parallel.
            ParallelProgress.ForRange(orderCount, progress, k =>
            {
                int u = order[k];
                int uy = u / width;
                drainageAreaSqKm[u] = cellAreaSqKm[uy];
                sedFlux[u] = 0f;

                float hu = h[u];
                if (hu <= 0f)
                {
                    receiver[u] = -1;
                    secondReceiver[u] = -1;
                    secondWeight[u] = 0f;
                    return;
                }

                int ux = u - uy * width;
                int uRank = rank[u];

                int best1 = -1;
                int best2 = -1;
                float bestSlope1 = 0f;
                float bestSlope2 = 0f;

                for (int d = 0; d < 8; d++)
                {
                    var (ox, oy) = D8Offsets[d];
                    int ny = uy + oy;
                    if (ny < 0 || ny >= height)
                        continue;

                    int nx = ux + ox;
                    if (nx < 0 || nx >= width)
                    {
                        if (!wrapsLongitude)
                            continue;
                        nx = (nx + width) % width;
                    }

                    int n = ny * width + nx;
                    if (rank[n] >= uRank)
                        continue;

                    float drop = hu - h[n];
                    if (drop <= 0f)
                        continue;

                    float dist = (ox != 0 && oy != 0) ? dDiagMeters[uy] : (ox != 0 ? dxMeters[uy] : dyMeters);
                    float s = drop / dist;
                    if (s > bestSlope1)
                    {
                        bestSlope2 = bestSlope1;
                        best2 = best1;
                        bestSlope1 = s;
                        best1 = n;
                    }
                    else if (s > bestSlope2)
                    {
                        bestSlope2 = s;
                        best2 = n;
                    }
                }

                if (best1 < 0)
                {
                    best1 = spillReceiver[u];
                    best2 = -1;
                    bestSlope1 = 2e-4f;
                }

                receiver[u] = best1;
                slope[u] = bestSlope1;

                if (best1 >= 0 && best2 >= 0 && bestSlope2 > bestSlope1 * 0.35f && h[best2] > 0f)
                {
                    secondReceiver[u] = best2;
                    secondWeight[u] = bestSlope2 / (bestSlope1 + bestSlope2);
                }
                else
                {
                    secondReceiver[u] = -1;
                    secondWeight[u] = 0f;
                }
            }, stepBaseFrac, stepEndFrac);

            // 1b. Accumulate drainage area upstream-to-downstream (deterministic sequential topological pass).
            for (int k = orderCount - 1; k >= 0; k--)
            {
                int u = order[k];
                int best1 = receiver[u];
                if (best1 >= 0)
                {
                    int best2 = secondReceiver[u];
                    if (best2 >= 0)
                    {
                        float w2 = secondWeight[u];
                        drainageAreaSqKm[best1] += drainageAreaSqKm[u] * (1f - w2);
                        drainageAreaSqKm[best2] += drainageAreaSqKm[u] * w2;
                    }
                    else if (h[best1] > 0f)
                    {
                        drainageAreaSqKm[best1] += drainageAreaSqKm[u];
                    }
                }
            }

            // 2. Coupled fluvial stream-power incision, sediment transport, and basin/coastal deposition.
            float kFluvial = settings.StreamPowerK;
            float mExp = settings.AreaExponentM;
            float nExp = settings.SlopeExponentN;
            float kCap = settings.SedimentTransportCapacity;
            float depRate = settings.DepositionRate;
            float upliftScale = settings.UpliftScale;
            float deltaStrength = settings.CoastalDeltaStrength;
            const float referenceGradient = 0.0025f; // 2.5 m/km reference channel slope

            for (int k = orderCount - 1; k >= 0; k--)
            {
                int u = order[k];
                float hu = h[u];
                if (hu <= 0f)
                    continue;

                int r1 = receiver[u];
                if (r1 < 0)
                    continue;

                int uy = u / width;
                float cellWeight = cosWeight[uy];

                // Effective catchment area in cosine-weighted cell equivalents.
                float normArea = MathF.Max(1f, drainageAreaSqKm[u] / meanCellAreaSqKm);
                float areaTerm = MathF.Pow(normArea, mExp);

                float hr = h[r1];
                float targetFloor = MathF.Max(0.12f, hr + 0.02f);
                float excessHeight = MathF.Max(0f, hu - targetFloor);

                // Dimensionless slope relative to reference gradient (2.5 m/km), scaled across resolutions.
                float effectiveGradient = slope[u] * MathF.Sqrt(dxMeters[uy] / refStepMeters);
                float normSlope = Math.Clamp(effectiveGradient / referenceGradient, 0.02f, 25.0f);
                float slopeTerm = MathF.Abs(nExp - 1.0f) < 1e-4f ? normSlope : MathF.Pow(normSlope, nExp);

                float ke = erodibility[u];
                float streamPower = kFluvial * ke * areaTerm * slopeTerm;
                float transportCap = kCap * areaTerm * (normSlope * MathF.Sqrt(MathF.Sqrt(normSlope))) * 14.0f * cellWeight;
                float qIn = sedFlux[u];

                // Active tectonic uplift in young convergent/arc belts counteracts erosion slightly.
                float uStep = dt * upliftScale * uplift[u] * 1.4f;
                if (uStep > 0f && hu > 150f)
                {
                    h[u] += uStep;
                    hu = h[u];
                    excessHeight = MathF.Max(0f, hu - targetFloor);
                }

                float qOut = qIn;

                if (qIn < transportCap && excessHeight > 0.01f)
                {
                    // Detachment-limited / under-capacity incision (semi-implicit form for unconditional stability).
                    float capacityFactor = 1f - qIn / (transportCap + 1e-4f);
                    float factor = dt * streamPower * capacityFactor * 0.18f;
                    float incise = (excessHeight * factor) / (1f + factor);

                    // Bound single-step incision to prevent receiver overshoot.
                    incise = MathF.Min(incise, excessHeight * 0.38f);
                    incise = MathF.Min(incise, 35f * dt);

                    if (incise > 0f)
                    {
                        h[u] -= incise;
                        if (sedimentThickness[u] >= incise)
                        {
                            sedimentThickness[u] -= incise;
                        }
                        else
                        {
                            float bedrockCut = incise - sedimentThickness[u];
                            sedimentThickness[u] = 0f;
                            erosionDepth[u] += bedrockCut;
                        }

                        qOut = qIn + incise * cellWeight;
                    }
                }
                else if (qIn > transportCap)
                {
                    // Transport-limited regime: deposit excess sediment in basins, foothills, and alluvial fans.
                    float excessSed = (qIn - transportCap) / cellWeight;
                    float deposit = depRate * dt * excessSed;

                    // Do not build artificial spikes: cap per-step deposition.
                    deposit = MathF.Min(deposit, 9f * dt);
                    if (deposit > 0f)
                    {
                        h[u] += deposit;
                        sedimentThickness[u] += deposit;
                        qOut = MathF.Max(0f, qIn - deposit * cellWeight);
                    }
                }

                // Route outgoing sediment flux downstream (or deposit a delta at the coast).
                if (hr > 0f)
                {
                    int r2 = secondReceiver[u];
                    if (r2 >= 0 && h[r2] > 0f)
                    {
                        float w2 = secondWeight[u];
                        sedFlux[r1] += qOut * (1f - w2);
                        sedFlux[r2] += qOut * w2;
                    }
                    else
                    {
                        sedFlux[r1] += qOut;
                    }
                }
                else if (deltaStrength > 0f && hr > -45f && qOut > 8f)
                {
                    // River mouth entering shallow shelf water: build a subaerial/shallow coastal delta (ER-4).
                    float deltaDep = MathF.Min(deltaStrength * 0.018f * (qOut / cellWeight), 6.0f * dt);
                    float maxDeltaTop = MathF.Min(1.8f, hu * 0.35f);
                    if (h[r1] + deltaDep > maxDeltaTop)
                    {
                        deltaDep = MathF.Max(0f, maxDeltaTop - h[r1]);
                    }

                    if (deltaDep > 0f)
                    {
                        h[r1] += deltaDep;
                        sedimentThickness[r1] += deltaDep;
                    }
                }
            }

            // 3. Hillslope diffusion (smooths sharp divides and fills small hollows on land).
            if (settings.HillslopeDiffusivity > 0f)
            {
                float diffRate = Math.Clamp(settings.HillslopeDiffusivity * dt * 0.12f, 0f, 0.18f);
                ApplyHillslopeDiffusion(
                    h, erodibility, erosionDepth, sedimentThickness, diffDelta,
                    width, height, wrapsLongitude, dxMeters, dyMeters, diffRate);
            }
        }

        stopwatch.Stop();

        var erodedMap = new Heightmap(width, height, source.View);
        h.CopyTo(erodedMap.Elevations, 0);

        // Compute summary statistics (area-weighted by cos(latitude)).
        double weightedErosionSum = 0.0;
        double weightedDepositionSum = 0.0;
        double totalLandWeight = 0.0;
        double maxErosion = 0.0;
        double maxDeposition = 0.0;
        int progradedCells = 0;

        ReadOnlySpan<float> srcSpan = source.Elevations;
        for (int y = 0; y < height; y++)
        {
            double w = cosWeight[y];
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                float e = erosionDepth[i];
                float d = sedimentThickness[i];
                if (e > maxErosion) maxErosion = e;
                if (d > maxDeposition) maxDeposition = d;

                if (srcSpan[i] <= 0f && h[i] > 0f)
                {
                    progradedCells++;
                }

                if (srcSpan[i] > 0f || h[i] > 0f)
                {
                    weightedErosionSum += e * w;
                    weightedDepositionSum += d * w;
                    totalLandWeight += w;
                }
            }
        }

        return new ErosionResult
        {
            Source = source,
            Eroded = erodedMap,
            ErosionDepthMeters = erosionDepth,
            SedimentThicknessMeters = sedimentThickness,
            DrainageAreaSqKm = drainageAreaSqKm,
            Settings = settings,
            MeanErosionMeters = totalLandWeight > 0 ? weightedErosionSum / totalLandWeight : 0.0,
            MaxErosionMeters = maxErosion,
            MeanDepositionMeters = totalLandWeight > 0 ? weightedDepositionSum / totalLandWeight : 0.0,
            MaxDepositionMeters = maxDeposition,
            CoastlineProgradedCells = progradedCells,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }

    private static void ApplyHillslopeDiffusion(
        float[] h,
        float[] erodibility,
        float[] erosionDepth,
        float[] sedimentThickness,
        float[] diffDelta,
        int width,
        int height,
        bool wrapsLongitude,
        float[] dxMeters,
        float dyMeters,
        float diffRate)
    {
        float invDy2 = 1f / (dyMeters * dyMeters);

        Parallel.For(0, height, y =>
        {
            float invDx2 = 1f / (dxMeters[y] * dxMeters[y]);
            float norm = 1f / (2f * (invDx2 + invDy2));
            int up = Math.Max(0, y - 1);
            int down = Math.Min(height - 1, y + 1);
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                float center = h[idx];
                if (center <= 0.15f)
                {
                    diffDelta[idx] = 0f;
                    continue;
                }

                int left = x > 0 ? x - 1 : (wrapsLongitude ? width - 1 : 0);
                int right = x + 1 < width ? x + 1 : (wrapsLongitude ? 0 : width - 1);

                float hL = MathF.Max(0f, h[rowOffset + left]);
                float hR = MathF.Max(0f, h[rowOffset + right]);
                float hU = MathF.Max(0f, h[up * width + x]);
                float hD = MathF.Max(0f, h[down * width + x]);

                float laplacian = ((hL - 2f * center + hR) * invDx2 + (hU - 2f * center + hD) * invDy2) * norm;
                float delta = diffRate * erodibility[idx] * laplacian;

                if (center + delta < 0.1f)
                {
                    delta = 0.1f - center;
                }

                diffDelta[idx] = delta;
            }
        });

        for (int i = 0; i < h.Length; i++)
        {
            float d = diffDelta[i];
            if (d == 0f)
                continue;

            h[i] += d;
            if (d < 0f)
            {
                erosionDepth[i] += -d;
            }
            else
            {
                sedimentThickness[i] += d;
            }
        }
    }

    /// <summary>
    /// Priority-Flood depression resolution (Barnes et al., 2014). Connects every subaerial cell to the ocean
    /// (or map border) along the lowest spill path and outputs cells in non-decreasing spill-elevation order.
    /// </summary>
    private static int ResolveDepressions(
        float[] h,
        int width,
        int height,
        bool wrapsLongitude,
        float[] spillElev,
        int[] spillReceiver,
        int[] order,
        int[] rank,
        bool[] visited,
        MinHeap heap,
        Queue<int> flatQueue)
    {
        Array.Clear(visited, 0, visited.Length);
        heap.Clear();
        flatQueue.Clear();

        int total = width * height;
        int orderCount = 0;

        // Seed ocean cells and non-wrapped map borders.
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            bool isTopOrBottomBorder = (y == 0 || y == height - 1);

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                float elev = h[i];
                bool isSideBorder = !wrapsLongitude && (x == 0 || x == width - 1);

                if (elev <= 0f)
                {
                    visited[i] = true;
                    spillElev[i] = elev;
                    spillReceiver[i] = -1;
                    rank[i] = -1; // Ocean cells are below all land cells (rank -1) and do not need to be in order[]
                }
                else if (isTopOrBottomBorder || isSideBorder)
                {
                    visited[i] = true;
                    spillElev[i] = elev;
                    spillReceiver[i] = -1;
                    heap.Push(elev, i);
                }
            }
        }

        // Seed coastal land cells (land cells directly adjacent to ocean) into the min-heap.
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                if (visited[i])
                    continue;

                int oceanNeighbor = FindAdjacentOcean(h, x, y, width, height, wrapsLongitude);
                if (oceanNeighbor >= 0)
                {
                    visited[i] = true;
                    spillElev[i] = h[i];
                    spillReceiver[i] = oceanNeighbor;
                    heap.Push(h[i], i);
                }
            }
        }

        // If the regional tile has no ocean at all and borders were seeded, or if nothing was seeded, seed minimum cell.
        if (heap.Count == 0 && orderCount < total)
        {
            int minIdx = 0;
            float minVal = h[0];
            for (int i = 1; i < total; i++)
            {
                if (h[i] < minVal)
                {
                    minVal = h[i];
                    minIdx = i;
                }
            }

            visited[minIdx] = true;
            spillElev[minIdx] = minVal;
            spillReceiver[minIdx] = -1;
            heap.Push(minVal, minIdx);
        }

        while (heap.Count > 0 || flatQueue.Count > 0)
        {
            int c;
            float cSpill;
            if (flatQueue.Count > 0)
            {
                c = flatQueue.Dequeue();
                cSpill = spillElev[c];
            }
            else
            {
                (cSpill, c) = heap.Pop();
            }

            rank[c] = orderCount;
            order[orderCount++] = c;

            int cy = c / width;
            int cx = c - cy * width;

            for (int d = 0; d < 8; d++)
            {
                var (ox, oy) = D8Offsets[d];
                int ny = cy + oy;
                if (ny < 0 || ny >= height)
                    continue;

                int nx = cx + ox;
                if (nx < 0 || nx >= width)
                {
                    if (!wrapsLongitude)
                        continue;
                    nx = (nx + width) % width;
                }

                int n = ny * width + nx;
                if (visited[n])
                    continue;

                visited[n] = true;
                spillReceiver[n] = c;
                float hn = h[n];

                if (hn <= cSpill)
                {
                    spillElev[n] = cSpill + 1e-4f;
                    flatQueue.Enqueue(n);
                }
                else
                {
                    spillElev[n] = hn;
                    heap.Push(hn, n);
                }
            }
        }

        return orderCount;
    }

    private static int FindAdjacentOcean(float[] h, int x, int y, int width, int height, bool wrapsLongitude)
    {
        int bestOcean = -1;
        float lowestOcean = 1f;

        for (int d = 0; d < 8; d++)
        {
            var (ox, oy) = D8Offsets[d];
            int ny = y + oy;
            if (ny < 0 || ny >= height)
                continue;

            int nx = x + ox;
            if (nx < 0 || nx >= width)
            {
                if (!wrapsLongitude)
                    continue;
                nx = (nx + width) % width;
            }

            int n = ny * width + nx;
            if (h[n] <= 0f && h[n] < lowestOcean)
            {
                lowestOcean = h[n];
                bestOcean = n;
            }
        }

        return bestOcean;
    }

    /// <summary>
    /// Evaluates spatial erodibility (from geomorphic provinces) and active uplift (from tectonic boundaries)
    /// on a 4x4 coarse grid and bilinearly interpolates to full raster resolution.
    /// </summary>
    private static void BuildSpatialFields(
        Heightmap map,
        int seed,
        PlanetTerrain? terrain,
        float[] erodibility,
        float[] uplift)
    {
        ProvinceField provinces = terrain?.Provinces ?? new ProvinceField(seed, 9);
        TectonicSketch tectonics = terrain?.Tectonics ?? new TectonicSketch(seed, 13, 5, 6_371_000f, 850_000f);

        int width = map.Width;
        int height = map.Height;
        const int step = 8;
        int cw = (width + step - 1) / step + 1;
        int ch = (height + step - 1) / step + 1;

        var coarseErod = new float[cw * ch];
        var coarseUplift = new float[cw * ch];

        Parallel.For(0, ch, cy =>
        {
            int py = Math.Min(height - 1, cy * step);
            double latRad = MathUtils.ToRadians(map.LatitudeAt(py));
            double sinLat = Math.Sin(latRad);
            double cosLat = Math.Cos(latRad);

            for (int cx = 0; cx < cw; cx++)
            {
                int px = Math.Min(width - 1, cx * step);
                double lonRad = MathUtils.ToRadians(map.LongitudeAt(px));
                var dir = new Vector3(
                    (float)(cosLat * Math.Cos(lonRad)),
                    (float)sinLat,
                    (float)(cosLat * Math.Sin(lonRad)));

                ProvinceParameters p = provinces.Sample(dir).Parameters;
                TectonicSample t = tectonics.Sample(dir);

                int cIdx = cy * cw + cx;
                coarseErod[cIdx] = Math.Clamp(p.Erodibility, 0.35f, 1.75f);
                coarseUplift[cIdx] = Math.Clamp(t.Orogeny + 0.65f * t.Arc + 0.35f * t.RiftShoulder, 0f, 1.5f);
            }
        });

        for (int y = 0; y < height; y++)
        {
            float gy = (float)y / step;
            int y0 = (int)gy;
            int y1 = Math.Min(ch - 1, y0 + 1);
            float ty = gy - y0;
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                float gx = (float)x / step;
                int x0 = (int)gx;
                int x1 = Math.Min(cw - 1, x0 + 1);
                float tx = gx - x0;

                int i00 = y0 * cw + x0;
                int i10 = y0 * cw + x1;
                int i01 = y1 * cw + x0;
                int i11 = y1 * cw + x1;

                float e0 = MathUtils.Lerp(coarseErod[i00], coarseErod[i10], tx);
                float e1 = MathUtils.Lerp(coarseErod[i01], coarseErod[i11], tx);
                erodibility[rowOffset + x] = MathUtils.Lerp(e0, e1, ty);

                float u0 = MathUtils.Lerp(coarseUplift[i00], coarseUplift[i10], tx);
                float u1 = MathUtils.Lerp(coarseUplift[i01], coarseUplift[i11], tx);
                uplift[rowOffset + x] = MathUtils.Lerp(u0, u1, ty);
            }
        }
    }

    private static ErosionResult CropResult(
        ErosionResult padded,
        MapView targetView,
        int width,
        int height,
        int offsetX,
        int offsetY)
    {
        var source = new Heightmap(width, height, targetView);
        var eroded = new Heightmap(width, height, targetView);
        var erosionDepth = new float[width * height];
        var sedimentThickness = new float[width * height];
        var drainageArea = new float[width * height];

        int paddedWidth = padded.Source.Width;
        for (int y = 0; y < height; y++)
        {
            int srcRow = (y + offsetY) * paddedWidth + offsetX;
            int dstRow = y * width;

            padded.Source.Elevations.AsSpan(srcRow, width).CopyTo(source.Elevations.AsSpan(dstRow, width));
            padded.Eroded.Elevations.AsSpan(srcRow, width).CopyTo(eroded.Elevations.AsSpan(dstRow, width));
            padded.ErosionDepthMeters.AsSpan(srcRow, width).CopyTo(erosionDepth.AsSpan(dstRow, width));
            padded.SedimentThicknessMeters.AsSpan(srcRow, width).CopyTo(sedimentThickness.AsSpan(dstRow, width));
            padded.DrainageAreaSqKm.AsSpan(srcRow, width).CopyTo(drainageArea.AsSpan(dstRow, width));
        }

        return new ErosionResult
        {
            Source = source,
            Eroded = eroded,
            ErosionDepthMeters = erosionDepth,
            SedimentThicknessMeters = sedimentThickness,
            DrainageAreaSqKm = drainageArea,
            Settings = padded.Settings,
            MeanErosionMeters = padded.MeanErosionMeters,
            MaxErosionMeters = padded.MaxErosionMeters,
            MeanDepositionMeters = padded.MeanDepositionMeters,
            MaxDepositionMeters = padded.MaxDepositionMeters,
            CoastlineProgradedCells = padded.CoastlineProgradedCells,
            ElapsedSeconds = padded.ElapsedSeconds,
        };
    }

    /// <summary>
    /// Array-backed binary min-heap keyed by float elevation with deterministic tie-breaking on cell index.
    /// </summary>
    private sealed class MinHeap(int initialCapacity)
    {
        private float[] _keys = new float[Math.Max(16, initialCapacity)];
        private int[] _values = new int[Math.Max(16, initialCapacity)];
        private int _count;

        public int Count => _count;

        public void Clear() => _count = 0;

        public void Push(float key, int value)
        {
            if (_count == _keys.Length)
            {
                Array.Resize(ref _keys, _keys.Length * 2);
                Array.Resize(ref _values, _values.Length * 2);
            }

            int i = _count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (LessOrEqual(_keys[parent], _values[parent], key, value))
                    break;

                _keys[i] = _keys[parent];
                _values[i] = _values[parent];
                i = parent;
            }

            _keys[i] = key;
            _values[i] = value;
        }

        public (float Key, int Value) Pop()
        {
            float resultKey = _keys[0];
            int resultVal = _values[0];
            int last = --_count;

            if (last > 0)
            {
                float k = _keys[last];
                int v = _values[last];
                int i = 0;
                int half = last >> 1;

                while (i < half)
                {
                    int left = (i << 1) + 1;
                    int right = left + 1;
                    int smallest = left;

                    if (right < last && Less(_keys[right], _values[right], _keys[left], _values[left]))
                    {
                        smallest = right;
                    }

                    if (LessOrEqual(k, v, _keys[smallest], _values[smallest]))
                        break;

                    _keys[i] = _keys[smallest];
                    _values[i] = _values[smallest];
                    i = smallest;
                }

                _keys[i] = k;
                _values[i] = v;
            }

            return (resultKey, resultVal);
        }

        private static bool Less(float k1, int v1, float k2, int v2) =>
            k1 < k2 || (k1 == k2 && v1 < v2);

        private static bool LessOrEqual(float k1, int v1, float k2, int v2) =>
            k1 < k2 || (k1 == k2 && v1 <= v2);
    }
}
