using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WorldGen;

/// <summary>
/// Canonical, stage-scoped keys for cached stage results. A stage key is the SHA-256 of a deterministic
/// description of the model version, the settings that affect that stage, and (for downstream stages) the
/// upstream stage key. UI state (camera, renderer, grid, window) is deliberately excluded.
/// </summary>
/// <remarks>
/// Settings are described by reflecting over the public properties of the settings record in ordinal name
/// order. Adding a parameter therefore changes the key automatically, which is the conservative choice: a new
/// parameter is a new cache identity even if its default happens to preserve the old behavior.
/// </remarks>
public static class StageKeys
{
    public static string BaseTopology(TerrainSettings settings, MapView view, int width, int height)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "s1", ModelVersions.BaseTopology, inputKey: null);
        AppendSettings(builder, settings);
        AppendMapView(builder, view);
        AppendValue(builder, "width", width);
        AppendValue(builder, "height", height);
        return Hash(builder.ToString());
    }

    public static string Erosion(string inputKey, ErosionSettings settings)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "s2", ModelVersions.Erosion, inputKey);
        AppendSettings(builder, settings);
        return Hash(builder.ToString());
    }

    public static string Climate(string inputKey, ClimateSettings settings)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "s3", ModelVersions.Climate, inputKey);
        AppendSettings(builder, settings);
        return Hash(builder.ToString());
    }

    public static string Hydrology(string inputKey, HydrologySettings settings)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "s4", ModelVersions.Hydrology, inputKey);
        AppendSettings(builder, settings);
        return Hash(builder.ToString());
    }

    public static string Biomes(string inputKey, BiomeSettings settings)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "s5", ModelVersions.Biomes, inputKey);
        AppendSettings(builder, settings);
        return Hash(builder.ToString());
    }

    /// <summary>Readable canonical settings, used as the cache sidecar's settings summary.</summary>
    public static string DescribeSettings(object settings)
    {
        var builder = new StringBuilder();
        AppendSettings(builder, settings);
        return builder.ToString();
    }

    private static void AppendHeader(StringBuilder builder, string stage, int modelVersion, string? inputKey)
    {
        builder.Append("cache=").Append(ModelVersions.CacheSchema).Append(';');
        builder.Append("settings=").Append(ModelVersions.SettingsSchema).Append(';');
        builder.Append("stage=").Append(stage).Append(';');
        builder.Append("model=").Append(modelVersion).Append(';');

        if (!string.IsNullOrEmpty(inputKey))
        {
            builder.Append("input=").Append(inputKey).Append(';');
        }
    }

    private static void AppendSettings(StringBuilder builder, object settings)
    {
        PropertyInfo[] properties = settings.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        builder.Append("settings{");
        foreach (PropertyInfo property in properties)
        {
            builder.Append(property.Name).Append('=');
            AppendValue(builder, property.GetValue(settings));
            builder.Append(';');
        }

        builder.Append("};");
    }

    private static void AppendMapView(StringBuilder builder, MapView view)
    {
        builder.Append("view{");
        AppendValue(builder, "north", view.NorthLatitude);
        AppendValue(builder, "south", view.SouthLatitude);
        AppendValue(builder, "west", view.WestLongitude);
        AppendValue(builder, "east", view.EastLongitude);
        builder.Append("};");
    }

    private static void AppendValue(StringBuilder builder, string name, object? value)
    {
        builder.Append(name).Append('=');
        AppendValue(builder, value);
        builder.Append(';');
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool boolean:
                builder.Append(boolean ? '1' : '0');
                break;
            case float single:
                builder.Append(single.ToString("R", CultureInfo.InvariantCulture));
                break;
            case double number:
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case int integer:
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case long longInteger:
                builder.Append(longInteger.ToString(CultureInfo.InvariantCulture));
                break;
            case string text:
                builder.Append(text.Length).Append(':').Append(text);
                break;
            case Enum enumValue:
                builder.Append(enumValue.GetType().Name).Append(':').Append(enumValue);
                break;
            default:
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string Hash(string canonical)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
