using System;
using System.Text.Json.Serialization;

namespace WorldGen.App.Settings;

/// <summary>Serialised orbit camera state; shared by the regional and globe cameras.</summary>
public sealed class CameraState
{
    public float TargetX { get; set; }
    public float TargetY { get; set; }
    public float TargetZ { get; set; }
    public float Distance { get; set; } = 1_500_000f;
    public float Yaw { get; set; } = 0.55f;
    public float Pitch { get; set; } = 0.85f;

    [JsonIgnore]
    public bool IsFinite =>
        float.IsFinite(TargetX) &&
        float.IsFinite(TargetY) &&
        float.IsFinite(TargetZ) &&
        float.IsFinite(Distance) &&
        float.IsFinite(Yaw) &&
        float.IsFinite(Pitch) &&
        Distance > 0f;
}

/// <summary>Window geometry in the form the OS gave it to us.</summary>
public sealed class WindowPreferences
{
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Normal (non-maximized) width in device-independent units.</summary>
    public double Width { get; set; } = 1540;

    /// <summary>Normal (non-maximized) height in device-independent units.</summary>
    public double Height { get; set; } = 940;

    /// <summary>True when the last non-minimized state was maximized.</summary>
    public bool Maximized { get; set; }

    /// <summary>False on first run, so (0, 0) is not mistaken for a real placement.</summary>
    public bool HasSavedPosition { get; set; }
}

/// <summary>Durable user choices. See docs/design/settings.md.</summary>
public sealed class AppPreferences
{
    public int SchemaVersion { get; set; } = SettingsFileStore.CurrentSchemaVersion;
    public WindowPreferences Window { get; set; } = new();
    public int DefaultWidth { get; set; } = 1024;
    public int DefaultHeight { get; set; } = 512;
    public int RendererPreference { get; set; }
    public double VerticalExaggeration { get; set; } = 1.0;
    public int ThreeDViewMode { get; set; }
    public int ThreeDControlMode { get; set; }
    public bool ShowGrid3D { get; set; } = true;
}

/// <summary>Free-flight camera state; separate from the orbit camera.</summary>
public sealed class FreeCameraState
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float SpeedMultiplier { get; set; } = 1f;

    [JsonIgnore]
    public bool IsFinite =>
        float.IsFinite(X) &&
        float.IsFinite(Y) &&
        float.IsFinite(Z) &&
        float.IsFinite(Yaw) &&
        float.IsFinite(Pitch) &&
        float.IsFinite(SpeedMultiplier);
}

/// <summary>Last-used Stage 1–4 controls, stored as numbers rather than display strings.</summary>
public sealed class GenerationState
{
    public int Seed { get; set; } = 1337;
    public int PlateCount { get; set; } = 13;
    public float LandFraction { get; set; } = 0.29f;
    public float OrogenWidthMeters { get; set; } = 850_000f;
    public float Warp1 { get; set; } = 0.45f;
    public float Warp2 { get; set; } = 0.22f;
    public float Rigidity { get; set; } = 0.70f;
    public bool WarpEnabled { get; set; } = true;
    public bool RigidityEnabled { get; set; } = true;

    public double NorthLatitude { get; set; } = 90.0;
    public double SouthLatitude { get; set; } = -90.0;
    public double WestLongitude { get; set; } = -180.0;
    public double EastLongitude { get; set; } = 180.0;

    public int ErosionSteps { get; set; } = 25;
    public float ErosionK { get; set; } = 0.032f;
    public float ErosionDiffusion { get; set; } = 0.15f;
    public float ErosionCapacity { get; set; } = 0.55f;
    public float ErosionDeposition { get; set; } = 0.35f;
    public float ErosionUplift { get; set; } = 0.28f;

    public float PrecipitationScale { get; set; } = 1.0f;
    public float OrographicLift { get; set; } = 1.15f;
    public float RainShadow { get; set; } = 1.25f;
    public float LapseRate { get; set; } = 6.5f;

    public float MinRiverDischarge { get; set; } = 220f;
    public int MinHeadwaterCells { get; set; } = 8;
    public int MinLakeCells { get; set; } = 12;
    public float MaxLakeDepth { get; set; } = 65f;

    public string BiomeSetId { get; set; } = "whittaker";
    public bool BiomeEcotoneEnabled { get; set; } = true;
    public int BiomeSmoothingPasses { get; set; } = 1;
}

/// <summary>Resumable working state; safe to delete.</summary>
public sealed class AppSession
{
    public int SchemaVersion { get; set; } = SettingsFileStore.CurrentSchemaVersion;
    public GenerationState Generation { get; set; } = new();
    public string ExportPrefix { get; set; } = "output/world";
    public bool ThreeDEnabled { get; set; }
    public CameraState? Camera { get; set; }
    public CameraState? GlobeCamera { get; set; }
    public FreeCameraState? RegionalFlight { get; set; }
    public FreeCameraState? GlobeFlight { get; set; }
}

public sealed record SettingsLoadResult(
    AppPreferences Preferences,
    AppSession Session,
    IReadOnlyList<string> Warnings);
