using System.Diagnostics;
using WorldGen;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 1;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

var climateSettings = new ClimateSettings
{
    PrecipitationScale = options.PrecipitationScale,
    OrographicLiftStrength = options.OrographicLiftStrength,
    RainShadowStrength = options.RainShadowStrength,
};

var hydroSettings = new HydrologySettings
{
    MinRiverDischargeM3s = options.MinRiverDischargeM3s,
    RiverThresholdSqKm = options.RiverThresholdSqKm,
    MinHeadwaterReachCells = options.MinHeadwaterReachCells,
    MinLakeCells = options.MinLakeCells,
    MinLakeDepthMeters = options.MinLakeDepthMeters,
    MaxLakeDepthMeters = options.MaxLakeDepthMeters,
};

var biomeSettings = new BiomeSettings
{
    BiomeSetId = options.BiomeSetId,
    EcotoneOctaves = options.EcotoneEnabled ? 3 : 0,
    SmoothingPasses = Math.Max(0, options.SmoothingPasses),
};

if (options.CacheList || options.CachePrune)
{
    return RunCacheCommand(options, climateSettings, hydroSettings);
}

static int RunCacheCommand(Options options, ClimateSettings climateSettings, HydrologySettings hydroSettings)
{
    StageCache cache;
    try
    {
        cache = new StageCache(options.CacheDirectory);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Could not open the stage cache: {exception.Message}");
        return 1;
    }

    if (options.CacheList)
    {
        Console.WriteLine($"Cache: {cache.Root}");
        Console.WriteLine($"{cache.Entries.Count} entries, {FormatBytes(cache.TotalSizeBytes)}");

        foreach (CacheEntry entry in cache.Entries
                     .OrderBy(entry => entry.Stage, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ModelVersionInfo? info = ModelRegistry.Default.Find(entry.Stage, entry.ModelVersion);
            string status = info?.Status.ToString() ?? "Unknown";
            string compatibility = info?.Compatibility ?? "?";
            string summary = entry.SettingsSummary.Length > 70
                ? entry.SettingsSummary[..70] + "..."
                : entry.SettingsSummary;

            Console.WriteLine(
                $"{entry.Stage}  {entry.Key[..12]}  model v{entry.ModelVersion,-2}  {status,-8}  " +
                $"{compatibility,-15}  {FormatBytes(entry.SizeBytes),9}  {entry.LastUsedUtc}  {summary}");
        }

        return 0;
    }

    var terrainSettings = new TerrainSettings
    {
        Seed = options.Seed,
        ContinentWarpStrength1 = options.ContinentWarpStrength1,
        ContinentWarpStrength2 = options.ContinentWarpStrength2,
        ContinentWarpEnabled = options.ContinentWarpEnabled,
        WarpRigidityEnabled = options.WarpRigidityEnabled,
        WarpRigidityScale = options.WarpRigidityScale,
        OrogenWidthMeters = options.OrogenWidthMeters,
    };

    var erosionSettings = new ErosionSettings
    {
        Seed = options.Seed,
        Iterations = options.ErosionSteps,
        StreamPowerK = options.ErosionK,
        HillslopeDiffusivity = options.ErosionDiffusion,
        SedimentTransportCapacity = options.ErosionCapacity,
        DepositionRate = options.ErosionDeposition,
        UpliftScale = options.ErosionUplift,
        HaloCells = options.ErosionHalo,
    };

    string s1 = StageKeys.BaseTopology(terrainSettings, options.View, options.Width, options.Height);
    string s2 = StageKeys.Erosion(s1, erosionSettings);
    string s3 = StageKeys.Climate(s2, climateSettings);
    string s4 = StageKeys.Hydrology(s3, hydroSettings);
    var keep = new HashSet<string>([s1, s2, s3, s4], StringComparer.OrdinalIgnoreCase);

    var stages = (options.CacheStageFilter ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    DateTimeOffset threshold = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, options.CacheOlderThanDays));

    CacheEntry[] victims = cache.Entries.Where(entry =>
    {
        if (stages.Count > 0 && !stages.Contains(entry.Stage))
        {
            return false;
        }

        if (keep.Contains(entry.Key))
        {
            return false;
        }

        if (options.CacheOlderThanDays > 0 &&
            (!DateTimeOffset.TryParse(entry.LastUsedUtc, out DateTimeOffset lastUsed) || lastUsed >= threshold))
        {
            return false;
        }

        ModelStatus status = ModelRegistry.Default.StatusOf(entry.Stage, entry.ModelVersion);
        if (!options.CacheIncludeReleased && status != ModelStatus.Dev)
        {
            return false;
        }

        return true;
    }).ToArray();

    long bytes = victims.Sum(entry => entry.SizeBytes);
    Console.WriteLine($"Cache: {cache.Root}");

    // Pruning an upstream stage also removes its descendants, which can no longer be reached.
    var victimSet = victims.ToHashSet();
    var victimKeys = victims.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    bool added;
    do
    {
        added = false;
        foreach (CacheEntry entry in cache.Entries)
        {
            if (victimSet.Contains(entry) || string.IsNullOrEmpty(entry.InputKey))
            {
                continue;
            }

            if (victimKeys.Contains(entry.InputKey))
            {
                victimSet.Add(entry);
                victimKeys.Add(entry.Key);
                added = true;
            }
        }
    }
    while (added);

    victims = victimSet.ToArray();
    bytes = victims.Sum(entry => entry.SizeBytes);

    Console.WriteLine($"{(options.CacheYes ? "Removing" : "Would remove")} {victims.Length} entries ({FormatBytes(bytes)}).");

    foreach (CacheEntry entry in victims)
    {
        ModelStatus status = ModelRegistry.Default.StatusOf(entry.Stage, entry.ModelVersion);
        Console.WriteLine($"  {entry.Stage}  {entry.Key[..12]}  model v{entry.ModelVersion} ({status})  {FormatBytes(entry.SizeBytes)}");
    }

    if (!options.CacheYes)
    {
        Console.WriteLine("Dry run. Add --cache-yes to delete.");
        return 0;
    }

    int removed = cache.Prune(entry => victims.Contains(entry), dryRun: false);
    Console.WriteLine($"Removed {removed} entries.");
    return 0;
}

