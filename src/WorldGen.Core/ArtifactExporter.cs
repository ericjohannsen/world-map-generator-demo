namespace WorldGen;

/// <summary>
/// Writes the canonical artifact bundle (<c>-height.tif</c>, <c>-height.png</c>, <c>-preview.png</c>,
/// <c>-manifest.json</c>, plus stage-specific companion files) for any pipeline stage, enforcing
/// stage-tagged filenames and source immutability.
/// </summary>
public static class ArtifactExporter
{
    public const float HeightmapMinMeters = -8_000f;
    public const float HeightmapMaxMeters = 9_000f;

    public static StageArtifactSet WriteStage1(
        string outputPrefix,
        Heightmap map,
        PlanetTerrain terrain)
    {
        StageArtifactSet paths = StageArtifactPaths.ForStage(outputPrefix, PipelineStage.BaseTopology);
        EnsureDirectory(paths.HeightGeoTiffPath);

        float radius = terrain.Settings.PlanetRadiusMeters;
        double metersPerPixel = radius * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;

        WriteGeoTiff(map, radius, paths.HeightGeoTiffPath, "WorldGen Stage 1 base topology; elevations in metres above sea level");
        WriteHeightPng(map, paths.HeightPngPath);
        WritePreviewPng(map, radius, paths.PreviewPngPath);

        var (min, max, mean, landFraction) = map.ComputeStatistics();
        var s = terrain.Settings;

        var manifest = new WorldManifest(
            FormatVersion: 4,
            Tool: "WorldGen",
            Stage: paths.StageManifestName,
            Seed: s.Seed,
            PlanetRadiusMeters: radius,
            Datum: "sea-level",
            View: new ManifestView(map.View.NorthLatitude, map.View.SouthLatitude, map.View.WestLongitude, map.View.EastLongitude),
            Width: map.Width,
            Height: map.Height,
            MetersPerPixelEquator: metersPerPixel,
            MetersPerPixelNorthSouth: radius * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height,
            PngEncodingRange: new ManifestElevationRange(HeightmapMinMeters, HeightmapMaxMeters),
            Encoding: "16-bit grayscale, linear meters with fixed window",
            Terrain: new ManifestTerrainParameters(
                s.TargetLandFraction,
                terrain.LandThreshold,
                s.ShelfDepthMeters,
                s.AbyssDepthMeters,
                s.ContinentalReliefMeters,
                s.LandBaseMeters,
                s.MountainReliefMeters,
                s.HillReliefMeters,
                s.DetailReliefMeters,
                s.RiftDepthMeters,
                s.RiftShoulderMeters,
                s.TrenchDepthMeters,
                s.ArcReliefMeters),
            Structure: new ManifestStructure(
                "kinematic-plate-sketch + blended-provinces + sparse-landmarks",
                terrain.Tectonics.PlateCount,
                terrain.Provinces.Count,
                terrain.Tectonics.Hotspots.Count,
                terrain.Landmarks.Count),
            Files: new ManifestFiles(
                Path.GetFileName(paths.HeightGeoTiffPath),
                Path.GetFileName(paths.HeightPngPath),
                Path.GetFileName(paths.PreviewPngPath)),
            Observed: new ManifestStatistics(
                landFraction,
                min,
                max,
                mean,
                "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."),
            ModelVersion: ModelVersions.BaseTopology);

        ManifestWriter.Write(paths.ManifestJsonPath, manifest);
        return paths;
    }

    public static StageArtifactSet WriteStage2(
        string outputPrefix,
        ErosionResult erosion,
        float planetRadiusMeters,
        PlanetTerrain? terrain = null,
        string? sourceGeoTiffPath = null,
        WorldManifest? sourceManifest = null)
    {
        StageArtifactSet paths = StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Erosion);
        StageArtifactPaths.EnsureNotOverwritingSource(sourceGeoTiffPath, paths.HeightGeoTiffPath);
        EnsureDirectory(paths.HeightGeoTiffPath);

        Heightmap map = erosion.Eroded;
        double metersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;

