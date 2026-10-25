using System.Diagnostics;

namespace WorldGen;

/// <summary>
/// Water state classification per raster cell (HY-5).
/// </summary>
public enum WaterBodyKind : byte
{
    DryLand = 0,
    Ocean = 1,
    Lake = 2,
    River = 3,
}

/// <summary>
/// Tunable parameters for Stage 4 hydrology (depression breaching/filling, lake water balance, and climate-driven river networks).
/// </summary>
public sealed record HydrologySettings
{
    /// <summary>Minimum volumetric river discharge (in m³/s) required to initiate a mapped river channel (HY-4).</summary>
    public float MinRiverDischargeM3s { get; init; } = 220f;

    /// <summary>Minimum drainage area (in km²) required to initiate a mapped river channel (HY-4).</summary>
    public float RiverThresholdSqKm { get; init; } = 26_000f;

    /// <summary>
    /// Minimum length (in pixels) for a Strahler Order-1 headwater branch. Short hillside stubs
    /// below this length are pruned so ridges and coasts do not sprout parallel comb teeth (HY-4).
    /// </summary>
    public int MinHeadwaterReachCells { get; init; } = 8;

    /// <summary>Minimum number of connected inundated pixels required to form a lake rather than a breached pit (HY-1, HY-3).</summary>
    public int MinLakeCells { get; init; } = 12;

    /// <summary>Minimum maximum depth (in meters) required for a closed depression to be retained as a lake (HY-3).</summary>
    public float MinLakeDepthMeters { get; init; } = 5.0f;

    /// <summary>
    /// Maximum sill depth (in meters) before the outlet sill is partially breached to a natural spill level.
    /// Prevents unphysically deep inland seas while preserving genuine lakes.
    /// </summary>
    public float MaxLakeDepthMeters { get; init; } = 65.0f;

    /// <summary>
    /// Minimum isoperimetric compactness ratio (4*pi*Area / Perimeter^2) for a multi-cell lake.
    /// Rejects 1-pixel-wide noise ribbons in favour of breached river channels.
    /// </summary>
    public float MinLakeCompactness { get; init; } = 0.22f;
}

/// <summary>
/// Metadata for a single hydrologically derived lake basin (HY-3, HY-5).
/// </summary>
public sealed record LakeFeature(
    int Id,
    float SurfaceElevationMeters,
    float MaxDepthMeters,
    float MeanDepthMeters,
    double AreaSqKm,
    int CellCount,
    int SpillPixelX,
    int SpillPixelY,
    double SpillLatitude,
    double SpillLongitude,
    double CentroidLatitude,
    double CentroidLongitude);

/// <summary>
/// Vertex along a traced river polyline (HY-4, HY-5).
/// </summary>
public sealed record RiverVertex(
    int PixelX,
    int PixelY,
    double Latitude,
    double Longitude,
    float ElevationMeters,
    float DrainageAreaSqKm,
    float DischargeM3s,
    float EstimatedWidthMeters,
    int StrahlerOrder);

/// <summary>
/// A continuous river reach or trunk polyline from source/confluence to confluence/lake/sea (HY-4, HY-5).
/// </summary>
public sealed record RiverPolyline(
    int Id,
    int MaxStrahlerOrder,
    float MaxDrainageAreaSqKm,
    float MaxDischargeM3s,
    float MaxWidthMeters,
    double LengthKm,
    bool DrainsToOcean,
    int? TerminalLakeId,
    IReadOnlyList<RiverVertex> Vertices);

/// <summary>
/// Complete output of Stage 4 hydrology (HY-1 through HY-6).
/// Preserves the unmodified Stage 2 eroded <see cref="Source"/> and Stage 3 <see cref="Climate"/>, and provides
/// the conditioned <see cref="HydrologicalHeightmap"/> alongside flow directions, volumetric discharge, drainage
/// accumulation, water mask, lakes, and river polylines.
/// </summary>
public sealed class HydrologyResult
{
    public required Heightmap Source { get; init; }

    public required ClimateResult Climate { get; init; }

    public required Heightmap HydrologicalHeightmap { get; init; }

    /// <summary>D8 flow direction index (0..7 clockwise from North) per cell, or -1 for ocean/sink (HY-2, HY-5).</summary>
    public required sbyte[] FlowDirection { get; init; }

    /// <summary>Downstream receiver cell index per cell, or -1 for ocean/sink.</summary>
    public required int[] ReceiverIndex { get; init; }

    /// <summary>Accumulated upstream drainage area in km² per cell (HY-4, HY-5).</summary>
    public required float[] DrainageAreaSqKm { get; init; }