static string FormatBytes(long bytes) => bytes switch
{
    >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
    >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
    _ => $"{bytes} B",
};

// Mode A0: Standalone Stage 5 Biomes from an existing Stage 2/3 GeoTIFF (--biome-from)
if (!string.IsNullOrWhiteSpace(options.BiomeFromTiff))
{
    LoadedElevationMap loaded = GeoTiffReader.ReadFloat32(options.BiomeFromTiff);
    int seed = loaded.SourceManifest?.Seed ?? options.Seed;
    string outPrefix = options.OutputExplicitlySet
        ? options.Output
        : StageArtifactPaths.NormalizePrefix(loaded.SourceGeoTiffPath);

    var terrain = new PlanetTerrain(new TerrainSettings { Seed = seed, PlanetRadiusMeters = loaded.PlanetRadiusMeters });
    Console.WriteLine($"Loaded GeoTIFF (read-only): {loaded.SourceGeoTiffPath} ({loaded.Map.Width}x{loaded.Map.Height})");

    ClimateResult climate = TerrainClimate.Analyze(loaded.Map, climateSettings, loaded.PlanetRadiusMeters);
    HydrologyResult biomeHydro = TerrainHydrology.Analyze(loaded.Map, hydroSettings, loaded.PlanetRadiusMeters, climate);
    BiomeResult biomes = TerrainBiomes.Analyze(biomeHydro, biomeSettings, seed, loaded.PlanetRadiusMeters);
    StageArtifactSet s5Paths = ArtifactExporter.WriteStage5Biomes(
        outPrefix,
        biomes,
        loaded.PlanetRadiusMeters,
        terrain,
        loaded.SourceGeoTiffPath,
        loaded.SourceManifest);

    PrintStage5Summary(biomes, s5Paths);

    if (options.Check)
    {
        double mpp = loaded.PlanetRadiusMeters * MathUtils.ToRadians(biomes.Elevation.View.LongitudeSpan) / biomes.Elevation.Width;
        TerrainDiagnostics.Print(terrain, biomes.Elevation, (float)mpp, Console.Out, null, climate, biomeHydro);
    }

    return 0;
}

