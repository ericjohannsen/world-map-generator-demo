using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldGen;

/// <summary>
/// Metadata sidecar for a generated world. GeoTIFF carries the georeferencing; this file records the things a
/// fictional planet cannot express through EPSG codes: seed, radius, generator parameters, stage provenance,
/// and stage-specific parameters/statistics.
/// </summary>
public sealed record WorldManifest(
    int FormatVersion,
    string Tool,
    string Stage,
    int Seed,
    double PlanetRadiusMeters,
    string Datum,
    ManifestView View,
    int Width,
    int Height,
    double MetersPerPixelEquator,
    double MetersPerPixelNorthSouth,
    ManifestElevationRange PngEncodingRange,
    string Encoding,
    ManifestTerrainParameters? Terrain,
    ManifestStructure? Structure,
    ManifestFiles Files,
    ManifestStatistics Observed,
    string? SourceHeightGeoTiff = null,
    ManifestErosionReport? Erosion = null,
    ManifestClimateReport? Climate = null,
    ManifestHydrologyReport? Hydrology = null,
    ManifestBiomeReport? Biome = null,
    int ModelVersion = 0,
    int SettingsSchemaVersion = ModelVersions.SettingsSchema,
    int CacheSchemaVersion = ModelVersions.CacheSchema);

public sealed record ManifestView(double North, double South, double West, double East);

public sealed record ManifestElevationRange(double Min, double Max);

public sealed record ManifestTerrainParameters(
    double TargetLandFraction,
    double LandThreshold,
    double ShelfDepthMeters,
    double AbyssDepthMeters,
    double ContinentalReliefMeters,
    double LandBaseMeters,
    double MountainReliefMeters,
    double HillReliefMeters,
    double DetailReliefMeters,
    double RiftDepthMeters,
    double RiftShoulderMeters,
    double TrenchDepthMeters,
    double ArcReliefMeters);

/// <summary>
/// Counts for the structural layers. The model name makes clear that plate motion is a sketch, not a
/// geodynamic simulation; consumers should not treat these as physical plate reconstructions.
/// </summary>
public sealed record ManifestStructure(
    string Model,
    int Plates,
    int Provinces,
    int Hotspots,
    int Landmarks);

public sealed record ManifestFiles(string HeightGeoTiff, string HeightPng, string PreviewPng);

public sealed record ManifestStatistics(
    double LandFraction,
    double MinElevation,
    double MaxElevation,
    double MeanElevation,
    string Method);

public sealed record ManifestErosionReport(
    int Iterations,
    double TimeStep,
    double StreamPowerK,
    double AreaExponentM,
    double SlopeExponentN,
    double HillslopeDiffusivity,
    double SedimentTransportCapacity,
    double DepositionRate,
    double UpliftScale,
    int HaloCells,
    double MeanErosionMeters,
    double MaxErosionMeters,
    double MeanDepositionMeters,
    double MaxDepositionMeters,
    int CoastlineProgradedCells);

public sealed record ManifestClimateReport(
    double EquatorialTempCelsius,
    double PolarTempCelsius,
    double LapseRateCPerKm,
    double PrecipitationScale,
    double OrographicLiftStrength,
    double RainShadowStrength,
    double MoistureDecayKm,
    double MeanLandTemperatureCelsius,
    double MeanLandPrecipitationMm,
    double MaxLandPrecipitationMm,
    double MeanLandRunoffMm,
    double AridLandFraction,
    double HumidLandFraction,
    string RainfallGeoTiff);

public sealed record ManifestHydrologyReport(
    double MinRiverDischargeM3s,
    double RiverThresholdSqKm,
    int MinHeadwaterReachCells,
    int MinLakeCells,
    double MinLakeDepthMeters,
    double MaxLakeDepthMeters,
    int LakeCount,
    double TotalLakeAreaSqKm,
    int RiverReachCount,
    double TotalRiverLengthKm,
    int MaxStrahlerOrder,
    int RiverCellCount,
    int BreachedDepressionCount,
    string VectorsFile);

public sealed record HydrologyVectorsArtifact(
    int FormatVersion,
    string Stage,
    int Width,
    int Height,
    ManifestView View,
    IReadOnlyList<LakeFeature> Lakes,
    IReadOnlyList<RiverPolyline> Rivers);

public sealed record ManifestBiomeReport(
    string BiomeSetId,
    int BiomeSetVersion,
    string Palette,
    int BiomeCount,
    int WaterClassCount,
    int UnclassifiedLandCells,
    double LandFraction,
    double RiparianStrength,
    double RiparianScaleCells,
    double CoastalHumidityStrength,
    double CoastalScaleCells,
    double EcotoneTempAmplitudeC,
    double EcotoneMoistureAmplitude,
    double EcotoneWavelengthKm,
    int SmoothingPasses,
    IReadOnlyDictionary<string, int> BiomeCells,
    string BiomeIdsGeoTiff,
    string PaletteFile);

public sealed record BiomePaletteEntry(int Id, string Key, string Name, string Kind, string Color);

public sealed record BiomePaletteArtifact(
    int FormatVersion,
    string Stage,
    string SetId,
    int SetVersion,
    string Palette,
    IReadOnlyList<BiomePaletteEntry> Entries);

public static class ManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(string path, WorldManifest manifest)
    {
        // Explicit newline keeps the file byte-identical across platforms.
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, Options) + "\n");
    }

    public static void WriteHydrologyVectors(string path, HydrologyVectorsArtifact vectors)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(vectors, Options) + "\n");
    }

    public static void WriteBiomePalette(string path, BiomePaletteArtifact palette)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(palette, Options) + "\n");
    }

    public static WorldManifest? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorldManifest>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}