    /// <summary>Accumulated volumetric river discharge in m³/s per cell driven by Stage 3 runoff (HY-4, HY-6).</summary>
    public required float[] DischargeM3s { get; init; }

    /// <summary>Per-cell water state mask: DryLand, Ocean, Lake, or River (HY-5).</summary>
    public required WaterBodyKind[] WaterMask { get; init; }

    /// <summary>1-based Lake ID per cell, or 0 if not in a lake (HY-3, HY-5).</summary>
    public required int[] LakeId { get; init; }

    /// <summary>Strahler stream order per cell (0 if not a river channel, 1..N for rivers) (HY-4, HY-5).</summary>
    public required byte[] RiverOrder { get; init; }

    /// <summary>Estimated hydraulic river channel width in meters per cell (HY-4).</summary>
    public required float[] ChannelWidthMeters { get; init; }

    public required IReadOnlyList<LakeFeature> Lakes { get; init; }

    public required IReadOnlyList<RiverPolyline> Rivers { get; init; }

    public required HydrologySettings Settings { get; init; }

    public double TotalLakeAreaSqKm { get; init; }

    public double TotalRiverLengthKm { get; init; }

    public int MaxStrahlerOrder { get; init; }

    public int RiverCellCount { get; init; }

    public int BreachedDepressionCount { get; init; }

    public double ElapsedSeconds { get; init; }
}

/// <summary>
/// Stage 4 Hydrology solver: Priority-Flood depression filling &amp; sill breaching with evaporative lake balance,
/// convergent D8 routing, Stage 3 rainfall/runoff discharge accumulation, headwater twig pruning, Strahler
/// stream ordering, and river polyline extraction.
/// </summary>
public static class TerrainHydrology
{
    private const float SecondsPerYear = 31_557_600f;

    // Clockwise D8 offsets starting from North (0..7).
    private static readonly (int Dx, int Dy)[] D8Offsets =
    [
        (0, -1),  // 0: N
        (1, -1),  // 1: NE
        (1, 0),   // 2: E
        (1, 1),   // 3: SE
        (0, 1),   // 4: S
        (-1, 1),  // 5: SW
        (-1, 0),  // 6: W
        (-1, -1), // 7: NW
    ];

    public static (int Dx, int Dy) GetDirectionOffset(sbyte dir) =>
        (dir >= 0 && dir < 8) ? D8Offsets[dir] : (0, 0);

    /// <summary>
    /// Runs Stage 4 hydrology on a Stage 2 eroded <see cref="Heightmap"/> and Stage 3 <see cref="ClimateResult"/>
    /// without modifying <paramref name="source"/>. If <paramref name="climate"/> is null, Stage 3 climate is
    /// evaluated automatically with default settings.
    /// </summary>
    public static HydrologyResult Analyze(
        Heightmap source,
        HydrologySettings settings,
        float planetRadiusMeters = 6_371_000f,
        ClimateResult? climate = null,
        ParallelProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var sw = Stopwatch.StartNew();

        climate ??= TerrainClimate.Analyze(source, new ClimateSettings(), planetRadiusMeters, progress);
        progress?.SetAll(0.15f);

        int width = source.Width;
        int height = source.Height;
        int totalCells = width * height;
        bool wrapsLongitude = source.View.LongitudeSpan >= 359.999;

        // Copy source elevations into working arrays; source is never mutated.
        var rawElev = new float[totalCells];
        source.Elevations.CopyTo(rawElev);

        // Precompute spherical row metrics.
        double dLatRad = MathUtils.ToRadians(source.View.LatitudeSpan / height);
        double dLonRad = MathUtils.ToRadians(source.View.LongitudeSpan / width);
        float dyMeters = (float)Math.Max(100.0, planetRadiusMeters * dLatRad);

        var dxMeters = new float[height];
        var dDiagMeters = new float[height];
        var cellAreaSqKm = new float[height];
        double totalSubaerialAreaSqKm = 0.0;
        int subaerialCount = 0;

        for (int y = 0; y < height; y++)
        {
            double latRad = MathUtils.ToRadians(source.LatitudeAt(y));
            double cosLat = Math.Max(0.05, Math.Cos(latRad));
            dxMeters[y] = (float)Math.Max(200.0, planetRadiusMeters * cosLat * dLonRad);
            dDiagMeters[y] = MathF.Sqrt(dxMeters[y] * dxMeters[y] + dyMeters * dyMeters);
            cellAreaSqKm[y] = (dxMeters[y] * dyMeters) * 1e-6f;

            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                if (rawElev[rowOffset + x] > 0f)
                {
                    totalSubaerialAreaSqKm += cellAreaSqKm[y];
                    subaerialCount++;
                }
            }
        }

