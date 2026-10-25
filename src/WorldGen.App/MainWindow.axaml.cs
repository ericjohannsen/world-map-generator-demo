using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WorldGen;
using WorldGen.App.Preview3D;
using WorldGen.App.Settings;

namespace WorldGen.App;

public partial class MainWindow : Window
{
    private enum DisplayMode
    {
        Stage1Base,
        Stage2Eroded,
        Stage2Overlay,
        Stage3Climate,
        Stage4Hydro,
        Stage5Biome,
    }

    private static readonly string[] D8CompassLabels = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private int _selectedWidth = 1024;
    private int _selectedHeight = 512;

    private PlanetTerrain? _currentTerrain;
    private Heightmap? _stage1Map;
    private float _planetRadiusMeters = 6_371_000f;
    private string? _loadedStage1TiffPath;
    private WorldManifest? _loadedStage1Manifest;

    private ErosionResult? _stage2Result;
    private Heightmap? _stage2Map;
    private string? _loadedStage2TiffPath;
    private WorldManifest? _loadedStage2Manifest;

    private ClimateResult? _stage3Result;
    private string? _savedStage3TiffPath;

    private HydrologyResult? _stage4Result;
    private BiomeResult? _stage5Result;
    private DisplayMode _displayMode = DisplayMode.Stage1Base;

    private byte[]? _stage1PreviewPng;
    private byte[]? _stage2PreviewPng;
    private byte[]? _stage2OverlayPng;
    private byte[]? _stage3PreviewPng;
    private byte[]? _stage4PreviewPng;
    private byte[]? _stage5PreviewPng;

    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private Point _panStart;

    private bool _is3DView;
    private bool _enable3DOnOpen;
    private TerrainScene? _scene3D;
    private DisplayMode _scene3DAlbedoMode = DisplayMode.Stage1Base;
    private CameraState? _restoredCamera;
    private CameraState? _restoredGlobeCamera;
    private Terrain3DViewMode _3dMode = Terrain3DViewMode.Regional;
    private Terrain3DControlMode _controlMode = Terrain3DControlMode.Orbit;
    private FreeCameraState? _restoredRegionalFlight;
    private FreeCameraState? _restoredGlobeFlight;
    private double _exaggeration = 1.0;

    private readonly AppSettingsStore _settings;
    private readonly DispatcherTimer _windowCaptureCooldown = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private (double Latitude, double Longitude, float Elevation)? _hudCursor;
    private StageCache? _cache;
    private string? _cacheWarning;
    private string? _stage1Key;
    private string? _stage2Key;
    private string? _stage3Key;
    private string? _stage4Key;
    private string? _stage5Key;
    private Rect _lastNormalWindowBounds;
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private bool _pendingMaximized;
    private bool _suppressSettingsCapture;
    private bool _suppressWindowCapture = true;

