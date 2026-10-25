using System.Text;
using System.Text.Json;

namespace WorldGen;

/// <summary>
/// Typed conversions between pipeline results and <see cref="StageBundle"/>s. The public GeoTIFF/PNG exports
/// are not used for cache reuse: downstream stages and diagnostics need fields that are not in those files.
/// </summary>
public static class TerrainStageCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static StageBundle CreateStage1(string key, TerrainSettings settings, Heightmap map)
    {
        StageBundle bundle = CreateBase("s1", key, string.Empty, ModelVersions.BaseTopology, settings, map);
        bundle.FloatArrays["height"] = map.Elevations;
        return bundle;
    }

    public static Heightmap ReadStage1(StageBundle bundle) => ReadMap(bundle, "height");

    public static StageBundle CreateStage2(string key, string inputKey, ErosionResult result)
    {
        StageBundle bundle = CreateBase("s2", key, inputKey, ModelVersions.Erosion, result.Settings, result.Eroded);
        int count = result.Eroded.Width * result.Eroded.Height;

        bundle.FloatArrays["height"] = result.Eroded.Elevations;
        bundle.FloatArrays["erosionDepth"] = RequireLength(result.ErosionDepthMeters, count, "ErosionDepthMeters");
        bundle.FloatArrays["sediment"] = RequireLength(result.SedimentThicknessMeters, count, "SedimentThicknessMeters");
        bundle.FloatArrays["drainage"] = RequireLength(result.DrainageAreaSqKm, count, "DrainageAreaSqKm");
        bundle.MetricsJson = JsonSerializer.Serialize(new ErosionMetrics(
            result.MeanErosionMeters,
            result.MaxErosionMeters,
            result.MeanDepositionMeters,
            result.MaxDepositionMeters,
            result.CoastlineProgradedCells,
            result.ElapsedSeconds), JsonOptions);

        return bundle;
    }

    public static ErosionResult ReadStage2(StageBundle bundle, Heightmap source)
    {
        Heightmap eroded = ReadMap(bundle, "height");
        int count = eroded.Width * eroded.Height;
        ErosionSettings settings = ReadSettings<ErosionSettings>(bundle);
        ErosionMetrics metrics = ReadMetrics<ErosionMetrics>(bundle);

        return new ErosionResult
        {
            Source = source,
            Eroded = eroded,
            ErosionDepthMeters = RequireArray(bundle.FloatArrays, "erosionDepth", count),
            SedimentThicknessMeters = RequireArray(bundle.FloatArrays, "sediment", count),
            DrainageAreaSqKm = RequireArray(bundle.FloatArrays, "drainage", count),
            Settings = settings,
            MeanErosionMeters = metrics.MeanErosionMeters,
            MaxErosionMeters = metrics.MaxErosionMeters,
            MeanDepositionMeters = metrics.MeanDepositionMeters,
            MaxDepositionMeters = metrics.MaxDepositionMeters,
            CoastlineProgradedCells = metrics.CoastlineProgradedCells,
            ElapsedSeconds = metrics.ElapsedSeconds,
        };
    }

    public static StageBundle CreateStage3(string key, string inputKey, ClimateResult result)
    {
        Heightmap source = result.Source;
        StageBundle bundle = CreateBase("s3", key, inputKey, ModelVersions.Climate, result.Settings, source);
        int count = source.Width * source.Height;

        bundle.FloatArrays["temperature"] = RequireLength(result.TemperatureCelsius, count, "TemperatureCelsius");
        bundle.FloatArrays["precipitation"] = RequireLength(result.PrecipitationMmPerYear, count, "PrecipitationMmPerYear");
        bundle.FloatArrays["potentialEvaporation"] = RequireLength(result.PotentialEvaporationMmPerYear, count, "PotentialEvaporationMmPerYear");
        bundle.FloatArrays["runoff"] = RequireLength(result.RunoffMmPerYear, count, "RunoffMmPerYear");
        bundle.MetricsJson = JsonSerializer.Serialize(new ClimateMetrics(
            result.MeanLandTemperatureCelsius,
            result.MeanLandPrecipitationMm,
            result.MaxLandPrecipitationMm,
            result.MeanLandRunoffMm,
            result.AridLandFraction,
            result.HumidLandFraction,
            result.ElapsedSeconds), JsonOptions);

        return bundle;
    }

    public static ClimateResult ReadStage3(StageBundle bundle, Heightmap source)
    {
        int count = source.Width * source.Height;
        ClimateSettings settings = ReadSettings<ClimateSettings>(bundle);
        ClimateMetrics metrics = ReadMetrics<ClimateMetrics>(bundle);

        return new ClimateResult
        {
            Source = source,
            TemperatureCelsius = RequireArray(bundle.FloatArrays, "temperature", count),
            PrecipitationMmPerYear = RequireArray(bundle.FloatArrays, "precipitation", count),
            PotentialEvaporationMmPerYear = RequireArray(bundle.FloatArrays, "potentialEvaporation", count),
            RunoffMmPerYear = RequireArray(bundle.FloatArrays, "runoff", count),
            Settings = settings,
            MeanLandTemperatureCelsius = metrics.MeanLandTemperatureCelsius,
            MeanLandPrecipitationMm = metrics.MeanLandPrecipitationMm,
            MaxLandPrecipitationMm = metrics.MaxLandPrecipitationMm,
            MeanLandRunoffMm = metrics.MeanLandRunoffMm,
            AridLandFraction = metrics.AridLandFraction,
            HumidLandFraction = metrics.HumidLandFraction,
            ElapsedSeconds = metrics.ElapsedSeconds,
        };
    }

    public static StageBundle CreateStage4(string key, string inputKey, HydrologyResult result)
    {
        Heightmap map = result.HydrologicalHeightmap;
        StageBundle bundle = CreateBase("s4", key, inputKey, ModelVersions.Hydrology, result.Settings, map);
        int count = map.Width * map.Height;

        bundle.FloatArrays["height"] = map.Elevations;
        bundle.SByteArrays["flow"] = RequireLength(result.FlowDirection, count, "FlowDirection");
        bundle.IntArrays["receiver"] = RequireLength(result.ReceiverIndex, count, "ReceiverIndex");
        bundle.FloatArrays["drainage"] = RequireLength(result.DrainageAreaSqKm, count, "DrainageAreaSqKm");
        bundle.FloatArrays["discharge"] = RequireLength(result.DischargeM3s, count, "DischargeM3s");
        bundle.IntArrays["lakeId"] = RequireLength(result.LakeId, count, "LakeId");
        bundle.ByteArrays["riverOrder"] = RequireLength(result.RiverOrder, count, "RiverOrder");
        bundle.FloatArrays["channelWidth"] = RequireLength(result.ChannelWidthMeters, count, "ChannelWidthMeters");

        var waterMask = new byte[count];
        for (int i = 0; i < count; i++)
        {
            waterMask[i] = (byte)result.WaterMask[i];
        }

        bundle.ByteArrays["waterMask"] = waterMask;
        bundle.ByteArrays["vectors"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new HydrologyVectors(result.Lakes.ToList(), result.Rivers.ToList()), JsonOptions));
        bundle.MetricsJson = JsonSerializer.Serialize(new HydrologyMetrics(
            result.TotalLakeAreaSqKm,
            result.TotalRiverLengthKm,
            result.MaxStrahlerOrder,
            result.RiverCellCount,
            result.BreachedDepressionCount,
            result.ElapsedSeconds), JsonOptions);

        return bundle;
    }

    public static HydrologyResult ReadStage4(StageBundle bundle, Heightmap source, ClimateResult climate)
    {
        Heightmap map = ReadMap(bundle, "height");
        int count = map.Width * map.Height;
        HydrologySettings settings = ReadSettings<HydrologySettings>(bundle);
        HydrologyMetrics metrics = ReadMetrics<HydrologyMetrics>(bundle);

        byte[] waterBytes = RequireArray(bundle.ByteArrays, "waterMask", count);
        var waterMask = new WaterBodyKind[count];
        for (int i = 0; i < count; i++)
        {
            waterMask[i] = (WaterBodyKind)waterBytes[i];
        }

        HydrologyVectors vectors = bundle.ByteArrays.TryGetValue("vectors", out byte[]? vectorBytes)
            ? JsonSerializer.Deserialize<HydrologyVectors>(vectorBytes, JsonOptions)
              ?? new HydrologyVectors(new List<LakeFeature>(), new List<RiverPolyline>())
            : new HydrologyVectors(new List<LakeFeature>(), new List<RiverPolyline>());

        return new HydrologyResult
        {
            Source = source,
            Climate = climate,
            HydrologicalHeightmap = map,
            FlowDirection = RequireArray(bundle.SByteArrays, "flow", count),
            ReceiverIndex = RequireArray(bundle.IntArrays, "receiver", count),
            DrainageAreaSqKm = RequireArray(bundle.FloatArrays, "drainage", count),
            DischargeM3s = RequireArray(bundle.FloatArrays, "discharge", count),
            WaterMask = waterMask,
            LakeId = RequireArray(bundle.IntArrays, "lakeId", count),
            RiverOrder = RequireArray(bundle.ByteArrays, "riverOrder", count),
            ChannelWidthMeters = RequireArray(bundle.FloatArrays, "channelWidth", count),
            Lakes = vectors.Lakes,
            Rivers = vectors.Rivers,
            Settings = settings,
            TotalLakeAreaSqKm = metrics.TotalLakeAreaSqKm,
            TotalRiverLengthKm = metrics.TotalRiverLengthKm,
            MaxStrahlerOrder = metrics.MaxStrahlerOrder,
            RiverCellCount = metrics.RiverCellCount,
            BreachedDepressionCount = metrics.BreachedDepressionCount,
            ElapsedSeconds = metrics.ElapsedSeconds,
        };
    }

    public static StageBundle CreateStage5(string key, string inputKey, BiomeResult result)
    {
        Heightmap map = result.Elevation;
        StageBundle bundle = CreateBase("s5", key, inputKey, ModelVersions.Biomes, result.Settings, map);
        int count = map.Width * map.Height;

        bundle.FloatArrays["height"] = map.Elevations;
        bundle.ByteArrays["biomeId"] = RequireLength(result.BiomeId, count, "BiomeId");
        bundle.FloatArrays["moisture"] = RequireLength(result.Moisture, count, "Moisture");
        bundle.MetricsJson = JsonSerializer.Serialize(new BiomeMetrics(
            result.Set.Id,
            result.Set.Version,
            result.UnclassifiedLandCells,
            result.LandFraction,
            result.BiomeCellCounts.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
            result.ElapsedSeconds), JsonOptions);

        return bundle;
    }

    public static BiomeResult ReadStage5(StageBundle bundle, HydrologyResult hydrology)
    {
        Heightmap map = ReadMap(bundle, "height");
        int count = map.Width * map.Height;
        BiomeSettings settings = ReadSettings<BiomeSettings>(bundle);
        BiomeMetrics metrics = ReadMetrics<BiomeMetrics>(bundle);

        BiomeSet set = BiomeTable.Default.GetSet(metrics.BiomeSetId ?? settings.BiomeSetId);
        IReadOnlyDictionary<int, int> counts = metrics.BiomeCells is { Count: > 0 }
            ? metrics.BiomeCells.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value)
            : new Dictionary<int, int>();

        return new BiomeResult
        {
            Elevation = map,
            Hydrology = hydrology,
            BiomeId = RequireArray(bundle.ByteArrays, "biomeId", count),
            Moisture = RequireArray(bundle.FloatArrays, "moisture", count),
            Settings = settings,
            Set = set,
            Palette = set.Palette(),
            BiomeCellCounts = counts,
            LandFraction = metrics.LandFraction,
            UnclassifiedLandCells = metrics.UnclassifiedLandCells,
            ElapsedSeconds = metrics.ElapsedSeconds,
        };
    }

    public static bool TryLoadStage5(
        StageCache cache,
        string key,
        HydrologyResult hydrology,
        out BiomeResult result)
    {
        result = null!;
        if (!cache.TryGet("s5", key, out CacheEntry entry))
        {
            return false;
        }

        try
        {
            StageBundle bundle = cache.Load(entry);
            if (!Matches(bundle, "s5", key))
            {
                return false;
            }

            result = ReadStage5(bundle, hydrology);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoadStage1(StageCache cache, string key, out Heightmap map) =>
        TryLoad(cache, "s1", key, ReadStage1, out map);

    public static bool TryLoadStage2(StageCache cache, string key, Heightmap source, out ErosionResult result)
    {
        result = null!;
        if (!cache.TryGet("s2", key, out CacheEntry entry))
        {
            return false;
        }

        try
        {
            StageBundle bundle = cache.Load(entry);
            if (!Matches(bundle, "s2", key))
            {
                return false;
            }

            result = ReadStage2(bundle, source);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoadStage3(StageCache cache, string key, Heightmap source, out ClimateResult result)
    {
        result = null!;
        if (!cache.TryGet("s3", key, out CacheEntry entry))
        {
            return false;
        }

        try
        {
            StageBundle bundle = cache.Load(entry);
            if (!Matches(bundle, "s3", key))
            {
                return false;
            }

            result = ReadStage3(bundle, source);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoadStage4(
        StageCache cache,
        string key,
        Heightmap source,
        ClimateResult climate,
        out HydrologyResult result)
    {
        result = null!;
        if (!cache.TryGet("s4", key, out CacheEntry entry))
        {
            return false;
        }

        try
        {
            StageBundle bundle = cache.Load(entry);
            if (!Matches(bundle, "s4", key))
            {
                return false;
            }

            result = ReadStage4(bundle, source, climate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SaveStage1(StageCache cache, string key, TerrainSettings settings, Heightmap map) =>
        cache.Store(CreateStage1(key, settings, map), StageKeys.DescribeSettings(settings));

    public static void SaveStage2(StageCache cache, string key, string inputKey, ErosionResult result) =>
        cache.Store(CreateStage2(key, inputKey, result), StageKeys.DescribeSettings(result.Settings));

    public static void SaveStage3(StageCache cache, string key, string inputKey, ClimateResult result) =>
        cache.Store(CreateStage3(key, inputKey, result), StageKeys.DescribeSettings(result.Settings));

    public static void SaveStage4(StageCache cache, string key, string inputKey, HydrologyResult result) =>
        cache.Store(CreateStage4(key, inputKey, result), StageKeys.DescribeSettings(result.Settings));

    public static void SaveStage5(StageCache cache, string key, string inputKey, BiomeResult result) =>
        cache.Store(CreateStage5(key, inputKey, result), StageKeys.DescribeSettings(result.Settings));

    private static StageBundle CreateBase(
        string stage,
        string key,
        string inputKey,
        int modelVersion,
        object settings,
        Heightmap map)
    {
        return new StageBundle
        {
            Stage = stage,
            Key = key,
            InputKey = inputKey,
            ModelVersion = modelVersion,
            SettingsSchemaVersion = ModelVersions.SettingsSchema,
            CacheSchemaVersion = ModelVersions.CacheSchema,
            SettingsJson = JsonSerializer.Serialize(settings, settings.GetType(), JsonOptions),
            Width = map.Width,
            Height = map.Height,
            View = map.View,
        };
    }

    private static Heightmap ReadMap(StageBundle bundle, string arrayName)
    {
        int count = bundle.Width * bundle.Height;
        float[] values = RequireArray(bundle.FloatArrays, arrayName, count);
        var map = new Heightmap(bundle.Width, bundle.Height, bundle.View);
        Array.Copy(values, map.Elevations, count);
        return map;
    }

    private static bool TryLoad<T>(
        StageCache cache,
        string stage,
        string key,
        Func<StageBundle, T> read,
        out T value)
    {
        value = default!;
        if (!cache.TryGet(stage, key, out CacheEntry entry))
        {
            return false;
        }

        try
        {
            StageBundle bundle = cache.Load(entry);
            if (!Matches(bundle, stage, key))
            {
                return false;
            }

            value = read(bundle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Matches(StageBundle bundle, string stage, string key) =>
        string.Equals(bundle.Stage, stage, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(bundle.Key, key, StringComparison.OrdinalIgnoreCase);

    private static T ReadSettings<T>(StageBundle bundle)
    {
        T? settings = JsonSerializer.Deserialize<T>(bundle.SettingsJson, JsonOptions);
        return settings ?? throw new InvalidDataException($"Missing {typeof(T).Name} in stage bundle.");
    }

    private static T ReadMetrics<T>(StageBundle bundle)
    {
        T? metrics = JsonSerializer.Deserialize<T>(bundle.MetricsJson ?? "{}", JsonOptions);
        return metrics ?? throw new InvalidDataException($"Missing metrics in stage bundle.");
    }

    private static T[] RequireArray<T>(Dictionary<string, T[]> arrays, string name, int count)
    {
        if (!arrays.TryGetValue(name, out T[]? values) || values.Length != count)
        {
            throw new InvalidDataException($"Stage bundle array '{name}' is missing or has the wrong length.");
        }

        return values;
    }

    private static T[] RequireLength<T>(T[] values, int count, string name)
    {
        if (values.Length != count)
        {
            throw new InvalidDataException($"{name} has {values.Length} values; expected {count}.");
        }

        return values;
    }

    private sealed record ErosionMetrics(
        double MeanErosionMeters,
        double MaxErosionMeters,
        double MeanDepositionMeters,
        double MaxDepositionMeters,
        int CoastlineProgradedCells,
        double ElapsedSeconds);

    private sealed record ClimateMetrics(
        double MeanLandTemperatureCelsius,
        double MeanLandPrecipitationMm,
        double MaxLandPrecipitationMm,
        double MeanLandRunoffMm,
        double AridLandFraction,
        double HumidLandFraction,
        double ElapsedSeconds);

    private sealed record HydrologyMetrics(
        double TotalLakeAreaSqKm,
        double TotalRiverLengthKm,
        int MaxStrahlerOrder,
        int RiverCellCount,
        int BreachedDepressionCount,
        double ElapsedSeconds);

    private sealed record BiomeMetrics(
        string BiomeSetId,
        int BiomeSetVersion,
        int UnclassifiedLandCells,
        double LandFraction,
        Dictionary<int, int> BiomeCells,
        double ElapsedSeconds);

    private sealed record HydrologyVectors(
        List<LakeFeature> Lakes,
        List<RiverPolyline> Rivers);
}
