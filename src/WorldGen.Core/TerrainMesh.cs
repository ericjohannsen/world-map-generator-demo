using System.Numerics;

namespace WorldGen;

/// <summary>
/// Maps between geographic coordinates and the local tangent plane used by the 3D preview.
/// </summary>
/// <remarks>
/// The preview is not a globe: it treats the map as a flat plane tangent at the window centre, with
/// longitudes scaled by the cosine of that latitude. This keeps a 2:1 world map 2:1 in world metres and
/// makes a regional window locally correct, which is what a "stand on the mountain" view needs. The
/// approximation degrades near the poles, where the scale is clamped so that positions stay finite.
/// </remarks>
public sealed class TerrainProjection
{
    private const float MinimumLatitudeScale = 0.05f;

    public TerrainProjection(MapView view, float planetRadiusMeters)
    {
        if (planetRadiusMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(planetRadiusMeters));
        }

        View = view;
        PlanetRadiusMeters = planetRadiusMeters;
        CenterLatitude = (view.NorthLatitude + view.SouthLatitude) * 0.5;
        CenterLongitude = (view.WestLongitude + view.EastLongitude) * 0.5;

        double radiansPerDegree = Math.PI / 180.0;
        MetersPerDegreeLatitude = (float)(planetRadiusMeters * radiansPerDegree);
        float cosLatitude = (float)Math.Cos(CenterLatitude * radiansPerDegree);
        MetersPerDegreeLongitude = (float)(planetRadiusMeters * radiansPerDegree) *
                                   MathF.Max(MinimumLatitudeScale, cosLatitude);

        WorldWidthMeters = (float)(view.LongitudeSpan * MetersPerDegreeLongitude);
        WorldHeightMeters = (float)(view.LatitudeSpan * MetersPerDegreeLatitude);
    }

    public MapView View { get; }

    public float PlanetRadiusMeters { get; }

    public double CenterLatitude { get; }

    public double CenterLongitude { get; }

    public float MetersPerDegreeLatitude { get; }

    public float MetersPerDegreeLongitude { get; }

    public float WorldWidthMeters { get; }

    public float WorldHeightMeters { get; }

    public Vector2 ToWorld(double latitude, double longitude) => new(
        (float)((longitude - CenterLongitude) * MetersPerDegreeLongitude),
        (float)((latitude - CenterLatitude) * MetersPerDegreeLatitude));

    public (double Latitude, double Longitude) ToGeographic(float x, float y) => (
        CenterLatitude + y / MetersPerDegreeLatitude,
        CenterLongitude + x / MetersPerDegreeLongitude);

    /// <summary>
    /// Bilinear height lookup in the local tangent plane, in metres. Points outside the map clamp to the
    /// nearest edge; the preview never invents terrain beyond the window.
    /// </summary>
    public float HeightAt(Heightmap map, float x, float y)
    {
        var (latitude, longitude) = ToGeographic(x, y);
        return SampleBilinear(map, latitude, longitude);
    }

    public static float SampleBilinear(Heightmap map, double latitude, double longitude)
    {
        double u = (longitude - map.View.WestLongitude) / map.View.LongitudeSpan * map.Width - 0.5;
        double v = (map.View.NorthLatitude - latitude) / map.View.LatitudeSpan * map.Height - 0.5;

        int x0 = (int)Math.Floor(u);
        int y0 = (int)Math.Floor(v);
        float tx = (float)(u - x0);
        float ty = (float)(v - y0);

        int x1 = x0 + 1;
        x0 = Wrap(x0, map.Width);
        x1 = Wrap(x1, map.Width);
        int yc0 = Math.Clamp(y0, 0, map.Height - 1);
        int yc1 = Math.Clamp(y0 + 1, 0, map.Height - 1);

        float top = map.Elevations[yc0 * map.Width + x0] * (1f - tx) +
                    map.Elevations[yc0 * map.Width + x1] * tx;
        float bottom = map.Elevations[yc1 * map.Width + x0] * (1f - tx) +
                       map.Elevations[yc1 * map.Width + x1] * tx;
        return top * (1f - ty) + bottom * ty;
    }

    public static int Wrap(int value, int size)
    {
        int wrapped = value % size;
        return wrapped < 0 ? wrapped + size : wrapped;
    }
}

/// <summary>
/// An indexed, interleaved terrain mesh in the local tangent plane (x east, y north, z up, metres).
/// </summary>
public sealed class TerrainMeshData
{
    public TerrainMeshData(
        MapView window,
        float[] positions,
        float[] normals,
        float[] uvs,
        int[] indices,
        int columns,
        int rows,
        float minimumElevation,
        float maximumElevation)
    {
        Window = window;
        Positions = positions;
        Normals = normals;
        Uvs = uvs;
        Indices = indices;
        Columns = columns;
        Rows = rows;
        MinimumElevation = minimumElevation;
        MaximumElevation = maximumElevation;
    }

    public MapView Window { get; }

    public float[] Positions { get; }

    public float[] Normals { get; }

    public float[] Uvs { get; }

    public int[] Indices { get; }

    public int Columns { get; }