    public MainWindow()
    {
        InitializeComponent();

        _settings = new AppSettingsStore();

        try
        {
            _cache = new StageCache();
        }
        catch (Exception exception)
        {
            _cache = null;
            _cacheWarning = $"Stage cache unavailable: {exception.Message}";
        }

        ThreadCountLabel.Text = $"{Environment.ProcessorCount} threads";
        _hudTimer.Tick += (_, _) => UpdateMapHud();

        PopulateBiomeSetCombo();
        ApplyWindowPreferences();
        ApplySessionToControls();

        if (_settings.Warnings.Count > 0)
        {
            DiagnosticsText.Text = string.Join(Environment.NewLine, _settings.Warnings);
        }

        if (_cacheWarning is not null)
        {
            DiagnosticsText.Text = string.IsNullOrEmpty(DiagnosticsText.Text)
                ? _cacheWarning
                : DiagnosticsText.Text + Environment.NewLine + _cacheWarning;
        }

        PositionChanged += (_, _) => ScheduleWindowCapture();
        SizeChanged += (_, _) => ScheduleWindowCapture();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty)
            {
                ScheduleWindowCapture();
            }
        };

        _windowCaptureCooldown.Tick += (_, _) =>
        {
            _windowCaptureCooldown.Stop();
            CaptureWindowState();
        };
        AddHandler(TextBox.TextChangedEvent, OnSettingsTextChanged, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnFlightKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnFlightKeyUp, RoutingStrategies.Tunnel);

        Res1024Button.Click += (_, _) => SelectResolution(1024, 512);
        Res2048Button.Click += (_, _) => SelectResolution(2048, 1024);
        Res4096Button.Click += (_, _) => SelectResolution(4096, 2048);
        Res8192Button.Click += (_, _) => SelectResolution(8192, 4096);

        GenerateButton.Click += async (_, _) => await GenerateStage1Async();
        LoadStage1TiffButton.Click += async (_, _) => await LoadStage1TiffAsync();
        ErodeButton.Click += async (_, _) => await ApplyStage2ErosionAsync();
        LoadStage2TiffButton.Click += async (_, _) => await LoadStage2TiffAsync();
        ClimateButton.Click += async (_, _) => await ApplyStage3ClimateAsync();
        HydroButton.Click += async (_, _) => await ApplyStage4HydrologyAsync();
        BiomeButton.Click += async (_, _) => await ApplyStage5BiomesAsync();

        ExportStage1Button.Click += async (_, _) => await ExportStage1Async();
        ExportStage2Button.Click += async (_, _) => await ExportStage2Async();
        ExportStage3Button.Click += async (_, _) => await ExportStage3Async();
        ExportStage4Button.Click += async (_, _) => await ExportStage4Async();
        ExportStage5Button.Click += async (_, _) => await ExportStage5Async();
        DiagnosticsButton.Click += async (_, _) => await RunDiagnosticsAsync();

        ViewStage1Button.Click += (_, _) => SetDisplayMode(DisplayMode.Stage1Base);
        ViewStage2Button.Click += (_, _) => SetDisplayMode(DisplayMode.Stage2Eroded);
        ViewOverlayButton.Click += (_, _) => SetDisplayMode(DisplayMode.Stage2Overlay);
        ViewStage3Button.Click += (_, _) => SetDisplayMode(DisplayMode.Stage3Climate);
        ViewStage4Button.Click += (_, _) => SetDisplayMode(DisplayMode.Stage4Hydro);
        ViewStage5Button.Click += (_, _) => SetDisplayMode(DisplayMode.Stage5Biome);

        ResetViewButton.Click += async (_, _) =>
        {
            NorthLatBox.Text = "90";
            SouthLatBox.Text = "-90";
            WestLonBox.Text = "-180";
            EastLonBox.Text = "180";
            ResetPanZoom();
            if (_stage1Map is not null)
            {
                await GenerateStage1Async();
            }
            else
            {
                StatusText.Text = "Whole-planet bounds set. Click Generate Stage 1 to create the map.";
            }
        };
        ResetPanZoomButton.Click += (_, _) => ResetPanZoom();

        View3DButton.Click += async (_, _) => await Toggle3DAsync();
        RendererCombo.SelectionChanged += (_, _) => ApplyRendererPreference();
        Regional3DButton.Click += (_, _) => Set3DMode(Terrain3DViewMode.Regional);
        Globe3DButton.Click += (_, _) => Set3DMode(Terrain3DViewMode.Globe);
        Orbit3DButton.Click += (_, _) => SetControlMode(Terrain3DControlMode.Orbit);
        Fly3DButton.Click += (_, _) => SetControlMode(Terrain3DControlMode.Fly);
        Exag1Button.Click += (_, _) => SelectExaggeration(1);
        Exag10Button.Click += (_, _) => SelectExaggeration(10);
        Exag50Button.Click += (_, _) => SelectExaggeration(50);
        GridCheckBox.IsCheckedChanged += (_, _) => ApplyGridToggle();
        BiomeSetCombo.SelectionChanged += (_, _) => OnBiomeControlChanged();
        BiomeEcotoneCheck.IsCheckedChanged += (_, _) => OnBiomeControlChanged();
        Reset3DCameraButton.Click += (_, _) => Terrain3DView.ResetCamera();
        Terrain3DView.RendererChanged += (_, message) => ThreeDStatusText.Text = message;
        Terrain3DView.CursorPicked += (_, pick) => Update3DCursorReadout(pick);
        Terrain3DView.FlightStatusChanged += (_, message) =>
        {
            ThreeDFlightText.Text = message;
            ThreeDFlightText.IsVisible = _is3DView && _controlMode == Terrain3DControlMode.Fly;
        };
        Terrain3DView.CameraSettled += (_, _) =>
        {
            CaptureCameras();
            _settings.RequestSave();
        };

        Closing += (_, _) =>
        {
            _hudTimer.Stop();
            // A minimized window must not overwrite the last maximized/normal geometry.
            CaptureWindowState(force: true);
            CaptureSessionFromControls();
            CaptureCameras();
            _settings.SaveNow();
        };
        Opened += async (_, _) =>
        {
            _suppressWindowCapture = false;

            if (_pendingMaximized)
            {
                WindowState = WindowState.Maximized;
            }

            await GenerateStage1Async(allowGeneration: false);
            if (_enable3DOnOpen)
            {
                _enable3DOnOpen = false;
                if (GetActiveMap() is not null)
                {
                    await Toggle3DAsync();
                }
            }
        };
    }

    private void SelectResolution(int width, int height, bool persist = true)
    {
        // Snap any arbitrary dimensions to the nearest supported 2:1 power-of-2 preset.
        (width, height) = width switch
        {
            <= 1024 => (1024, 512),
            <= 2048 => (2048, 1024),
            <= 4096 => (4096, 2048),
            _ => (8192, 4096),
        };

        _selectedWidth = width;
        _selectedHeight = height;

        SetResolutionButtonClass(Res1024Button, width == 1024);
        SetResolutionButtonClass(Res2048Button, width == 2048);
        SetResolutionButtonClass(Res4096Button, width == 4096);
        SetResolutionButtonClass(Res8192Button, width == 8192);

        if (persist)
        {
            _settings.Preferences.DefaultWidth = width;
            _settings.Preferences.DefaultHeight = height;
            _settings.RequestSave();
        }
    }

    private static void SetResolutionButtonClass(Button btn, bool active)
    {
        if (active)
        {
            if (!btn.Classes.Contains("res-active"))
                btn.Classes.Add("res-active");
        }
        else
        {
            btn.Classes.Remove("res-active");
        }
    }

    private void ApplyWindowPreferences()
    {
        WindowPreferences window = _settings.Preferences.Window;
        Screen? primary = Screens.Primary ?? Screens.All.FirstOrDefault();
        double scale = primary?.Scaling ?? 1.0;

        double maximumWidth = primary is null ? 6000 : Math.Max(640, primary.WorkingArea.Width / scale - 40);
        double maximumHeight = primary is null ? 4000 : Math.Max(480, primary.WorkingArea.Height / scale - 80);

        Width = Math.Clamp(window.Width, 640, Math.Min(6000, maximumWidth));
        Height = Math.Clamp(window.Height, 480, Math.Min(4000, maximumHeight));

        WindowStartupLocation = WindowStartupLocation.Manual;

        if (window.HasSavedPosition && IsWindowPositionVisible(window.X, window.Y, Width, Height))
        {
            Position = new PixelPoint(window.X, window.Y);
        }
        else
        {
            CenterOnPrimaryScreen();
        }

        _lastNormalWindowBounds = new Rect(Position.X, Position.Y, Width, Height);
        _lastNonMinimizedState = window.Maximized ? WindowState.Maximized : WindowState.Normal;
        _pendingMaximized = window.Maximized;
    }

    private bool IsWindowPositionVisible(int x, int y, double width, double height)
    {
        double scale = Screens.Primary?.Scaling ?? 1.0;
        int pixelWidth = Math.Max(1, (int)(width * scale));
        int pixelHeight = Math.Max(1, (int)(height * scale));

        foreach (Screen screen in Screens.All)
        {
            PixelRect area = screen.WorkingArea;
            int left = Math.Max(area.X, x);
            int top = Math.Max(area.Y, y);
            int right = Math.Min(area.Right, x + pixelWidth);
            int bottom = Math.Min(area.Bottom, y + pixelHeight);

            if (right - left >= 120 && bottom - top >= 80)
            {
                return true;
            }
        }

        return false;
    }

    private void CenterOnPrimaryScreen()
    {
        Screen? screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        double scale = screen.Scaling;
        int pixelWidth = Math.Max(1, (int)(Width * scale));
        int pixelHeight = Math.Max(1, (int)(Height * scale));

        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - pixelWidth) / 2),
            area.Y + Math.Max(0, (area.Height - pixelHeight) / 2));
    }

    private void ScheduleWindowCapture()
    {
        if (_suppressWindowCapture)
        {
            return;
        }

        // Window state transitions emit position/size events before WindowState settles; capture after idle.
        _windowCaptureCooldown.Stop();
        _windowCaptureCooldown.Start();
    }

    private bool IsEffectivelyMaximized()
    {
        Screen? screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return false;
        }

        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        double workWidth = screen.WorkingArea.Width / scale;
        double workHeight = screen.WorkingArea.Height / scale;

        return workWidth > 0 &&
               workHeight > 0 &&
               Width >= workWidth * 0.96 &&
               Height >= workHeight * 0.94;
    }

    private void CaptureWindowState(bool force = false)
    {
        if (_suppressWindowCapture && !force)
        {
            return;
        }

        WindowState state = WindowState;

        // ST-9: minimized is transient; keep the last normal/maximized geometry and flag.
        if (state == WindowState.Minimized)
        {
            return;
        }

        if (state == WindowState.Maximized || IsEffectivelyMaximized())
        {
            _lastNonMinimizedState = WindowState.Maximized;
        }
        else if (state == WindowState.Normal)
        {
            _lastNormalWindowBounds = new Rect(Position.X, Position.Y, Width, Height);
            _lastNonMinimizedState = WindowState.Normal;
        }
        else
        {
            return;
        }

        WindowPreferences window = _settings.Preferences.Window;
        window.X = (int)_lastNormalWindowBounds.X;
        window.Y = (int)_lastNormalWindowBounds.Y;
        window.Width = _lastNormalWindowBounds.Width;
        window.Height = _lastNormalWindowBounds.Height;
        window.HasSavedPosition = true;
        window.Maximized = _lastNonMinimizedState == WindowState.Maximized;

        _settings.RequestSave();
    }

    private void ApplySessionToControls()
    {
        _suppressSettingsCapture = true;

        GenerationState generation = _settings.Session.Generation;
        SeedBox.Text = generation.Seed.ToString(CultureInfo.InvariantCulture);
        PlateCountBox.Text = generation.PlateCount.ToString(CultureInfo.InvariantCulture);
        LandFractionBox.Text = generation.LandFraction.ToString("0.###", CultureInfo.InvariantCulture);
        OrogenWidthBox.Text = generation.OrogenWidthMeters.ToString("0", CultureInfo.InvariantCulture);
        Warp1Box.Text = generation.Warp1.ToString("0.###", CultureInfo.InvariantCulture);
        Warp2Box.Text = generation.Warp2.ToString("0.###", CultureInfo.InvariantCulture);
        RigidityBox.Text = generation.Rigidity.ToString("0.###", CultureInfo.InvariantCulture);
        WarpEnabledBox.IsChecked = generation.WarpEnabled;
        RigidityEnabledBox.IsChecked = generation.RigidityEnabled;

        NorthLatBox.Text = generation.NorthLatitude.ToString("0.##", CultureInfo.InvariantCulture);
        SouthLatBox.Text = generation.SouthLatitude.ToString("0.##", CultureInfo.InvariantCulture);
        WestLonBox.Text = generation.WestLongitude.ToString("0.##", CultureInfo.InvariantCulture);
        EastLonBox.Text = generation.EastLongitude.ToString("0.##", CultureInfo.InvariantCulture);

        ErosionStepsBox.Text = generation.ErosionSteps.ToString(CultureInfo.InvariantCulture);
        ErosionKBox.Text = generation.ErosionK.ToString("0.####", CultureInfo.InvariantCulture);
        ErosionDiffBox.Text = generation.ErosionDiffusion.ToString("0.###", CultureInfo.InvariantCulture);
        ErosionCapBox.Text = generation.ErosionCapacity.ToString("0.###", CultureInfo.InvariantCulture);
        ErosionDepBox.Text = generation.ErosionDeposition.ToString("0.###", CultureInfo.InvariantCulture);
        ErosionUpliftBox.Text = generation.ErosionUplift.ToString("0.###", CultureInfo.InvariantCulture);

        PrecipScaleBox.Text = generation.PrecipitationScale.ToString("0.###", CultureInfo.InvariantCulture);
        OrographicLiftBox.Text = generation.OrographicLift.ToString("0.###", CultureInfo.InvariantCulture);
        RainShadowBox.Text = generation.RainShadow.ToString("0.###", CultureInfo.InvariantCulture);
        LapseRateBox.Text = generation.LapseRate.ToString("0.##", CultureInfo.InvariantCulture);

        MinDischargeBox.Text = generation.MinRiverDischarge.ToString("0.#", CultureInfo.InvariantCulture);
        MinTwigCellsBox.Text = generation.MinHeadwaterCells.ToString(CultureInfo.InvariantCulture);
        MinLakeCellsBox.Text = generation.MinLakeCells.ToString(CultureInfo.InvariantCulture);
        MaxLakeDepthBox.Text = generation.MaxLakeDepth.ToString("0.#", CultureInfo.InvariantCulture);

        SelectBiomeSet(generation.BiomeSetId);
        BiomeSmoothBox.Text = generation.BiomeSmoothingPasses.ToString(CultureInfo.InvariantCulture);
        BiomeEcotoneCheck.IsChecked = generation.BiomeEcotoneEnabled;

        ExportPrefixBox.Text = _settings.Session.ExportPrefix;

        RendererCombo.SelectedIndex = Math.Clamp(_settings.Preferences.RendererPreference, 0, 2);
        _3dMode = (Terrain3DViewMode)Math.Clamp(_settings.Preferences.ThreeDViewMode, 0, 1);
        _controlMode = (Terrain3DControlMode)Math.Clamp(_settings.Preferences.ThreeDControlMode, 0, 1);
        GridCheckBox.IsChecked = _settings.Preferences.ShowGrid3D;
        Terrain3DView.ShowGrid = _settings.Preferences.ShowGrid3D;
        Update3DModeButtons();
        UpdateControlModeButtons();
        UpdateThreeDHint();
        SelectExaggeration(_settings.Preferences.VerticalExaggeration);
        SelectResolution(_settings.Preferences.DefaultWidth, _settings.Preferences.DefaultHeight, persist: false);

        _restoredCamera = _settings.Session.Camera;
        _restoredGlobeCamera = _settings.Session.GlobeCamera;
        _restoredRegionalFlight = _settings.Session.RegionalFlight;
        _restoredGlobeFlight = _settings.Session.GlobeFlight;
        _enable3DOnOpen = _settings.Session.ThreeDEnabled;

        _suppressSettingsCapture = false;
    }

    private void CaptureSessionFromControls()
    {
        GenerationState generation = _settings.Session.Generation;

        generation.Seed = ParseInt(SeedBox.Text, 1337);
        generation.PlateCount = Math.Clamp(ParseInt(PlateCountBox.Text, 13), 4, 60);
        generation.LandFraction = Math.Clamp(ParseFloat(LandFractionBox.Text, 0.29f), 0.05f, 0.90f);
        generation.OrogenWidthMeters = Math.Max(50_000f, ParseFloat(OrogenWidthBox.Text, 850_000f));
        generation.Warp1 = ParseFloat(Warp1Box.Text, 0.45f);
        generation.Warp2 = ParseFloat(Warp2Box.Text, 0.22f);
        generation.Rigidity = Math.Clamp(ParseFloat(RigidityBox.Text, 0.70f), 0f, 1f);
        generation.WarpEnabled = WarpEnabledBox.IsChecked ?? true;
        generation.RigidityEnabled = RigidityEnabledBox.IsChecked ?? true;

        generation.NorthLatitude = ParseDouble(NorthLatBox.Text, 90.0);
        generation.SouthLatitude = ParseDouble(SouthLatBox.Text, -90.0);
        generation.WestLongitude = ParseDouble(WestLonBox.Text, -180.0);
        generation.EastLongitude = ParseDouble(EastLonBox.Text, 180.0);

        generation.ErosionSteps = Math.Clamp(ParseInt(ErosionStepsBox.Text, 25), 1, 500);
        generation.ErosionK = Math.Clamp(ParseFloat(ErosionKBox.Text, 0.032f), 0.0005f, 0.5f);
        generation.ErosionDiffusion = Math.Clamp(ParseFloat(ErosionDiffBox.Text, 0.15f), 0f, 2f);
        generation.ErosionCapacity = Math.Clamp(ParseFloat(ErosionCapBox.Text, 0.55f), 0.05f, 5f);
        generation.ErosionDeposition = Math.Clamp(ParseFloat(ErosionDepBox.Text, 0.35f), 0.01f, 1f);
        generation.ErosionUplift = Math.Clamp(ParseFloat(ErosionUpliftBox.Text, 0.28f), 0f, 2f);

        generation.PrecipitationScale = Math.Clamp(ParseFloat(PrecipScaleBox.Text, 1f), 0.1f, 5f);
        generation.OrographicLift = Math.Clamp(ParseFloat(OrographicLiftBox.Text, 1.15f), 0f, 5f);
        generation.RainShadow = Math.Clamp(ParseFloat(RainShadowBox.Text, 1.25f), 0f, 5f);
        generation.LapseRate = Math.Clamp(ParseFloat(LapseRateBox.Text, 6.5f), 1f, 12f);

        generation.MinRiverDischarge = Math.Clamp(ParseFloat(MinDischargeBox.Text, 220f), 5f, 50_000f);
        generation.MinHeadwaterCells = Math.Clamp(ParseInt(MinTwigCellsBox.Text, 8), 1, 64);
        generation.MinLakeCells = Math.Clamp(ParseInt(MinLakeCellsBox.Text, 12), 1, 5000);
        generation.MaxLakeDepth = Math.Clamp(ParseFloat(MaxLakeDepthBox.Text, 65f), 5f, 2000f);

        generation.BiomeSetId = SelectedBiomeSetId();
        generation.BiomeSmoothingPasses = Math.Clamp(ParseInt(BiomeSmoothBox.Text, 1), 0, 8);
        generation.BiomeEcotoneEnabled = BiomeEcotoneCheck.IsChecked ?? true;

        _settings.Session.ExportPrefix = string.IsNullOrWhiteSpace(ExportPrefixBox.Text)
            ? "output/world"
            : ExportPrefixBox.Text.Trim();
        _settings.Session.ThreeDEnabled = _is3DView;
    }

    private void CaptureCameras()
    {
        if (_scene3D is null)
        {
            return;
        }

        _settings.Session.Camera = Terrain3DView.CaptureCamera(Terrain3DViewMode.Regional);
        _settings.Session.GlobeCamera = Terrain3DView.CaptureCamera(Terrain3DViewMode.Globe);
        _settings.Session.RegionalFlight = Terrain3DView.CaptureFlight(Terrain3DViewMode.Regional);
        _settings.Session.GlobeFlight = Terrain3DView.CaptureFlight(Terrain3DViewMode.Globe);
    }

    private void SaveSessionNow()
    {
        CaptureSessionFromControls();
        CaptureCameras();
        _settings.SaveNow();
    }

    private void OnSettingsTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSettingsCapture)
        {
            return;
        }

        if (e.Source is TextBox box && ReferenceEquals(box, DiagnosticsText))
        {
            return;
        }

        CaptureSessionFromControls();
        CaptureCameras();
        _settings.RequestSave();
    }

    private void OnBiomeControlChanged()
    {
        if (_suppressSettingsCapture)
        {
            return;
        }

        CaptureSessionFromControls();
        _settings.RequestSave();
    }

    private void OnFlightKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_is3DView || _controlMode != Terrain3DControlMode.Fly || e.Source is TextBox)
        {
            return;
        }

        if (Terrain3DView.HandleFlightKey(e.Key, isDown: true))
        {
            e.Handled = true;
        }
    }

    private void OnFlightKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_is3DView || _controlMode != Terrain3DControlMode.Fly || e.Source is TextBox)
        {
            return;
        }

        if (Terrain3DView.HandleFlightKey(e.Key, isDown: false))
        {
            e.Handled = true;
        }
    }

    private (TerrainSettings Settings, MapView View, int Width, int Height) ReadStage1Parameters()
    {
        int seed = ParseInt(SeedBox.Text, 1337);
        int plates = Math.Clamp(ParseInt(PlateCountBox.Text, 13), 4, 60);
        float landFraction = Math.Clamp(ParseFloat(LandFractionBox.Text, 0.29f), 0.05f, 0.90f);
        float orogenWidth = Math.Max(50_000f, ParseFloat(OrogenWidthBox.Text, 850_000f));
        float warp1 = ParseFloat(Warp1Box.Text, 0.45f);
        float warp2 = ParseFloat(Warp2Box.Text, 0.22f);
        float rigidity = Math.Clamp(ParseFloat(RigidityBox.Text, 0.70f), 0f, 1f);
        bool warpEnabled = WarpEnabledBox.IsChecked ?? true;
        bool rigidityEnabled = RigidityEnabledBox.IsChecked ?? true;

        int width = _selectedWidth;
        int height = _selectedHeight;

        double north = ParseDouble(NorthLatBox.Text, 90.0);
        double south = ParseDouble(SouthLatBox.Text, -90.0);
        double west = ParseDouble(WestLonBox.Text, -180.0);
        double east = ParseDouble(EastLonBox.Text, 180.0);

        var view = new MapView(north, south, west, east);
        view.Validate();

        var settings = new TerrainSettings
        {
            Seed = seed,
            PlateCount = plates,
            TargetLandFraction = landFraction,
            OrogenWidthMeters = orogenWidth,
            ContinentWarpStrength1 = warp1,
            ContinentWarpStrength2 = warp2,
            ContinentWarpEnabled = warpEnabled,
            WarpRigidityScale = rigidity,
            WarpRigidityEnabled = rigidityEnabled,
        };

        return (settings, view, width, height);
    }

    private ErosionSettings ReadErosionParameters(int fallbackSeed) => new()
    {
        Seed = fallbackSeed,
        Iterations = Math.Clamp(ParseInt(ErosionStepsBox.Text, 25), 1, 500),
        StreamPowerK = Math.Clamp(ParseFloat(ErosionKBox.Text, 0.032f), 0.0005f, 0.5f),
        HillslopeDiffusivity = Math.Clamp(ParseFloat(ErosionDiffBox.Text, 0.15f), 0f, 2.0f),
        SedimentTransportCapacity = Math.Clamp(ParseFloat(ErosionCapBox.Text, 0.55f), 0.05f, 5.0f),
        DepositionRate = Math.Clamp(ParseFloat(ErosionDepBox.Text, 0.35f), 0.01f, 1.0f),
        UpliftScale = Math.Clamp(ParseFloat(ErosionUpliftBox.Text, 0.28f), 0f, 2.0f),
        HaloCells = 16,
    };

    private ClimateSettings ReadClimateParameters() => new()
    {
        PrecipitationScale = Math.Clamp(ParseFloat(PrecipScaleBox.Text, 1.0f), 0.1f, 5.0f),
        OrographicLiftStrength = Math.Clamp(ParseFloat(OrographicLiftBox.Text, 1.15f), 0.0f, 5.0f),
        RainShadowStrength = Math.Clamp(ParseFloat(RainShadowBox.Text, 1.25f), 0.0f, 5.0f),
        LapseRateCPerKm = Math.Clamp(ParseFloat(LapseRateBox.Text, 6.5f), 1.0f, 12.0f),
    };

    private HydrologySettings ReadHydrologyParameters() => new()
    {
        MinRiverDischargeM3s = Math.Clamp(ParseFloat(MinDischargeBox.Text, 220f), 5f, 50_000f),
        MinHeadwaterReachCells = Math.Clamp(ParseInt(MinTwigCellsBox.Text, 8), 1, 64),
        MinLakeCells = Math.Clamp(ParseInt(MinLakeCellsBox.Text, 12), 1, 5000),
        MaxLakeDepthMeters = Math.Clamp(ParseFloat(MaxLakeDepthBox.Text, 65.0f), 5.0f, 2000f),
    };

    private void PopulateBiomeSetCombo()
    {
        BiomeSetCombo.Items.Clear();
        foreach (BiomeSet set in BiomeTable.Default.Sets.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
        {
            BiomeSetCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{set.Name} ({set.Id} v{set.Version})",
                Tag = set.Id,
            });
        }

        if (BiomeSetCombo.Items.Count > 0)
        {
            BiomeSetCombo.SelectedIndex = 0;
        }
    }

    private string SelectedBiomeSetId() =>
        BiomeSetCombo.SelectedItem is ComboBoxItem { Tag: string id } && !string.IsNullOrWhiteSpace(id)
            ? id
            : BiomeTable.Default.DefaultSetId;

    private void SelectBiomeSet(string? id)
    {
        string target = BiomeTable.Default.GetSet(id).Id;
        for (int i = 0; i < BiomeSetCombo.Items.Count; i++)
        {
            if (BiomeSetCombo.Items[i] is ComboBoxItem { Tag: string tag } &&
                string.Equals(tag, target, StringComparison.OrdinalIgnoreCase))
            {
                BiomeSetCombo.SelectedIndex = i;
                return;
            }
        }

        if (BiomeSetCombo.Items.Count > 0 && BiomeSetCombo.SelectedIndex < 0)
        {
            BiomeSetCombo.SelectedIndex = 0;
        }
    }

    private BiomeSettings ReadBiomeParameters()
    {
        string setId = SelectedBiomeSetId();
        BiomeSet set = BiomeTable.Default.GetSet(setId);
        bool ecotone = BiomeEcotoneCheck.IsChecked ?? true;

        return new BiomeSettings
        {
            BiomeSetId = set.Id,
            BiomeSetVersion = set.Version,
            EcotoneOctaves = ecotone ? 3 : 0,
            SmoothingPasses = Math.Clamp(ParseInt(BiomeSmoothBox.Text, 1), 0, 8),
        };
    }

    private int _percentDisplayed = -1;

    private ParallelProgress CreateUiProgress()
    {
        var progress = new ParallelProgress(
            Environment.ProcessorCount,
            fractions =>
            {
                ThreadProgressBar.UpdateFractions(fractions);

                float total = 0f;
                for (int i = 0; i < fractions.Length; i++)
                {
                    total += fractions[i];
                }

                int percent = fractions.Length == 0 ? 0 : (int)MathF.Round(100f * total / fractions.Length);
                if (Interlocked.Exchange(ref _percentDisplayed, percent) != percent)
                {
                    Dispatcher.UIThread.Post(
                        () => ThreadProgressPercent.Text = $"{percent}%",
                        DispatcherPriority.Background);
                }
            },
            minNotifyIntervalMs: 45);
        progress.Reset();
        return progress;
    }

    private static string ShortKey(string key) => key.Length <= 8 ? key : key[..8];

    private async Task TryLoadCachedDownstreamAsync()
    {
        if (_cache is null || _stage1Map is null || _stage1Key is null || _currentTerrain is null)
        {
            return;
        }

        try
        {
            Heightmap stage1 = _stage1Map;
            float radius = _planetRadiusMeters;

            ErosionSettings erosionSettings = ReadErosionParameters(_currentTerrain.Settings.Seed);
            string s2Key = StageKeys.Erosion(_stage1Key, erosionSettings);
            if (!TerrainStageCodec.TryLoadStage2(_cache, s2Key, stage1, out ErosionResult erosion))
            {
                return;
            }

            var (erodedPng, overlayPng) = await Task.Run(() =>
            {
                byte[] rgb = TerrainPreview.Render(erosion.Eroded, radius);
                byte[] png = PngWriter.EncodeRgb(rgb, erosion.Eroded.Width, erosion.Eroded.Height);
                byte[] overlayRgb = TerrainPreview.RenderErosionOverlay(erosion, radius);
                byte[] overlay = PngWriter.EncodeRgb(overlayRgb, erosion.Eroded.Width, erosion.Eroded.Height);
                return (png, overlay);
            });

            _stage2Result = erosion;
            _stage2Map = erosion.Eroded;
            _stage2Key = s2Key;
            _loadedStage2TiffPath = null;
            _loadedStage2Manifest = null;
            _stage2PreviewPng = erodedPng;
            _stage2OverlayPng = overlayPng;
            Stage2SourceText.Text = $"Source: cache {ShortKey(s2Key)} (Stage 2 model v{ModelVersions.Erosion})";

            ClimateSettings climateSettings = ReadClimateParameters();
            string s3Key = StageKeys.Climate(s2Key, climateSettings);
            if (!TerrainStageCodec.TryLoadStage3(_cache, s3Key, erosion.Eroded, out ClimateResult climate))
            {
                return;
            }

            byte[] climatePng = await Task.Run(() =>
            {
                byte[] rgb = TerrainPreview.RenderClimate(climate, radius);
                return PngWriter.EncodeRgb(rgb, climate.Source.Width, climate.Source.Height);
            });

            _stage3Result = climate;
            _stage3Key = s3Key;
            _savedStage3TiffPath = null;
            _stage3PreviewPng = climatePng;
            Stage3SourceText.Text = $"Source: cache {ShortKey(s3Key)} (Stage 3 model v{ModelVersions.Climate})";

            HydrologySettings hydroSettings = ReadHydrologyParameters();
            string s4Key = StageKeys.Hydrology(s3Key, hydroSettings);
            if (!TerrainStageCodec.TryLoadStage4(_cache, s4Key, erosion.Eroded, climate, out HydrologyResult hydro))
            {
                return;
            }

            byte[] hydroPng = await Task.Run(() =>
            {
                byte[] rgb = TerrainPreview.RenderHydrology(hydro, radius);
                return PngWriter.EncodeRgb(
                    rgb,
                    hydro.HydrologicalHeightmap.Width,
                    hydro.HydrologicalHeightmap.Height);
            });

            _stage4Result = hydro;
            _stage4Key = s4Key;
            _stage4PreviewPng = hydroPng;

            BiomeSettings biomeSettings = ReadBiomeParameters();
            string s5Key = StageKeys.Biomes(s4Key, biomeSettings);
            if (!TerrainStageCodec.TryLoadStage5(_cache, s5Key, hydro, out BiomeResult biomes))
            {
                return;
            }

            byte[] biomePng = await Task.Run(() =>
            {
                byte[] rgb = TerrainPreview.RenderBiomes(biomes, radius);
                return PngWriter.EncodeRgb(rgb, biomes.Elevation.Width, biomes.Elevation.Height);
            });

            _stage5Result = biomes;
            _stage5Key = s5Key;
            _stage5PreviewPng = biomePng;
            Stage5SourceText.Text = $"Source: cache {ShortKey(s5Key)} (Stage 5 model v{ModelVersions.Biomes})";
        }
        catch
        {
            // A partial or stale cache chain is not fatal; whatever loaded remains available.
        }
    }

    private async Task GenerateStage1Async(bool allowGeneration = true)
    {
        try
        {
            if (allowGeneration)
            {
                SaveSessionNow();
            }

            SetButtonsEnabled(false);
            StatusText.Text = allowGeneration
                ? "Generating Stage 1 base topology..."
                : "Looking for a cached Stage 1...";
            ParallelProgress progress = CreateUiProgress();

            var (settings, view, w, h) = ReadStage1Parameters();
            string cacheKey = StageKeys.BaseTopology(settings, view, w, h);
            _stage1Key = cacheKey;

            var sw = Stopwatch.StartNew();
            bool cacheHit = false;
            PlanetTerrain terrain;
            Heightmap map;
            byte[] pngBytes;

            Heightmap? cachedMap = null;
            if (_cache is not null)
            {
                try
                {
                    cacheHit = TerrainStageCodec.TryLoadStage1(_cache, cacheKey, out cachedMap);
                }
                catch
                {
                    cacheHit = false;
                }
            }

            if (!cacheHit && !allowGeneration)
            {
                // Startup with no cached Stage 1: nothing ran, so the footer must show 0%, not a completed bar.
                progress.Reset();
                sw.Stop();
                _stage1Key = null;
                _currentTerrain = null;
                _stage1Map = null;
                _stage1PreviewPng = null;
                _loadedStage1TiffPath = null;
                _loadedStage1Manifest = null;
                PreviewImage.Source = null;
                ClearDownstreamFromStage2();
                _hudCursor = null;
                MapHud.IsVisible = false;
                MapSummaryText.Text = "No map loaded.";
                Stage1SourceText.Text = "Source: no Stage 1 artifact loaded";
                Stage2SourceText.Text = "Source: Run or load Stage 2 eroded terrain";
                StatusText.Text =
                    $"No cached Stage 1 for the current settings (key {ShortKey(cacheKey)}). Adjust parameters, then click Generate Stage 1.";
                return;
            }

            if (cacheHit && cachedMap is not null)
            {
                map = cachedMap;
                terrain = new PlanetTerrain(settings);
                pngBytes = await Task.Run(() =>
                {
                    byte[] rgb = TerrainPreview.Render(map, terrain.Settings.PlanetRadiusMeters);
                    return PngWriter.EncodeRgb(rgb, w, h);
                });
                if (allowGeneration)
                {
                    progress.Complete();
                }
                else
                {
                    // A cached load on startup is not work performed; leave the footer at 0%.
                    progress.Reset();
                }
            }
            else
            {
                (terrain, map, pngBytes) = await Task.Run(() =>
                {
                    var t = new PlanetTerrain(settings);
                    var m = HeightmapRenderer.Render(t, view, w, h, progress);
                    byte[] rgb = TerrainPreview.Render(m, t.Settings.PlanetRadiusMeters);
                    byte[] png = PngWriter.EncodeRgb(rgb, w, h);
                    progress.Complete();
                    return (t, m, png);
                });

                if (_cache is not null)
                {
                    try
                    {
                        TerrainStageCodec.SaveStage1(_cache, cacheKey, settings, map);
                    }
                    catch (Exception exception)
                    {
                        DiagnosticsText.Text = $"Stage 1 cache write failed: {exception.Message}";
                    }
                }
            }

            sw.Stop();
            _currentTerrain = terrain;
            _stage1Map = map;
            _planetRadiusMeters = terrain.Settings.PlanetRadiusMeters;
            _loadedStage1TiffPath = null;
            _loadedStage1Manifest = null;
            _stage1PreviewPng = pngBytes;

            ClearDownstreamFromStage2();
            _stage1Key = cacheKey;

            Stage1SourceText.Text = cacheHit
                ? $"Source: cache {ShortKey(cacheKey)} (Stage 1 model v{ModelVersions.BaseTopology})"
                : $"Source: In-memory Stage 1 ({w}×{h}, seed {settings.Seed})";
            Stage2SourceText.Text = "Source: Run or load Stage 2 eroded terrain";

            await TryLoadCachedDownstreamAsync();

            SetDisplayMode(DisplayMode.Stage1Base);
            string suffix = _stage5Result is not null
                ? " Cached downstream stages 1–5 loaded."
                : _stage4Result is not null
                    ? " Cached downstream stages 1–4 loaded."
                    : _stage2Result is not null
                        ? " Cached downstream stages loaded up to the last hit."
                        : string.Empty;
            StatusText.Text = cacheHit
                ? $"Loaded cached Stage 1 ({w}×{h}, key {ShortKey(cacheKey)}) in {sw.Elapsed.TotalSeconds:F2}s.{suffix}"
                : $"Generated Stage 1 ({w}×{h}) in {sw.Elapsed.TotalSeconds:F2}s.{suffix}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stage 1 error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task LoadStage1TiffAsync()
    {
        try
        {
            string? localPath = await PickGeoTiffFileAsync("Load Stage 1 (.s1-base) float32 GeoTIFF (read-only)");
            if (string.IsNullOrWhiteSpace(localPath))
                return;

            SetButtonsEnabled(false);
            StatusText.Text = $"Loading {Path.GetFileName(localPath)} (read-only)...";

            var (loaded, terrain, pngBytes) = await Task.Run(() =>
            {
                LoadedElevationMap l = GeoTiffReader.ReadFloat32(localPath);
                int seed = l.SourceManifest?.Seed ?? ParseInt(SeedBox.Text, 1337);
                var t = new PlanetTerrain(new TerrainSettings
                {
                    Seed = seed,
                    PlanetRadiusMeters = l.PlanetRadiusMeters,
                });
                byte[] rgb = TerrainPreview.Render(l.Map, l.PlanetRadiusMeters);
                byte[] png = PngWriter.EncodeRgb(rgb, l.Map.Width, l.Map.Height);
                return (l, t, png);
            });

            _stage1Map = loaded.Map;
            _planetRadiusMeters = loaded.PlanetRadiusMeters;
            _loadedStage1TiffPath = loaded.SourceGeoTiffPath;
            _loadedStage1Manifest = loaded.SourceManifest;
            _currentTerrain = terrain;
            _stage1PreviewPng = pngBytes;

            ClearDownstreamFromStage2();
            _stage1Key = null;

            ApplyLoadedMapMetadataToBoxes(loaded);
            Stage1SourceText.Text = $"Source TIFF: {Path.GetFileName(loaded.SourceGeoTiffPath)} (immutable)";
            Stage2SourceText.Text = "Source: Run or load Stage 2 eroded terrain";

            ResetPanZoom();
            SetDisplayMode(DisplayMode.Stage1Base);
            StatusText.Text = $"Loaded Stage 1 TIFF: {Path.GetFileName(loaded.SourceGeoTiffPath)} ({loaded.Map.Width}×{loaded.Map.Height}).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load Stage 1 TIFF error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ApplyStage2ErosionAsync()
    {
        try
        {
            SaveSessionNow();
            if (_stage1Map is null)
            {
                StatusText.Text = "Generate or load Stage 1 before running erosion.";
                return;
            }

            SetButtonsEnabled(false);
            Heightmap sourceMap = _stage1Map;
            PlanetTerrain? terrain = _currentTerrain;
            float radius = _planetRadiusMeters;
            int seed = terrain?.Settings.Seed ?? _loadedStage1Manifest?.Seed ?? ParseInt(SeedBox.Text, 1337);
            ErosionSettings erosionSettings = ReadErosionParameters(seed);

            string? s2Key = _cache is not null && _stage1Key is not null
                ? StageKeys.Erosion(_stage1Key, erosionSettings)
                : null;

            if (_cache is not null && s2Key is not null)
            {
                try
                {
                    if (TerrainStageCodec.TryLoadStage2(_cache, s2Key, sourceMap, out ErosionResult cached))
                    {
                        var (cachedErodedPng, cachedOverlayPng) = await Task.Run(() =>
                        {
                            byte[] rgbEroded = TerrainPreview.Render(cached.Eroded, radius);
                            byte[] pngEroded = PngWriter.EncodeRgb(rgbEroded, cached.Eroded.Width, cached.Eroded.Height);
                            byte[] rgbOverlay = TerrainPreview.RenderErosionOverlay(cached, radius);
                            byte[] pngOverlay = PngWriter.EncodeRgb(rgbOverlay, cached.Eroded.Width, cached.Eroded.Height);
                            return (pngEroded, pngOverlay);
                        });

                        _stage2Result = cached;
                        _stage2Map = cached.Eroded;
                        _stage2Key = s2Key;
                        _loadedStage2TiffPath = null;
                        _loadedStage2Manifest = null;
                        _stage2PreviewPng = cachedErodedPng;
                        _stage2OverlayPng = cachedOverlayPng;
                        _stage3Result = null;
                        _stage3Key = null;
                        _savedStage3TiffPath = null;
                        _stage3PreviewPng = null;
                        _stage4Result = null;
                        _stage4Key = null;
                        _stage4PreviewPng = null;
                        _stage5Result = null;
                        _stage5Key = null;
                        _stage5PreviewPng = null;
                        Stage2SourceText.Text = $"Source: cache {ShortKey(s2Key)} (Stage 2 model v{ModelVersions.Erosion})";
                        SetDisplayMode(DisplayMode.Stage2Eroded);
                        StatusText.Text = $"Loaded cached Stage 2 ({cached.Eroded.Width}×{cached.Eroded.Height}, key {ShortKey(s2Key)}).";
                        return;
                    }
                }
                catch
                {
                    // Treat a corrupt cache entry as a miss and regenerate.
                }
            }

            StatusText.Text = $"Running Stage 2 erosion ({erosionSettings.Iterations} iterations)...";
            ParallelProgress progress = CreateUiProgress();

            var (result, erodedPng, overlayPng) = await Task.Run(() =>
            {
                ErosionResult er = TerrainErosion.Erode(sourceMap, erosionSettings, terrain, radius, progress);
                byte[] rgbEroded = TerrainPreview.Render(er.Eroded, radius);
                byte[] pngEroded = PngWriter.EncodeRgb(rgbEroded, er.Eroded.Width, er.Eroded.Height);

                byte[] rgbOverlay = TerrainPreview.RenderErosionOverlay(er, radius);
                byte[] pngOverlay = PngWriter.EncodeRgb(rgbOverlay, er.Eroded.Width, er.Eroded.Height);
                progress.Complete();
                return (er, pngEroded, pngOverlay);
            });

            _stage2Result = result;
            _stage2Map = result.Eroded;
            _stage2Key = s2Key;
            _loadedStage2TiffPath = null;
            _loadedStage2Manifest = null;
            _stage2PreviewPng = erodedPng;
            _stage2OverlayPng = overlayPng;

            _stage3Result = null;
            _stage3Key = null;
            _savedStage3TiffPath = null;
            _stage3PreviewPng = null;
            _stage4Result = null;
            _stage4Key = null;
            _stage4PreviewPng = null;
            _stage5Result = null;
            _stage5Key = null;
            _stage5PreviewPng = null;

            if (_cache is not null && s2Key is not null && _stage1Key is not null)
            {
                try
                {
                    TerrainStageCodec.SaveStage2(_cache, s2Key, _stage1Key, result);
                }
                catch (Exception exception)
                {
                    DiagnosticsText.Text = $"Stage 2 cache write failed: {exception.Message}";
                }
            }

            Stage2SourceText.Text = $"Source: In-memory Stage 2 ({result.Eroded.Width}×{result.Eroded.Height})";
            SetDisplayMode(DisplayMode.Stage2Eroded);
            StatusText.Text =
                $"Stage 2 complete ({result.ElapsedSeconds:F2}s) | Mean incision {result.MeanErosionMeters:F1} m (max {result.MaxErosionMeters:F0} m), mean deposition {result.MeanDepositionMeters:F1} m (max {result.MaxDepositionMeters:F0} m).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erosion error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task LoadStage2TiffAsync()
    {
        try
        {
            string? localPath = await PickGeoTiffFileAsync("Load Stage 2 (.s2-eroded) float32 GeoTIFF (read-only)");
            if (string.IsNullOrWhiteSpace(localPath))
                return;

            SetButtonsEnabled(false);
            StatusText.Text = $"Loading {Path.GetFileName(localPath)} (read-only)...";

            var (loaded, terrain, pngBytes) = await Task.Run(() =>
            {
                LoadedElevationMap l = GeoTiffReader.ReadFloat32(localPath);
                int seed = l.SourceManifest?.Seed ?? ParseInt(SeedBox.Text, 1337);
                var t = new PlanetTerrain(new TerrainSettings
                {
                    Seed = seed,
                    PlanetRadiusMeters = l.PlanetRadiusMeters,
                });
                byte[] rgb = TerrainPreview.Render(l.Map, l.PlanetRadiusMeters);
                byte[] png = PngWriter.EncodeRgb(rgb, l.Map.Width, l.Map.Height);
                return (l, t, png);
            });

            _stage2Result = null;
            _stage2Map = loaded.Map;
            _stage1Key = null;
            _stage2Key = null;
            _stage3Key = null;
            _stage4Key = null;
            _planetRadiusMeters = loaded.PlanetRadiusMeters;
            _loadedStage2TiffPath = loaded.SourceGeoTiffPath;
            _loadedStage2Manifest = loaded.SourceManifest;
            _currentTerrain = terrain;
            _stage2PreviewPng = pngBytes;
            _stage2OverlayPng = null;

            _stage3Result = null;
            _savedStage3TiffPath = null;
            _stage3PreviewPng = null;
            _stage4Result = null;
            _stage4PreviewPng = null;
            _stage5Result = null;
            _stage5PreviewPng = null;

            ApplyLoadedMapMetadataToBoxes(loaded);
            Stage2SourceText.Text = $"Source TIFF: {Path.GetFileName(loaded.SourceGeoTiffPath)} (immutable)";

            ResetPanZoom();
            SetDisplayMode(DisplayMode.Stage2Eroded);
            StatusText.Text = $"Loaded Stage 2 TIFF: {Path.GetFileName(loaded.SourceGeoTiffPath)} ({loaded.Map.Width}×{loaded.Map.Height}).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load Stage 2 TIFF error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ApplyStage3ClimateAsync()
    {
        try
        {
            SaveSessionNow();
            if (_stage2Map is null)
            {
                StatusText.Text = "Run or load Stage 2 before running climate.";
                return;
            }

            SetButtonsEnabled(false);
            Heightmap sourceErodedMap = _stage2Map;
            float radius = _planetRadiusMeters;
            ClimateSettings climateSettings = ReadClimateParameters();

            string? s3Key = _cache is not null && _stage2Key is not null
                ? StageKeys.Climate(_stage2Key, climateSettings)
                : null;

            if (_cache is not null && s3Key is not null)
            {
                try
                {
                    if (TerrainStageCodec.TryLoadStage3(_cache, s3Key, sourceErodedMap, out ClimateResult cached))
                    {
                        byte[] cachedPng = await Task.Run(() =>
                        {
                            byte[] rgb = TerrainPreview.RenderClimate(cached, radius);
                            return PngWriter.EncodeRgb(rgb, cached.Source.Width, cached.Source.Height);
                        });

                        _stage3Result = cached;
                        _stage3Key = s3Key;
                        _savedStage3TiffPath = null;
                        _stage3PreviewPng = cachedPng;
                        _stage4Result = null;
                        _stage4Key = null;
                        _stage4PreviewPng = null;
                        _stage5Result = null;
                        _stage5Key = null;
                        _stage5PreviewPng = null;
                        Stage3SourceText.Text = $"Source: cache {ShortKey(s3Key)} (Stage 3 model v{ModelVersions.Climate})";
                        SetDisplayMode(DisplayMode.Stage3Climate);
                        StatusText.Text = $"Loaded cached Stage 3 (mean {cached.MeanLandPrecipitationMm:F0} mm/yr, key {ShortKey(s3Key)}).";
                        return;
                    }
                }
                catch
                {
                    // Treat a corrupt cache entry as a miss and regenerate.
                }
            }

            StatusText.Text = "Running Stage 3 climate & orographic rainfall...";
            ParallelProgress progress = CreateUiProgress();

            var (result, climatePng) = await Task.Run(() =>
            {
                ClimateResult cr = TerrainClimate.Analyze(sourceErodedMap, climateSettings, radius, progress);
                byte[] rgbClimate = TerrainPreview.RenderClimate(cr, radius);
                byte[] pngClimate = PngWriter.EncodeRgb(rgbClimate, cr.Source.Width, cr.Source.Height);
                progress.Complete();
                return (cr, pngClimate);
            });

            _stage3Result = result;
            _stage3Key = s3Key;
            _stage3PreviewPng = climatePng;
            _stage4Result = null;
            _stage4Key = null;
            _stage4PreviewPng = null;
            _stage5Result = null;
            _stage5Key = null;
            _stage5PreviewPng = null;

            if (_cache is not null && s3Key is not null && _stage2Key is not null)
            {
                try
                {
                    TerrainStageCodec.SaveStage3(_cache, s3Key, _stage2Key, result);
                }
                catch (Exception exception)
                {
                    DiagnosticsText.Text = $"Stage 3 cache write failed: {exception.Message}";
                }
            }

            Stage3SourceText.Text =
                $"Stage 3 ready: mean {result.MeanLandPrecipitationMm:F0} mm/yr, arid {result.AridLandFraction:P0}, humid {result.HumidLandFraction:P0}";
            SetDisplayMode(DisplayMode.Stage3Climate);
            StatusText.Text =
                $"Stage 3 complete ({result.ElapsedSeconds:F2}s) | Mean land temp {result.MeanLandTemperatureCelsius:F1} °C, rainfall {result.MeanLandPrecipitationMm:F0} mm/yr (max {result.MaxLandPrecipitationMm:F0} mm/yr).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Climate error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ApplyStage4HydrologyAsync()
    {
        try
        {
            SaveSessionNow();
            if (_stage2Map is null)
            {
                StatusText.Text = "Run or load Stage 2 before running hydrology.";
                return;
            }

            if (_stage3Result is null)
            {
                StatusText.Text = "Run or load Stage 3 before running hydrology.";
                return;
            }

            SetButtonsEnabled(false);
            Heightmap sourceErodedMap = _stage2Map;
            ClimateResult climate = _stage3Result;
            float radius = _planetRadiusMeters;
            HydrologySettings hydroSettings = ReadHydrologyParameters();

            string? s4Key = _cache is not null && _stage3Key is not null
                ? StageKeys.Hydrology(_stage3Key, hydroSettings)
                : null;

            if (_cache is not null && s4Key is not null)
            {
                try
                {
                    if (TerrainStageCodec.TryLoadStage4(_cache, s4Key, sourceErodedMap, climate, out HydrologyResult cached))
                    {
                        byte[] cachedPng = await Task.Run(() =>
                        {
                            byte[] rgb = TerrainPreview.RenderHydrology(cached, radius);
                            return PngWriter.EncodeRgb(
                                rgb,
                                cached.HydrologicalHeightmap.Width,
                                cached.HydrologicalHeightmap.Height);
                        });

                        _stage4Result = cached;
                        _stage4Key = s4Key;
                        _stage4PreviewPng = cachedPng;
                        _stage5Result = null;
                        _stage5Key = null;
                        _stage5PreviewPng = null;
                        SetDisplayMode(DisplayMode.Stage4Hydro);
                        StatusText.Text =
                            $"Loaded cached Stage 4 ({cached.Lakes.Count} lakes, {cached.Rivers.Count} river reaches, key {ShortKey(s4Key)}).";
                        return;
                    }
                }
                catch
                {
                    // Treat a corrupt cache entry as a miss and regenerate.
                }
            }

            StatusText.Text = "Running Stage 4 hydrology (rainfall-driven discharge, lakes, twig pruning, rivers)...";
            ParallelProgress progress = CreateUiProgress();

            var (result, hydroPng) = await Task.Run(() =>
            {
                HydrologyResult hr = TerrainHydrology.Analyze(sourceErodedMap, hydroSettings, radius, climate, progress);
                byte[] rgbHydro = TerrainPreview.RenderHydrology(hr, radius);
                byte[] pngHydro = PngWriter.EncodeRgb(rgbHydro, hr.HydrologicalHeightmap.Width, hr.HydrologicalHeightmap.Height);
                progress.Complete();
                return (hr, pngHydro);
            });

            _stage4Result = result;
            _stage4Key = s4Key;
            _stage4PreviewPng = hydroPng;
            _stage5Result = null;
            _stage5Key = null;
            _stage5PreviewPng = null;

            if (_cache is not null && s4Key is not null && _stage3Key is not null)
            {
                try
                {
                    TerrainStageCodec.SaveStage4(_cache, s4Key, _stage3Key, result);
                }
                catch (Exception exception)
                {
                    DiagnosticsText.Text = $"Stage 4 cache write failed: {exception.Message}";
                }
            }

            SetDisplayMode(DisplayMode.Stage4Hydro);
            StatusText.Text =
                $"Stage 4 complete ({result.ElapsedSeconds:F2}s) | {result.Lakes.Count} lakes ({result.TotalLakeAreaSqKm:N0} km²), {result.Rivers.Count} river reaches ({result.TotalRiverLengthKm:N0} km, max order {result.MaxStrahlerOrder}).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Hydrology error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ApplyStage5BiomesAsync()
    {
        try
        {
            SaveSessionNow();
            if (_stage4Result is null)
            {
                StatusText.Text = "Run or load Stage 4 before running biomes.";
                return;
            }

            SetButtonsEnabled(false);
            HydrologyResult hydro = _stage4Result;
            float radius = _planetRadiusMeters;
            int seed = _currentTerrain?.Settings.Seed ?? ParseInt(SeedBox.Text, 1337);
            BiomeSettings biomeSettings = ReadBiomeParameters();

            string? s5Key = _stage4Key is not null
                ? StageKeys.Biomes(_stage4Key, biomeSettings)
                : null;

            if (_cache is not null && s5Key is not null)
            {
                try
                {
                    if (TerrainStageCodec.TryLoadStage5(_cache, s5Key, hydro, out BiomeResult cached))
                    {
                        byte[] cachedPng = await Task.Run(() =>
                        {
                            byte[] rgb = TerrainPreview.RenderBiomes(cached, radius);
                            return PngWriter.EncodeRgb(rgb, cached.Elevation.Width, cached.Elevation.Height);
                        });

                        _stage5Result = cached;
                        _stage5Key = s5Key;
                        _stage5PreviewPng = cachedPng;
                        Stage5SourceText.Text = $"Source: cache {ShortKey(s5Key)} (Stage 5 model v{ModelVersions.Biomes})";
                        SetDisplayMode(DisplayMode.Stage5Biome);
                        StatusText.Text =
                            $"Loaded cached Stage 5 ({cached.Set.Id} v{cached.Set.Version}, {cached.UnclassifiedLandCells} unclassified land cells, key {ShortKey(s5Key)}).";
                        return;
                    }
                }
                catch
                {
                    // Treat a corrupt cache entry as a miss and regenerate.
                }
            }

            StatusText.Text = "Running Stage 5 biome classification (moisture indices, ecotones, smoothing)...";
            ParallelProgress progress = CreateUiProgress();

            var (result, biomePng) = await Task.Run(() =>
            {
                BiomeResult br = TerrainBiomes.Analyze(hydro, biomeSettings, seed, radius, progress);
                byte[] rgbBiome = TerrainPreview.RenderBiomes(br, radius);
                byte[] pngBiome = PngWriter.EncodeRgb(rgbBiome, br.Elevation.Width, br.Elevation.Height);
                progress.Complete();
                return (br, pngBiome);
            });

            _stage5Result = result;
            _stage5Key = s5Key;
            _stage5PreviewPng = biomePng;

            if (_cache is not null && s5Key is not null && _stage4Key is not null)
            {
                try
                {
                    TerrainStageCodec.SaveStage5(_cache, s5Key, _stage4Key, result);
                }
                catch (Exception exception)
                {
                    DiagnosticsText.Text = $"Stage 5 cache write failed: {exception.Message}";
                }
            }

            Stage5SourceText.Text =
                $"Stage 5 ready: {result.Set.Name} ({result.Set.Id} v{result.Set.Version}), {result.Set.Biomes.Count} land biomes";
            SetDisplayMode(DisplayMode.Stage5Biome);
            StatusText.Text =
                $"Stage 5 complete ({result.ElapsedSeconds:F2}s) | land {result.LandFraction:P1} of surface, {result.UnclassifiedLandCells} unclassified land cells.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Biome error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ExportStage5Async()
    {
        try
        {
            SaveSessionNow();
            if (_stage5Result is null)
            {
                StatusText.Text = "Run Stage 5 before exporting it.";
                return;
            }

            SetButtonsEnabled(false);
            string prefix = string.IsNullOrWhiteSpace(ExportPrefixBox.Text) ? "output/world" : ExportPrefixBox.Text.Trim();
            StatusText.Text = "Saving Stage 5 (.s5-biome) artifacts...";

            BiomeResult biomes = _stage5Result;
            float radius = _planetRadiusMeters;
            PlanetTerrain? terrain = _currentTerrain;
            string? srcTiff = _savedStage3TiffPath ?? _loadedStage2TiffPath ?? _loadedStage1TiffPath;
            WorldManifest? srcManifest = _loadedStage2Manifest ?? _loadedStage1Manifest;
            ErosionResult? erosion = _stage2Result;

            StageArtifactSet paths = await Task.Run(() =>
                ArtifactExporter.WriteStage5Biomes(prefix, biomes, radius, terrain, srcTiff, srcManifest, erosion));

            StatusText.Text =
                $"Saved Stage 5: {Path.GetFileName(paths.HeightGeoTiffPath)} + {paths.StageTag}-ids.tif + {paths.StageTag}-palette.json (source untouched).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stage 5 export error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ExportStage1Async()
    {
        try
        {
            SaveSessionNow();
            if (_stage1Map is null || _currentTerrain is null)
            {
                StatusText.Text = "Generate or load Stage 1 before exporting.";
                return;
            }

            SetButtonsEnabled(false);
            string prefix = string.IsNullOrWhiteSpace(ExportPrefixBox.Text) ? "output/world" : ExportPrefixBox.Text.Trim();
            StatusText.Text = "Saving Stage 1 (.s1-base) artifacts...";

            Heightmap map = _stage1Map;
            PlanetTerrain terrain = _currentTerrain;
            StageArtifactSet paths = await Task.Run(() => ArtifactExporter.WriteStage1(prefix, map, terrain));

            _loadedStage1TiffPath = paths.HeightGeoTiffPath;
            Stage1SourceText.Text = $"Source TIFF: {Path.GetFileName(paths.HeightGeoTiffPath)} (immutable)";
            StatusText.Text = $"Saved Stage 1: {Path.GetFileName(paths.HeightGeoTiffPath)} (+ PNG, preview, manifest).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stage 1 export error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ExportStage2Async()
    {
        try
        {
            SaveSessionNow();
            if (_stage2Result is null)
            {
                StatusText.Text = "Run Stage 2 before exporting it.";
                return;
            }

            SetButtonsEnabled(false);
            string prefix = string.IsNullOrWhiteSpace(ExportPrefixBox.Text) ? "output/world" : ExportPrefixBox.Text.Trim();
            StatusText.Text = "Saving Stage 2 (.s2-eroded) artifacts...";

            ErosionResult erosion = _stage2Result;
            float radius = _planetRadiusMeters;
            PlanetTerrain? terrain = _currentTerrain;
            string? srcTiff = _loadedStage1TiffPath;
            WorldManifest? srcManifest = _loadedStage1Manifest;

            StageArtifactSet paths = await Task.Run(() =>
                ArtifactExporter.WriteStage2(prefix, erosion, radius, terrain, srcTiff, srcManifest));

            _loadedStage2TiffPath = paths.HeightGeoTiffPath;
            Stage2SourceText.Text = $"Source TIFF: {Path.GetFileName(paths.HeightGeoTiffPath)} (immutable)";
            StatusText.Text = $"Saved Stage 2: {Path.GetFileName(paths.HeightGeoTiffPath)} (source Stage 1 untouched).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stage 2 export error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ExportStage3Async()
    {
        try
        {
            SaveSessionNow();
            if (_stage3Result is null)
            {
                StatusText.Text = "Run Stage 3 before exporting it.";
                return;
            }

            SetButtonsEnabled(false);
            string prefix = string.IsNullOrWhiteSpace(ExportPrefixBox.Text) ? "output/world" : ExportPrefixBox.Text.Trim();
            StatusText.Text = "Saving Stage 3 (.s3-climate) artifacts...";

            ClimateResult climate = _stage3Result;
            float radius = _planetRadiusMeters;
            PlanetTerrain? terrain = _currentTerrain;
            string? srcTiff = _loadedStage2TiffPath ?? _loadedStage1TiffPath;
            WorldManifest? srcManifest = _loadedStage2Manifest ?? _loadedStage1Manifest;
            ErosionResult? erosion = _stage2Result;

            StageArtifactSet paths = await Task.Run(() =>
                ArtifactExporter.WriteStage3Climate(prefix, climate, radius, terrain, srcTiff, srcManifest, erosion));

            _savedStage3TiffPath = paths.HeightGeoTiffPath;
            StatusText.Text =
                $"Saved Stage 3: {Path.GetFileName(paths.HeightGeoTiffPath)} + {paths.StageTag}-rainfall.tif (source untouched).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stage 3 export error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ExportStage4Async()
    {
        try
        {
            SaveSessionNow();
            if (_stage4Result is null)
            {
                StatusText.Text = "Run Stage 4 before exporting it.";
                return;
            }

            SetButtonsEnabled(false);
            string prefix = string.IsNullOrWhiteSpace(ExportPrefixBox.Text) ? "output/world" : ExportPrefixBox.Text.Trim();
            StatusText.Text = "Saving Stage 4 (.s4-hydro) artifacts...";

            HydrologyResult hydro = _stage4Result;
            float radius = _planetRadiusMeters;
            PlanetTerrain? terrain = _currentTerrain;
            string? srcTiff = _savedStage3TiffPath ?? _loadedStage2TiffPath ?? _loadedStage1TiffPath;
            WorldManifest? srcManifest = _loadedStage2Manifest ?? _loadedStage1Manifest;
            ErosionResult? erosion = _stage2Result;

            StageArtifactSet paths = await Task.Run(() =>
                ArtifactExporter.WriteStage4Hydrology(prefix, hydro, radius, terrain, srcTiff, srcManifest, erosion));

            StatusText.Text =
                $"Saved Stage 4: {Path.GetFileName(paths.HeightGeoTiffPath)} + {paths.StageTag}-vectors.json (source untouched).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stage 4 export error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        try
        {
            SetButtonsEnabled(false);
            StatusText.Text = "Running diagnostics...";

            if (_stage1Map is null && _stage2Map is null)
            {
                StatusText.Text = "Generate or load terrain before running diagnostics.";
                return;
            }

            Heightmap? activeMap = GetActiveMap();
            if (activeMap is null || _currentTerrain is null)
                return;

            PlanetTerrain terrain = _currentTerrain;
            ErosionResult? erosion = _stage2Result;
            ClimateResult? climate = _stage3Result;
            HydrologyResult? hydro = _stage4Result;

            float metersPerPixel = (float)(_planetRadiusMeters * MathUtils.ToRadians(activeMap.View.LongitudeSpan) / activeMap.Width);
            string report = await Task.Run(() => TerrainDiagnostics.Format(terrain, activeMap, metersPerPixel, erosion, climate, hydro));
            DiagnosticsText.Text = report;
            StatusText.Text = "Diagnostics complete.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Diagnostics error: {ex.Message}";
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void ClearDownstreamFromStage2()
    {
        _stage2Key = null;
        _stage3Key = null;
        _stage4Key = null;
        _stage5Key = null;

        _stage2Result = null;
        _stage2Map = null;
        _loadedStage2TiffPath = null;
        _loadedStage2Manifest = null;
        _stage2PreviewPng = null;
        _stage2OverlayPng = null;

        _stage3Result = null;
        _savedStage3TiffPath = null;
        _stage3PreviewPng = null;

        _stage4Result = null;
        _stage4PreviewPng = null;

        _stage5Result = null;
        _stage5PreviewPng = null;
    }

    private async Task<string?> PickGeoTiffFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GeoTIFF Elevation Files")
                {
                    Patterns = ["*.tif", "*.tiff"],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private void ApplyLoadedMapMetadataToBoxes(LoadedElevationMap loaded)
    {
        if (loaded.SourceManifest is not null)
        {
            SeedBox.Text = loaded.SourceManifest.Seed.ToString(CultureInfo.InvariantCulture);
        }

        SelectResolution(loaded.Map.Width, loaded.Map.Height, persist: true);
        NorthLatBox.Text = loaded.Map.View.NorthLatitude.ToString("F2", CultureInfo.InvariantCulture);
        SouthLatBox.Text = loaded.Map.View.SouthLatitude.ToString("F2", CultureInfo.InvariantCulture);
        WestLonBox.Text = loaded.Map.View.WestLongitude.ToString("F2", CultureInfo.InvariantCulture);
        EastLonBox.Text = loaded.Map.View.EastLongitude.ToString("F2", CultureInfo.InvariantCulture);

        ExportPrefixBox.Text = StageArtifactPaths.NormalizePrefix(loaded.SourceGeoTiffPath);
        SaveSessionNow();
    }

    private async Task Toggle3DAsync()
    {
        if (_is3DView)
        {
            Set3DView(false);
            return;
        }

        Heightmap? map = GetActiveMap();
        if (map is null)
        {
            StatusText.Text = "Generate or load terrain before opening the 3D view.";
            return;
        }

        await Ensure3DSceneAsync(map);
        if (_scene3D is null)
        {
            return;
        }

        Set3DView(true);
        Terrain3DView.RequestFrame();
    }

    private void Set3DView(bool enabled)
    {
        _is3DView = enabled;
        PreviewImage.IsVisible = !enabled;
        Terrain3DView.IsActive = enabled;
        ThreeDPanel.IsVisible = enabled;
        ThreeDFlightText.IsVisible = enabled && _controlMode == Terrain3DControlMode.Fly;

        if (enabled)
        {
            if (!View3DButton.Classes.Contains("res-active"))
            {
                View3DButton.Classes.Add("res-active");
            }

            _hudTimer.Start();
            Terrain3DView.RequestFrame();
        }
        else
        {
            _hudTimer.Stop();
            View3DButton.Classes.Remove("res-active");
        }

        UpdateMapHud();

        if (!_suppressSettingsCapture)
        {
            _settings.Session.ThreeDEnabled = enabled;
            CaptureCameras();
            _settings.RequestSave();
        }
    }

    private async Task Ensure3DSceneAsync(Heightmap map, bool resetCamera = true)
    {
        if (_scene3D is not null && ReferenceEquals(_scene3D.Map, map) && _scene3DAlbedoMode == _displayMode)
        {
            return;
        }

        try
        {
            StatusText.Text = "Preparing 3D preview (albedo + mesh)...";
            DisplayMode albedoMode = _displayMode;
            BiomeResult? biomeAlbedo = albedoMode == DisplayMode.Stage5Biome ? _stage5Result : null;
            byte[] albedo = await Task.Run(() =>
                biomeAlbedo is not null
                    ? TerrainPreview.RenderBiomeAlbedo(biomeAlbedo)
                    : TerrainPreview.RenderAlbedo(map));

            if (!ReferenceEquals(GetActiveMap(), map))
            {
                return;
            }

            var scene = new TerrainScene(map, _planetRadiusMeters, albedo);
            _scene3D = scene;
            _scene3DAlbedoMode = albedoMode;
            Terrain3DView.SetScene(scene, resetCamera);
            Terrain3DView.ShowGrid = _settings.Preferences.ShowGrid3D;

            ApplyRendererPreference();
            Terrain3DView.VerticalExaggeration = (float)_exaggeration;
            Terrain3DView.SetViewMode(_3dMode);
            Terrain3DView.SetControlMode(_controlMode);

            if (_restoredCamera is not null)
            {
                Terrain3DView.RestoreCamera(Terrain3DViewMode.Regional, _restoredCamera);
                _restoredCamera = null;
            }

            if (_restoredGlobeCamera is not null)
            {
                Terrain3DView.RestoreCamera(Terrain3DViewMode.Globe, _restoredGlobeCamera);
                _restoredGlobeCamera = null;
            }

            if (_restoredRegionalFlight is not null)
            {
                Terrain3DView.RestoreFlight(Terrain3DViewMode.Regional, _restoredRegionalFlight);
                _restoredRegionalFlight = null;
            }

            if (_restoredGlobeFlight is not null)
            {
                Terrain3DView.RestoreFlight(Terrain3DViewMode.Globe, _restoredGlobeFlight);
                _restoredGlobeFlight = null;
            }

            ThreeDFlightText.IsVisible = _controlMode == Terrain3DControlMode.Fly;
            StatusText.Text = "3D preview ready. Drag to orbit, wheel to zoom at the cursor.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"3D preview error: {exception.Message}";
        }
    }

    private void ApplyRendererPreference()
    {
        int index = Math.Clamp(RendererCombo.SelectedIndex, 0, 2);
        Terrain3DView.Preference = (RendererPreference)index;

        if (!_suppressSettingsCapture)
        {
            _settings.Preferences.RendererPreference = index;
            _settings.RequestSave();
        }
    }

    private void ApplyGridToggle()
    {
        bool show = GridCheckBox.IsChecked ?? true;
        Terrain3DView.ShowGrid = show;

        if (!_suppressSettingsCapture)
        {
            _settings.Preferences.ShowGrid3D = show;
            _settings.RequestSave();
        }
    }

    private void Set3DMode(Terrain3DViewMode mode)
    {
        _3dMode = mode;
        Terrain3DView.SetViewMode(mode);
        Update3DModeButtons();

        if (!_suppressSettingsCapture)
        {
            _settings.Preferences.ThreeDViewMode = (int)mode;
            _settings.RequestSave();
        }

        if (_is3DView)
        {
            Terrain3DView.RequestFrame();
        }
    }

    private void Update3DModeButtons()
    {
        SetActiveButton(Regional3DButton, _3dMode == Terrain3DViewMode.Regional);
        SetActiveButton(Globe3DButton, _3dMode == Terrain3DViewMode.Globe);
    }

    private void SetControlMode(Terrain3DControlMode mode)
    {
        _controlMode = mode;
        Terrain3DView.SetControlMode(mode);
        UpdateControlModeButtons();
        UpdateThreeDHint();

        if (!_suppressSettingsCapture)
        {
            _settings.Preferences.ThreeDControlMode = (int)mode;
            _settings.RequestSave();
        }

        if (_is3DView)
        {
            ThreeDFlightText.IsVisible = mode == Terrain3DControlMode.Fly;
            if (mode == Terrain3DControlMode.Fly)
            {
                ThreeDFlightText.Text = "Click the view, then W/A/S/D to fly.";
            }

            Terrain3DView.RequestFrame();
        }
    }

    private void UpdateControlModeButtons()
    {
        SetActiveButton(Orbit3DButton, _controlMode == Terrain3DControlMode.Orbit);
        SetActiveButton(Fly3DButton, _controlMode == Terrain3DControlMode.Fly);
    }

    private void UpdateThreeDHint()
    {
        ThreeDHintText.Text = _controlMode == Terrain3DControlMode.Fly
            ? "W/A/S/D move · Space up · C down · Q/E turn · +/- speed · Shift boost · Ctrl slow · Drag to look · Wheel speed · Double-click resets"
            : "Drag to orbit · Shift+drag or middle-drag to pan (regional) · Wheel zooms · Double-click resets";
    }

    private void SelectExaggeration(double value)
    {
        // Presets only: 1x for shape, 10x for legibility, 50x for drama.
        double snapped = value <= 5.5 ? 1 : value <= 30 ? 10 : 50;
        _exaggeration = snapped;

        SetActiveButton(Exag1Button, snapped == 1);
        SetActiveButton(Exag10Button, snapped == 10);
        SetActiveButton(Exag50Button, snapped == 50);

        if (!_suppressSettingsCapture)
        {
            _settings.Preferences.VerticalExaggeration = snapped;
            _settings.RequestSave();
        }

        Terrain3DView.VerticalExaggeration = (float)snapped;
    }

    private static void SetActiveButton(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("res-active"))
            {
                button.Classes.Add("res-active");
            }
        }
        else
        {
            button.Classes.Remove("res-active");
        }
    }

    private void Update3DCursorReadout(TerrainPickResult pick)
    {
        if (!pick.Hit)
        {
            return;
        }

        string latStr = $"{Math.Abs(pick.Latitude):F2}°{(pick.Latitude >= 0 ? "N" : "S")}";
        string lonStr = $"{Math.Abs(pick.Longitude):F2}°{(pick.Longitude >= 0 ? "E" : "W")}";
        string stage = _displayMode switch
        {
            DisplayMode.Stage4Hydro => "Stage 4 terrain",
            DisplayMode.Stage3Climate => "Stage 3 terrain",
            DisplayMode.Stage2Eroded => "Stage 2 eroded terrain",
            DisplayMode.Stage2Overlay => "Stage 2 eroded terrain",
            _ => "Stage 1 base terrain",
        };

        CursorText.Text =
            $"{latStr}, {lonStr} | Surface: {pick.ElevationMeters:+0;-0;0} m | {stage} | 3D " +
            $"{(_3dMode == Terrain3DViewMode.Globe ? "globe" : "regional")} " +
            $"{(_controlMode == Terrain3DControlMode.Fly ? "fly" : "orbit")} " +
            $"({_exaggeration:F0}×, {Terrain3DView.Preference})";
    }

    private void UpdateMapHud()
    {
        if (_is3DView)
        {
            Terrain3DHudTelemetry telemetry = Terrain3DView.GetHudTelemetry();
            if (!telemetry.HasData)
            {
                MapHud.IsVisible = false;
                return;
            }

            SetMapHud(telemetry.Latitude, telemetry.Longitude, telemetry.HeadingDegrees, telemetry.AltitudeMeters);
            return;
        }

        Heightmap? map = GetActiveMap();
        if (map is null)
        {
            MapHud.IsVisible = false;
            return;
        }

        if (_hudCursor is { } point &&
            point.Latitude <= map.View.NorthLatitude &&
            point.Latitude >= map.View.SouthLatitude &&
            point.Longitude >= map.View.WestLongitude &&
            point.Longitude <= map.View.EastLongitude)
        {
            float elevation = TerrainProjection.SampleBilinear(map, point.Latitude, point.Longitude);
            SetMapHud(point.Latitude, point.Longitude, 0.0, elevation);
            return;
        }

        double latitude = (map.View.NorthLatitude + map.View.SouthLatitude) * 0.5;
        double longitude = (map.View.WestLongitude + map.View.EastLongitude) * 0.5;
        float centerElevation = TerrainProjection.SampleBilinear(map, latitude, longitude);
        SetMapHud(latitude, longitude, 0.0, centerElevation);
    }

    private void SetMapHud(double latitude, double longitude, double? headingDegrees, float altitudeMeters)
    {
        longitude = ((longitude + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        string latitudeDirection = latitude >= 0.0 ? "N" : "S";
        string longitudeDirection = longitude >= 0.0 ? "E" : "W";
        string latitudeText = Math.Abs(latitude).ToString("F3", CultureInfo.InvariantCulture);
        string longitudeText = Math.Abs(longitude).ToString("F3", CultureInfo.InvariantCulture);

        string headingText;
        if (headingDegrees is { } heading)
        {
            int degrees = (int)Math.Round(heading) % 360;
            if (degrees < 0)
            {
                degrees += 360;
            }

            headingText = degrees.ToString("000", CultureInfo.InvariantCulture) + "°";
        }
        else
        {
            headingText = "---°";
        }

        string altitudeText = MathF.Abs(altitudeMeters) >= 1_000f
            ? (altitudeMeters / 1_000f).ToString("0.0", CultureInfo.InvariantCulture) + " km"
            : altitudeMeters.ToString("0", CultureInfo.InvariantCulture) + " m";

        MapHudText.Text =
            $"LAT  {latitudeText}° {latitudeDirection}\n" +
            $"LON  {longitudeText}° {longitudeDirection}\n" +
            $"HDG  {headingText}\n" +
            $"ALT  {altitudeText}";
        MapHud.IsVisible = true;
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        if (mode == DisplayMode.Stage5Biome && _stage5PreviewPng is null)
        {
            mode = _stage4PreviewPng is not null
                ? DisplayMode.Stage4Hydro
                : _stage3PreviewPng is not null
                    ? DisplayMode.Stage3Climate
                    : _stage2PreviewPng is not null ? DisplayMode.Stage2Eroded : DisplayMode.Stage1Base;
        }

        if (mode == DisplayMode.Stage4Hydro && _stage4PreviewPng is null)
        {
            mode = _stage3PreviewPng is not null ? DisplayMode.Stage3Climate : (_stage2PreviewPng is not null ? DisplayMode.Stage2Eroded : DisplayMode.Stage1Base);
        }
        else if (mode == DisplayMode.Stage3Climate && _stage3PreviewPng is null)
        {
            mode = _stage2PreviewPng is not null ? DisplayMode.Stage2Eroded : DisplayMode.Stage1Base;
        }
        else if ((mode == DisplayMode.Stage2Eroded || mode == DisplayMode.Stage2Overlay) && _stage2PreviewPng is null)
        {
            mode = DisplayMode.Stage1Base;
        }

        _displayMode = mode;
        byte[]? pngBytes = mode switch
        {
            DisplayMode.Stage5Biome => _stage5PreviewPng ?? _stage4PreviewPng ?? _stage3PreviewPng ?? _stage2PreviewPng ?? _stage1PreviewPng,
            DisplayMode.Stage4Hydro => _stage4PreviewPng ?? _stage3PreviewPng ?? _stage2PreviewPng ?? _stage1PreviewPng,
            DisplayMode.Stage3Climate => _stage3PreviewPng ?? _stage2PreviewPng ?? _stage1PreviewPng,
            DisplayMode.Stage2Eroded => _stage2PreviewPng ?? _stage1PreviewPng,
            DisplayMode.Stage2Overlay => _stage2OverlayPng ?? _stage2PreviewPng ?? _stage1PreviewPng,
            _ => _stage1PreviewPng,
        };

        if (pngBytes is not null)
        {
            using var ms = new MemoryStream(pngBytes);
            PreviewImage.Source = new Bitmap(ms);
        }

        Heightmap? activeMap = GetActiveMap();
        if (activeMap is not null)
        {
            if (_is3DView)
            {
                _ = Ensure3DSceneAsync(activeMap, resetCamera: false);
            }

            var (min, max, mean, landFraction) = activeMap.ComputeStatistics();
            double mpp = _planetRadiusMeters * MathUtils.ToRadians(activeMap.View.LongitudeSpan) / activeMap.Width;
            string stageLabel = mode switch
            {
                DisplayMode.Stage5Biome => $"Stage 5 (.s5-biome: {_stage5Result?.Set.Name ?? "biomes"}, {_stage5Result?.Set.Biomes.Count ?? 0} land biomes)",
                DisplayMode.Stage4Hydro => $"Stage 4 (.s4-hydro: {_stage4Result?.Lakes.Count ?? 0} lakes, {_stage4Result?.Rivers.Count ?? 0} rivers)",
                DisplayMode.Stage3Climate => $"Stage 3 (.s3-climate: mean {_stage3Result?.MeanLandPrecipitationMm ?? 0:F0} mm/yr)",
                DisplayMode.Stage2Eroded => "Stage 2 (.s2-eroded)",
                DisplayMode.Stage2Overlay => "Stage 2 (Incision/Deposition)",
                _ => "Stage 1 (.s1-base)",
            };

            MapSummaryText.Text =
                $"{stageLabel} | {activeMap.Width}×{activeMap.Height} ({mpp / 1000.0:F1} km/px) | Land: {landFraction:P1} | Elev: {min:F0}..{max:F0} m";
        }

        if (!_is3DView)
        {
            UpdateMapHud();
        }

        UpdateBiomeLegend(mode);
    }

    private void UpdateBiomeLegend(DisplayMode mode)
    {
        if (mode != DisplayMode.Stage5Biome || _stage5Result is null)
        {
            BiomeLegendPanel.IsVisible = false;
            BiomeLegendItems.Children.Clear();
            return;
        }

        BiomeResult biomes = _stage5Result;
        BiomeLegendTitle.Text = $"Biomes — {biomes.Set.Name} ({biomes.Set.Id} v{biomes.Set.Version})";
        BiomeLegendItems.Children.Clear();

        void AddEntry(int id, string label, BiomeColor color)
        {
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            };

            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(swatch);
            row.Children.Add(text);
            BiomeLegendItems.Children.Add(row);
        }

        // Water classes first, then every land biome present in the result.
        if (biomes.UnclassifiedLandCells > 0)
        {
            AddEntry(0, $"Unclassified ({biomes.UnclassifiedLandCells:N0})", BiomeColor.Unclassified);
        }

        int waterClasses = 0;
        foreach (BiomeWaterClass water in BiomeTable.Default.WaterClasses.OrderBy(w => w.Id))
        {
            AddEntry(water.Id, water.Name, biomes.Palette.ColorOf(water.Id));
            waterClasses++;
            if (waterClasses >= 6)
            {
                break;
            }
        }

        foreach (KeyValuePair<int, int> cell in biomes.BiomeCellCounts.OrderBy(kv => kv.Key))
        {
            if (cell.Key < 16 || !biomes.Set.ById.TryGetValue(cell.Key, out BiomeDefinition? def))
            {
                continue;
            }

            AddEntry(cell.Key, $"{def.Name} ({cell.Value:N0})", biomes.Palette.ColorOf(cell.Key));
        }

        BiomeLegendPanel.IsVisible = true;
    }

    private static string BiomeNameFor(BiomeResult biomes, byte id)
    {
        if (biomes.Set.ById.TryGetValue(id, out BiomeDefinition? land))
        {
            return land.Name;
        }

        foreach (BiomeWaterClass water in BiomeTable.Default.WaterClasses)
        {
            if (water.Id == id)
            {
                return water.Name;
            }
        }

        return id == 0 ? "Unclassified" : $"Biome {id}";
    }

    private Heightmap? GetActiveMap() => _displayMode switch
    {
        DisplayMode.Stage5Biome when _stage5Result is not null => _stage5Result.Elevation,
        DisplayMode.Stage4Hydro when _stage4Result is not null => _stage4Result.HydrologicalHeightmap,
        DisplayMode.Stage3Climate when _stage3Result is not null => _stage3Result.Source,
        DisplayMode.Stage2Eroded or DisplayMode.Stage2Overlay when _stage2Map is not null => _stage2Map,
        _ => _stage1Map ?? _stage2Map ?? _stage4Result?.HydrologicalHeightmap,
    };

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_is3DView)
        {
            return;
        }

        double factor = e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2;
        _zoom = Math.Clamp(_zoom * factor, 1.0, 20.0);
        if (_zoom <= 1.01)
        {
            _zoom = 1.0;
            _panX = 0;
            _panY = 0;
        }
        ApplyTransform();
        e.Handled = true;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_is3DView)
        {
            return;
        }

        if (e.GetCurrentPoint(ViewportBorder).Properties.IsLeftButtonPressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(ViewportBorder) - new Point(_panX, _panY);
            e.Pointer.Capture(ViewportBorder);
        }
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_is3DView)
        {
            return;
        }

        if (_isPanning)
        {
            Point pos = e.GetPosition(ViewportBorder);
            _panX = pos.X - _panStart.X;
            _panY = pos.Y - _panStart.Y;
            ApplyTransform();
        }

        UpdateCursorReadout(e);
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
        }
    }

    private async void OnViewportDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_is3DView)
        {
            return;
        }

        Heightmap? map = GetActiveMap();
        if (map is null)
            return;

        if (!TryGetMapPixel(e.GetPosition(PreviewImage), map, out int px, out int py))
            return;

        double clickLat = map.LatitudeAt(py);
        double clickLon = map.LongitudeAt(px);

        double halfLatSpan = Math.Max(1.0, map.View.LatitudeSpan * 0.25);
        double halfLonSpan = Math.Max(2.0, map.View.LongitudeSpan * 0.25);

        double north = Math.Clamp(clickLat + halfLatSpan, -89.0, 90.0);
        double south = Math.Clamp(clickLat - halfLatSpan, -90.0, north - 1.0);
        double west = Math.Clamp(clickLon - halfLonSpan, -180.0, 178.0);
        double east = Math.Clamp(clickLon + halfLonSpan, west + 1.0, 180.0);

        NorthLatBox.Text = north.ToString("F2", CultureInfo.InvariantCulture);
        SouthLatBox.Text = south.ToString("F2", CultureInfo.InvariantCulture);
        WestLonBox.Text = west.ToString("F2", CultureInfo.InvariantCulture);
        EastLonBox.Text = east.ToString("F2", CultureInfo.InvariantCulture);

        ResetPanZoom();
        await GenerateStage1Async();
    }

    private void UpdateCursorReadout(PointerEventArgs e)
    {
        Heightmap? map = GetActiveMap();
        if (map is null)
            return;

        if (!TryGetMapPixel(e.GetPosition(PreviewImage), map, out int px, out int py))
            return;

        double lat = map.LatitudeAt(py);
        double lon = map.LongitudeAt(px);
        float elev = map[px, py];
        string latStr = $"{Math.Abs(lat):F2}°{(lat >= 0 ? "N" : "S")}";
        string lonStr = $"{Math.Abs(lon):F2}°{(lon >= 0 ? "E" : "W")}";

        if (_displayMode == DisplayMode.Stage5Biome && _stage5Result is not null &&
            px < _stage5Result.Elevation.Width && py < _stage5Result.Elevation.Height)
        {
            int idx = py * _stage5Result.Elevation.Width + px;
            byte id = _stage5Result.BiomeId[idx];
            string name = BiomeNameFor(_stage5Result, id);
            float moisture = _stage5Result.Moisture[idx];
            CursorText.Text =
                $"{latStr}, {lonStr} ({px},{py}) | Elev: {elev:+0;-0;0} m | Biome: {name} (id {id}) | Moisture: {moisture:P0}";
        }
        else if (_stage4Result is not null && px < _stage4Result.HydrologicalHeightmap.Width && py < _stage4Result.HydrologicalHeightmap.Height)
        {
            int idx = py * _stage4Result.HydrologicalHeightmap.Width + px;
            WaterBodyKind kind = _stage4Result.WaterMask[idx];
            sbyte dir = _stage4Result.FlowDirection[idx];
            string dirStr = (dir >= 0 && dir < 8) ? D8CompassLabels[dir] : "—";
            float q = _stage4Result.DischargeM3s[idx];
            float precip = _stage4Result.Climate.PrecipitationMmPerYear[idx];
            float temp = _stage4Result.Climate.TemperatureCelsius[idx];

            string waterDetail = kind switch
            {
                WaterBodyKind.Lake =>
                    $"Lake #{_stage4Result.LakeId[idx]} (surf {_stage4Result.HydrologicalHeightmap.Elevations[idx]:F0} m, depth {Math.Max(0f, _stage4Result.HydrologicalHeightmap.Elevations[idx] - _stage4Result.Source.Elevations[idx]):F1} m)",
                WaterBodyKind.River =>
                    $"River (Order {_stage4Result.RiverOrder[idx]}, Q={q:N0} m³/s, W~{_stage4Result.ChannelWidthMeters[idx]:F0} m)",
                WaterBodyKind.Ocean => "Ocean",
                _ => $"Land (Q={q:N0} m³/s)",
            };

            CursorText.Text =
                $"{latStr}, {lonStr} ({px},{py}) | Elev: {elev:+0;-0;0} m | {temp:F1}°C, {precip:F0} mm/yr | {waterDetail} | Flow: {dirStr}";
        }
        else if (_stage3Result is not null && px < _stage3Result.Source.Width && py < _stage3Result.Source.Height)
        {
            int idx = py * _stage3Result.Source.Width + px;
            float temp = _stage3Result.TemperatureCelsius[idx];
            float precip = _stage3Result.PrecipitationMmPerYear[idx];
            float runoff = _stage3Result.RunoffMmPerYear[idx];
            CursorText.Text =
                $"{latStr}, {lonStr} ({px},{py}) | Elev: {elev:+0;-0;0} m | Temp: {temp:F1} °C | Precip: {precip:F0} mm/yr | Runoff: {runoff:F0} mm/yr";
        }
        else if (_stage2Result is not null && px < _stage2Result.Eroded.Width && py < _stage2Result.Eroded.Height)
        {
            int idx = py * _stage2Result.Eroded.Width + px;
            float s1Elev = _stage2Result.Source.Elevations[idx];
            float s2Elev = _stage2Result.Eroded.Elevations[idx];
            float inc = _stage2Result.ErosionDepthMeters[idx];
            float dep = _stage2Result.SedimentThicknessMeters[idx];
            float area = _stage2Result.DrainageAreaSqKm[idx];

            CursorText.Text =
                $"{latStr}, {lonStr} ({px},{py}) | S1: {s1Elev:+0;-0;0} m → S2: {s2Elev:+0;-0;0} m | Incised: {inc:F1} m | Deposited: {dep:F1} m | Catchment: {area:N0} km²";
        }
        else
        {
            string zone = elev >= 0f ? "Land" : "Ocean";
            CursorText.Text = $"Cursor: {latStr}, {lonStr}  |  Pixel ({px}, {py})  |  Stage 1 Elevation: {elev:+0;-0;0} m ({zone})";
        }

        _hudCursor = (lat, lon, elev);
        if (!_is3DView)
        {
            SetMapHud(lat, lon, 0.0, elev);
        }
    }

    private bool TryGetMapPixel(Point imagePoint, Heightmap map, out int px, out int py)
    {
        px = 0;
        py = 0;

        Rect bounds = PreviewImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        double u = imagePoint.X / bounds.Width;
        double v = imagePoint.Y / bounds.Height;
        if (u < 0 || u >= 1.0 || v < 0 || v >= 1.0)
            return false;

        px = Math.Clamp((int)(u * map.Width), 0, map.Width - 1);
        py = Math.Clamp((int)(v * map.Height), 0, map.Height - 1);
        return true;
    }

    private void ResetPanZoom()
    {
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (PreviewImage.RenderTransform is TransformGroup group && group.Children.Count >= 2)
        {
            if (group.Children[0] is ScaleTransform scale)
            {
                scale.ScaleX = _zoom;
                scale.ScaleY = _zoom;
            }
            if (group.Children[1] is TranslateTransform translate)
            {
                translate.X = _panX;
                translate.Y = _panY;
            }
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        bool hasStage1 = _stage1Map is not null;
        bool hasStage2 = _stage2Map is not null;
        bool hasStage3 = _stage3Result is not null;
        bool hasStage4 = _stage4Result is not null;
        bool hasStage5 = _stage5Result is not null;
        bool hasAnyMap = hasStage1 || hasStage2 || hasStage3 || hasStage4 || hasStage5;

        Res1024Button.IsEnabled = enabled;
        Res2048Button.IsEnabled = enabled;
        Res4096Button.IsEnabled = enabled;
        Res8192Button.IsEnabled = enabled;
        GenerateButton.IsEnabled = enabled;
        LoadStage1TiffButton.IsEnabled = enabled;
        ErodeButton.IsEnabled = enabled && hasStage1;
        LoadStage2TiffButton.IsEnabled = enabled;
        ClimateButton.IsEnabled = enabled && hasStage2;
        HydroButton.IsEnabled = enabled && hasStage3;
        BiomeButton.IsEnabled = enabled && hasStage4;
        BiomeSetCombo.IsEnabled = enabled;
        BiomeSmoothBox.IsEnabled = enabled;
        BiomeEcotoneCheck.IsEnabled = enabled;
        ExportStage1Button.IsEnabled = enabled && hasStage1 && _currentTerrain is not null;
        ExportStage2Button.IsEnabled = enabled && _stage2Result is not null;
        ExportStage3Button.IsEnabled = enabled && hasStage3;
        ExportStage4Button.IsEnabled = enabled && hasStage4;
        ExportStage5Button.IsEnabled = enabled && hasStage5;
        DiagnosticsButton.IsEnabled = enabled && (hasStage1 || hasStage2);
        ResetViewButton.IsEnabled = enabled && hasStage1;
        ViewStage1Button.IsEnabled = enabled && _stage1PreviewPng is not null;
        ViewStage2Button.IsEnabled = enabled && _stage2PreviewPng is not null;
        ViewOverlayButton.IsEnabled = enabled && _stage2OverlayPng is not null;
        ViewStage3Button.IsEnabled = enabled && _stage3PreviewPng is not null;
        ViewStage4Button.IsEnabled = enabled && _stage4PreviewPng is not null;
        ViewStage5Button.IsEnabled = enabled && _stage5PreviewPng is not null;
        View3DButton.IsEnabled = enabled && hasAnyMap;
        Reset3DCameraButton.IsEnabled = enabled && hasAnyMap;
        Orbit3DButton.IsEnabled = enabled && hasAnyMap;
        Fly3DButton.IsEnabled = enabled && hasAnyMap;
        GridCheckBox.IsEnabled = enabled && hasAnyMap;
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static float ParseFloat(string? text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