        // Scale effective thresholds smoothly so regional maps resolve regional streams while global maps stay clean.
        float areaRatio = (float)Math.Clamp(totalSubaerialAreaSqKm / 145_000_000.0, 0.02, 1.0);
        float scaleFactor = MathF.Pow(areaRatio, 0.72f);
        float effectiveAreaThresholdSqKm = settings.RiverThresholdSqKm * scaleFactor;
        float effectiveDischargeThresholdM3s = settings.MinRiverDischargeM3s * scaleFactor;

        // 1. Priority-Flood depression analysis from fixed sea level (0 m) (HY-1, HY-6).
        var filledElev = new float[totalCells];
        var spillParent = new int[totalCells];
        var spillRoot = new int[totalCells];
        var order = new int[totalCells];
        var rank = new int[totalCells];

        int landOrderCount = RunPriorityFlood(
            rawElev, width, height, wrapsLongitude,
            filledElev, spillParent, spillRoot, order, rank);

        // 2. Classify depressions into genuine Lake basins vs. breached pits/ribbons, gated by climate aridity (HY-1, HY-3).
        var condElev = new float[totalCells];
        rawElev.CopyTo(condElev, 0);

        var lakeId = new int[totalCells];
        var lakes = new List<LakeFeature>();
        int breachedCount = ExtractLakesAndBreachSills(
            source, rawElev, filledElev, condElev, spillParent,
            climate.PrecipitationMmPerYear, climate.PotentialEvaporationMmPerYear,
            width, height, wrapsLongitude, cellAreaSqKm, settings,
            lakeId, lakes);

        // 3. Re-run Priority-Flood on the conditioned + lake-leveled surface to obtain final monotone routing order.
        progress?.SetAll(0.45f);
        var routingElev = new float[totalCells];
        landOrderCount = RunPriorityFlood(
            condElev, width, height, wrapsLongitude,
            routingElev, spillParent, spillRoot, order, rank);
        progress?.SetAll(0.65f);

        // 4. Convergent D8 flow routing (HY-2).
        // To prevent parallel streams running 1 pixel apart across flat plains, we use a two-stage pass:
        // Pass 4a: initial steepest descent to estimate preliminary catchment convergence;
        // Pass 4b: final D8 routing where gentle slopes slightly pull toward higher-accumulation neighbor channels.
        var flowDir = new sbyte[totalCells];
        var receiver = new int[totalCells];
        var prelimArea = new float[totalCells];
        Array.Fill(flowDir, (sbyte)-1);
        Array.Fill(receiver, -1);

        for (int k = 0; k < landOrderCount; k++)
        {
            int u = order[k];
            prelimArea[u] = cellAreaSqKm[u / width];
        }

        for (int k = landOrderCount - 1; k >= 0; k--)
        {
            int u = order[k];
            if (condElev[u] <= 0f)
                continue;

            int uy = u / width;
            int ux = u - uy * width;
            int uRank = rank[u];
            float hu = condElev[u];

            if (lakeId[u] > 0)
            {
                int p = spillParent[u];
                if (p >= 0 && condElev[p] > 0f)
                {
                    prelimArea[p] += prelimArea[u];
                }
                continue;
            }

            int bestN = -1;
            float bestSlope = 0f;
            for (sbyte d = 0; d < 8; d++)
            {
                var (ox, oy) = D8Offsets[d];
                int ny = uy + oy;
                if (ny < 0 || ny >= height) continue;
                int nx = ux + ox;
                if (nx < 0 || nx >= width)
                {
                    if (!wrapsLongitude) continue;
                    nx = (nx + width) % width;
                }

                int n = ny * width + nx;
                if (rank[n] >= uRank) continue;
                float drop = hu - condElev[n];
                if (drop <= 0f) continue;

                float dist = (ox != 0 && oy != 0) ? dDiagMeters[uy] : (ox != 0 ? dxMeters[uy] : dyMeters);
                float slope = drop / dist;
                if (slope > bestSlope)
                {
                    bestSlope = slope;
                    bestN = n;
                }
            }

            if (bestN < 0) bestN = spillParent[u];
            if (bestN >= 0 && condElev[bestN] > 0f)
            {
                prelimArea[bestN] += prelimArea[u];
            }
        }