        WriteGeoTiff(map, planetRadiusMeters, paths.HeightGeoTiffPath, "WorldGen Stage 2 eroded topology; elevations in metres above sea level");
        WriteHeightPng(map, paths.HeightPngPath);
        WritePreviewPng(map, planetRadiusMeters, paths.PreviewPngPath);

        var (min, max, mean, landFraction) = map.ComputeStatistics();
        int seed = terrain?.Settings.Seed ?? sourceManifest?.Seed ?? erosion.Settings.Seed;
        var (terrainParams, structureParams) = BuildStructuralMetadata(terrain, sourceManifest);
        ManifestErosionReport erosionReport = BuildErosionReport(erosion);

        string recordedSourceTiff = !string.IsNullOrWhiteSpace(sourceGeoTiffPath)
            ? Path.GetFileName(sourceGeoTiffPath)
            : Path.GetFileName(StageArtifactPaths.ForStage(outputPrefix, PipelineStage.BaseTopology).HeightGeoTiffPath);

        var manifest = new WorldManifest(
            FormatVersion: 4,
            Tool: "WorldGen",
            Stage: paths.StageManifestName,
            Seed: seed,
            PlanetRadiusMeters: planetRadiusMeters,
            Datum: "sea-level",
            View: new ManifestView(map.View.NorthLatitude, map.View.SouthLatitude, map.View.WestLongitude, map.View.EastLongitude),
            Width: map.Width,
            Height: map.Height,
            MetersPerPixelEquator: metersPerPixel,
            MetersPerPixelNorthSouth: planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height,
            PngEncodingRange: new ManifestElevationRange(HeightmapMinMeters, HeightmapMaxMeters),
            Encoding: "16-bit grayscale, linear meters with fixed window",
            Terrain: terrainParams,
            Structure: structureParams,
            Files: new ManifestFiles(
                Path.GetFileName(paths.HeightGeoTiffPath),
                Path.GetFileName(paths.HeightPngPath),
                Path.GetFileName(paths.PreviewPngPath)),
            Observed: new ManifestStatistics(
                landFraction,
                min,
                max,
                mean,
                "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."),
            SourceHeightGeoTiff: recordedSourceTiff,
            Erosion: erosionReport,
            ModelVersion: ModelVersions.Erosion);

