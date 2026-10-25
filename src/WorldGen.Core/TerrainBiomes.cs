using System.Diagnostics;
using System.Numerics;

namespace WorldGen;

/// <summary>
/// Tunable parameters for Stage 5 biome classification. All values are primitives so the reflected cache
/// key changes automatically. Palette selection is deliberately absent: palettes are presentation only and
/// must not invalidate the cached classification (BI-2).
/// </summary>
public sealed record BiomeSettings
{
    /// <summary>Named biome set from <c>biomes.json</c> (BI-5).</summary>
    public string BiomeSetId { get; init; } = "whittaker";

    /// <summary>Must match the set's declared version; bump together with the table content.</summary>
    public int BiomeSetVersion { get; init; } = 1;

    /// <summary>Maximum moisture added immediately beside a river or lake (BI-3).</summary>
    public float RiparianStrength { get; init; } = 0.35f;

    /// <summary>E-folding distance in cells for the riparian moisture bonus (BI-3).</summary>
    public float RiparianScaleCells { get; init; } = 2.5f;

    /// <summary>Maximum humidity added immediately beside the ocean (BI-3).</summary>
    public float CoastalHumidityStrength { get; init; } = 0.12f;

    /// <summary>E-folding distance in cells for the coastal humidity bonus (BI-3).</summary>
    public float CoastalScaleCells { get; init; } = 6f;

    /// <summary>Strength of the maritime temperature moderation on coastal highlands (BI-3).</summary>
    public float CoastalTempModeration { get; init; } = 0.2f;

    /// <summary>Ocean cells colder than this become sea ice.</summary>
    public float SeaIceTemperatureC { get; init; } = -2f;

    /// <summary>Amplitude in degrees Celsius of the sphere-noise ecotone perturbation (BI-6).</summary>
    public float EcotoneTempAmplitudeC { get; init; } = 2.5f;

    /// <summary>Amplitude of the sphere-noise ecotone perturbation on moisture (BI-6).</summary>
    public float EcotoneMoistureAmplitude { get; init; } = 0.06f;

    /// <summary>Wavelength in kilometres of the ecotone field; larger means broader wobble (BI-6).</summary>
    public float EcotoneWavelengthKm { get; init; } = 320f;

    /// <summary>Fractal octaves for the ecotone field (0 disables it) (BI-6).</summary>
    public int EcotoneOctaves { get; init; } = 3;

    /// <summary>Deterministic majority-filter passes after classification (BI-4).</summary>
    public int SmoothingPasses { get; init; } = 1;

    /// <summary>Majority-filter window radius in cells (BI-4).</summary>
    public int SmoothingRadiusCells { get; init; } = 1;
}

/// <summary>Complete output of Stage 5 biome classification (BI-1 through BI-6).</summary>
public sealed class BiomeResult
{
    /// <summary>The Stage 4 hydrological heightmap used as the elevation input.</summary>
    public required Heightmap Elevation { get; init; }

    public required HydrologyResult Hydrology { get; init; }

    /// <summary>Biome id per cell: 0 unclassified, 1 ocean, 2 lake, 3 river, 4 sea ice, 16+ land biomes.</summary>
    public required byte[] BiomeId { get; init; }

    /// <summary>Effective moisture in [0, 1] per cell (diagnostic).</summary>
    public required float[] Moisture { get; init; }

    public required BiomeSettings Settings { get; init; }

    public required BiomeSet Set { get; init; }

    public required BiomePalette Palette { get; init; }

    /// <summary>Cell counts by biome id, ordered by id.</summary>
    public required IReadOnlyDictionary<int, int> BiomeCellCounts { get; init; }

    public double LandFraction { get; init; }

    public int UnclassifiedLandCells { get; init; }

    public double ElapsedSeconds { get; init; }
}