        // Pass 4b: Final convergent D8 routing.
        for (int k = landOrderCount - 1; k >= 0; k--)
        {
            int u = order[k];
            if (condElev[u] <= 0f)
                continue;

            int uy = u / width;
            int ux = u - uy * width;
            int uRank = rank[u];
            float hu = condElev[u];

            if (lakeId[u] > 0)
            {
                int p = spillParent[u];
                if (p >= 0)
                {
                    receiver[u] = p;
                    flowDir[u] = FindD8Direction(ux, uy, p % width, p / width, width);
                }
                continue;
            }

            int bestNeighbor = -1;
            sbyte bestDir = -1;
            float bestScore = 0f;

            for (sbyte d = 0; d < 8; d++)
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

                float drop = hu - condElev[n];
                if (drop <= 0f)
                    continue;

                float dist = (ox != 0 && oy != 0) ? dDiagMeters[uy] : (ox != 0 ? dxMeters[uy] : dyMeters);
                float slope = drop / dist;

                // Valley convergence bias: gently pull adjacent paths on plains into existing larger channels.
                float convergenceBonus = 1.0f + 0.08f * MathF.Log10(1.0f + prelimArea[n] / MathF.Max(1f, cellAreaSqKm[uy]));
                float score = slope * convergenceBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestNeighbor = n;
                    bestDir = d;
                }
            }

            if (bestNeighbor < 0 && spillParent[u] >= 0)
            {
                bestNeighbor = spillParent[u];
                bestDir = FindD8Direction(ux, uy, bestNeighbor % width, bestNeighbor / width, width);
            }

            receiver[u] = bestNeighbor;
            flowDir[u] = bestDir;
        }

        // 5. Accumulate both geometric drainage area (km²) and climate-driven volumetric discharge (m³/s) (HY-4, HY-5, HY-6).
        var drainageAreaSqKm = new float[totalCells];
        var dischargeM3s = new float[totalCells];
        ReadOnlySpan<float> runoffMm = climate.RunoffMmPerYear;

        for (int y = 0; y < height; y++)
        {
            float areaSqKm = cellAreaSqKm[y];
            float areaSqM = areaSqKm * 1e6f;
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                drainageAreaSqKm[i] = areaSqKm;
                // Convert mm/yr * m² -> m³/s
                dischargeM3s[i] = (runoffMm[i] * 1e-3f * areaSqM) / SecondsPerYear;
            }
        }

        for (int k = landOrderCount - 1; k >= 0; k--)
        {
            int u = order[k];
            int r = receiver[u];
            if (r >= 0 && condElev[r] > 0f)
            {
                drainageAreaSqKm[r] += drainageAreaSqKm[u];

                // Account for modest transmission loss in hyper-arid desert cells (P < 120 mm/yr).
                float transmission = climate.PrecipitationMmPerYear[r] < 120f ? 0.985f : 1.0f;
                dischargeM3s[r] += dischargeM3s[u] * transmission;
            }
        }

        // 6. Identify candidate river channel cells (must exceed BOTH minimum discharge and minimum catchment area).
        var isChannel = new bool[totalCells];
        for (int k = landOrderCount - 1; k >= 0; k--)
        {
            int u = order[k];
            if (condElev[u] <= 0f)
                continue;

            if (dischargeM3s[u] >= effectiveDischargeThresholdM3s &&
                drainageAreaSqKm[u] >= effectiveAreaThresholdSqKm)
            {
                isChannel[u] = true;
            }
        }

        // 7. Prune short Order-1 headwater twigs ("comb teeth") shorter than MinHeadwaterReachCells (HY-4).
        if (settings.MinHeadwaterReachCells > 1)
        {
            PruneShortHeadwaterTwigs(
                isChannel, receiver, dischargeM3s,
                effectiveDischargeThresholdM3s, settings.MinHeadwaterReachCells);
        }

        // 8. Compute Strahler stream order and hydraulic channel width on the pruned channel network.
        var riverOrder = new byte[totalCells];
        var maxIncomingOrder = new byte[totalCells];
        var maxIncomingCount = new byte[totalCells];
        var channelWidthMeters = new float[totalCells];
        var waterMask = new WaterBodyKind[totalCells];

        int maxStrahler = 0;
        int riverCellCount = 0;

        for (int i = 0; i < totalCells; i++)
        {
            if (condElev[i] <= 0f)
            {
                waterMask[i] = WaterBodyKind.Ocean;
            }
            else if (lakeId[i] > 0)
            {
                waterMask[i] = WaterBodyKind.Lake;
            }
            else
            {
                waterMask[i] = WaterBodyKind.DryLand;
            }
        }

        for (int k = landOrderCount - 1; k >= 0; k--)
        {
            int u = order[k];
            if (!isChannel[u] || condElev[u] <= 0f)
                continue;

            byte ord;
            if (maxIncomingOrder[u] == 0)
            {
                ord = 1;
            }
            else if (maxIncomingCount[u] >= 2)
            {
                ord = (byte)Math.Min(12, maxIncomingOrder[u] + 1);
            }
            else
            {
                ord = maxIncomingOrder[u];
            }

            riverOrder[u] = ord;
            // Leopold & Maddock hydraulic geometry: W ≈ 7.2 * Q^0.50 meters.
            channelWidthMeters[u] = 7.2f * MathF.Sqrt(MathF.Max(1f, dischargeM3s[u]));

            if (lakeId[u] == 0)
            {
                waterMask[u] = WaterBodyKind.River;
                riverCellCount++;
                if (ord > maxStrahler)
                {
                    maxStrahler = ord;
                }
            }

            int r = receiver[u];
            if (r >= 0 && isChannel[r] && condElev[r] > 0f)
            {
                if (ord > maxIncomingOrder[r])
                {
                    maxIncomingOrder[r] = ord;
                    maxIncomingCount[r] = 1;
                }
                else if (ord == maxIncomingOrder[r])
                {
                    if (maxIncomingCount[r] < 250)
                        maxIncomingCount[r]++;
                }
            }
        }

        // 9. Extract continuous river polylines (HY-4, HY-5).
        List<RiverPolyline> rivers = ExtractRiverPolylines(
            source, condElev, receiver, drainageAreaSqKm, dischargeM3s, channelWidthMeters, riverOrder, lakeId,
            width, dxMeters, dyMeters, dDiagMeters, out double totalRiverLengthKm);

        sw.Stop();

        var hydroMap = new Heightmap(width, height, source.View);
        condElev.CopyTo(hydroMap.Elevations, 0);

        double totalLakeArea = lakes.Sum(l => l.AreaSqKm);

        return new HydrologyResult
        {
            Source = source,
            Climate = climate,
            HydrologicalHeightmap = hydroMap,
            FlowDirection = flowDir,
            ReceiverIndex = receiver,
            DrainageAreaSqKm = drainageAreaSqKm,
            DischargeM3s = dischargeM3s,
            WaterMask = waterMask,
            LakeId = lakeId,
            RiverOrder = riverOrder,
            ChannelWidthMeters = channelWidthMeters,
            Lakes = lakes,
            Rivers = rivers,
            Settings = settings,
            TotalLakeAreaSqKm = totalLakeArea,
            TotalRiverLengthKm = totalRiverLengthKm,
            MaxStrahlerOrder = maxStrahler,
            RiverCellCount = riverCellCount,
            BreachedDepressionCount = breachedCount,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
        };
    }

    private static void PruneShortHeadwaterTwigs(
        bool[] isChannel,
        int[] receiver,
        float[] dischargeM3s,
        float baseDischargeThreshold,
        int minReachCells)
    {
        int total = isChannel.Length;
        var inDegree = new byte[total];

        // Perform 2 passes of twig pruning so tiny Y-forks on hillsides also collapse cleanly.
        for (int pass = 0; pass < 2; pass++)
        {
            Array.Clear(inDegree, 0, total);
            for (int i = 0; i < total; i++)
            {
                if (!isChannel[i])
                    continue;

                int r = receiver[i];
                if (r >= 0 && isChannel[r] && inDegree[r] < 250)
                {
                    inDegree[r]++;
                }
            }

            var twigCells = new List<int>(16);
            for (int start = 0; start < total; start++)
            {
                if (!isChannel[start] || inDegree[start] != 0)
                    continue;

                twigCells.Clear();
                int curr = start;
                float peakQ = 0f;

                while (curr >= 0 && isChannel[curr])
                {
                    if (curr != start && inDegree[curr] >= 2)
                    {
                        break;
                    }

                    twigCells.Add(curr);
                    if (dischargeM3s[curr] > peakQ)
                        peakQ = dischargeM3s[curr];

                    if (twigCells.Count >= minReachCells)
                        break;

                    curr = receiver[curr];
                }

                // Prune short headwater twigs unless they carry major discharge (>2.8x threshold).
                if (twigCells.Count < minReachCells && peakQ < baseDischargeThreshold * 2.8f)
                {
                    foreach (int c in twigCells)
                    {
                        isChannel[c] = false;
                    }
                }
            }
        }
    }

    private static int RunPriorityFlood(
        float[] h,
        int width,
        int height,
        bool wrapsLongitude,
        float[] spillElev,
        int[] spillParent,
        int[] spillRoot,
        int[] order,
        int[] rank)
    {
        int total = width * height;
        var visited = new bool[total];
        var heap = new MinHeap(total / 2 + 1024);
        var flatQueue = new Queue<int>(4096);
        int orderCount = 0;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            bool isTopOrBottom = (y == 0 || y == height - 1);

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                float elev = h[i];
                bool isSideBorder = !wrapsLongitude && (x == 0 || x == width - 1);

                if (elev <= 0f)
                {
                    visited[i] = true;
                    spillElev[i] = elev;
                    spillParent[i] = -1;
                    spillRoot[i] = i;
                    rank[i] = -1;
                }
                else if (isTopOrBottom || isSideBorder)
                {
                    visited[i] = true;
                    spillElev[i] = elev;
                    spillParent[i] = -1;
                    spillRoot[i] = i;
                    heap.Push(elev, i);
                }
            }
        }

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
                    spillParent[i] = oceanNeighbor;
                    spillRoot[i] = i;
                    heap.Push(h[i], i);
                }
            }
        }

        if (heap.Count == 0)
        {
            for (int i = 0; i < total; i++)
            {
                if (!visited[i])
                {
                    visited[i] = true;
                    spillElev[i] = h[i];
                    spillParent[i] = -1;
                    spillRoot[i] = i;
                    heap.Push(h[i], i);
                    break;
                }
            }
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
                spillParent[n] = c;
                float hn = h[n];

                if (hn <= cSpill)
                {
                    spillElev[n] = cSpill + 1e-4f;
                    spillRoot[n] = spillRoot[c];
                    flatQueue.Enqueue(n);
                }
                else
                {
                    spillElev[n] = hn;
                    spillRoot[n] = n;
                    heap.Push(hn, n);
                }
            }
        }

        return orderCount;
    }

    private static int ExtractLakesAndBreachSills(
        Heightmap map,
        float[] rawElev,
        float[] filledElev,
        float[] condElev,
        int[] spillParent,
        float[] precipMm,
        float[] petMm,
        int width,
        int height,
        bool wrapsLongitude,
        float[] cellAreaSqKm,
        HydrologySettings settings,
        int[] lakeId,
        List<LakeFeature> lakes)
    {
        int total = width * height;
        var inDepression = new bool[total];
        for (int i = 0; i < total; i++)
        {
            if (rawElev[i] > 0f && filledElev[i] - rawElev[i] > 0.25f)
            {
                inDepression[i] = true;
            }
        }

        var visited = new bool[total];
        var compCells = new List<int>(256);
        var queue = new Queue<int>(256);
        int breachedCount = 0;
        int nextLakeId = 1;

        for (int start = 0; start < total; start++)
        {
            if (!inDepression[start] || visited[start])
                continue;

            compCells.Clear();
            queue.Clear();
            visited[start] = true;
            queue.Enqueue(start);

            float minFloor = rawElev[start];
            float maxSpill = filledElev[start];
            int lowestCell = start;
            double sumPrecip = 0.0;
            double sumPet = 0.0;

            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                compCells.Add(c);
                sumPrecip += precipMm[c];
                sumPet += petMm[c];

                if (rawElev[c] < minFloor)
                {
                    minFloor = rawElev[c];
                    lowestCell = c;
                }

                if (filledElev[c] > maxSpill)
                {
                    maxSpill = filledElev[c];
                }

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
                    if (inDepression[n] && !visited[n])
                    {
                        visited[n] = true;
                        queue.Enqueue(n);
                    }
                }
            }

            // Climate water-balance gate: in arid basins (P / PET < 0.35) or polar permafrost (>56°), cap sill fill depth.
            double aridityIndex = sumPet > 0 ? sumPrecip / sumPet : 1.0;
            double approxLat = Math.Abs(map.LatitudeAt(lowestCell / width));
            float polarFactor = 1.0f - 0.65f * MathUtils.SmoothStep(54f, 72f, (float)approxLat);
            float climateDepthFactor = Math.Clamp((float)((aridityIndex - 0.25) / 0.55), 0.0f, 1.0f) * polarFactor;
            float effectiveMaxDepth = settings.MaxLakeDepthMeters * climateDepthFactor;

            float targetSurface = MathF.Min(maxSpill, minFloor + effectiveMaxDepth);

            int resScale = Math.Max(1, (width * height) / (1024 * 512));
            int effectiveMinLakeCells = settings.MinLakeCells * (1 + (resScale - 1) / 2);

            var inundated = new List<int>(compCells.Count);
            foreach (int c in compCells)
            {
                if (rawElev[c] < targetSurface - 0.1f)
                {
                    inundated.Add(c);
                }
            }

            float maxDepth = targetSurface - minFloor;
            bool qualifiesAsLake = inundated.Count >= effectiveMinLakeCells && maxDepth >= settings.MinLakeDepthMeters;

            if (qualifiesAsLake)
            {
                int boundaryEdges = 0;
                var inSet = new HashSet<int>(inundated);
                foreach (int c in inundated)
                {
                    int cy = c / width;
                    int cx = c - cy * width;
                    for (int d = 0; d < 8; d += 2)
                    {
                        var (ox, oy) = D8Offsets[d];
                        int ny = cy + oy;
                        int nx = cx + ox;
                        if (wrapsLongitude)
                            nx = (nx + width) % width;

                        if (ny < 0 || ny >= height || nx < 0 || nx >= width || !inSet.Contains(ny * width + nx))
                        {
                            boundaryEdges++;
                        }
                    }
                }

                float compactness = boundaryEdges > 0
                    ? (float)(4.0 * Math.PI * inundated.Count / (boundaryEdges * boundaryEdges))
                    : 0f;

                if (compactness < settings.MinLakeCompactness)
                {
                    qualifiesAsLake = false;
                }
            }

            if (!qualifiesAsLake)
            {
                BreachPath(condElev, spillParent, lowestCell, minFloor);
                breachedCount++;
                continue;
            }

            if (targetSurface < maxSpill - 0.05f)
            {
                BreachPath(condElev, spillParent, lowestCell, targetSurface);
            }

            int id = nextLakeId++;
            double areaSqKm = 0.0;
            double depthSum = 0.0;
            double latWeightedSum = 0.0;
            double lonWeightedSum = 0.0;
            int spillCell = inundated[0];

            foreach (int c in inundated)
            {
                lakeId[c] = id;
                condElev[c] = targetSurface;

                int cy = c / width;
                int cx = c - cy * width;
                float a = cellAreaSqKm[cy];
                areaSqKm += a;
                depthSum += (targetSurface - rawElev[c]) * a;
                latWeightedSum += map.LatitudeAt(cy) * a;
                lonWeightedSum += map.LongitudeAt(cx) * a;

                int p = spillParent[c];
                if (p >= 0 && (!inDepression[p] || rawElev[p] >= targetSurface - 0.1f))
                {
                    spillCell = c;
                }
            }

            int sx = spillCell % width;
            int sy = spillCell / width;

            lakes.Add(new LakeFeature(
                Id: id,
                SurfaceElevationMeters: targetSurface,
                MaxDepthMeters: maxDepth,
                MeanDepthMeters: areaSqKm > 0 ? (float)(depthSum / areaSqKm) : maxDepth * 0.5f,
                AreaSqKm: areaSqKm,
                CellCount: inundated.Count,
                SpillPixelX: sx,
                SpillPixelY: sy,
                SpillLatitude: map.LatitudeAt(sy),
                SpillLongitude: map.LongitudeAt(sx),
                CentroidLatitude: areaSqKm > 0 ? latWeightedSum / areaSqKm : map.LatitudeAt(sy),
                CentroidLongitude: areaSqKm > 0 ? lonWeightedSum / areaSqKm : map.LongitudeAt(sx)));
        }

        return breachedCount;
    }

    private static void BreachPath(float[] condElev, int[] spillParent, int startCell, float startElev)
    {
        int curr = spillParent[startCell];
        int steps = 0;
        float target = startElev;

        while (curr >= 0 && steps < 128)
        {
            steps++;
            if (condElev[curr] <= target)
                break;

            condElev[curr] = MathF.Max(0.1f, target);
            target = MathF.Max(0.1f, target - 1e-3f);
            curr = spillParent[curr];
        }
    }

    private static List<RiverPolyline> ExtractRiverPolylines(
        Heightmap map,
        float[] condElev,
        int[] receiver,
        float[] drainageAreaSqKm,
        float[] dischargeM3s,
        float[] channelWidthMeters,
        byte[] riverOrder,
        int[] lakeId,
        int width,
        float[] dxMeters,
        float dyMeters,
        float[] dDiagMeters,
        out double totalLengthKm)
    {
        int total = condElev.Length;
        var riverInDegree = new byte[total];

        for (int i = 0; i < total; i++)
        {
            if (riverOrder[i] == 0)
                continue;

            int r = receiver[i];
            if (r >= 0 && riverOrder[r] > 0)
            {
                if (riverInDegree[r] < 250)
                    riverInDegree[r]++;
            }
        }

        var polylines = new List<RiverPolyline>();
        var visitedSegmentStart = new bool[total];
        int nextRiverId = 1;
        totalLengthKm = 0.0;

        for (int i = 0; i < total; i++)
        {
            if (riverOrder[i] == 0)
                continue;

            if (riverInDegree[i] != 0 && riverInDegree[i] < 2)
                continue;

            if (visitedSegmentStart[i])
                continue;

            visitedSegmentStart[i] = true;
            var vertices = new List<RiverVertex>(32);
            int curr = i;
            int maxOrder = 0;
            float maxArea = 0f;
            float maxQ = 0f;
            float maxWidth = 0f;
            double lengthKm = 0.0;
            bool drainsToOcean = false;
            int? terminalLakeId = null;

            int guard = 0;
            while (curr >= 0 && guard++ < 8192)
            {
                int cy = curr / width;
                int cx = curr - cy * width;
                byte ord = Math.Max((byte)1, riverOrder[curr]);
                float area = drainageAreaSqKm[curr];
                float q = dischargeM3s[curr];
                float w = channelWidthMeters[curr];

                if (ord > maxOrder) maxOrder = ord;
                if (area > maxArea) maxArea = area;
                if (q > maxQ) maxQ = q;
                if (w > maxWidth) maxWidth = w;

                vertices.Add(new RiverVertex(
                    PixelX: cx,
                    PixelY: cy,
                    Latitude: map.LatitudeAt(cy),
                    Longitude: map.LongitudeAt(cx),
                    ElevationMeters: condElev[curr],
                    DrainageAreaSqKm: area,
                    DischargeM3s: q,
                    EstimatedWidthMeters: w,
                    StrahlerOrder: ord));

                int next = receiver[curr];
                if (next < 0)
                    break;

                int ny = next / width;
                int nx = next - ny * width;
                int ox = Math.Abs(nx - cx);
                if (ox > 1) ox = 1;
                int oy = Math.Abs(ny - cy);
                float stepMeters = (ox != 0 && oy != 0) ? dDiagMeters[cy] : (ox != 0 ? dxMeters[cy] : dyMeters);
                lengthKm += stepMeters * 1e-3;

                if (condElev[next] <= 0f)
                {
                    drainsToOcean = true;
                    vertices.Add(new RiverVertex(
                        PixelX: nx,
                        PixelY: ny,
                        Latitude: map.LatitudeAt(ny),
                        Longitude: map.LongitudeAt(nx),
                        ElevationMeters: 0f,
                        DrainageAreaSqKm: area,
                        DischargeM3s: q,
                        EstimatedWidthMeters: w,
                        StrahlerOrder: ord));
                    break;
                }

                if (lakeId[next] > 0 && lakeId[curr] == 0)
                {
                    terminalLakeId = lakeId[next];
                }

                if (riverInDegree[next] >= 2 && next != i)
                {
                    vertices.Add(new RiverVertex(
                        PixelX: nx,
                        PixelY: ny,
                        Latitude: map.LatitudeAt(ny),
                        Longitude: map.LongitudeAt(nx),
                        ElevationMeters: condElev[next],
                        DrainageAreaSqKm: drainageAreaSqKm[next],
                        DischargeM3s: dischargeM3s[next],
                        EstimatedWidthMeters: channelWidthMeters[next],
                        StrahlerOrder: Math.Max((byte)1, riverOrder[next])));
                    break;
                }

                if (riverOrder[next] == 0)
                    break;

                curr = next;
            }

            if (vertices.Count >= 2)
            {
                totalLengthKm += lengthKm;
                polylines.Add(new RiverPolyline(
                    Id: nextRiverId++,
                    MaxStrahlerOrder: maxOrder,
                    MaxDrainageAreaSqKm: maxArea,
                    MaxDischargeM3s: maxQ,
                    MaxWidthMeters: maxWidth,
                    LengthKm: Math.Round(lengthKm, 2),
                    DrainsToOcean: drainsToOcean,
                    TerminalLakeId: terminalLakeId,
                    Vertices: vertices));
            }
        }

        return polylines;
    }

    private static sbyte FindD8Direction(int x0, int y0, int x1, int y1, int width)
    {
        int dx = x1 - x0;
        if (dx > 1) dx -= width;
        else if (dx < -1) dx += width;

        int dy = y1 - y0;
        for (sbyte d = 0; d < 8; d++)
        {
            if (D8Offsets[d].Dx == dx && D8Offsets[d].Dy == dy)
                return d;
        }

        return 0;
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

    private sealed class MinHeap(int initialCapacity)
    {
        private float[] _keys = new float[Math.Max(16, initialCapacity)];
        private int[] _values = new int[Math.Max(16, initialCapacity)];
        private int _count;

        public int Count => _count;

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