// Mode A: Standalone Stage 4 Hydrology from an existing Stage 2/3 GeoTIFF (--hydro-from)
if (!string.IsNullOrWhiteSpace(options.HydroFromTiff))
{
    LoadedElevationMap loaded = GeoTiffReader.ReadFloat32(options.HydroFromTiff);
    int seed = loaded.SourceManifest?.Seed ?? options.Seed;
    string outPrefix = options.OutputExplicitlySet
        ? options.Output
        : StageArtifactPaths.NormalizePrefix(loaded.SourceGeoTiffPath);

    var terrain = new PlanetTerrain(new TerrainSettings { Seed = seed, PlanetRadiusMeters = loaded.PlanetRadiusMeters });
    Console.WriteLine($"Loaded GeoTIFF (read-only): {loaded.SourceGeoTiffPath} ({loaded.Map.Width}x{loaded.Map.Height})");

    ClimateResult climate = TerrainClimate.Analyze(loaded.Map, climateSettings, loaded.PlanetRadiusMeters);
    HydrologyResult hydro = TerrainHydrology.Analyze(loaded.Map, hydroSettings, loaded.PlanetRadiusMeters, climate);
    StageArtifactSet s4Paths = ArtifactExporter.WriteStage4Hydrology(
        outPrefix,
        hydro,
        loaded.PlanetRadiusMeters,
        terrain,
        loaded.SourceGeoTiffPath,
        loaded.SourceManifest);

    PrintStage4Summary(hydro, s4Paths);

    if (options.Check)
    {
        double mpp = loaded.PlanetRadiusMeters * MathUtils.ToRadians(hydro.HydrologicalHeightmap.View.LongitudeSpan) / hydro.HydrologicalHeightmap.Width;
        TerrainDiagnostics.Print(terrain, hydro.HydrologicalHeightmap, (float)mpp, Console.Out, null, climate, hydro);
    }

    return 0;
}

// Mode B: Standalone Stage 3 Climate (and optional Stage 4 Hydrology) from an existing Stage 2 GeoTIFF (--climate-from)
if (!string.IsNullOrWhiteSpace(options.ClimateFromTiff))
{
    LoadedElevationMap loaded = GeoTiffReader.ReadFloat32(options.ClimateFromTiff);
    int seed = loaded.SourceManifest?.Seed ?? options.Seed;
    string outPrefix = options.OutputExplicitlySet
        ? options.Output
        : StageArtifactPaths.NormalizePrefix(loaded.SourceGeoTiffPath);

    var terrain = new PlanetTerrain(new TerrainSettings { Seed = seed, PlanetRadiusMeters = loaded.PlanetRadiusMeters });
    Console.WriteLine($"Loaded Stage 2 GeoTIFF (read-only): {loaded.SourceGeoTiffPath} ({loaded.Map.Width}x{loaded.Map.Height})");

    ClimateResult climate = TerrainClimate.Analyze(loaded.Map, climateSettings, loaded.PlanetRadiusMeters);
    StageArtifactSet s3Paths = ArtifactExporter.WriteStage3Climate(
        outPrefix,
        climate,
        loaded.PlanetRadiusMeters,
        terrain,
        loaded.SourceGeoTiffPath,
        loaded.SourceManifest);

    PrintStage3ClimateSummary(climate, s3Paths);

    HydrologyResult? hydro = null;
    if (options.Hydro)
    {
        hydro = TerrainHydrology.Analyze(loaded.Map, hydroSettings, loaded.PlanetRadiusMeters, climate);
        StageArtifactSet s4Paths = ArtifactExporter.WriteStage4Hydrology(
            outPrefix,
            hydro,
            loaded.PlanetRadiusMeters,
            terrain,
            s3Paths.HeightGeoTiffPath,
            loaded.SourceManifest);
        Console.WriteLine();
        PrintStage4Summary(hydro, s4Paths);
    }

    if (options.Check)
    {
        Heightmap activeMap = hydro?.HydrologicalHeightmap ?? loaded.Map;
        double mpp = loaded.PlanetRadiusMeters * MathUtils.ToRadians(activeMap.View.LongitudeSpan) / activeMap.Width;
        TerrainDiagnostics.Print(terrain, activeMap, (float)mpp, Console.Out, null, climate, hydro);
    }

    return 0;
}

