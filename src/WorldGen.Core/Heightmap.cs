using System.Numerics;

namespace WorldGen;

/// <summary>
/// A rectangular window on the sphere, in degrees. Latitude runs north to south, longitude west to east.
/// </summary>
public readonly record struct MapView(double NorthLatitude, double SouthLatitude, double WestLongitude, double EastLongitude)
{
    public static MapView WholePlanet => new(90.0, -90.0, -180.0, 180.0);

    public double LatitudeSpan => NorthLatitude - SouthLatitude;

    public double LongitudeSpan => EastLongitude - WestLongitude;

    public void Validate()
    {
        if (NorthLatitude is > 90.0 or < -90.0 || SouthLatitude is > 90.0 or < -90.0)
        {
            throw new ArgumentOutOfRangeException(nameof(NorthLatitude), "Latitudes must be within [-90, 90] degrees.");
        }

        if (NorthLatitude <= SouthLatitude)
        {
            throw new ArgumentOutOfRangeException(nameof(NorthLatitude), "North latitude must be greater than south latitude.");
        }

        if (LongitudeSpan <= 0.0 || LongitudeSpan > 360.0)
        {
            throw new ArgumentOutOfRangeException(nameof(WestLongitude), "Longitude span must be in (0, 360] degrees.");
        }
    }
}

/// <summary>
/// Elevation samples on an equirectangular latitude/longitude grid, in meters relative to sea level.
/// </summary>
public sealed class Heightmap
{
    public Heightmap(int width, int height, MapView view)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        view.Validate();

        Width = width;
        Height = height;
        View = view;
        Elevations = new float[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public MapView View { get; }

    public float[] Elevations { get; }

    public float this[int x, int y]
    {
        get => Elevations[y * Width + x];
        set => Elevations[y * Width + x] = value;
    }

    /// <summary>Latitude of the centre of row <paramref name="y"/>.</summary>
    public double LatitudeAt(int y) => View.NorthLatitude - (y + 0.5) / Height * View.LatitudeSpan;

    /// <summary>Longitude of the centre of column <paramref name="x"/>.</summary>
    public double LongitudeAt(int x) => View.WestLongitude + (x + 0.5) / Width * View.LongitudeSpan;

    /// <summary>Unit vector pointing from the planet centre toward the centre of pixel (x, y).</summary>
    public Vector3 DirectionAt(int x, int y)
    {
        double latitude = MathUtils.ToRadians(LatitudeAt(y));
        double longitude = MathUtils.ToRadians(LongitudeAt(x));
        double cosLatitude = Math.Cos(latitude);

        return new Vector3(
            (float)(cosLatitude * Math.Cos(longitude)),
            (float)Math.Sin(latitude),
            (float)(cosLatitude * Math.Sin(longitude)));
    }

    /// <summary>
    /// Area-weighted statistics over the whole map. Rows are weighted by cos(latitude) so that the unavoidable
    /// oversampling near the poles of an equirectangular grid does not skew the results.
    /// </summary>
    public (float Min, float Max, float Mean, float LandFraction) ComputeStatistics()
    {
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        double weightedSum = 0.0;
        double totalWeight = 0.0;
        double landWeight = 0.0;

        for (int y = 0; y < Height; y++)
        {
            double weight = Math.Cos(MathUtils.ToRadians(LatitudeAt(y)));

            for (int x = 0; x < Width; x++)
            {
                float elevation = this[x, y];
                min = MathF.Min(min, elevation);
                max = MathF.Max(max, elevation);
                weightedSum += elevation * weight;
                totalWeight += weight;
                if (elevation > 0f)
                {
                    landWeight += weight;
                }
            }
        }

        return (min, max, (float)(weightedSum / totalWeight), (float)(landWeight / totalWeight));
    }
}

/// <summary>
/// Samples <see cref="PlanetTerrain"/> into a latitude/longitude grid.
/// </summary>
public static class HeightmapRenderer
{
    public static Heightmap Render(
        PlanetTerrain terrain,
        MapView view,
        int width,
        int height,
        ParallelProgress? progress = null)
    {
        var map = new Heightmap(width, height, view);

        float radius = terrain.Settings.PlanetRadiusMeters;
        double northSouthMetersPerPixel = radius * MathUtils.ToRadians(view.LatitudeSpan) / height;
        double equatorMetersPerPixel = radius * MathUtils.ToRadians(view.LongitudeSpan) / width;

        var cosLon = new float[width];
        var sinLon = new float[width];
        for (int x = 0; x < width; x++)
        {
            double lon = MathUtils.ToRadians(map.LongitudeAt(x));
            cosLon[x] = (float)Math.Cos(lon);
            sinLon[x] = (float)Math.Sin(lon);
        }

        ParallelProgress.ForRange(height, progress, y =>
        {
            double latitude = MathUtils.ToRadians(map.LatitudeAt(y));
            float sinLat = (float)Math.Sin(latitude);
            float cosLat = (float)Math.Cos(latitude);

            // Use the shorter pixel dimension so detail is never sampled above the grid's Nyquist limit.
            double eastWestMetersPerPixel = equatorMetersPerPixel * Math.Cos(latitude);
            float metersPerPixel = (float)Math.Max(100.0, Math.Min(eastWestMetersPerPixel, northSouthMetersPerPixel));
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                var direction = new Vector3(cosLat * cosLon[x], sinLat, cosLat * sinLon[x]);
                map.Elevations[rowOffset + x] = terrain.Elevation(direction, metersPerPixel);
            }
        });

        return map;
    }
}
