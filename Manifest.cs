using System.Text.Json;

namespace WorldGen;

/// <summary>
/// Metadata sidecar for a generated world. GeoTIFF carries the georeferencing; this file records the things a
/// fictional planet cannot express through EPSG codes: seed, radius, generator parameters, and provenance.
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
    ManifestTerrainParameters Terrain,
    ManifestFiles Files,
    ManifestStatistics Observed);

public sealed record ManifestView(double North, double South, double West, double East);

public sealed record ManifestElevationRange(double Min, double Max);

public sealed record ManifestTerrainParameters(
    double TargetLandFraction,
    double LandThreshold,
    double ShelfDepthMeters,
    double AbyssDepthMeters,
    double ContinentalReliefMeters,
    double MountainReliefMeters,
    double HillReliefMeters,
    double DetailReliefMeters);

public sealed record ManifestFiles(string HeightGeoTiff, string HeightPng, string PreviewPng);

public sealed record ManifestStatistics(
    double LandFraction,
    double MinElevation,
    double MaxElevation,
    double MeanElevation,
    string Method);

public static class ManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(string path, WorldManifest manifest)
    {
        // Explicit newline keeps the file byte-identical across platforms.
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, Options) + "\n");
    }
}