// Mode C: Standalone Stage 2 Erosion (and optional Stage 3/4) from an existing Stage 1 GeoTIFF (--erode-from)
if (!string.IsNullOrWhiteSpace(options.ErodeFromTiff))
{
    LoadedElevationMap loaded = GeoTiffReader.ReadFloat32(options.ErodeFromTiff);
    int seed = loaded.SourceManifest?.Seed ?? options.Seed;
    string outPrefix = options.OutputExplicitlySet
        ? options.Output
        : StageArtifactPaths.NormalizePrefix(loaded.SourceGeoTiffPath);

    var terrain = new PlanetTerrain(new TerrainSettings { Seed = seed, PlanetRadiusMeters = loaded.PlanetRadiusMeters });
    var erosionSettings = new ErosionSettings
    {
        Seed = seed,
        Iterations = options.ErosionSteps,
        StreamPowerK = options.ErosionK,
        HillslopeDiffusivity = options.ErosionDiffusion,
        SedimentTransportCapacity = options.ErosionCapacity,
        DepositionRate = options.ErosionDeposition,
        UpliftScale = options.ErosionUplift,
        HaloCells = options.ErosionHalo,
    };

    Console.WriteLine($"Loaded Stage 1 GeoTIFF (read-only): {loaded.SourceGeoTiffPath} ({loaded.Map.Width}x{loaded.Map.Height})");
    ErosionResult erosion = TerrainErosion.Erode(loaded.Map, erosionSettings, terrain, loaded.PlanetRadiusMeters);
    StageArtifactSet s2Paths = ArtifactExporter.WriteStage2(
        outPrefix,
        erosion,
        loaded.PlanetRadiusMeters,
        terrain,
        loaded.SourceGeoTiffPath,
        loaded.SourceManifest);

    var (eMin, eMax, eMean, eLand) = erosion.Eroded.ComputeStatistics();
    double mpp = loaded.PlanetRadiusMeters * MathUtils.ToRadians(erosion.Eroded.View.LongitudeSpan) / erosion.Eroded.Width;

    Console.WriteLine($"Stage 2 eroded map ({erosion.ElapsedSeconds:F2}s): Land {eLand:P1}, elevation {eMin:F0} .. {eMax:F0} m (mean {eMean:F0} m)");
    Console.WriteLine($"  Incision: mean {erosion.MeanErosionMeters:F1} m, max {erosion.MaxErosionMeters:F0} m | Deposition: mean {erosion.MeanDepositionMeters:F1} m, max {erosion.MaxDepositionMeters:F0} m");
    Console.WriteLine($"  Heightmap (GeoTIFF): {s2Paths.HeightGeoTiffPath}");
    Console.WriteLine($"  Heightmap (PNG):     {s2Paths.HeightPngPath}");
    Console.WriteLine($"  Preview:             {s2Paths.PreviewPngPath}");
    Console.WriteLine($"  Manifest:            {s2Paths.ManifestJsonPath}");

    ClimateResult? climate = null;
    StageArtifactSet? s3Paths = null;
    if (options.Climate)
    {
        climate = TerrainClimate.Analyze(erosion.Eroded, climateSettings, loaded.PlanetRadiusMeters);
        s3Paths = ArtifactExporter.WriteStage3Climate(
            outPrefix,
            climate,
            loaded.PlanetRadiusMeters,
            terrain,
            s2Paths.HeightGeoTiffPath,
            loaded.SourceManifest,
            erosion);
        Console.WriteLine();
        PrintStage3ClimateSummary(climate, s3Paths);
    }

    HydrologyResult? hydro = null;
    if (options.Hydro)
    {
        climate ??= TerrainClimate.Analyze(erosion.Eroded, climateSettings, loaded.PlanetRadiusMeters);
        hydro = TerrainHydrology.Analyze(erosion.Eroded, hydroSettings, loaded.PlanetRadiusMeters, climate);
        StageArtifactSet s4Paths = ArtifactExporter.WriteStage4Hydrology(
            outPrefix,
            hydro,
            loaded.PlanetRadiusMeters,
            terrain,
            s3Paths?.HeightGeoTiffPath ?? s2Paths.HeightGeoTiffPath,
            loaded.SourceManifest,
            erosion);
        Console.WriteLine();
        PrintStage4Summary(hydro, s4Paths);
    }

    if (options.Check)
    {
        Heightmap activeMap = hydro?.HydrologicalHeightmap ?? erosion.Eroded;
        TerrainDiagnostics.Print(terrain, activeMap, (float)mpp, Console.Out, erosion, climate, hydro);
    }

    return 0;
}

