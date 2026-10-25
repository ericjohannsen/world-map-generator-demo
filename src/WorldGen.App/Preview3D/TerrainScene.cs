using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WorldGen;

namespace WorldGen.App.Preview3D;

/// <summary>
/// Immutable description of the terrain shown by the 3D preview: the active heightmap, its albedo, the
/// local tangent projection, and the mesh caches that feed the GPU and software rasterisers.
/// </summary>
public sealed class TerrainScene
{
    private readonly object _gate = new();
    private float _verticalExaggeration = 1f;

    public TerrainScene(Heightmap map, float planetRadiusMeters, byte[]? albedoRgb = null)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
        PlanetRadiusMeters = planetRadiusMeters;
        Projection = new TerrainProjection(map.View, planetRadiusMeters);
        AlbedoRgb = albedoRgb ?? TerrainPreview.RenderAlbedo(map);

        (MinimumElevation, MaximumElevation, _, _) = map.ComputeStatistics();
        CellSizeMeters = Projection.WorldWidthMeters / map.Width;
        WorldDiagonal = MathF.Sqrt(
            Projection.WorldWidthMeters * Projection.WorldWidthMeters +
            Projection.WorldHeightMeters * Projection.WorldHeightMeters);

        GpuMeshes = new TerrainMeshCache(this, maxColumns: 1025);
        CpuMeshes = new TerrainMeshCache(this, maxColumns: 257);
    }

    public Heightmap Map { get; }

    public float PlanetRadiusMeters { get; }

    public TerrainProjection Projection { get; }

    public byte[] AlbedoRgb { get; }

    public float MinimumElevation { get; }

    public float MaximumElevation { get; }

    public float CellSizeMeters { get; }

    public float WorldWidthMeters => Projection.WorldWidthMeters;

    public float WorldHeightMeters => Projection.WorldHeightMeters;

    public float WorldDiagonal { get; }

    public TerrainMeshCache GpuMeshes { get; }

    public TerrainMeshCache CpuMeshes { get; }

    public float VerticalExaggeration
    {
        get => _verticalExaggeration;
        set => _verticalExaggeration = Math.Clamp(value, 1f, 50f);
    }

    public float HeightAt(float x, float y) => Projection.HeightAt(Map, x, y);

    /// <summary>Visible surface height in metres: terrain, or the flat sea at 0 m.</summary>
    public float SurfaceAt(float x, float y) => MathF.Max(0f, HeightAt(x, y));

    /// <summary>
    /// Surface height in the mesh's display frame, which is scaled by vertical exaggeration. The camera
    /// and picking live in this frame; readouts convert back to real metres.
    /// </summary>
    public float DisplaySurfaceAt(float x, float y) => SurfaceAt(x, y) * VerticalExaggeration;

    /// <summary>
    /// Radius of the visible globe surface (sea-level sphere or displaced land) under a planet-centric
    /// position. Used to keep free flight above the ground.
    /// </summary>
    public float GlobeSurfaceRadiusAt(Vector3 position)
    {
        float length = position.Length();
        if (length < 1e-3f)
        {
            return PlanetRadiusMeters;
        }

        Vector3 direction = position / length;
        double latitude = MathUtils.ToDegrees(Math.Asin(Math.Clamp(direction.Y, -1f, 1f)));
        double longitude = MathUtils.ToDegrees(Math.Atan2(direction.Z, direction.X));
        float elevation = TerrainProjection.SampleBilinear(Map, latitude, longitude);
        return PlanetRadiusMeters + MathF.Max(0f, elevation) * VerticalExaggeration;
    }

    public Vector3 DefaultTarget => new(0f, 0f, DisplaySurfaceAt(0f, 0f));

    public float DefaultDistance => MathF.Max(1_000f, WorldDiagonal * 0.62f);

    public float MinimumCameraDistance => MathF.Max(120f, CellSizeMeters * 1.2f);

    public float MaximumCameraDistance => WorldDiagonal * 4f;

    public bool TryPickScreen(
        Vector2 point,
        float viewportWidth,
        float viewportHeight,
        IPreviewCamera camera,
        out TerrainPickResult result)
    {
        result = default;
        if (viewportWidth <= 1f || viewportHeight <= 1f)
        {
            return false;
        }

        float ndcX = point.X / viewportWidth * 2f - 1f;
        float ndcY = 1f - point.Y / viewportHeight * 2f;
        Vector3 direction = camera.CreateRayDirection(
            ndcX, ndcY, viewportWidth / viewportHeight, WorldDiagonal);

        return TryPick(camera.Eye, direction, out result);
    }

    /// <summary>
    /// Globe picking intersects the sea-level sphere rather than the displaced surface. The returned
    /// elevation still comes from the heightmap, so the readout is right even though the hit point on a
    /// tall mountain is a little in front of the visible surface.
    /// </summary>
    public bool TryPickGlobeScreen(
        Vector2 point,
        float viewportWidth,
        float viewportHeight,
        IPreviewCamera camera,
        out TerrainPickResult result)
    {
        result = default;
        if (viewportWidth <= 1f || viewportHeight <= 1f)
        {
            return false;
        }

        float ndcX = point.X / viewportWidth * 2f - 1f;
        float ndcY = 1f - point.Y / viewportHeight * 2f;
        Vector3 direction = camera.CreateRayDirection(
            ndcX, ndcY, viewportWidth / viewportHeight, WorldDiagonal);

        return TryPickGlobe(camera.Eye, direction, out result);
    }

    public bool TryPickGlobe(Vector3 origin, Vector3 direction, out TerrainPickResult result)
    {
        result = default;

        float radius = PlanetRadiusMeters;
        float b = Vector3.Dot(origin, direction);
        float c = Vector3.Dot(origin, origin) - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0f)
        {
            return false;
        }

        float root = MathF.Sqrt(discriminant);
        float t = -b - root;
        if (t < 0f)
        {
            t = -b + root;
        }

        if (t < 0f)
        {
            return false;
        }

        Vector3 point = origin + direction * t;
        float length = point.Length();
        if (length < 1e-3f)
        {
            return false;
        }

        double latitude = MathUtils.ToDegrees(Math.Asin(Math.Clamp(point.Y / length, -1f, 1f)));
        double longitude = MathUtils.ToDegrees(Math.Atan2(point.Z, point.X));
        float elevation = TerrainProjection.SampleBilinear(Map, latitude, longitude);

        result = new TerrainPickResult(
            true,
            point,
            latitude,
            longitude,
            MathF.Max(0f, elevation));
        return true;
    }

    public bool TryPick(Vector3 origin, Vector3 direction, out TerrainPickResult result)
    {
        result = default;

        float halfWidth = WorldWidthMeters * 0.5f;
        float halfHeight = WorldHeightMeters * 0.5f;
        float minimumZ = MathF.Min(MinimumElevation * VerticalExaggeration, 0f) - 100f;
        float maximumZ = MathF.Max(MaximumElevation * VerticalExaggeration, 0f) + 100f;

        if (!IntersectBox(origin, direction, halfWidth, halfHeight, minimumZ, maximumZ, out float tEnter, out float tExit))
        {
            return false;
        }

        tEnter = MathF.Max(0f, tEnter);
        if (tExit <= tEnter)
        {
            return false;
        }

        const int Steps = 320;
        float step = (tExit - tEnter) / Steps;
        float previousT = tEnter;
        float previousGap = 0f;
        Vector3 previousPoint = origin + direction * previousT;
        float previousSurface = DisplaySurfaceAt(previousPoint.X, previousPoint.Y);
        previousGap = previousPoint.Z - previousSurface;

        for (int i = 1; i <= Steps; i++)
        {
            float t = tEnter + step * i;
            Vector3 point = origin + direction * t;
            float surface = DisplaySurfaceAt(point.X, point.Y);
            float gap = point.Z - surface;

            if (gap <= 0f && previousGap > 0f)
            {
                float low = previousT;
                float high = t;
                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float middle = (low + high) * 0.5f;
                    Vector3 sample = origin + direction * middle;
                    if (sample.Z - DisplaySurfaceAt(sample.X, sample.Y) > 0f)
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                Vector3 hit = origin + direction * high;
                float elevation = HeightAt(hit.X, hit.Y);
                var (latitude, longitude) = Projection.ToGeographic(hit.X, hit.Y);
                result = new TerrainPickResult(
                    true,
                    hit,
                    latitude,
                    longitude,
                    MathF.Max(0f, elevation));
                return true;
            }

            previousT = t;
            previousGap = gap;
        }

        // A ray that starts below the surface (for example while zoomed into a valley) still reports a hit.
        if (previousGap <= 0f)
        {
            Vector3 point = origin + direction * tEnter;
            float elevation = HeightAt(point.X, point.Y);
            var (latitude, longitude) = Projection.ToGeographic(point.X, point.Y);
            result = new TerrainPickResult(true, point, latitude, longitude, MathF.Max(0f, elevation));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Downsamples the map albedo to an RGBA8 texture no larger than the given limits. Geometry and
    /// normals stay at full map resolution; only the colour texture is capped.
    /// </summary>
    public (byte[] Rgba, int Width, int Height) BuildAlbedoTexture(int maximumWidth, int maximumHeight)
    {
        int targetWidth = Math.Max(1, Math.Min(Map.Width, maximumWidth));
        int targetHeight = Math.Max(1, Math.Min(Map.Height, maximumHeight));

        if (targetWidth == Map.Width && targetHeight == Map.Height)
        {
            var direct = new byte[Map.Width * Map.Height * 4];
            for (int i = 0, s = 0, d = 0; i < Map.Width * Map.Height; i++, s += 3, d += 4)
            {
                direct[d] = AlbedoRgb[s];
                direct[d + 1] = AlbedoRgb[s + 1];
                direct[d + 2] = AlbedoRgb[s + 2];
                direct[d + 3] = 255;
            }

            return (direct, Map.Width, Map.Height);
        }

        var rgba = new byte[targetWidth * targetHeight * 4];
        double scaleX = Map.Width / (double)targetWidth;
        double scaleY = Map.Height / (double)targetHeight;

        Parallel.For(0, targetHeight, y =>
        {
            int sourceY0 = (int)(y * scaleY);
            int sourceY1 = Math.Max(sourceY0 + 1, (int)((y + 1) * scaleY));
            sourceY1 = Math.Min(sourceY1, Map.Height);

            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX0 = (int)(x * scaleX);
                int sourceX1 = Math.Max(sourceX0 + 1, (int)((x + 1) * scaleX));
                sourceX1 = Math.Min(sourceX1, Map.Width);

                int red = 0;
                int green = 0;
                int blue = 0;
                int count = 0;
                for (int sy = sourceY0; sy < sourceY1; sy++)
                {
                    int row = sy * Map.Width;
                    for (int sx = sourceX0; sx < sourceX1; sx++)
                    {
                        int offset = (row + sx) * 3;
                        red += AlbedoRgb[offset];
                        green += AlbedoRgb[offset + 1];
                        blue += AlbedoRgb[offset + 2];
                        count++;
                    }
                }

                int target = (y * targetWidth + x) * 4;
                rgba[target] = (byte)(red / Math.Max(1, count));
                rgba[target + 1] = (byte)(green / Math.Max(1, count));
                rgba[target + 2] = (byte)(blue / Math.Max(1, count));
                rgba[target + 3] = 255;
            }
        });

        return (rgba, targetWidth, targetHeight);
    }

    internal MeshPlan CreateMeshPlan(Vector3 focusPoint, float requestedHalfExtent, int maximumColumns)
    {
        float halfExtent = Math.Clamp(
            requestedHalfExtent,
            MathF.Max(CellSizeMeters * 1.5f, 200f),
            WorldDiagonal);

        var (centerLatitude, centerLongitude) = Projection.ToGeographic(focusPoint.X, focusPoint.Y);
        double latitudeRadius = halfExtent / Projection.MetersPerDegreeLatitude;
        double longitudeRadius = halfExtent / Projection.MetersPerDegreeLongitude;

        MapView mapView = Map.View;
        double south = Math.Max(mapView.SouthLatitude, centerLatitude - latitudeRadius);
        double north = Math.Min(mapView.NorthLatitude, centerLatitude + latitudeRadius);
        double west = Math.Max(mapView.WestLongitude, centerLongitude - longitudeRadius);
        double east = Math.Min(mapView.EastLongitude, centerLongitude + longitudeRadius);

        if (north - south < 1e-5 || east - west < 1e-5)
        {
            north = mapView.NorthLatitude;
            south = mapView.SouthLatitude;
            west = mapView.WestLongitude;
            east = mapView.EastLongitude;
        }

        var window = new MapView(north, south, west, east);

        double sourceCellLongitude = mapView.LongitudeSpan / Map.Width;
        int desiredColumns = (int)Math.Ceiling(window.LongitudeSpan / sourceCellLongitude);

        bool wholeMap = window.LongitudeSpan >= mapView.LongitudeSpan * 0.98 &&
                        window.LatitudeSpan >= mapView.LatitudeSpan * 0.98;

        int columns = wholeMap
            ? Math.Min(maximumColumns, Map.Width + 1)
            : Math.Clamp(desiredColumns, 65, maximumColumns);

        columns = Math.Max(3, columns);
        float windowWidth = (float)window.LongitudeSpan * Projection.MetersPerDegreeLongitude;
        float windowHeight = (float)window.LatitudeSpan * Projection.MetersPerDegreeLatitude;
        int rows = Math.Clamp(
            (int)MathF.Round(columns * (windowHeight / Math.Max(1f, windowWidth))),
            33,
            maximumColumns);
        rows = Math.Max(3, rows);

        return new MeshPlan(window, columns, rows, VerticalExaggeration);
    }

    internal TerrainMeshData BuildMesh(MeshPlan plan) => TerrainMesh.Build(
        Map,
        plan.Window,
        PlanetRadiusMeters,
        plan.Columns,
        plan.Rows,
        plan.VerticalExaggeration);

    private static bool IntersectBox(
        Vector3 origin,
        Vector3 direction,
        float halfWidth,
        float halfHeight,
        float minimumZ,
        float maximumZ,
        out float tEnter,
        out float tExit)
    {
        tEnter = float.NegativeInfinity;
        tExit = float.PositiveInfinity;

        if (!IntersectSlab(origin.X, direction.X, -halfWidth, halfWidth, ref tEnter, ref tExit) ||
            !IntersectSlab(origin.Y, direction.Y, -halfHeight, halfHeight, ref tEnter, ref tExit) ||
            !IntersectSlab(origin.Z, direction.Z, minimumZ, maximumZ, ref tEnter, ref tExit))
        {
            return false;
        }

        return tExit > 0f;
    }

    private static bool IntersectSlab(float origin, float direction, float minimum, float maximum, ref float tEnter, ref float tExit)
    {
        if (MathF.Abs(direction) < 1e-9f)
        {
            return origin >= minimum && origin <= maximum;
        }

        float inverse = 1f / direction;
        float t0 = (minimum - origin) * inverse;
        float t1 = (maximum - origin) * inverse;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        tEnter = MathF.Max(tEnter, t0);
        tExit = MathF.Min(tExit, t1);
        return tExit >= tEnter;
    }
}

