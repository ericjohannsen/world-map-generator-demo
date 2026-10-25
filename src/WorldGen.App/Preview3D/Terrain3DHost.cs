using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WorldGen;
using WorldGen.App.Settings;

namespace WorldGen.App.Preview3D;

public enum RendererPreference
{
    Auto,
    Gpu,
    Cpu,
}

public enum Terrain3DViewMode
{
    Regional,
    Globe,
}

public enum Terrain3DControlMode
{
    Orbit,
    Fly,
}

/// <summary>Position and orientation shown by the in-view HUD.</summary>
public readonly record struct Terrain3DHudTelemetry(
    bool HasData,
    double Latitude,
    double Longitude,
    double? HeadingDegrees,
    float AltitudeMeters);

/// <summary>
/// Hosts the 3D preview: chooses between the OpenGL renderer and the software fallback, owns the orbit and
/// free-flight cameras for both the regional and globe views, and routes pointer and keyboard input.
/// </summary>
public sealed class Terrain3DHost : Grid
{
    private const int GlobeGpuLongitudeSegments = 512;
    private const int GlobeGpuLatitudeSegments = 256;
    private const int GlobeCpuLongitudeSegments = 192;
    private const int GlobeCpuLatitudeSegments = 96;
    private const float KeyboardTurnStepRadians = 3f * MathF.PI / 180f;

    private readonly Terrain3DGlControl _gl = new();
    private readonly Terrain3DSoftwareControl _software = new();
    private readonly TextBlock _placeholder;
    private readonly DispatcherTimer _gpuProbeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _flyTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _gridTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _flyClock = new();
    private readonly HashSet<Key> _pressedKeys = new();

    private readonly OrbitCamera _regionalOrbit = new();
    private readonly OrbitCamera _globeOrbit = new()
    {
        MinimumPitch = -1.45f,
        MaximumPitch = 1.45f,
        Pitch = 0.42f,
        Yaw = 0.8f,
    };

    private readonly FreeFlightCamera _regionalFly = new(Terrain3DViewMode.Regional);
    private readonly FreeFlightCamera _globeFly = new(Terrain3DViewMode.Globe);

    private TerrainScene? _scene;
    private IPreviewCamera _camera;
    private RendererPreference _preference = RendererPreference.Auto;
    private Terrain3DViewMode _mode = Terrain3DViewMode.Regional;
    private Terrain3DControlMode _controlMode = Terrain3DControlMode.Orbit;
    private bool _usingGpu = true;
    private DragMode _dragMode;
    private Point _lastPoint;
    private bool _active;

    private TerrainMeshData? _globeGpuMesh;
    private TerrainMeshData? _globeCpuMesh;
    private float _globeMeshExaggeration = -1f;
    private bool _globeBuilding;
    private bool _globeRebuildPending;
    private int _globeGeneration;

    private long _lastFlightStatusTimestamp;
    private bool _showGrid = true;
    private string _gridKey = string.Empty;

    public Terrain3DHost()
    {
        Background = new SolidColorBrush(Color.Parse("#0B0E13"));
        ClipToBounds = true;
        Focusable = true;

        _camera = _regionalOrbit;
        _gl.Camera = _camera;
        _software.Camera = _camera;

        _placeholder = new TextBlock
        {
            Text = "Generate or load terrain, then switch to 3D to inspect it.",
            Foreground = new SolidColorBrush(Color.Parse("#8892A0")),
            FontSize = 13,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        Children.Add(_software);
        Children.Add(_gl);
        Children.Add(_placeholder);

        _gl.RendererFailed += (_, message) => Dispatcher.UIThread.Post(() => FallBackToSoftware(message));
        _gl.FirstFrameRendered += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _gpuProbeTimer.Stop();
            if (_usingGpu)
            {
                RendererChanged?.Invoke(this, $"3D renderer: GPU — {_gl.RendererDescription}");
            }
        });

        _software.RenderFailed += (_, message) =>
            Dispatcher.UIThread.Post(() => RendererChanged?.Invoke(this, $"3D software renderer error: {message}"));

        _gl.FrameCaptured += (_, capture) => FrameCaptured?.Invoke(this, capture);

        _gpuProbeTimer.Tick += (_, _) =>
        {
            _gpuProbeTimer.Stop();
            if (_usingGpu && !_gl.HasRenderedFrame)
            {
                FallBackToSoftware("OpenGL did not produce a frame; using the software renderer.");
            }
        };

        _flyTimer.Tick += (_, _) => OnFlyTick();
        _gridTimer.Tick += (_, _) => UpdateGridIfNeeded();

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => ClearKeys();
        DoubleTapped += (_, _) => ResetCamera();