// Mode D: Full pipeline from Stage 1 (with optional Stage 2, Stage 3, and Stage 4)
var terrainSettings = new TerrainSettings
{
    Seed = options.Seed,
    ContinentWarpStrength1 = options.ContinentWarpStrength1,
    ContinentWarpStrength2 = options.ContinentWarpStrength2,
    ContinentWarpEnabled = options.ContinentWarpEnabled,
    WarpRigidityEnabled = options.WarpRigidityEnabled,
    WarpRigidityScale = options.WarpRigidityScale,
    OrogenWidthMeters = options.OrogenWidthMeters,
};

var stage1Terrain = new PlanetTerrain(terrainSettings);

StageCache? stageCache = null;
if (!options.NoCache)
{
    try
    {
        stageCache = new StageCache(options.CacheDirectory);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Stage cache unavailable: {exception.Message}");
    }
}

string s1Key = StageKeys.BaseTopology(terrainSettings, options.View, options.Width, options.Height);
var stopwatch = Stopwatch.StartNew();
Heightmap? cachedStage1 = null;
bool stage1CacheHit = stageCache is not null &&
                      TerrainStageCodec.TryLoadStage1(stageCache, s1Key, out cachedStage1);

Heightmap map;
if (stage1CacheHit && cachedStage1 is not null)
{
    map = cachedStage1;
    stopwatch.Stop();
    Console.WriteLine($"Stage 1 cache hit: {s1Key[..12]}");
}
else
{
    map = HeightmapRenderer.Render(stage1Terrain, options.View, options.Width, options.Height);
    stopwatch.Stop();

    if (stageCache is not null)
    {
        try
        {
            TerrainStageCodec.SaveStage1(stageCache, s1Key, terrainSettings, map);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Stage 1 cache write failed: {exception.Message}");
        }
    }
}

var (min, max, mean, landFraction) = map.ComputeStatistics();

float radius = stage1Terrain.Settings.PlanetRadiusMeters;
double metersPerPixel = radius * MathUtils.ToRadians(options.View.LongitudeSpan) / options.Width;

StageArtifactSet s1Paths = ArtifactExporter.WriteStage1(options.Output, map, stage1Terrain);

Console.WriteLine($"Stage 1 ({s1Paths.StageTag}): {map.Width}x{map.Height} equirectangular map, {metersPerPixel / 1000.0:F1} km/px at the equator ({stopwatch.Elapsed.TotalSeconds:F1}s)");
Console.WriteLine($"Land {landFraction:P1} of surface, elevation {min:F0} .. {max:F0} m (mean {mean:F0} m)");
Console.WriteLine($"Heightmap (GeoTIFF): {s1Paths.HeightGeoTiffPath}");
Console.WriteLine($"Heightmap (PNG):     {s1Paths.HeightPngPath}");
Console.WriteLine($"Preview:             {s1Paths.PreviewPngPath}");
Console.WriteLine($"Manifest:            {s1Paths.ManifestJsonPath}");