/// <summary>
/// Stage 5 biome classifier. Consumes the Stage 4 hydrology result (which carries the Stage 3 climate fields
/// and the eroded terrain), derives moisture/elevation indices, and resolves every cell to a biome id from a
/// named, versioned set. Water ids are a deterministic projection of Stage 4's water mask.
/// </summary>
public static class TerrainBiomes
{
    public static BiomeResult Analyze(
        HydrologyResult hydrology,
        BiomeSettings settings,
        int planetSeed,
        float planetRadiusMeters = 6_371_000f,
        ParallelProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(hydrology);
        ArgumentNullException.ThrowIfNull(settings);

        var stopwatch = Stopwatch.StartNew();

        BiomeSet set = BiomeTable.Default.GetSet(settings.BiomeSetId);
        if (settings.BiomeSetVersion != set.Version)
        {
            throw new InvalidOperationException(
                $"Biome set '{set.Id}' is version {set.Version} but settings request version {settings.BiomeSetVersion}. " +
                "Bump BiomeSetVersion together with the table content.");
        }

        Heightmap map = hydrology.HydrologicalHeightmap;
        ClimateResult climate = hydrology.Climate;
        int width = map.Width;
        int height = map.Height;
        int count = width * height;
        bool wrapsLongitude = map.View.LongitudeSpan >= 359.999;

        WaterBodyKind[] water = hydrology.WaterMask;
        float[] elev = map.Elevations;
        float[] temp = climate.TemperatureCelsius;
        float[] precip = climate.PrecipitationMmPerYear;
        float[] pet = climate.PotentialEvaporationMmPerYear;

        progress?.SetAll(0.05f);
        int[] distWater = DistanceTransform(width, height, wrapsLongitude, i => water[i] is WaterBodyKind.River or WaterBodyKind.Lake);
        progress?.SetAll(0.2f);
        int[] distOcean = DistanceTransform(width, height, wrapsLongitude, i => water[i] == WaterBodyKind.Ocean);
        progress?.SetAll(0.3f);

        float riparianScaleCells = MathF.Max(0.25f, settings.RiparianScaleCells);
        float coastalScaleCells = MathF.Max(0.25f, settings.CoastalScaleCells);

        float tEq = climate.Settings.EquatorialTempCelsius;
        float tPole = climate.Settings.PolarTempCelsius;

        float ecotoneFrequency = (float)(planetRadiusMeters / Math.Max(1000.0, settings.EcotoneWavelengthKm * 1000.0));
        int octaves = Math.Max(0, settings.EcotoneOctaves);
        bool ecotoneEnabled = octaves > 0 &&
                              (MathF.Abs(settings.EcotoneTempAmplitudeC) > 0f ||
                               MathF.Abs(settings.EcotoneMoistureAmplitude) > 0f);
        var tempNoise = new PerlinNoise(MathUtils.DeriveSeed(planetSeed, 71));
        var moistureNoise = new PerlinNoise(MathUtils.DeriveSeed(planetSeed, 72));

        var biomeId = new byte[count];
        var moistureArr = new float[count];
        BiomeDefinition[] rules = set.Biomes.ToArray();
        float seaIceTemperature = settings.SeaIceTemperatureC;

        ParallelProgress.ForRange(height, progress, y =>
        {
            double latRad = MathUtils.ToRadians(map.LatitudeAt(y));
            float sinLat = (float)Math.Sin(latRad);
            float zonalSeaLevel = tEq - (tEq - tPole) * (sinLat * sinLat) + 2.2f * (float)Math.Cos(2.0 * latRad);
            var indices = new float[BiomeIndices.Count];
            int row = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float e = elev[i];
                WaterBodyKind cell = water[i];

                float distWaterCells = distWater[i] == int.MaxValue ? 1e9f : distWater[i];
                float distOceanCells = distOcean[i] == int.MaxValue ? 1e9f : distOcean[i];
                float riparian = MathF.Exp(-distWaterCells / riparianScaleCells);
                float coastal = MathF.Exp(-distOceanCells / coastalScaleCells);

                float moistureIndex = precip[i] / (precip[i] + pet[i] + 1e-3f);
                float moisture = Math.Clamp(
                    moistureIndex + settings.RiparianStrength * riparian + settings.CoastalHumidityStrength * coastal,
                    0f,
                    1f);

                float t = temp[i];
                if (settings.CoastalTempModeration > 0f && e > 0f)
                {
                    // Maritime coasts are less thermally extreme: pull a fraction toward the latitude's
                    // zonal sea-level temperature, so coastal highlands shed part of their lapse cooling.
                    t += (zonalSeaLevel - t) * settings.CoastalTempModeration * coastal;
                }

                if (ecotoneEnabled && e > 0f)
                {
                    Vector3 direction = map.DirectionAt(x, y) * ecotoneFrequency;
                    float nt = tempNoise.Fractal(direction, octaves) * 2f - 1f;
                    float nm = moistureNoise.Fractal(direction, octaves) * 2f - 1f;
                    t += settings.EcotoneTempAmplitudeC * nt;
                    moisture = Math.Clamp(moisture + settings.EcotoneMoistureAmplitude * nm, 0f, 1f);
                }

                moistureArr[i] = moisture;

                if (cell != WaterBodyKind.DryLand)
                {
                    biomeId[i] = cell switch
                    {
                        WaterBodyKind.Ocean => temp[i] < seaIceTemperature ? (byte)4 : (byte)1,
                        WaterBodyKind.Lake => 2,
                        WaterBodyKind.River => 3,
                        _ => 1,
                    };
                    continue;
                }

                indices[BiomeIndices.TemperatureC] = t;
                indices[BiomeIndices.Moisture] = moisture;
                indices[BiomeIndices.ElevationM] = e;
                indices[BiomeIndices.Riparian] = riparian;
                indices[BiomeIndices.Coastal] = coastal;

                byte id = 0;
                for (int r = 0; r < rules.Length; r++)
                {
                    if (rules[r].Matches(indices))
                    {
                        id = (byte)rules[r].Id;
                        break;
                    }
                }

                biomeId[i] = id;
            }
        }, 0.3f, 0.8f);