        UpdateVisibility();
    }

    public event EventHandler<string>? RendererChanged;

    public event EventHandler<TerrainPickResult>? CursorPicked;

    public event EventHandler<TerrainFrameCapture>? FrameCaptured;

    /// <summary>Raised when a camera interaction finishes, so the window can persist the view.</summary>
    public event EventHandler? CameraSettled;

    /// <summary>Altitude/speed readout while flying, raised a few times per second.</summary>
    public event EventHandler<string>? FlightStatusChanged;

    public bool IsUsingGpu => _usingGpu;

    public Terrain3DViewMode ViewMode => _mode;

    public Terrain3DControlMode ControlMode => _controlMode;

    public IPreviewCamera Camera => _camera;

    /// <summary>
    /// Geographic position, view heading, and altitude of the active 3D camera. Altitude is above sea
    /// level in real metres (vertical exaggeration removed). Heading is null when the view looks straight
    /// down at the globe centre and no compass direction is meaningful.
    /// </summary>
    public Terrain3DHudTelemetry GetHudTelemetry()
    {
        if (_scene is null || !_active)
        {
            return default;
        }

        Vector3 eye = _camera.Eye;
        float exaggeration = MathF.Max(0.05f, _scene.VerticalExaggeration);
        double latitude;
        double longitude;
        float altitudeDisplay;

        if (_mode == Terrain3DViewMode.Globe)
        {
            float length = eye.Length();
            if (length < 1e-3f)
            {
                return default;
            }

            Vector3 direction = eye / length;
            latitude = MathUtils.ToDegrees(Math.Asin(Math.Clamp(direction.Y, -1f, 1f)));
            longitude = MathUtils.ToDegrees(Math.Atan2(direction.Z, direction.X));
            altitudeDisplay = MathF.Max(0f, length - _scene.PlanetRadiusMeters);
        }
        else
        {
            (latitude, longitude) = _scene.Projection.ToGeographic(eye.X, eye.Y);
            latitude = Math.Clamp(latitude, -90.0, 90.0);
            altitudeDisplay = MathF.Max(0f, eye.Z);
        }

        double? heading = null;
        if (_controlMode == Terrain3DControlMode.Fly)
        {
            heading = FlyHeading(ActiveFlyCamera);
        }
        else if (_mode == Terrain3DViewMode.Regional)
        {
            Vector3 view = _regionalOrbit.Target - eye;
            float horizontal = MathF.Sqrt(view.X * view.X + view.Y * view.Y);
            if (horizontal > 1e-4f)
            {
                heading = NormalizeHeading(MathF.Atan2(view.X, view.Y));
            }
        }

        return new Terrain3DHudTelemetry(
            true,
            latitude,
            NormalizeLongitude(longitude),
            heading,
            altitudeDisplay / exaggeration);
    }

    public RendererPreference Preference
    {
        get => _preference;
        set
        {
            if (_preference == value)
            {
                return;
            }

            _preference = value;
            ApplyPreference();
        }
    }

    public float VerticalExaggeration
    {
        get => _scene?.VerticalExaggeration ?? 1f;
        set
        {
            if (_scene is null)
            {
                return;
            }

            _scene.VerticalExaggeration = value;
            _scene.GpuMeshes.Invalidate();
            _scene.CpuMeshes.Invalidate();

            _regionalFly.ClearanceMeters = 1.8f * value;
            _globeFly.ClearanceMeters = 20f * value;

            if (_mode == Terrain3DViewMode.Globe)
            {
                EnsureGlobeMesh(force: true);
            }

            ClampCamera();
            _gridKey = string.Empty;
            UpdateGridIfNeeded();
            RequestFrame();
        }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            if (_showGrid == value)
            {
                return;
            }

            _showGrid = value;
            _gridKey = string.Empty;
            UpdateGridIfNeeded();
        }
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            IsVisible = value;

            if (value)
            {
                ClampCamera();
                RequestFrame();
                _gridTimer.Start();
                UpdateGridIfNeeded();
                if (_controlMode == Terrain3DControlMode.Fly)
                {
                    StartFlyClock();
                }
            }
            else
            {
                StopFlyClock();
                _gridTimer.Stop();
            }
        }
    }

    public void SetScene(TerrainScene? scene, bool resetCamera)
    {
        if (_scene is not null)
        {
            _scene.GpuMeshes.MeshUpdated -= OnGpuMeshUpdated;
            _scene.CpuMeshes.MeshUpdated -= OnCpuMeshUpdated;
        }

        _scene = scene;
        _globeGpuMesh = null;
        _globeCpuMesh = null;
        _globeMeshExaggeration = -1f;
        _globeGeneration++;

        if (_scene is not null)
        {
            _scene.GpuMeshes.MeshUpdated += OnGpuMeshUpdated;
            _scene.CpuMeshes.MeshUpdated += OnCpuMeshUpdated;
        }

        _gl.Scene = scene;
        _software.Scene = scene;
        _gridKey = string.Empty;

        if (scene is null)
        {
            ApplyFixedMesh();
            _gl.Grid = null;
            _software.Grid = null;
            _placeholder.IsVisible = true;
            RendererChanged?.Invoke(this, "3D preview has no terrain.");
            return;
        }

        _placeholder.IsVisible = false;

        if (resetCamera)
        {
            _regionalOrbit.Reset(scene.DefaultTarget, scene.DefaultDistance);
            _regionalOrbit.MinimumPitch = 0.05f;
            _regionalOrbit.MaximumPitch = 1.5533f;

            float radius = scene.PlanetRadiusMeters;
            _globeOrbit.Target = Vector3.Zero;
            _globeOrbit.Distance = radius * 2.4f;
            _globeOrbit.Yaw = 0.8f;
            _globeOrbit.Pitch = 0.42f;

            _regionalFly.HasState = false;
            _globeFly.HasState = false;
            _regionalFly.PlanetRadiusMeters = radius;
            _globeFly.PlanetRadiusMeters = radius;
            _regionalFly.ClearanceMeters = 1.8f * scene.VerticalExaggeration;
            _globeFly.ClearanceMeters = 20f * scene.VerticalExaggeration;
        }

        _camera = ActiveCamera;
        _gl.Camera = _camera;
        _software.Camera = _camera;

        ApplyFixedMesh();
        if (_mode == Terrain3DViewMode.Globe)
        {
            EnsureGlobeMesh(force: true);
        }

        if (_controlMode == Terrain3DControlMode.Fly)
        {
            EnsureFlyInitialized();
        }

        ApplyPreference();
        ClampCamera();
        UpdateGridIfNeeded();
        RequestFrame();
    }

    public void SetViewMode(Terrain3DViewMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        _camera = ActiveCamera;
        _gl.Camera = _camera;
        _software.Camera = _camera;

        if (mode == Terrain3DViewMode.Globe)
        {
            ApplyFixedMesh();
            EnsureGlobeMesh(force: _globeGpuMesh is null);
        }
        else
        {
            ApplyFixedMesh();
        }

        if (_controlMode == Terrain3DControlMode.Fly)
        {
            EnsureFlyInitialized();
        }

        ClampCamera();
        _gridKey = string.Empty;
        UpdateGridIfNeeded();
        RequestFrame();
        CameraSettled?.Invoke(this, EventArgs.Empty);
    }

    public void SetControlMode(Terrain3DControlMode mode)
    {
        if (_controlMode == mode)
        {
            return;
        }

        _controlMode = mode;
        _camera = ActiveCamera;
        _gl.Camera = _camera;
        _software.Camera = _camera;

        if (mode == Terrain3DControlMode.Fly)
        {
            EnsureFlyInitialized();
            Focus();
            StartFlyClock();
        }
        else
        {
            StopFlyClock();
        }

        ClampCamera();
        _gridKey = string.Empty;
        UpdateGridIfNeeded();
        RequestFrame();
        CameraSettled?.Invoke(this, EventArgs.Empty);
    }

    public void ResetCamera()
    {
        if (_scene is null)
        {
            return;
        }

        if (_controlMode == Terrain3DControlMode.Fly)
        {
            FreeFlightCamera fly = ActiveFlyCamera;
            fly.HasState = false;
            EnsureFlyInitialized();
            ClampCamera();
            RequestFrame();
            CameraSettled?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_mode == Terrain3DViewMode.Globe)
        {
            float radius = _scene.PlanetRadiusMeters;
            _globeOrbit.Target = Vector3.Zero;
            _globeOrbit.Distance = radius * 2.4f;
            _globeOrbit.Yaw = 0.8f;
            _globeOrbit.Pitch = 0.42f;
        }
        else
        {
            _regionalOrbit.Reset(_scene.DefaultTarget, _scene.DefaultDistance);
        }

        ClampCamera();
        RequestFrame();
        CameraSettled?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreCamera(Terrain3DViewMode mode, CameraState state)
    {
        if (_scene is null)
        {
            return;
        }

        OrbitCamera camera = mode == Terrain3DViewMode.Globe ? _globeOrbit : _regionalOrbit;
        camera.Target = new Vector3(state.TargetX, state.TargetY, state.TargetZ);
        camera.Distance = state.Distance;
        camera.Yaw = state.Yaw;
        camera.Pitch = state.Pitch;
        ClampCamera();
        RequestFrame();
    }

    public CameraState CaptureCamera(Terrain3DViewMode mode)
    {
        OrbitCamera camera = mode == Terrain3DViewMode.Globe ? _globeOrbit : _regionalOrbit;
        return new CameraState
        {
            TargetX = camera.Target.X,
            TargetY = camera.Target.Y,
            TargetZ = camera.Target.Z,
            Distance = camera.Distance,
            Yaw = camera.Yaw,
            Pitch = camera.Pitch,
        };
    }

    public void RestoreFlight(Terrain3DViewMode mode, FreeCameraState state)
    {
        FreeFlightCamera fly = mode == Terrain3DViewMode.Globe ? _globeFly : _regionalFly;
        if (!state.IsFinite)
        {
            return;
        }

        if (mode == Terrain3DViewMode.Globe && state.X == 0f && state.Y == 0f && state.Z == 0f)
        {
            return;
        }

        fly.PlanetRadiusMeters = _scene?.PlanetRadiusMeters ?? fly.PlanetRadiusMeters;
        fly.Position = new Vector3(state.X, state.Y, state.Z);
        fly.SetYawPitch(state.Yaw, Math.Clamp(state.Pitch, -1.53f, 1.53f));
        fly.SpeedMultiplier = state.SpeedMultiplier;
        fly.HasState = true;
        ClampCamera();
        UpdateFlyGround(fly);
        RequestFrame();
    }

    public FreeCameraState? CaptureFlight(Terrain3DViewMode mode)
    {
        FreeFlightCamera fly = mode == Terrain3DViewMode.Globe ? _globeFly : _regionalFly;
        if (!fly.HasState)
        {
            return null;
        }

        return new FreeCameraState
        {
            X = fly.Position.X,
            Y = fly.Position.Y,
            Z = fly.Position.Z,
            Yaw = fly.Yaw,
            Pitch = fly.Pitch,
            SpeedMultiplier = fly.SpeedMultiplier,
        };
    }

    public void CaptureNextFrame()
    {
        _gl.CaptureNextFrame = true;
        _gl.RequestFrame();
    }

    public void RequestFrame()
    {
        if (!_active || _scene is null)
        {
            return;
        }

        if (_usingGpu)
        {
            _gl.RequestFrame();
        }
        else
        {
            _software.RequestFrame();
        }
    }

    private IPreviewCamera ActiveCamera => _controlMode == Terrain3DControlMode.Fly
        ? (_mode == Terrain3DViewMode.Globe ? _globeFly : _regionalFly)
        : (_mode == Terrain3DViewMode.Globe ? _globeOrbit : _regionalOrbit);

    private FreeFlightCamera ActiveFlyCamera =>
        _mode == Terrain3DViewMode.Globe ? _globeFly : _regionalFly;

    private void EnsureFlyInitialized()
    {
        if (_scene is null)
        {
            return;
        }

        FreeFlightCamera fly = ActiveFlyCamera;
        if (fly.HasState)
        {
            return;
        }

        fly.PlanetRadiusMeters = _scene.PlanetRadiusMeters;
        fly.ClearanceMeters = _mode == Terrain3DViewMode.Globe ? 20f : 1.8f;

        if (_mode == Terrain3DViewMode.Globe)
        {
            // Reset view: equator, 0 degrees longitude, 100 km above sea level, facing due east.
            Vector3 position = Vector3.UnitX * (_scene.PlanetRadiusMeters + 100_000f);
            fly.Reset(position, Vector3.UnitZ);
            fly.SetYawPitch(MathF.PI * 0.5f, 0f);
        }
        else
        {
            float altitude = Math.Clamp(_regionalOrbit.Distance * 0.01f, 300f, 2_000f);
            Vector3 position = _regionalOrbit.Target + Vector3.UnitZ * altitude;
            fly.Reset(position, _regionalOrbit.Target - position);
            fly.Pitch = -0.35f;
        }

        UpdateFlyGround(fly);
    }

    private void ApplyFixedMesh()
    {
        bool globe = _mode == Terrain3DViewMode.Globe;
        _gl.FixedMesh = globe ? _globeGpuMesh : null;
        _software.FixedMesh = globe ? _globeCpuMesh : null;
        _gl.GlobeMode = globe;
        _software.GlobeMode = globe;
    }

    private void EnsureGlobeMesh(bool force)
    {
        if (_scene is null)
        {
            return;
        }

        float exaggeration = _scene.VerticalExaggeration;
        if (!force && _globeGpuMesh is not null &&
            MathF.Abs(_globeMeshExaggeration - exaggeration) < 1e-3f)
        {
            ApplyFixedMesh();
            return;
        }

        if (_globeBuilding)
        {
            Volatile.Write(ref _globeRebuildPending, true);
            return;
        }

        _globeBuilding = true;
        Volatile.Write(ref _globeRebuildPending, false);
        int generation = ++_globeGeneration;
        TerrainScene scene = _scene;

        _ = Task.Run(() =>
            {
                TerrainMeshData gpu = TerrainGlobeMesh.Build(
                    scene.Map,
                    scene.PlanetRadiusMeters,
                    GlobeGpuLongitudeSegments,
                    GlobeGpuLatitudeSegments,
                    exaggeration);

                TerrainMeshData cpu = TerrainGlobeMesh.Build(
                    scene.Map,
                    scene.PlanetRadiusMeters,
                    GlobeCpuLongitudeSegments,
                    GlobeCpuLatitudeSegments,
                    exaggeration);

                return (Gpu: gpu, Cpu: cpu);
            })
            .ContinueWith(task =>
            {
                _globeBuilding = false;
                bool retry = Volatile.Read(ref _globeRebuildPending);
                Volatile.Write(ref _globeRebuildPending, false);

                if (task.Status != TaskStatus.RanToCompletion || generation != _globeGeneration)
                {
                    if (retry)
                    {
                        Dispatcher.UIThread.Post(() => EnsureGlobeMesh(force: true));
                    }

                    return;
                }

                _globeGpuMesh = task.Result.Gpu;
                _globeCpuMesh = task.Result.Cpu;
                _globeMeshExaggeration = exaggeration;

                Dispatcher.UIThread.Post(() =>
                {
                    if (_scene is not null && _mode == Terrain3DViewMode.Globe)
                    {
                        ApplyFixedMesh();
                        RequestFrame();
                    }

                    if (retry || (_scene is not null &&
                                  MathF.Abs(_scene.VerticalExaggeration - exaggeration) > 1e-3f))
                    {
                        EnsureGlobeMesh(force: true);
                    }
                });
            }, TaskScheduler.Default);
    }

    private void ApplyPreference()
    {
        bool useGpu = _preference != RendererPreference.Cpu;
        if (useGpu && _gl.IsRendererFailed)
        {
            useGpu = false;
        }

        _usingGpu = useGpu;
        UpdateVisibility();
        RequestFrame();

        if (useGpu)
        {
            _gpuProbeTimer.Stop();
            _gpuProbeTimer.Start();
            RendererChanged?.Invoke(this, $"3D renderer: starting GPU — {_gl.RendererDescription}");
        }
        else
        {
            RendererChanged?.Invoke(this, "3D renderer: CPU software rasteriser (reduced resolution).");
        }
    }

    private void FallBackToSoftware(string message)
    {
        if (!_usingGpu)
        {
            RendererChanged?.Invoke(this, message);
            return;
        }

        _usingGpu = false;
        _gpuProbeTimer.Stop();
        UpdateVisibility();
        _software.RequestFrame();
        RendererChanged?.Invoke(this, $"{message} Using the CPU software renderer.");
    }

    private void UpdateVisibility()
    {
        _gl.IsVisible = _usingGpu;
        _software.IsVisible = !_usingGpu;
    }

    private void OnGpuMeshUpdated(object? sender, EventArgs e)
    {
        if (_usingGpu && _active)
        {
            _gl.RequestFrame();
        }
    }

    private void OnCpuMeshUpdated(object? sender, EventArgs e)
    {
        if (!_usingGpu && _active)
        {
            _software.RequestFrame();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_active || _scene is null)
        {
            return;
        }

        Focus();

        PointerPoint point = e.GetCurrentPoint(this);
        _dragMode = _controlMode == Terrain3DControlMode.Fly
            ? DragMode.Look
            : point.Properties.IsMiddleButtonPressed ||
              (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                ? DragMode.Pan
                : point.Properties.IsLeftButtonPressed
                    ? DragMode.Orbit
                    : DragMode.None;

        if (_dragMode == DragMode.None)
        {
            return;
        }

        _lastPoint = point.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_active || _scene is null)
        {
            return;
        }

        Point position = e.GetPosition(this);

        if (_dragMode == DragMode.None)
        {
            bool picked = _mode == Terrain3DViewMode.Globe
                ? _scene.TryPickGlobeScreen(
                    new Vector2((float)position.X, (float)position.Y),
                    (float)Bounds.Width,
                    (float)Bounds.Height,
                    _camera,
                    out TerrainPickResult pick)
                : _scene.TryPickScreen(
                    new Vector2((float)position.X, (float)position.Y),
                    (float)Bounds.Width,
                    (float)Bounds.Height,
                    _camera,
                    out pick);

            if (picked)
            {
                CursorPicked?.Invoke(this, pick);
            }

            return;
        }

        double deltaX = position.X - _lastPoint.X;
        double deltaY = position.Y - _lastPoint.Y;
        _lastPoint = position;

        if (_dragMode == DragMode.Look)
        {
            ActiveFlyCamera.Look((float)(deltaX * 0.0035), (float)(-deltaY * 0.0035));
        }
        else if (_dragMode == DragMode.Orbit || _mode == Terrain3DViewMode.Globe)
        {
            OrbitCamera orbit = ActiveOrbitCamera;
            orbit.Orbit((float)(-deltaX * 0.006), (float)(-deltaY * 0.006));
        }
        else
        {
            OrbitCamera orbit = ActiveOrbitCamera;
            float worldUnitsPerPixel = WorldUnitsPerPixel(orbit);
            orbit.Pan((float)-deltaX, (float)deltaY, worldUnitsPerPixel);
        }

        ClampCamera();
        RequestFrame();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            CameraSettled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_active || _scene is null)
        {
            return;
        }

        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.001)
        {
            return;
        }

        if (_controlMode == Terrain3DControlMode.Fly)
        {
            ActiveFlyCamera.AdjustSpeed((float)delta);
            RaiseFlightStatus();
            RequestFrame();
            e.Handled = true;
            CameraSettled?.Invoke(this, EventArgs.Empty);
            return;
        }

        OrbitCamera orbit = ActiveOrbitCamera;
        float factor = MathF.Exp((float)-delta * 0.18f);

        Vector3? anchor = null;
        if (_mode == Terrain3DViewMode.Regional)
        {
            Point position = e.GetPosition(this);
            if (_scene.TryPickScreen(
                    new Vector2((float)position.X, (float)position.Y),
                    (float)Bounds.Width,
                    (float)Bounds.Height,
                    orbit,
                    out TerrainPickResult pick))
            {
                anchor = pick.World;
            }
        }

        if (_mode == Terrain3DViewMode.Globe)
        {
            float radius = _scene.PlanetRadiusMeters;
            orbit.Zoom(factor, null, radius * 1.03f, radius * 12f);
        }
        else
        {
            orbit.Zoom(factor, anchor, _scene.MinimumCameraDistance, _scene.MaximumCameraDistance);
        }

        ClampCamera();
        RequestFrame();
        e.Handled = true;
        CameraSettled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a flight key regardless of which control currently has focus. The window forwards keys here
    /// so WASD works after clicking a panel button; text boxes are excluded by the caller.
    /// </summary>
    public bool HandleFlightKey(Key key, bool isDown)
    {
        if (!_active || _controlMode != Terrain3DControlMode.Fly)
        {
            return false;
        }

        if (key is Key.Q or Key.E)
        {
            // Edge-triggered yaw. A tap must produce the same turn every time; sampling a held key once
            // per render frame made the turn depend on timer latency and occasionally miss short taps.
            if (isDown)
            {
                ActiveFlyCamera.TurnBy(
                    key == Key.E ? KeyboardTurnStepRadians : -KeyboardTurnStepRadians);
                RaiseFlightStatus();
                RequestFrame();
            }
            else
            {
                CameraSettled?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        if (IsSpeedKey(key))
        {
            if (isDown)
            {
                ActiveFlyCamera.AdjustSpeedStep(key is Key.OemPlus or Key.Add ? 1 : -1);
                RaiseFlightStatus();
                RequestFrame();
                CameraSettled?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        if (!IsFlightKey(key))
        {
            return false;
        }

        if (isDown)
        {
            _pressedKeys.Add(key);
        }
        else if (_pressedKeys.Remove(key) && _pressedKeys.Count == 0)
        {
            CameraSettled?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    private static bool IsSpeedKey(Key key) => key is
        Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract;

    private static bool IsFlightKey(Key key) => key is
        Key.W or Key.A or Key.S or Key.D or
        Key.C or Key.Space or
        Key.Up or Key.Down or Key.Left or Key.Right or
        Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl;

    private FlightInput BuildFlightInput() => new()
    {
        Forward = (HasKey(Key.W) || HasKey(Key.Up) ? 1f : 0f) -
                  (HasKey(Key.S) || HasKey(Key.Down) ? 1f : 0f),
        Right = (HasKey(Key.D) || HasKey(Key.Right) ? 1f : 0f) -
                (HasKey(Key.A) || HasKey(Key.Left) ? 1f : 0f),
        Up = HasKey(Key.Space) ? 1f : HasKey(Key.C) ? -1f : 0f,
        Boost = HasKey(Key.LeftShift) || HasKey(Key.RightShift),
        Precision = HasKey(Key.LeftCtrl) || HasKey(Key.RightCtrl),
    };

    private bool HasKey(Key key) => _pressedKeys.Contains(key);

    private void ClearKeys() => _pressedKeys.Clear();

    private void StartFlyClock()
    {
        _flyClock.Restart();
        _lastFlightStatusTimestamp = 0;
        _flyTimer.Start();
    }

    private void StopFlyClock()
    {
        _flyTimer.Stop();
        _flyClock.Stop();
        ClearKeys();
        ActiveFlyCamera.Stop();
    }

    private void OnFlyTick()
    {
        if (!_active || _controlMode != Terrain3DControlMode.Fly || _scene is null)
        {
            _flyTimer.Stop();
            return;
        }

        double elapsed = _flyClock.Elapsed.TotalSeconds;
        _flyClock.Restart();
        if (elapsed <= 0)
        {
            return;
        }

        FreeFlightCamera fly = ActiveFlyCamera;
        UpdateFlyGround(fly);
        fly.Update((float)Math.Clamp(elapsed, 0.001, 0.1), BuildFlightInput());
        ClampFly(fly);
        UpdateFlyGround(fly);
        RequestFrame();

        long now = Stopwatch.GetTimestamp();
        if (now - _lastFlightStatusTimestamp > Stopwatch.Frequency / 5)
        {
            _lastFlightStatusTimestamp = now;
            RaiseFlightStatus();
        }
    }

    private void RaiseFlightStatus()
    {
        if (_controlMode != Terrain3DControlMode.Fly || _scene is null)
        {
            return;
        }

        FreeFlightCamera fly = ActiveFlyCamera;
        UpdateFlyGround(fly);
        string speed = FormatMeters(fly.CruiseSpeed) + "/s";
        string altitude = FormatMeters(fly.AltitudeAboveGround);
        FlightStatusChanged?.Invoke(this,
            $"Flight · {speed} · {altitude} above ground · ×{fly.SpeedMultiplier:F1} · +/- speed");
    }

    private void UpdateFlyGround(FreeFlightCamera fly)
    {
        if (_scene is null)
        {
            return;
        }

        fly.GroundElevationMeters = _mode == Terrain3DViewMode.Globe
            ? _scene.GlobeSurfaceRadiusAt(fly.Position) - _scene.PlanetRadiusMeters
            : _scene.DisplaySurfaceAt(fly.Position.X, fly.Position.Y);
    }

    private static string FormatMeters(float meters) =>
        meters >= 1_000f ? $"{meters / 1_000f:F1} km" : $"{meters:F0} m";

    private OrbitCamera ActiveOrbitCamera =>
        _mode == Terrain3DViewMode.Globe ? _globeOrbit : _regionalOrbit;

    private float WorldUnitsPerPixel(OrbitCamera orbit)
    {
        double height = Math.Max(1, Bounds.Height);
        float visibleHeight = 2f * orbit.Distance *
                              MathF.Tan((float)MathUtils.ToRadians(orbit.FieldOfViewDegrees) * 0.5f);
        return visibleHeight / (float)height;
    }

    private void ClampCamera()
    {
        if (_scene is null)
        {
            return;
        }

        if (_controlMode == Terrain3DControlMode.Fly)
        {
            ClampFly(ActiveFlyCamera);
            return;
        }

        OrbitCamera orbit = ActiveOrbitCamera;

        if (_mode == Terrain3DViewMode.Globe)
        {
            float radius = _scene.PlanetRadiusMeters;
            orbit.Target = Vector3.Zero;
            orbit.Distance = Math.Clamp(orbit.Distance, radius * 1.03f, radius * 12f);
            orbit.Pitch = Math.Clamp(orbit.Pitch, orbit.MinimumPitch, orbit.MaximumPitch);
            return;
        }

        float clearance = MathF.Max(15f, orbit.Distance * 0.012f);
        orbit.ClampToGround(_scene.DisplaySurfaceAt, clearance);
        orbit.Target = new Vector3(
            Math.Clamp(orbit.Target.X, -_scene.WorldWidthMeters * 1.1f, _scene.WorldWidthMeters * 1.1f),
            Math.Clamp(orbit.Target.Y, -_scene.WorldHeightMeters * 1.1f, _scene.WorldHeightMeters * 1.1f),
            orbit.Target.Z);
        orbit.Distance = Math.Clamp(
            orbit.Distance,
            _scene.MinimumCameraDistance,
            _scene.MaximumCameraDistance);
    }

    private void ClampFly(FreeFlightCamera fly)
    {
        if (_scene is null)
        {
            return;
        }

        if (_mode == Terrain3DViewMode.Globe)
        {
            float length = fly.Position.Length();
            if (length < 1e-3f)
            {
                return;
            }

            Vector3 direction = fly.Position / length;
            float surface = _scene.GlobeSurfaceRadiusAt(fly.Position);
            float minimum = surface + fly.ClearanceMeters;

            if (length < minimum)
            {
                fly.Position = direction * minimum;
            }

            return;
        }

        float ground = _scene.DisplaySurfaceAt(fly.Position.X, fly.Position.Y);
        float minimumZ = ground + fly.ClearanceMeters;
        float x = Math.Clamp(fly.Position.X, -_scene.WorldWidthMeters * 0.55f, _scene.WorldWidthMeters * 0.55f);
        float y = Math.Clamp(fly.Position.Y, -_scene.WorldHeightMeters * 0.55f, _scene.WorldHeightMeters * 0.55f);
        fly.Position = new Vector3(x, y, MathF.Max(fly.Position.Z, minimumZ));
    }

    private void UpdateGridIfNeeded()
    {
        if (_scene is null || !_active)
        {
            _gl.Grid = null;
            _software.Grid = null;
            return;
        }

        string key = BuildGridKey();
        if (key == _gridKey)
        {
            return;
        }

        _gridKey = key;
        GridGeometry? grid = _showGrid ? BuildGridGeometry() : null;

        _gl.ShowGrid = _showGrid;
        _software.ShowGrid = _showGrid;
        _gl.Grid = grid;
        _software.Grid = grid;
        RequestFrame();
    }

    private GridGeometry? BuildGridGeometry()
    {
        if (_scene is null)
        {
            return null;
        }

        if (_mode == Terrain3DViewMode.Globe)
        {
            bool flying = _controlMode == Terrain3DControlMode.Fly;
            Vector3 position = flying ? _globeFly.Position : _globeOrbit.Eye;
            float altitude = flying
                ? _globeFly.AltitudeAboveGround
                : MathF.Max(0f, _globeOrbit.Distance - _scene.PlanetRadiusMeters);

            return PreviewGrid.BuildGlobe(_scene, position, altitude);
        }

        return PreviewGrid.BuildRegional(_scene, _camera.MeshFocus, _camera.MeshHalfExtent);
    }

    private string BuildGridKey()
    {
        if (_scene is null)
        {
            return string.Empty;
        }

        if (_mode == Terrain3DViewMode.Globe)
        {
            bool flying = _controlMode == Terrain3DControlMode.Fly;
            Vector3 position = flying ? _globeFly.Position : _globeOrbit.Eye;
            float altitude = flying
                ? _globeFly.AltitudeAboveGround
                : MathF.Max(0f, _globeOrbit.Distance - _scene.PlanetRadiusMeters);

            float length = position.Length();
            Vector3 direction = length > 1e-3f ? position / length : Vector3.UnitX;
            float latitude = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) * 180f / MathF.PI;
            float longitude = MathF.Atan2(direction.Z, direction.X) * 180f / MathF.PI;

            return $"G|{MathF.Round(latitude, 2)}|{MathF.Round(longitude, 2)}|{MathF.Round(altitude / 50f)}";
        }

        float step = MathF.Max(100f, _camera.MeshHalfExtent / 10f);
        Vector3 focus = _camera.MeshFocus;
        return $"R|{MathF.Round(focus.X / step)}|{MathF.Round(focus.Y / step)}|{MathF.Round(_camera.MeshHalfExtent)}";
    }

    private enum DragMode
    {
        None,
        Orbit,
        Pan,
        Look,
    }

    /// <summary>
    /// Compass heading of the free-flight camera, derived from its look direction and the local surface
    /// basis. Returns null when the view is close to straight up/down and no heading is meaningful.
    /// </summary>
    private double? FlyHeading(FreeFlightCamera fly)
    {
        Vector3 radial = _mode == Terrain3DViewMode.Globe
            ? (fly.Position.LengthSquared() > 1e-6f ? Vector3.Normalize(fly.Position) : Vector3.UnitZ)
            : Vector3.UnitZ;

        Vector3 forward = fly.Forward;
        Vector3 horizontal = forward - radial * Vector3.Dot(forward, radial);
        if (horizontal.LengthSquared() < 1e-6f)
        {
            return null;
        }

        Vector3 east;
        if (_mode == Terrain3DViewMode.Globe)
        {
            float rXZ = MathF.Sqrt(fly.Position.X * fly.Position.X + fly.Position.Z * fly.Position.Z);
            if (rXZ < 1e-4f)
            {
                east = Vector3.Cross(Vector3.UnitY, radial);
            }
            else
            {
                float longitude = MathF.Atan2(fly.Position.Z, fly.Position.X);
                east = new Vector3(-MathF.Sin(longitude), 0f, MathF.Cos(longitude));
            }
        }
        else
        {
            east = Vector3.UnitX;
        }

        east -= radial * Vector3.Dot(east, radial);
        if (east.LengthSquared() < 1e-8f)
        {
            east = Vector3.Cross(radial, Vector3.UnitY);
        }
        if (east.LengthSquared() < 1e-8f)
        {
            east = Vector3.Cross(radial, Vector3.UnitX);
        }
        east = Vector3.Normalize(east);
        Vector3 north = Vector3.Normalize(Vector3.Cross(east, radial));

        return NormalizeHeading(MathF.Atan2(Vector3.Dot(horizontal, east), Vector3.Dot(horizontal, north)));
    }

    private static double NormalizeHeading(float radians)
    {
        double degrees = MathUtils.ToDegrees(radians) % 360.0;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    private static double NormalizeLongitude(double longitude)
    {
        longitude = (longitude + 180.0) % 360.0;
        if (longitude < 0.0)
        {
            longitude += 360.0;
        }

        return longitude - 180.0;
    }
}
