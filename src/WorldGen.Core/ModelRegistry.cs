using System.Reflection;
using System.Text.Json;

namespace WorldGen;

/// <summary>Lifecycle of a model version. Every model is <see cref="Dev"/> until the release pipeline marks it.</summary>
public enum ModelStatus
{
    Dev,
    Released,
    Unknown,
}

/// <summary>One registry entry: what the version means and whether it is released or still in development.</summary>
public sealed record ModelVersionInfo(
    string Stage,
    int Version,
    ModelStatus Status,
    string Compatibility,
    string Reason,
    string? ReleasedIn,
    IReadOnlyList<string> Introduced,
    IReadOnlyList<string> Removed);

/// <summary>
/// Current model versions. Bump a stage when code, constants, defaults, or operation order can change that
/// stage's numbers. Bump <see cref="CacheSchema"/> when the stage bundle layout changes. Bump
/// <see cref="SettingsSchema"/> when the settings shape or its meaning changes.
/// </summary>
public static class ModelVersions
{
    public const int CacheSchema = 1;
    public const int SettingsSchema = 1;

    public const int BaseTopology = 1;
    public const int Erosion = 1;
    public const int Climate = 1;
    public const int Hydrology = 1;
    public const int Biomes = 1;

    public static IReadOnlyDictionary<string, int> Current { get; } = new Dictionary<string, int>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["s1"] = BaseTopology,
        ["s2"] = Erosion,
        ["s3"] = Climate,
        ["s4"] = Hydrology,
        ["s5"] = Biomes,
    };
}

/// <summary>
/// Read-only view of <c>models.json</c>. The registry is the source of truth for released-versus-dev status
/// and for the human-readable reason and compatibility of each model version.
/// </summary>
public sealed class ModelRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, ModelVersionInfo> _models = new(StringComparer.OrdinalIgnoreCase);

    private ModelRegistry()
    {
    }

    public static ModelRegistry Default { get; } = LoadEmbedded();

    public IReadOnlyCollection<ModelVersionInfo> All => _models.Values.OrderBy(m => m.Stage).ThenBy(m => m.Version).ToArray();

    public ModelVersionInfo? Find(string stage, int version) =>
        _models.TryGetValue($"{stage}:{version}", out ModelVersionInfo? info) ? info : null;

    public ModelStatus StatusOf(string stage, int version) =>
        Find(stage, version)?.Status ?? ModelStatus.Unknown;

    /// <summary>Fails fast if the code's current versions are missing from the registry.</summary>
    public void ValidateCurrent()
    {
        foreach ((string stage, int version) in ModelVersions.Current)
        {
            if (Find(stage, version) is null)
            {
                throw new InvalidOperationException(
                    $"Model registry is missing stage {stage} version {version}. " +
                    "Update models.json whenever a model version is bumped.");
            }
        }
    }

    public static ModelRegistry Parse(string json)
    {
        RegistryDocument? document = JsonSerializer.Deserialize<RegistryDocument>(json, Options);
        if (document is null)
        {
            throw new InvalidOperationException("The model registry is empty.");
        }

        var registry = new ModelRegistry();
        foreach ((string stage, List<RegistryEntry> entries) in document.Stages)
        {
            foreach (RegistryEntry entry in entries)
            {
                ModelStatus status = entry.Status?.ToLowerInvariant() switch
                {
                    "released" => ModelStatus.Released,
                    "dev" => ModelStatus.Dev,
                    _ => ModelStatus.Unknown,
                };

                var info = new ModelVersionInfo(
                    stage,
                    entry.Version,
                    status,
                    entry.Compatibility ?? "unknown",
                    entry.Reason ?? string.Empty,
                    entry.ReleasedIn,
                    entry.Introduced ?? new List<string>(),
                    entry.Removed ?? new List<string>());

                registry._models[$"{stage}:{entry.Version}"] = info;
            }
        }

        return registry;
    }

    private static ModelRegistry LoadEmbedded()
    {
        Assembly assembly = typeof(ModelRegistry).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("models.json", StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidOperationException("The embedded model registry (models.json) is missing.");
        }

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        ModelRegistry registry = Parse(reader.ReadToEnd());
        registry.ValidateCurrent();
        return registry;
    }

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, List<RegistryEntry>> Stages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RegistryEntry
    {
        public int Version { get; set; }
        public string? Status { get; set; }
        public string? Compatibility { get; set; }
        public string? Reason { get; set; }
        public string? ReleasedIn { get; set; }
        public List<string>? Introduced { get; set; }
        public List<string>? Removed { get; set; }
    }
}