    public int Rows { get; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;

    /// <summary>Elevation range in metres before vertical exaggeration.</summary>
    public float MinimumElevation { get; }

    public float MaximumElevation { get; }
}

/// <summary>
/// Builds a terrain mesh for a map window by bilinear sampling of a <see cref="Heightmap"/>.
/// </summary>
/// <remarks>
/// The mesh carries elevations in metres up to vertical exaggeration; it never quantises to 8-bit.
/// Geomorphology is not re-derived here: the caller passes the heightmap that a stage produced, so the
/// 3D view and the 2D map cannot disagree about the terrain (see P3D-9).
/// </remarks>
public static class TerrainMesh
{
    public static TerrainMeshData Build(
        Heightmap source,
        MapView window,
        float planetRadiusMeters,
        int columns,
        int rows,
        float verticalExaggeration = 1f)
    {
        ArgumentNullException.ThrowIfNull(source);
        window.Validate();

        columns = Math.Clamp(columns, 2, 4097);
        rows = Math.Clamp(rows, 2, 4097);
        verticalExaggeration = Math.Clamp(verticalExaggeration, 0.05f, 64f);

        var projection = new TerrainProjection(source.View, planetRadiusMeters);

        int vertexCount = columns * rows;
        var positions = new float[vertexCount * 3];
        var normals = new float[vertexCount * 3];
        var uvs = new float[vertexCount * 2];
        var heights = new float[vertexCount];

        float latitudeStep = (float)(window.LatitudeSpan / (rows - 1));
        float longitudeStep = (float)(window.LongitudeSpan / (columns - 1));
        float latitude0 = (float)window.NorthLatitude;
        float longitude0 = (float)window.WestLongitude;
        double u0 = (window.WestLongitude - source.View.WestLongitude) / source.View.LongitudeSpan;
        double v0 = (source.View.NorthLatitude - window.NorthLatitude) / source.View.LatitudeSpan;
        double uSpan = window.LongitudeSpan / source.View.LongitudeSpan;
        double vSpan = window.LatitudeSpan / source.View.LatitudeSpan;

        float minimumElevation = float.PositiveInfinity;
        float maximumElevation = float.NegativeInfinity;

        Parallel.For(0, rows, j =>
        {
            float latitude = latitude0 - j * latitudeStep;
            int row = j * columns;

            // v runs north to south; keep it inside [0, 1] so edge cells do not sample past the map.
            float v = (float)Math.Clamp(v0 + vSpan * (j / (double)(rows - 1)), 0.0, 1.0);

            for (int i = 0; i < columns; i++)
            {
                float longitude = longitude0 + i * longitudeStep;
                float elevation = TerrainProjection.SampleBilinear(source, latitude, longitude);
                heights[row + i] = elevation;

                Vector2 world = projection.ToWorld(latitude, longitude);
                int index = (row + i) * 3;
                positions[index] = world.X;
                positions[index + 1] = world.Y;
                positions[index + 2] = elevation * verticalExaggeration;

                float u = (float)(u0 + uSpan * (i / (double)(columns - 1)));
                u -= MathF.Floor(u);
                int uvIndex = (row + i) * 2;
                uvs[uvIndex] = u;
                uvs[uvIndex + 1] = v;
            }
        });

        float metresPerVertexX = projection.WorldWidthMeters * (float)(window.LongitudeSpan / source.View.LongitudeSpan) / Math.Max(1, columns - 1);
        float metresPerVertexY = projection.WorldHeightMeters * (float)(window.LatitudeSpan / source.View.LatitudeSpan) / Math.Max(1, rows - 1);
        metresPerVertexX = MathF.Max(0.01f, metresPerVertexX);
        metresPerVertexY = MathF.Max(0.01f, metresPerVertexY);

        Parallel.For(0, rows, j =>
        {
            int row = j * columns;
            int rowAbove = Math.Max(0, j - 1) * columns;
            int rowBelow = Math.Min(rows - 1, j + 1) * columns;
            float dzdy = (heights[rowBelow] - heights[rowAbove]) /
                         (MathF.Max(1, rowBelow / columns - rowAbove / columns) * metresPerVertexY) *
                         verticalExaggeration;

            for (int i = 0; i < columns; i++)
            {
                int left = Math.Max(0, i - 1);
                int right = Math.Min(columns - 1, i + 1);
                float dzdx = (heights[row + right] - heights[row + left]) /
                             (MathF.Max(1, right - left) * metresPerVertexX) * verticalExaggeration;

                float nx = -dzdx;
                float ny = -dzdy;
                float nz = 1f;
                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-6f)
                {
                    length = 1f;
                }

                int index = (row + i) * 3;
                normals[index] = nx / length;
                normals[index + 1] = ny / length;
                normals[index + 2] = nz / length;
            }
        });

        for (int i = 0; i < heights.Length; i++)
        {
            float elevation = heights[i];
            if (elevation < minimumElevation)
            {
                minimumElevation = elevation;
            }

            if (elevation > maximumElevation)
            {
                maximumElevation = elevation;
            }
        }

        var indices = new int[(columns - 1) * (rows - 1) * 6];
        int cursor = 0;
        for (int j = 0; j < rows - 1; j++)
        {
            int row = j * columns;
            int nextRow = (j + 1) * columns;
            for (int i = 0; i < columns - 1; i++)
            {
                int a = row + i;
                int b = nextRow + i;
                int c = row + i + 1;
                int d = nextRow + i + 1;

                // Upward-facing winding in a right-handed frame with y pointing north.
                indices[cursor++] = a;
                indices[cursor++] = b;
                indices[cursor++] = c;

                indices[cursor++] = c;
                indices[cursor++] = b;
                indices[cursor++] = d;
            }
        }

        return new TerrainMeshData(
            window,
            positions,
            normals,
            uvs,
            indices,
            columns,
            rows,
            minimumElevation,
            maximumElevation);
    }
}