ErosionResult? erosionResult = null;
StageArtifactSet? stage2Paths = null;
string? s2Key = null;
if (options.Erode)
{
    var erosionSettings = new ErosionSettings
    {
        Seed = options.Seed,
        Iterations = options.ErosionSteps,
        StreamPowerK = options.ErosionK,
        HillslopeDiffusivity = options.ErosionDiffusion,
        SedimentTransportCapacity = options.ErosionCapacity,
        DepositionRate = options.ErosionDeposition,
        UpliftScale = options.ErosionUplift,
        HaloCells = options.ErosionHalo,
    };

    s2Key = StageKeys.Erosion(s1Key, erosionSettings);
    ErosionResult? cachedErosion = null;
    bool erosionCacheHit = stageCache is not null &&
                           TerrainStageCodec.TryLoadStage2(stageCache, s2Key, map, out cachedErosion);

    if (erosionCacheHit && cachedErosion is not null)
    {
        erosionResult = cachedErosion;
        Console.WriteLine($"Stage 2 cache hit: {s2Key[..12]}");
    }
    else
    {
        bool isRegional = options.View != MapView.WholePlanet && options.ErosionHalo > 0;
        erosionResult = isRegional
            ? TerrainErosion.RenderAndErodeWithHalo(stage1Terrain, options.View, options.Width, options.Height, erosionSettings)
            : TerrainErosion.Erode(map, erosionSettings, stage1Terrain, radius);

        if (stageCache is not null)
        {
            try
            {
                TerrainStageCodec.SaveStage2(stageCache, s2Key, s1Key, erosionResult);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Stage 2 cache write failed: {exception.Message}");
            }
        }
    }

    stage2Paths = ArtifactExporter.WriteStage2(
        options.Output,
        erosionResult,
        radius,
        stage1Terrain,
        s1Paths.HeightGeoTiffPath);

    var (eMin, eMax, eMean, eLand) = erosionResult.Eroded.ComputeStatistics();
    Console.WriteLine();
    Console.WriteLine($"Stage 2 ({stage2Paths.StageTag}): {erosionSettings.Iterations} steps ({erosionResult.ElapsedSeconds:F2}s)");
    Console.WriteLine($"Land {eLand:P1} of surface, elevation {eMin:F0} .. {eMax:F0} m (mean {eMean:F0} m)");
    Console.WriteLine($"Incision: mean {erosionResult.MeanErosionMeters:F1} m, max {erosionResult.MaxErosionMeters:F0} m | Deposition: mean {erosionResult.MeanDepositionMeters:F1} m, max {erosionResult.MaxDepositionMeters:F0} m");
    Console.WriteLine($"Heightmap (GeoTIFF): {stage2Paths.HeightGeoTiffPath}");
    Console.WriteLine($"Heightmap (PNG):     {stage2Paths.HeightPngPath}");
    Console.WriteLine($"Preview:             {stage2Paths.PreviewPngPath}");
    Console.WriteLine($"Manifest:            {stage2Paths.ManifestJsonPath}");
}

ClimateResult? climateResult = null;
StageArtifactSet? stage3Paths = null;
string? s3Key = null;
if (options.Climate)
{
    Heightmap climateInput = erosionResult?.Eroded ?? map;
    string sourceTiffForS3 = stage2Paths?.HeightGeoTiffPath ?? s1Paths.HeightGeoTiffPath;
    string climateInputKey = erosionResult is not null && s2Key is not null ? s2Key : s1Key;
    s3Key = StageKeys.Climate(climateInputKey, climateSettings);

    ClimateResult? cachedClimate = null;
    bool climateCacheHit = stageCache is not null &&
                           TerrainStageCodec.TryLoadStage3(stageCache, s3Key, climateInput, out cachedClimate);

    if (climateCacheHit && cachedClimate is not null)
    {
        climateResult = cachedClimate;
        Console.WriteLine($"Stage 3 cache hit: {s3Key[..12]}");
    }
    else
    {
        climateResult = TerrainClimate.Analyze(climateInput, climateSettings, radius);

        if (stageCache is not null)
        {
            try
            {
                TerrainStageCodec.SaveStage3(stageCache, s3Key, climateInputKey, climateResult);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Stage 3 cache write failed: {exception.Message}");
            }
        }
    }

    stage3Paths = ArtifactExporter.WriteStage3Climate(
        options.Output,
        climateResult,
        radius,
        stage1Terrain,
        sourceTiffForS3,
        null,
        erosionResult);

    Console.WriteLine();
    PrintStage3ClimateSummary(climateResult, stage3Paths);
}