        progress?.SetAll(0.8f);

        if (settings.SmoothingPasses > 0 && settings.SmoothingRadiusCells > 0)
        {
            var land = new bool[count];
            for (int i = 0; i < count; i++)
            {
                land[i] = water[i] == WaterBodyKind.DryLand;
            }

            int radius = Math.Clamp(settings.SmoothingRadiusCells, 1, 8);
            for (int pass = 0; pass < settings.SmoothingPasses; pass++)
            {
                biomeId = SmoothOnce(biomeId, width, height, wrapsLongitude, radius, land);
            }
        }

        progress?.SetAll(0.95f);

        var counts = new Dictionary<int, int>();
        int unclassified = 0;
        for (int i = 0; i < count; i++)
        {
            byte id = biomeId[i];
            counts[id] = counts.TryGetValue(id, out int existing) ? existing + 1 : 1;
            if (id == 0 && water[i] == WaterBodyKind.DryLand)
            {
                unclassified++;
            }
        }

        IReadOnlyDictionary<int, int> orderedCounts = counts
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var (_, _, _, landFraction) = map.ComputeStatistics();
        progress?.Complete();

        return new BiomeResult
        {
            Elevation = map,
            Hydrology = hydrology,
            BiomeId = biomeId,
            Moisture = moistureArr,
            Settings = settings,
            Set = set,
            Palette = set.Palette(),
            BiomeCellCounts = orderedCounts,
            LandFraction = landFraction,
            UnclassifiedLandCells = unclassified,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }

    /// <summary>
    /// Multi-source breadth-first distance (in cells) from every cell to the nearest source cell. Longitude wraps
    /// when the view spans the whole planet, so coasts and rivers are continuous across the antimeridian.
    /// </summary>
    private static int[] DistanceTransform(int width, int height, bool wraps, Func<int, bool> isSource)
    {
        int count = width * height;
        var distance = new int[count];
        var queue = new int[count];
        int head = 0;
        int tail = 0;

        Array.Fill(distance, int.MaxValue);
        for (int i = 0; i < count; i++)
        {
            if (isSource(i))
            {
                distance[i] = 0;
                queue[tail++] = i;
            }
        }

        while (head < tail)
        {
            int i = queue[head++];
            int next = distance[i] + 1;
            int x = i % width;
            int y = i / width;

            if (x > 0)
            {
                Push(i - 1);
            }
            else if (wraps)
            {
                Push(i + width - 1);
            }

            if (x < width - 1)
            {
                Push(i + 1);
            }
            else if (wraps)
            {
                Push(i - width + 1);
            }

            if (y > 0)
            {
                Push(i - width);
            }

            if (y < height - 1)
            {
                Push(i + width);
            }

            void Push(int neighbour)
            {
                if (distance[neighbour] > next)
                {
                    distance[neighbour] = next;
                    queue[tail++] = neighbour;
                }
            }
        }

        return distance;
    }

    /// <summary>
    /// One deterministic majority-filter pass over land cells only. Ties resolve to the lowest biome id. Water
    /// cells are never changed. The halo for a regional tile is <c>radius</c> cells per pass.
    /// </summary>
    private static byte[] SmoothOnce(byte[] source, int width, int height, bool wraps, int radius, bool[] land)
    {
        var result = new byte[source.Length];
        Array.Copy(source, result, source.Length);

        Parallel.For(0, height, y =>
        {
            var counts = new int[256];
            var touched = new int[256];

            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(height - 1, y + radius);

            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (!land[i])
                {
                    continue;
                }

                int touchedCount = 0;
                int bestId = -1;
                int bestCount = -1;

                for (int ny = y0; ny <= y1; ny++)
                {
                    int row = ny * width;
                    for (int nxRaw = x - radius; nxRaw <= x + radius; nxRaw++)
                    {
                        int nx = nxRaw;
                        if (nx < 0)
                        {
                            if (!wraps)
                            {
                                continue;
                            }

                            nx += width;
                        }
                        else if (nx >= width)
                        {
                            if (!wraps)
                            {
                                continue;
                            }

                            nx -= width;
                        }

                        int neighbour = row + nx;
                        if (!land[neighbour])
                        {
                            continue;
                        }

                        int id = source[neighbour];
                        if (counts[id] == 0)
                        {
                            touched[touchedCount++] = id;
                        }

                        counts[id]++;
                        int c = counts[id];
                        if (c > bestCount || (c == bestCount && id < bestId))
                        {
                            bestCount = c;
                            bestId = id;
                        }
                    }
                }

                if (bestId >= 0)
                {
                    result[i] = (byte)bestId;
                }

                for (int k = 0; k < touchedCount; k++)
                {
                    counts[touched[k]] = 0;
                }
            }
        });

        return result;
    }
}