internal readonly record struct MeshPlan(MapView Window, int Columns, int Rows, float VerticalExaggeration);

public readonly record struct TerrainPickResult(
    bool Hit,
    Vector3 World,
    double Latitude,
    double Longitude,
    float ElevationMeters);

/// <summary>
/// Caches one camera-following mesh and rebuilds it on a worker when the camera outgrows it.
/// </summary>
/// <remarks>
/// Rebuild decisions are deliberately coarse: the mesh does not need to be re-centred for every mouse
/// event, only when the camera leaves the central part of the window or changes zoom by a large factor.
/// That keeps sampling off the render thread while the view stays visually continuous.
/// </remarks>
public sealed class TerrainMeshCache
{
    private readonly TerrainScene _scene;
    private readonly int _maximumColumns;
    private readonly object _gate = new();
    private TerrainMeshData? _current;
    private MeshPlan _currentPlan;
    private bool _building;
    private int _generation;
    private long _lastBuildTimestamp;

    public TerrainMeshCache(TerrainScene scene, int maxColumns)
    {
        _scene = scene;
        _maximumColumns = maxColumns;
    }

    public event EventHandler? MeshUpdated;

    public TerrainMeshData? Current => Volatile.Read(ref _current);

    public TerrainMeshData? Get(IPreviewCamera camera)
    {
        MeshPlan desired = _scene.CreateMeshPlan(camera.MeshFocus, camera.MeshHalfExtent, _maximumColumns);
        TerrainMeshData? current = Volatile.Read(ref _current);
        if (current is not null && !NeedsRebuild(current, desired))
        {
            return current;
        }

        // Throttle rebuilds: a fast drag can outrun sampling, and the old window is still valid terrain.
        if (current is not null &&
            Stopwatch.GetTimestamp() - Volatile.Read(ref _lastBuildTimestamp) < Stopwatch.Frequency / 5)
        {
            return current;
        }

        StartBuild(desired);
        return current;
    }