HydrologyResult? hydroResult = null;
StageArtifactSet? stage4Paths = null;
string? s4Key = null;
if (options.Hydro)
{
    Heightmap hydroInput = erosionResult?.Eroded ?? map;
    climateResult ??= TerrainClimate.Analyze(hydroInput, climateSettings, radius);
    string sourceTiffForS4 = stage3Paths?.HeightGeoTiffPath ?? stage2Paths?.HeightGeoTiffPath ?? s1Paths.HeightGeoTiffPath;
    s4Key = StageKeys.Hydrology(s3Key ?? s1Key, hydroSettings);

    HydrologyResult? cachedHydro = null;
    bool hydroCacheHit = stageCache is not null &&
                         TerrainStageCodec.TryLoadStage4(
                             stageCache, s4Key, hydroInput, climateResult, out cachedHydro);

    if (hydroCacheHit && cachedHydro is not null)
    {
        hydroResult = cachedHydro;
        Console.WriteLine($"Stage 4 cache hit: {s4Key[..12]}");
    }
    else
    {
        hydroResult = TerrainHydrology.Analyze(hydroInput, hydroSettings, radius, climateResult);

        if (stageCache is not null)
        {
            try
            {
                TerrainStageCodec.SaveStage4(stageCache, s4Key, s3Key ?? s1Key, hydroResult);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Stage 4 cache write failed: {exception.Message}");
            }
        }
    }

    StageArtifactSet s4Paths = ArtifactExporter.WriteStage4Hydrology(
        options.Output,
        hydroResult,
        radius,
        stage1Terrain,
        sourceTiffForS4,
        null,
        erosionResult);

    stage4Paths = s4Paths;

    Console.WriteLine();
    PrintStage4Summary(hydroResult, s4Paths);
}

BiomeResult? biomeResult = null;
if (options.Biome)
{
    HydrologyResult hydroForBiomes = hydroResult
        ?? throw new InvalidOperationException("Stage 5 requires Stage 4 hydrology; run with --biome (or --hydro).");

    string biomeInputKey = s4Key ?? s3Key ?? s2Key ?? s1Key;
    string s5Key = StageKeys.Biomes(biomeInputKey, biomeSettings);

    BiomeResult? cachedBiomes = null;
    bool biomeCacheHit = stageCache is not null &&
                         TerrainStageCodec.TryLoadStage5(stageCache, s5Key, hydroForBiomes, out cachedBiomes);

    if (biomeCacheHit && cachedBiomes is not null)
    {
        biomeResult = cachedBiomes;
        Console.WriteLine($"Stage 5 cache hit: {s5Key[..12]}");
    }
    else
    {
        biomeResult = TerrainBiomes.Analyze(hydroForBiomes, biomeSettings, options.Seed, radius);

        if (stageCache is not null)
        {
            try
            {
                TerrainStageCodec.SaveStage5(stageCache, s5Key, biomeInputKey, biomeResult);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Stage 5 cache write failed: {exception.Message}");
            }
        }
    }

    string sourceTiffForS5 = stage4Paths?.HeightGeoTiffPath
        ?? stage3Paths?.HeightGeoTiffPath
        ?? stage2Paths?.HeightGeoTiffPath
        ?? s1Paths.HeightGeoTiffPath;

    StageArtifactSet s5Paths = ArtifactExporter.WriteStage5Biomes(
        options.Output,
        biomeResult,
        radius,
        stage1Terrain,
        sourceTiffForS5,
        null,
        erosionResult);

    Console.WriteLine();
    PrintStage5Summary(biomeResult, s5Paths);
}

if (options.Check)
{
    Heightmap activeMap = hydroResult?.HydrologicalHeightmap ?? erosionResult?.Eroded ?? map;
    TerrainDiagnostics.Print(stage1Terrain, activeMap, (float)metersPerPixel, Console.Out, erosionResult, climateResult, hydroResult);
}

return 0;