        ManifestWriter.Write(paths.ManifestJsonPath, manifest);
        return paths;
    }

    public static StageArtifactSet WriteStage3Climate(
        string outputPrefix,
        ClimateResult climate,
        float planetRadiusMeters,
        PlanetTerrain? terrain = null,
        string? sourceGeoTiffPath = null,
        WorldManifest? sourceManifest = null,
        ErosionResult? erosion = null)
    {
        StageArtifactSet paths = StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Climate);
        StageArtifactPaths.EnsureNotOverwritingSource(sourceGeoTiffPath, paths.HeightGeoTiffPath);
        EnsureDirectory(paths.HeightGeoTiffPath);

        Heightmap map = climate.Source;
        double metersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;

        WriteGeoTiff(map, planetRadiusMeters, paths.HeightGeoTiffPath, "WorldGen Stage 3 climate reference elevation; metres above sea level");
        WriteHeightPng(map, paths.HeightPngPath);

        string rainfallTiffPath = $"{paths.BasePrefix}.{paths.StageTag}-rainfall.tif";
        WriteFloatRasterGeoTiff(
            climate.PrecipitationMmPerYear,
            map.Width,
            map.Height,
            map.View,
            planetRadiusMeters,
            rainfallTiffPath,
            "WorldGen Stage 3 mean annual precipitation in millimetres per year");

        byte[] climatePreviewRgb = TerrainPreview.RenderClimate(climate, planetRadiusMeters);
        PngWriter.WriteRgb(paths.PreviewPngPath, climatePreviewRgb, map.Width, map.Height);

        var (min, max, mean, landFraction) = map.ComputeStatistics();
        int seed = terrain?.Settings.Seed ?? sourceManifest?.Seed ?? 1337;
        var (terrainParams, structureParams) = BuildStructuralMetadata(terrain, sourceManifest);
        ManifestErosionReport? erosionReport = erosion is not null ? BuildErosionReport(erosion) : sourceManifest?.Erosion;
        ManifestClimateReport climateReport = BuildClimateReport(climate, Path.GetFileName(rainfallTiffPath));

        string recordedSourceTiff = !string.IsNullOrWhiteSpace(sourceGeoTiffPath)
            ? Path.GetFileName(sourceGeoTiffPath)
            : Path.GetFileName(StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Erosion).HeightGeoTiffPath);

        var manifest = new WorldManifest(
            FormatVersion: 4,
            Tool: "WorldGen",
            Stage: paths.StageManifestName,
            Seed: seed,
            PlanetRadiusMeters: planetRadiusMeters,
            Datum: "sea-level",
            View: new ManifestView(map.View.NorthLatitude, map.View.SouthLatitude, map.View.WestLongitude, map.View.EastLongitude),
            Width: map.Width,
            Height: map.Height,
            MetersPerPixelEquator: metersPerPixel,
            MetersPerPixelNorthSouth: planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height,
            PngEncodingRange: new ManifestElevationRange(HeightmapMinMeters, HeightmapMaxMeters),
            Encoding: "16-bit grayscale, linear meters with fixed window",
            Terrain: terrainParams,
            Structure: structureParams,
            Files: new ManifestFiles(
                Path.GetFileName(paths.HeightGeoTiffPath),
                Path.GetFileName(paths.HeightPngPath),
                Path.GetFileName(paths.PreviewPngPath)),
            Observed: new ManifestStatistics(
                landFraction,
                min,
                max,
                mean,
                "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."),
            SourceHeightGeoTiff: recordedSourceTiff,
            Erosion: erosionReport,
            Climate: climateReport,
            ModelVersion: ModelVersions.Climate);

        ManifestWriter.Write(paths.ManifestJsonPath, manifest);
        return paths;
    }

    public static StageArtifactSet WriteStage4Hydrology(
        string outputPrefix,
        HydrologyResult hydrology,
        float planetRadiusMeters,
        PlanetTerrain? terrain = null,
        string? sourceGeoTiffPath = null,
        WorldManifest? sourceManifest = null,
        ErosionResult? erosion = null)
    {
        StageArtifactSet paths = StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Hydrology);
        StageArtifactPaths.EnsureNotOverwritingSource(sourceGeoTiffPath, paths.HeightGeoTiffPath);
        EnsureDirectory(paths.HeightGeoTiffPath);

        Heightmap map = hydrology.HydrologicalHeightmap;
        double metersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;

        WriteGeoTiff(map, planetRadiusMeters, paths.HeightGeoTiffPath, "WorldGen Stage 4 hydrologically conditioned topology; elevations in metres above sea level");
        WriteHeightPng(map, paths.HeightPngPath);

        byte[] hydroPreviewRgb = TerrainPreview.RenderHydrology(hydrology, planetRadiusMeters);
        PngWriter.WriteRgb(paths.PreviewPngPath, hydroPreviewRgb, map.Width, map.Height);

        string vectorsPath = $"{paths.BasePrefix}.{paths.StageTag}-vectors.json";
        var vectorsArtifact = new HydrologyVectorsArtifact(
            FormatVersion: 1,
            Stage: paths.StageManifestName,
            Width: map.Width,
            Height: map.Height,
            View: new ManifestView(map.View.NorthLatitude, map.View.SouthLatitude, map.View.WestLongitude, map.View.EastLongitude),
            Lakes: hydrology.Lakes,
            Rivers: hydrology.Rivers);
        ManifestWriter.WriteHydrologyVectors(vectorsPath, vectorsArtifact);

        var (min, max, mean, landFraction) = map.ComputeStatistics();
        int seed = terrain?.Settings.Seed ?? sourceManifest?.Seed ?? 1337;
        var (terrainParams, structureParams) = BuildStructuralMetadata(terrain, sourceManifest);
        ManifestErosionReport? erosionReport = erosion is not null ? BuildErosionReport(erosion) : sourceManifest?.Erosion;

        string rainfallFileName = Path.GetFileName($"{paths.BasePrefix}.s3-climate-rainfall.tif");
        ManifestClimateReport climateReport = BuildClimateReport(hydrology.Climate, rainfallFileName);

        var hs = hydrology.Settings;
        ManifestHydrologyReport hydroReport = BuildHydrologyReport(hydrology, Path.GetFileName(vectorsPath));

        string recordedSourceTiff = !string.IsNullOrWhiteSpace(sourceGeoTiffPath)
            ? Path.GetFileName(sourceGeoTiffPath)
            : Path.GetFileName(StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Climate).HeightGeoTiffPath);

        var manifest = new WorldManifest(
            FormatVersion: 4,
            Tool: "WorldGen",
            Stage: paths.StageManifestName,
            Seed: seed,
            PlanetRadiusMeters: planetRadiusMeters,
            Datum: "sea-level",
            View: new ManifestView(map.View.NorthLatitude, map.View.SouthLatitude, map.View.WestLongitude, map.View.EastLongitude),
            Width: map.Width,
            Height: map.Height,
            MetersPerPixelEquator: metersPerPixel,
            MetersPerPixelNorthSouth: planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height,
            PngEncodingRange: new ManifestElevationRange(HeightmapMinMeters, HeightmapMaxMeters),
            Encoding: "16-bit grayscale, linear meters with fixed window",
            Terrain: terrainParams,
            Structure: structureParams,
            Files: new ManifestFiles(
                Path.GetFileName(paths.HeightGeoTiffPath),
                Path.GetFileName(paths.HeightPngPath),
                Path.GetFileName(paths.PreviewPngPath)),
            Observed: new ManifestStatistics(
                landFraction,
                min,
                max,
                mean,
                "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."),
            SourceHeightGeoTiff: recordedSourceTiff,
            Erosion: erosionReport,
            Climate: climateReport,
            Hydrology: hydroReport,
            ModelVersion: ModelVersions.Hydrology);

        ManifestWriter.Write(paths.ManifestJsonPath, manifest);
        return paths;
    }

    public static StageArtifactSet WriteStage5Biomes(
        string outputPrefix,
        BiomeResult biomes,
        float planetRadiusMeters,
        PlanetTerrain? terrain = null,
        string? sourceGeoTiffPath = null,
        WorldManifest? sourceManifest = null,
        ErosionResult? erosion = null)
    {
        StageArtifactSet paths = StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Biomes);
        StageArtifactPaths.EnsureNotOverwritingSource(sourceGeoTiffPath, paths.HeightGeoTiffPath);
        EnsureDirectory(paths.HeightGeoTiffPath);

        Heightmap map = biomes.Elevation;
        double metersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;

        WriteGeoTiff(map, planetRadiusMeters, paths.HeightGeoTiffPath, "WorldGen Stage 5 biome reference elevation; metres above sea level");
        WriteHeightPng(map, paths.HeightPngPath);

        // Categorical biome ids as float32 (exact for the id range; the GeoTIFF writer is float32-only today).
        var idsFloat = new float[map.Width * map.Height];
        for (int i = 0; i < idsFloat.Length; i++)
        {
            idsFloat[i] = biomes.BiomeId[i];
        }

        string idsTiffPath = StageArtifactPaths.ExtraPath(outputPrefix, PipelineStage.Biomes, "ids.tif");
        WriteFloatRasterGeoTiff(idsFloat, map.Width, map.Height, map.View, planetRadiusMeters, idsTiffPath, "WorldGen Stage 5 biome ids (categorical)");

        string idsPngPath = StageArtifactPaths.ExtraPath(outputPrefix, PipelineStage.Biomes, "ids.png");
        PngWriter.WriteRgb(idsPngPath, TerrainPreview.RenderBiomeAlbedo(biomes), map.Width, map.Height);

        PngWriter.WriteRgb(paths.PreviewPngPath, TerrainPreview.RenderBiomes(biomes, planetRadiusMeters), map.Width, map.Height);

        string palettePath = StageArtifactPaths.ExtraPath(outputPrefix, PipelineStage.Biomes, "palette.json");
        ManifestWriter.WriteBiomePalette(palettePath, BuildPaletteArtifact(biomes, paths.StageManifestName));

        var (min, max, mean, landFraction) = map.ComputeStatistics();
        int seed = terrain?.Settings.Seed ?? sourceManifest?.Seed ?? 1337;
        var (terrainParams, structureParams) = BuildStructuralMetadata(terrain, sourceManifest);
        ManifestErosionReport? erosionReport = erosion is not null ? BuildErosionReport(erosion) : sourceManifest?.Erosion;

        var biomeReport = new ManifestBiomeReport(
            BiomeSetId: biomes.Set.Id,
            BiomeSetVersion: biomes.Set.Version,
            Palette: biomes.Palette.Id,
            BiomeCount: biomes.Set.Biomes.Count,
            WaterClassCount: BiomeTable.Default.WaterClasses.Count,
            UnclassifiedLandCells: biomes.UnclassifiedLandCells,
            LandFraction: Math.Round(biomes.LandFraction, 4),
            RiparianStrength: Math.Round(biomes.Settings.RiparianStrength, 4),
            RiparianScaleCells: Math.Round(biomes.Settings.RiparianScaleCells, 4),
            CoastalHumidityStrength: Math.Round(biomes.Settings.CoastalHumidityStrength, 4),
            CoastalScaleCells: Math.Round(biomes.Settings.CoastalScaleCells, 4),
            EcotoneTempAmplitudeC: Math.Round(biomes.Settings.EcotoneTempAmplitudeC, 4),
            EcotoneMoistureAmplitude: Math.Round(biomes.Settings.EcotoneMoistureAmplitude, 4),
            EcotoneWavelengthKm: Math.Round(biomes.Settings.EcotoneWavelengthKm, 4),
            SmoothingPasses: biomes.Settings.SmoothingPasses,
            BiomeCells: BuildBiomeCellNames(biomes),
            BiomeIdsGeoTiff: Path.GetFileName(idsTiffPath),
            PaletteFile: Path.GetFileName(palettePath));

        string recordedSourceTiff = !string.IsNullOrWhiteSpace(sourceGeoTiffPath)
            ? Path.GetFileName(sourceGeoTiffPath)
            : Path.GetFileName(StageArtifactPaths.ForStage(outputPrefix, PipelineStage.Hydrology).HeightGeoTiffPath);

        string rainfallFileName = Path.GetFileName($"{paths.BasePrefix}.s3-climate-rainfall.tif");
        ManifestClimateReport climateReport = BuildClimateReport(biomes.Hydrology.Climate, rainfallFileName);
        ManifestHydrologyReport hydroReport = BuildHydrologyReport(
            biomes.Hydrology,
            Path.GetFileName($"{paths.BasePrefix}.s4-hydro-vectors.json"));

        var manifest = new WorldManifest(
            FormatVersion: 4,
            Tool: "WorldGen",
            Stage: paths.StageManifestName,
            Seed: seed,
            PlanetRadiusMeters: planetRadiusMeters,
            Datum: "sea-level",
            View: new ManifestView(map.View.NorthLatitude, map.View.SouthLatitude, map.View.WestLongitude, map.View.EastLongitude),
            Width: map.Width,
            Height: map.Height,
            MetersPerPixelEquator: metersPerPixel,
            MetersPerPixelNorthSouth: planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height,
            PngEncodingRange: new ManifestElevationRange(HeightmapMinMeters, HeightmapMaxMeters),
            Encoding: "16-bit grayscale, linear meters with fixed window",
            Terrain: terrainParams,
            Structure: structureParams,
            Files: new ManifestFiles(
                Path.GetFileName(paths.HeightGeoTiffPath),
                Path.GetFileName(paths.HeightPngPath),
                Path.GetFileName(paths.PreviewPngPath)),
            Observed: new ManifestStatistics(
                landFraction,
                min,
                max,
                mean,
                "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."),
            SourceHeightGeoTiff: recordedSourceTiff,
            Erosion: erosionReport,
            Climate: climateReport,
            Hydrology: hydroReport,
            Biome: biomeReport,
            ModelVersion: ModelVersions.Biomes);

        ManifestWriter.Write(paths.ManifestJsonPath, manifest);
        return paths;
    }

    private static IReadOnlyDictionary<string, int> BuildBiomeCellNames(BiomeResult biomes)
    {
        var names = new Dictionary<int, string>();
        foreach (BiomeWaterClass water in BiomeTable.Default.WaterClasses)
        {
            names[water.Id] = water.Key;
        }

        foreach (BiomeDefinition biome in biomes.Set.Biomes)
        {
            names[biome.Id] = biome.Key;
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((int id, int count) in biomes.BiomeCellCounts.OrderBy(pair => pair.Key))
        {
            string name = id == 0 ? "unclassified" : names.TryGetValue(id, out string? key) ? key : $"id_{id}";
            result[name] = count;
        }

        return result;
    }

    private static BiomePaletteArtifact BuildPaletteArtifact(BiomeResult biomes, string stageName)
    {
        BiomePalette palette = biomes.Palette;
        var entries = new List<BiomePaletteEntry>
        {
            new(0, "unclassified", "Unclassified", "unclassified", BiomeColor.Unclassified.ToHex()),
        };

        foreach (BiomeWaterClass water in BiomeTable.Default.WaterClasses.OrderBy(water => water.Id))
        {
            entries.Add(new BiomePaletteEntry(water.Id, water.Key, water.Name, "water", palette.ColorOf(water.Id).ToHex()));
        }

        foreach (BiomeDefinition biome in biomes.Set.Biomes.OrderBy(biome => biome.Id))
        {
            entries.Add(new BiomePaletteEntry(biome.Id, biome.Key, biome.Name, "land", palette.ColorOf(biome.Id).ToHex()));
        }

        return new BiomePaletteArtifact(1, stageName, biomes.Set.Id, biomes.Set.Version, palette.Id, entries);
    }

    private static (ManifestTerrainParameters? Terrain, ManifestStructure? Structure) BuildStructuralMetadata(
        PlanetTerrain? terrain,
        WorldManifest? sourceManifest)
    {
        if (terrain is null)
        {
            return (sourceManifest?.Terrain, sourceManifest?.Structure);
        }

        var s = terrain.Settings;
        var terrainParams = new ManifestTerrainParameters(
            s.TargetLandFraction,
            terrain.LandThreshold,
            s.ShelfDepthMeters,
            s.AbyssDepthMeters,
            s.ContinentalReliefMeters,
            s.LandBaseMeters,
            s.MountainReliefMeters,
            s.HillReliefMeters,
            s.DetailReliefMeters,
            s.RiftDepthMeters,
            s.RiftShoulderMeters,
            s.TrenchDepthMeters,
            s.ArcReliefMeters);
        var structureParams = new ManifestStructure(
            "kinematic-plate-sketch + blended-provinces + sparse-landmarks",
            terrain.Tectonics.PlateCount,
            terrain.Provinces.Count,
            terrain.Tectonics.Hotspots.Count,
            terrain.Landmarks.Count);

        return (terrainParams, structureParams);
    }

    private static ManifestErosionReport BuildErosionReport(ErosionResult erosion)
    {
        var es = erosion.Settings;
        return new ManifestErosionReport(
            Iterations: es.Iterations,
            TimeStep: es.TimeStep,
            StreamPowerK: es.StreamPowerK,
            AreaExponentM: es.AreaExponentM,
            SlopeExponentN: es.SlopeExponentN,
            HillslopeDiffusivity: es.HillslopeDiffusivity,
            SedimentTransportCapacity: es.SedimentTransportCapacity,
            DepositionRate: es.DepositionRate,
            UpliftScale: es.UpliftScale,
            HaloCells: es.HaloCells,
            MeanErosionMeters: Math.Round(erosion.MeanErosionMeters, 3),
            MaxErosionMeters: Math.Round(erosion.MaxErosionMeters, 3),
            MeanDepositionMeters: Math.Round(erosion.MeanDepositionMeters, 3),
            MaxDepositionMeters: Math.Round(erosion.MaxDepositionMeters, 3),
            CoastlineProgradedCells: erosion.CoastlineProgradedCells);
    }

    private static ManifestClimateReport BuildClimateReport(ClimateResult climate, string rainfallFileName)
    {
        var cs = climate.Settings;
        return new ManifestClimateReport(
            EquatorialTempCelsius: cs.EquatorialTempCelsius,
            PolarTempCelsius: cs.PolarTempCelsius,
            LapseRateCPerKm: cs.LapseRateCPerKm,
            PrecipitationScale: cs.PrecipitationScale,
            OrographicLiftStrength: cs.OrographicLiftStrength,
            RainShadowStrength: cs.RainShadowStrength,
            MoistureDecayKm: cs.MoistureDecayKm,
            MeanLandTemperatureCelsius: Math.Round(climate.MeanLandTemperatureCelsius, 2),
            MeanLandPrecipitationMm: Math.Round(climate.MeanLandPrecipitationMm, 1),
            MaxLandPrecipitationMm: Math.Round(climate.MaxLandPrecipitationMm, 1),
            MeanLandRunoffMm: Math.Round(climate.MeanLandRunoffMm, 1),
            AridLandFraction: Math.Round(climate.AridLandFraction, 4),
            HumidLandFraction: Math.Round(climate.HumidLandFraction, 4),
            RainfallGeoTiff: rainfallFileName);
    }

    private static ManifestHydrologyReport BuildHydrologyReport(HydrologyResult hydrology, string vectorsFileName)
    {
        var hs = hydrology.Settings;
        return new ManifestHydrologyReport(
            MinRiverDischargeM3s: hs.MinRiverDischargeM3s,
            RiverThresholdSqKm: hs.RiverThresholdSqKm,
            MinHeadwaterReachCells: hs.MinHeadwaterReachCells,
            MinLakeCells: hs.MinLakeCells,
            MinLakeDepthMeters: hs.MinLakeDepthMeters,
            MaxLakeDepthMeters: hs.MaxLakeDepthMeters,
            LakeCount: hydrology.Lakes.Count,
            TotalLakeAreaSqKm: Math.Round(hydrology.TotalLakeAreaSqKm, 1),
            RiverReachCount: hydrology.Rivers.Count,
            TotalRiverLengthKm: Math.Round(hydrology.TotalRiverLengthKm, 1),
            MaxStrahlerOrder: hydrology.MaxStrahlerOrder,
            RiverCellCount: hydrology.RiverCellCount,
            BreachedDepressionCount: hydrology.BreachedDepressionCount,
            VectorsFile: vectorsFileName);
    }

    private static void EnsureDirectory(string filePath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void WriteHeightPng(Heightmap map, string path)
    {
        var pixels = new ushort[map.Width * map.Height];
        float span = HeightmapMaxMeters - HeightmapMinMeters;
        for (int i = 0; i < map.Elevations.Length; i++)
        {
            float meters = map.Elevations[i];
            float normalized = Math.Clamp((meters - HeightmapMinMeters) / span, 0.0f, 1.0f);
            pixels[i] = (ushort)Math.Clamp(MathF.Round(normalized * 65535.0f), 0.0f, 65535.0f);
        }

        PngWriter.WriteGrayscale16(path, pixels, map.Width, map.Height);
    }

    private static void WritePreviewPng(Heightmap map, float planetRadiusMeters, string path)
    {
        byte[] pixels = TerrainPreview.Render(map, planetRadiusMeters);
        PngWriter.WriteRgb(path, pixels, map.Width, map.Height);
    }

    private static void WriteGeoTiff(Heightmap map, float planetRadiusMeters, string path, string description) =>
        WriteFloatRasterGeoTiff(map.Elevations, map.Width, map.Height, map.View, planetRadiusMeters, path, description);

    private static void WriteFloatRasterGeoTiff(
        ReadOnlySpan<float> values,
        int width,
        int height,
        MapView view,
        float planetRadiusMeters,
        string path,
        string description)
    {
        var tiffOptions = new GeoTiffOptions
        {
            WestLongitude = view.WestLongitude,
            NorthLatitude = view.NorthLatitude,
            LongitudeSpan = view.LongitudeSpan,
            LatitudeSpan = view.LatitudeSpan,
            PlanetRadiusMeters = planetRadiusMeters,
            Description = description,
        };
        GeoTiffWriter.WriteFloat32(path, values, width, height, tiffOptions);
    }
}