    public void Invalidate()
    {
        Volatile.Write(ref _current, null);
        lock (_gate)
        {
            _generation++;
        }
    }

    private bool NeedsRebuild(TerrainMeshData current, MeshPlan desired)
    {
        double currentCenterLat = (current.Window.NorthLatitude + current.Window.SouthLatitude) * 0.5;
        double currentCenterLon = (current.Window.WestLongitude + current.Window.EastLongitude) * 0.5;
        double desiredCenterLat = (desired.Window.NorthLatitude + desired.Window.SouthLatitude) * 0.5;
        double desiredCenterLon = (desired.Window.WestLongitude + desired.Window.EastLongitude) * 0.5;

        double halfSpan = current.Window.LatitudeSpan * 0.5;
        double halfLonSpan = current.Window.LongitudeSpan * 0.5;
        bool centreMoved = Math.Abs(desiredCenterLat - currentCenterLat) > halfSpan * 0.3 ||
                           Math.Abs(desiredCenterLon - currentCenterLon) > halfLonSpan * 0.3;

        double spanRatio = desired.Window.LatitudeSpan / current.Window.LatitudeSpan;
        bool zoomChanged = spanRatio is < 0.7 or > 1.4;

        int columnRatio = Math.Abs(desired.Columns - current.Columns);
        bool resolutionChanged = columnRatio > Math.Max(16, current.Columns / 8);

        bool exaggerationChanged = MathF.Abs(desired.VerticalExaggeration - _currentPlan.VerticalExaggeration) > 1e-3;

        return centreMoved || zoomChanged || resolutionChanged || exaggerationChanged;
    }

    private void StartBuild(MeshPlan plan)
    {
        lock (_gate)
        {
            if (_building)
            {
                return;
            }

            _building = true;
            _currentPlan = plan;
            Volatile.Write(ref _lastBuildTimestamp, Stopwatch.GetTimestamp());
            int generation = ++_generation;

            _ = Task.Run(() => _scene.BuildMesh(plan)).ContinueWith(task =>
            {
                bool updated = false;
                lock (_gate)
                {
                    _building = false;
                    if (task.Status == TaskStatus.RanToCompletion && generation == _generation)
                    {
                        Volatile.Write(ref _current, task.Result);
                        updated = true;
                    }
                }

                if (updated)
                {
                    MeshUpdated?.Invoke(this, EventArgs.Empty);
                }
            }, TaskScheduler.Default);
        }
    }
}

