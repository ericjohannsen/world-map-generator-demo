using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WorldGen.App.Settings;

/// <summary>
/// Reads and writes the two settings files. Pure file I/O with no dispatcher dependency, so it can be
/// exercised from a test harness; <see cref="AppSettingsStore"/> adds the UI debounce.
/// </summary>
public static class SettingsFileStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SettingsLoadResult Load(string root)
    {
        var warnings = new List<string>();

        string preferencesPath = Path.Combine(root, "preferences.json");
        string sessionPath = Path.Combine(root, "session.json");
        string legacyPath = Path.Combine(root, "ui-settings.json");

        AppPreferences? preferences = ReadJson<AppPreferences>(preferencesPath, warnings);
        AppSession? session = ReadJson<AppSession>(sessionPath, warnings);

        if (preferences is null && session is null && File.Exists(legacyPath))
        {
            LegacyUiSettings? legacy = ReadJson<LegacyUiSettings>(legacyPath, warnings);
            if (legacy is not null)
            {
                (preferences, session) = ImportLegacy(legacy);
                warnings.Add("Imported settings from ui-settings.json.");
                Save(root, preferences, session, warnings);
                RetireLegacy(legacyPath, warnings);
            }
        }

        preferences ??= new AppPreferences();
        session ??= new AppSession();

        if (preferences.SchemaVersion > CurrentSchemaVersion)
        {
            warnings.Add("preferences.json was written by a newer version; reading it best-effort.");
        }

        if (session.SchemaVersion > CurrentSchemaVersion)
        {
            warnings.Add("session.json was written by a newer version; reading it best-effort.");
        }

        Normalize(preferences);
        Normalize(session);

        return new SettingsLoadResult(preferences, session, warnings);
    }

    public static void Save(
        string root,
        AppPreferences preferences,
        AppSession session,
        List<string>? warnings = null)
    {
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception exception)
        {
            warnings?.Add($"Could not create the settings directory: {exception.Message}");
            return;
        }

        Normalize(preferences);
        Normalize(session);

        WriteAtomic(Path.Combine(root, "preferences.json"), preferences, warnings);
        WriteAtomic(Path.Combine(root, "session.json"), session, warnings);
    }

    public static void Normalize(AppPreferences preferences)
    {
        preferences.Window ??= new WindowPreferences();
        WindowPreferences window = preferences.Window;

        if (!double.IsFinite(window.Width) || window.Width < 1.0)
        {
            window.Width = 1540;
        }

        if (!double.IsFinite(window.Height) || window.Height < 1.0)
        {
            window.Height = 940;
        }

        window.Width = Math.Clamp(window.Width, 640.0, 6000.0);
        window.Height = Math.Clamp(window.Height, 480.0, 4000.0);

        if (!window.HasSavedPosition)
        {
            window.X = 0;
            window.Y = 0;
        }

        (preferences.DefaultWidth, preferences.DefaultHeight) = SnapResolution(preferences.DefaultWidth);

        preferences.RendererPreference = Math.Clamp(preferences.RendererPreference, 0, 2);

        if (!double.IsFinite(preferences.VerticalExaggeration))
        {
            preferences.VerticalExaggeration = 1.0;
        }

        preferences.VerticalExaggeration = preferences.VerticalExaggeration <= 5.5
            ? 1.0
            : preferences.VerticalExaggeration <= 30.0
                ? 10.0
                : 50.0;

        preferences.ThreeDViewMode = Math.Clamp(preferences.ThreeDViewMode, 0, 1);
        preferences.ThreeDControlMode = Math.Clamp(preferences.ThreeDControlMode, 0, 1);
        preferences.SchemaVersion = CurrentSchemaVersion;
    }

    public static void Normalize(AppSession session)
    {
        session.Generation ??= new GenerationState();
        GenerationState generation = session.Generation;

        generation.PlateCount = Math.Clamp(generation.PlateCount, 4, 60);
        generation.LandFraction = Math.Clamp(Finite(generation.LandFraction, 0.29f), 0.05f, 0.90f);
        generation.OrogenWidthMeters = Math.Max(50_000f, Finite(generation.OrogenWidthMeters, 850_000f));
        generation.Warp1 = Finite(generation.Warp1, 0.45f);
        generation.Warp2 = Finite(generation.Warp2, 0.22f);
        generation.Rigidity = Math.Clamp(Finite(generation.Rigidity, 0.70f), 0f, 1f);

        generation.NorthLatitude = Math.Clamp(Finite(generation.NorthLatitude, 90.0), -90.0, 90.0);
        generation.SouthLatitude = Math.Clamp(Finite(generation.SouthLatitude, -90.0), -90.0, 90.0);
        generation.WestLongitude = Math.Clamp(Finite(generation.WestLongitude, -180.0), -180.0, 180.0);
        generation.EastLongitude = Math.Clamp(Finite(generation.EastLongitude, 180.0), -180.0, 180.0);

        if (generation.NorthLatitude <= generation.SouthLatitude)
        {
            generation.NorthLatitude = 90.0;
            generation.SouthLatitude = -90.0;
        }

        if (generation.EastLongitude <= generation.WestLongitude)
        {
            generation.WestLongitude = -180.0;
            generation.EastLongitude = 180.0;
        }

        generation.ErosionSteps = Math.Clamp(generation.ErosionSteps, 1, 500);
        generation.ErosionK = Math.Clamp(Finite(generation.ErosionK, 0.032f), 0.0005f, 0.5f);
        generation.ErosionDiffusion = Math.Clamp(Finite(generation.ErosionDiffusion, 0.15f), 0f, 2f);
        generation.ErosionCapacity = Math.Clamp(Finite(generation.ErosionCapacity, 0.55f), 0.05f, 5f);
        generation.ErosionDeposition = Math.Clamp(Finite(generation.ErosionDeposition, 0.35f), 0.01f, 1f);
        generation.ErosionUplift = Math.Clamp(Finite(generation.ErosionUplift, 0.28f), 0f, 2f);

        generation.PrecipitationScale = Math.Clamp(Finite(generation.PrecipitationScale, 1f), 0.1f, 5f);
        generation.OrographicLift = Math.Clamp(Finite(generation.OrographicLift, 1.15f), 0f, 5f);
        generation.RainShadow = Math.Clamp(Finite(generation.RainShadow, 1.25f), 0f, 5f);
        generation.LapseRate = Math.Clamp(Finite(generation.LapseRate, 6.5f), 1f, 12f);

        generation.MinRiverDischarge = Math.Clamp(Finite(generation.MinRiverDischarge, 220f), 5f, 50_000f);
        generation.MinHeadwaterCells = Math.Clamp(generation.MinHeadwaterCells, 1, 64);
        generation.MinLakeCells = Math.Clamp(generation.MinLakeCells, 1, 5000);
        generation.MaxLakeDepth = Math.Clamp(Finite(generation.MaxLakeDepth, 65f), 5f, 2000f);

        session.ExportPrefix = string.IsNullOrWhiteSpace(session.ExportPrefix)
            ? "output/world"
            : session.ExportPrefix.Trim();

        if (session.Camera is { IsFinite: false })
        {
            session.Camera = null;
        }

        if (session.GlobeCamera is { IsFinite: false })
        {
            session.GlobeCamera = null;
        }

        session.RegionalFlight = NormalizeFlight(session.RegionalFlight);
        session.GlobeFlight = NormalizeFlight(session.GlobeFlight);

        session.SchemaVersion = CurrentSchemaVersion;
    }

    private static FreeCameraState? NormalizeFlight(FreeCameraState? state)
    {
        if (state is null || !state.IsFinite)
        {
            return null;
        }

        state.Pitch = Math.Clamp(state.Pitch, -1.53f, 1.53f);
        state.SpeedMultiplier = Math.Clamp(state.SpeedMultiplier, 0f, 20f);
        return state;
    }

    private static (int Width, int Height) SnapResolution(int width) => width switch
    {
        <= 1024 => (1024, 512),
        <= 2048 => (2048, 1024),
        <= 4096 => (4096, 2048),
        _ => (8192, 4096),
    };

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static T? ReadJson<T>(string path, List<string> warnings) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            T? value = JsonSerializer.Deserialize<T>(json, Options);
            if (value is null)
            {
                throw new JsonException("The file is empty.");
            }

            return value;
        }
        catch (Exception exception)
        {
            string directory = Path.GetDirectoryName(path) ?? ".";
            string quarantine = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");

            try
            {
                File.Move(path, quarantine, overwrite: true);
            }
            catch
            {
                // Keeping the bad file in place is better than deleting it.
            }

            warnings.Add(
                $"Could not read {Path.GetFileName(path)}: {exception.Message} " +
                $"Defaults were used; the bad file was kept as {Path.GetFileName(quarantine)}.");
            return null;
        }
    }

    private static void WriteAtomic<T>(string path, T value, List<string>? warnings)
    {
        string temporary = path + ".tmp";

        try
        {
            string json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(temporary, json);

            if (File.Exists(path))
            {
                try
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch
                {
                    // The backup is best-effort; the replace below is the important part.
                }

                File.Move(temporary, path, overwrite: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch (Exception exception)
        {
            warnings?.Add($"Could not save {Path.GetFileName(path)}: {exception.Message}");

            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static void RetireLegacy(string legacyPath, List<string> warnings)
    {
        try
        {
            string directory = Path.GetDirectoryName(legacyPath) ?? ".";
            string retired = Path.Combine(
                directory,
                $"ui-settings.imported-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(legacyPath, retired, overwrite: true);
        }
        catch (Exception exception)
        {
            warnings.Add($"Imported ui-settings.json but could not retire it: {exception.Message}");
        }
    }

    private static (AppPreferences Preferences, AppSession Session) ImportLegacy(LegacyUiSettings legacy)
    {
        var preferences = new AppPreferences
        {
            DefaultWidth = legacy.SelectedWidth,
            DefaultHeight = legacy.SelectedHeight,
            RendererPreference = legacy.RendererPreference,
            VerticalExaggeration = legacy.VerticalExaggeration,
            ThreeDViewMode = legacy.ThreeDViewMode,
            Window = new WindowPreferences(),
        };

        var session = new AppSession
        {
            Generation = new GenerationState
            {
                Seed = ParseInt(legacy.Seed, 1337),
                PlateCount = ParseInt(legacy.Plates, 13),
                LandFraction = ParseFloat(legacy.LandFraction, 0.29f),
                OrogenWidthMeters = ParseFloat(legacy.OrogenWidth, 850_000f),
                Warp1 = ParseFloat(legacy.Warp1, 0.45f),
                Warp2 = ParseFloat(legacy.Warp2, 0.22f),
                Rigidity = ParseFloat(legacy.Rigidity, 0.70f),
                WarpEnabled = legacy.WarpEnabled,
                RigidityEnabled = legacy.RigidityEnabled,
                NorthLatitude = ParseDouble(legacy.NorthLat, 90.0),
                SouthLatitude = ParseDouble(legacy.SouthLat, -90.0),
                WestLongitude = ParseDouble(legacy.WestLon, -180.0),
                EastLongitude = ParseDouble(legacy.EastLon, 180.0),
                ErosionSteps = ParseInt(legacy.ErosionSteps, 25),
                ErosionK = ParseFloat(legacy.ErosionK, 0.032f),
                ErosionDiffusion = ParseFloat(legacy.ErosionDiff, 0.15f),
                ErosionCapacity = ParseFloat(legacy.ErosionCap, 0.55f),
                ErosionDeposition = ParseFloat(legacy.ErosionDep, 0.35f),
                ErosionUplift = ParseFloat(legacy.ErosionUplift, 0.28f),
                PrecipitationScale = ParseFloat(legacy.PrecipScale, 1.0f),
                OrographicLift = ParseFloat(legacy.OrographicLift, 1.15f),
                RainShadow = ParseFloat(legacy.RainShadow, 1.25f),
                LapseRate = ParseFloat(legacy.LapseRate, 6.5f),
                MinRiverDischarge = ParseFloat(legacy.MinDischarge, 220f),
                MinHeadwaterCells = ParseInt(legacy.MinTwigCells, 8),
                MinLakeCells = ParseInt(legacy.MinLakeCells, 12),
                MaxLakeDepth = ParseFloat(legacy.MaxLakeDepth, 65f),
            },
            ExportPrefix = string.IsNullOrWhiteSpace(legacy.ExportPrefix)
                ? "output/world"
                : legacy.ExportPrefix.Trim(),
            ThreeDEnabled = legacy.ThreeDEnabled,
            Camera = legacy.Camera,
            GlobeCamera = legacy.GlobeCamera,
        };

        return (preferences, session);
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static float ParseFloat(string? text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    /// <summary>Shape of the pre-split `ui-settings.json`; only used for one-time import.</summary>
    private sealed class LegacyUiSettings
    {
        public int SelectedWidth { get; set; } = 1024;
        public int SelectedHeight { get; set; } = 512;
        public string? Seed { get; set; }
        public string? Plates { get; set; }
        public string? LandFraction { get; set; }
        public string? OrogenWidth { get; set; }
        public string? Warp1 { get; set; }
        public string? Warp2 { get; set; }
        public string? Rigidity { get; set; }
        public bool WarpEnabled { get; set; } = true;
        public bool RigidityEnabled { get; set; } = true;
        public string? NorthLat { get; set; }
        public string? SouthLat { get; set; }
        public string? WestLon { get; set; }
        public string? EastLon { get; set; }
        public string? ErosionSteps { get; set; }
        public string? ErosionK { get; set; }
        public string? ErosionDiff { get; set; }
        public string? ErosionCap { get; set; }
        public string? ErosionDep { get; set; }
        public string? ErosionUplift { get; set; }
        public string? PrecipScale { get; set; }
        public string? OrographicLift { get; set; }
        public string? RainShadow { get; set; }
        public string? LapseRate { get; set; }
        public string? MinDischarge { get; set; }
        public string? MinTwigCells { get; set; }
        public string? MinLakeCells { get; set; }
        public string? MaxLakeDepth { get; set; }
        public string? ExportPrefix { get; set; }
        public bool ThreeDEnabled { get; set; }
        public int RendererPreference { get; set; }
        public double VerticalExaggeration { get; set; } = 1.0;
        public int ThreeDViewMode { get; set; }
        public CameraState? Camera { get; set; }
        public CameraState? GlobeCamera { get; set; }
    }
}
