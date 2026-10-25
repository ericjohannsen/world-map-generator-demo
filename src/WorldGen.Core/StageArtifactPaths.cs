namespace WorldGen;

/// <summary>
/// Pipeline stage identifiers used in file naming and manifests so every elevation file clearly identifies
/// its processing stage and later stages never overwrite earlier stages.
/// </summary>
public enum PipelineStage
{
    BaseTopology = 1,
    Erosion = 2,
    Climate = 3,
    Hydrology = 4,
    Biomes = 5,
}

public sealed record StageArtifactSet(
    string BasePrefix,
    PipelineStage Stage,
    string StageTag,
    string StageManifestName,
    string HeightGeoTiffPath,
    string HeightPngPath,
    string PreviewPngPath,
    string ManifestJsonPath);

/// <summary>
/// Enforces the stage-tagged artifact naming convention:
/// <list type="bullet">
///   <item><c>&lt;prefix&gt;.s1-base-height.tif</c> (Stage 1: Base topology)</item>
///   <item><c>&lt;prefix&gt;.s2-eroded-height.tif</c> (Stage 2: Erosion)</item>
///   <item><c>&lt;prefix&gt;.s3-climate-height.tif</c> + <c>-rainfall.tif</c> (Stage 3: Climate &amp; Rainfall)</item>
///   <item><c>&lt;prefix&gt;.s4-hydro-height.tif</c> + <c>-vectors.json</c> (Stage 4: Hydrology)</item>
///   <item><c>&lt;prefix&gt;.s5-biome-height.tif</c> + <c>-ids.tif</c> + <c>-palette.json</c> (Stage 5: Biomes)</item>
/// </list>
/// </summary>
public static class StageArtifactPaths
{
    private static readonly string[] KnownSuffixes =
    [
        ".s1-base-height.tif",
        ".s1-base-height.png",
        ".s1-base-preview.png",
        ".s1-base-manifest.json",
        ".s1-base",
        ".s2-eroded-height.tif",
        ".s2-eroded-height.png",
        ".s2-eroded-preview.png",
        ".s2-eroded-manifest.json",
        ".s2-eroded",
        ".s3-climate-height.tif",
        ".s3-climate-rainfall.tif",
        ".s3-climate-height.png",
        ".s3-climate-preview.png",
        ".s3-climate-manifest.json",
        ".s3-climate",
        ".s4-hydro-height.tif",
        ".s4-hydro-height.png",
        ".s4-hydro-preview.png",
        ".s4-hydro-manifest.json",
        ".s4-hydro-vectors.json",
        ".s4-hydro",
        ".s3-hydro-height.tif",
        ".s3-hydro-height.png",
        ".s3-hydro-preview.png",
        ".s3-hydro-manifest.json",
        ".s3-hydro-vectors.json",
        ".s3-hydro",
        ".s5-biome-height.tif",
        ".s5-biome-ids.tif",
        ".s5-biome-ids.png",
        ".s5-biome-preview.png",
        ".s5-biome-palette.json",
        ".s5-biome-manifest.json",
        ".s5-biome",
        ".s4-biome-height.tif",
        ".s4-biome",
        "-height.tif",
        "-height.png",
        "-preview.png",
        "-manifest.json",
        ".tif",
        ".tiff",
    ];

    public static (string Tag, string ManifestName) GetStageLabels(PipelineStage stage) => stage switch
    {
        PipelineStage.BaseTopology => ("s1-base", "base-topology"),
        PipelineStage.Erosion => ("s2-eroded", "erosion"),
        PipelineStage.Climate => ("s3-climate", "climate"),
        PipelineStage.Hydrology => ("s4-hydro", "hydrology"),
        PipelineStage.Biomes => ("s5-biome", "biomes"),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown pipeline stage."),
    };

    /// <summary>
    /// Normalizes a user-supplied output prefix or file path by stripping any existing stage/artifact suffix.
    /// </summary>
    public static string NormalizePrefix(string pathOrPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrPrefix);
        string trimmed = pathOrPrefix.Trim();

        foreach (string suffix in KnownSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
            {
                return trimmed[..^suffix.Length];
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Resolves the four canonical artifact paths for a given prefix and pipeline stage.
    /// </summary>
    public static StageArtifactSet ForStage(string pathOrPrefix, PipelineStage stage)
    {
        string prefix = Path.GetFullPath(NormalizePrefix(pathOrPrefix));
        var (tag, manifestName) = GetStageLabels(stage);
        string stemmed = $"{prefix}.{tag}";

        return new StageArtifactSet(
            BasePrefix: prefix,
            Stage: stage,
            StageTag: tag,
            StageManifestName: manifestName,
            HeightGeoTiffPath: $"{stemmed}-height.tif",
            HeightPngPath: $"{stemmed}-height.png",
            PreviewPngPath: $"{stemmed}-preview.png",
            ManifestJsonPath: $"{stemmed}-manifest.json");
    }

    /// <summary>
    /// Resolves a stage-tagged companion file path beyond the four canonical artifacts, e.g.
    /// <c>&lt;prefix&gt;.s5-biome-ids.tif</c> for <c>ExtraPath(prefix, PipelineStage.Biomes, "ids.tif")</c>.
    /// </summary>
    public static string ExtraPath(string pathOrPrefix, PipelineStage stage, string suffix)
    {
        string prefix = Path.GetFullPath(NormalizePrefix(pathOrPrefix));
        var (tag, _) = GetStageLabels(stage);
        return $"{prefix}.{tag}-{suffix}";
    }

    /// <summary>
    /// Given a source GeoTIFF path, attempts to locate its companion manifest JSON file.
    /// </summary>
    public static string? TryFindCompanionManifest(string geoTiffPath)
    {
        string full = Path.GetFullPath(geoTiffPath);
        if (full.EndsWith("-height.tif", StringComparison.OrdinalIgnoreCase))
        {
            string candidate = full[..^"-height.tif".Length] + "-manifest.json";
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string prefix = NormalizePrefix(full);
        string[] stageTags = ["s3-climate", "s2-eroded", "s1-base"];
        foreach (string tag in stageTags)
        {
            string candidate = $"{prefix}.{tag}-manifest.json";
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string legacyCandidate = $"{prefix}-manifest.json";
        if (File.Exists(legacyCandidate))
        {
            return legacyCandidate;
        }

        return null;
    }

    /// <summary>
    /// Given a source GeoTIFF path, attempts to locate a companion Stage 3 rainfall GeoTIFF if one exists.
    /// </summary>
    public static string? TryFindCompanionRainfallTiff(string geoTiffPath)
    {
        string prefix = Path.GetFullPath(NormalizePrefix(geoTiffPath));
        string candidate = $"{prefix}.s3-climate-rainfall.tif";
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Guards against ever overwriting a source GeoTIFF during a downstream stage.
    /// </summary>
    public static void EnsureNotOverwritingSource(string? sourceGeoTiffPath, string targetGeoTiffPath)
    {
        if (string.IsNullOrWhiteSpace(sourceGeoTiffPath))
        {
            return;
        }

        string sourceFull = Path.GetFullPath(sourceGeoTiffPath);
        string targetFull = Path.GetFullPath(targetGeoTiffPath);
        if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite source elevation file '{sourceFull}'. Stage outputs must write to a distinct stage-tagged file.");
        }
    }
}