static void PrintStage3ClimateSummary(ClimateResult climate, StageArtifactSet paths)
{
    Console.WriteLine($"Stage 3 ({paths.StageTag}): ({climate.ElapsedSeconds:F2}s) Land mean temp {climate.MeanLandTemperatureCelsius:F1} °C, precip {climate.MeanLandPrecipitationMm:F0} mm/yr (max {climate.MaxLandPrecipitationMm:F0} mm/yr), runoff {climate.MeanLandRunoffMm:F0} mm/yr");
    Console.WriteLine($"Moisture zones: arid (<250 mm/yr) {climate.AridLandFraction:P1} of land, humid (≥1000 mm/yr) {climate.HumidLandFraction:P1} of land");
    Console.WriteLine($"Heightmap (GeoTIFF): {paths.HeightGeoTiffPath}");
    Console.WriteLine($"Rainfall (GeoTIFF):  {paths.BasePrefix}.{paths.StageTag}-rainfall.tif");
    Console.WriteLine($"Heightmap (PNG):     {paths.HeightPngPath}");
    Console.WriteLine($"Preview:             {paths.PreviewPngPath}");
    Console.WriteLine($"Manifest:            {paths.ManifestJsonPath}");
}

static void PrintStage4Summary(HydrologyResult hydro, StageArtifactSet paths)
{
    var (hMin, hMax, hMean, hLand) = hydro.HydrologicalHeightmap.ComputeStatistics();
    Console.WriteLine($"Stage 4 ({paths.StageTag}): ({hydro.ElapsedSeconds:F2}s) Land {hLand:P1}, elevation {hMin:F0} .. {hMax:F0} m (mean {hMean:F0} m)");
    Console.WriteLine($"Lakes: {hydro.Lakes.Count} basins ({hydro.TotalLakeAreaSqKm:N0} km²), breached {hydro.BreachedDepressionCount} minor/arid depressions");
    Console.WriteLine($"Rivers: {hydro.Rivers.Count} reaches ({hydro.TotalRiverLengthKm:N0} km total, max Strahler order {hydro.MaxStrahlerOrder})");
    Console.WriteLine($"Heightmap (GeoTIFF): {paths.HeightGeoTiffPath}");
    Console.WriteLine($"Heightmap (PNG):     {paths.HeightPngPath}");
    Console.WriteLine($"Preview:             {paths.PreviewPngPath}");
    Console.WriteLine($"Manifest:            {paths.ManifestJsonPath}");
    Console.WriteLine($"Vectors (JSON):      {paths.BasePrefix}.{paths.StageTag}-vectors.json");
}

static void PrintStage5Summary(BiomeResult biomes, StageArtifactSet paths)
{
    Console.WriteLine($"Stage 5 ({paths.StageTag}): ({biomes.ElapsedSeconds:F2}s) set {biomes.Set.Id} v{biomes.Set.Version} ({biomes.Set.Biomes.Count} land biomes, {BiomeTable.Default.WaterClasses.Count} water classes)");
    Console.WriteLine($"Land {biomes.LandFraction:P1} of surface; unclassified land cells {biomes.UnclassifiedLandCells:N0}");

    IReadOnlyList<string> top = biomes.BiomeCellCounts
        .Where(pair => pair.Key >= 16 && biomes.Set.ById.TryGetValue(pair.Key, out _))
        .OrderByDescending(pair => pair.Value)
        .Take(6)
        .Select(pair => $"{biomes.Set.ById[pair.Key].Name} {pair.Value:N0}")
        .ToList();
    if (top.Count > 0)
    {
        Console.WriteLine($"Land biomes: {string.Join(", ", top)}");
    }

    Console.WriteLine($"Heightmap (GeoTIFF): {paths.HeightGeoTiffPath}");
    Console.WriteLine($"Biome ids (GeoTIFF): {paths.BasePrefix}.{paths.StageTag}-ids.tif");
    Console.WriteLine($"Biome ids (PNG):     {paths.BasePrefix}.{paths.StageTag}-ids.png");
    Console.WriteLine($"Heightmap (PNG):     {paths.HeightPngPath}");
    Console.WriteLine($"Preview:             {paths.PreviewPngPath}");
    Console.WriteLine($"Palette (JSON):      {paths.BasePrefix}.{paths.StageTag}-palette.json");
    Console.WriteLine($"Manifest:            {paths.ManifestJsonPath}");
}
