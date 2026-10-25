using System;
using System.Collections.Generic;
using System.Numerics;

namespace WorldGen.App.Preview3D;

/// <summary>Interleaved grid line vertices: x, y, z, alpha (four floats per vertex, two per segment).</summary>
public sealed class GridGeometry
{
    public GridGeometry(float[] vertices, int vertexCount)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
    }

    public float[] Vertices { get; }

    public int VertexCount { get; }

    public static GridGeometry Empty { get; } = new(Array.Empty<float>(), 0);
}

/// <summary>
/// Builds faint reference lines at sea level: a metric grid for the regional view and a lat/long
/// graticule for the globe. Land rises above the lines, so the grid is naturally hidden over mountains and
/// visible over water, where it provides a sense of motion.
/// </summary>
public static class PreviewGrid
{
    private const float SurfaceOffsetMeters = 2f;
    private const float MinorAlpha = 0.055f;
    private const float MajorAlpha = 0.145f;
    private const int MaximumVertices = 240_000;

    public static GridGeometry BuildRegional(TerrainScene scene, Vector3 focus, float halfExtent)
    {
        float extent = Math.Clamp(halfExtent * 1.15f, 200f, scene.WorldDiagonal);
        float halfWidth = scene.WorldWidthMeters * 0.5f;
        float halfHeight = scene.WorldHeightMeters * 0.5f;

        float xMin = MathF.Max(-halfWidth, focus.X - extent);
        float xMax = MathF.Min(halfWidth, focus.X + extent);
        float yMin = MathF.Max(-halfHeight, focus.Y - extent);
        float yMax = MathF.Min(halfHeight, focus.Y + extent);

        if (xMax - xMin < 1f || yMax - yMin < 1f)
        {
            return GridGeometry.Empty;
        }

        float major = NiceSpacing(MathF.Max(xMax - xMin, yMax - yMin) / 8f);
        float minor = major / 10f;

        while (EstimatedVertexCount(xMin, xMax, yMin, yMax, minor) > MaximumVertices && minor < 100_000f)
        {
            major *= 2f;
            minor = major / 10f;
        }

        var vertices = new List<float>(8192);

        AddVerticalLines(vertices, xMin, xMax, yMin, yMax, minor, MinorAlpha);
        AddHorizontalLines(vertices, xMin, xMax, yMin, yMax, minor, MinorAlpha);
        AddVerticalLines(vertices, xMin, xMax, yMin, yMax, major, MajorAlpha);
        AddHorizontalLines(vertices, xMin, xMax, yMin, yMax, major, MajorAlpha);

        return new GridGeometry(vertices.ToArray(), vertices.Count / 4);
    }

    public static GridGeometry BuildGlobe(TerrainScene scene, Vector3 cameraPosition, float altitudeAboveSurface)
    {
        float radius = scene.PlanetRadiusMeters;
        float altitude = MathF.Max(altitudeAboveSurface, 5f);
        float visibleDegrees = MathF.Acos(Math.Clamp(radius / (radius + altitude), -1f, 1f)) * 180f / MathF.PI;
        float margin = Math.Clamp(visibleDegrees * 1.15f + 0.1f, 0.15f, 90f);

        Vector3 direction = cameraPosition.LengthSquared() > 1e-6f
            ? Vector3.Normalize(cameraPosition)
            : Vector3.UnitX;

        float centerLatitude = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) * 180f / MathF.PI;
        float centerLongitude = MathF.Atan2(direction.Z, direction.X) * 180f / MathF.PI;

        float latitudeMin = MathF.Max(-90f, centerLatitude - margin);
        float latitudeMax = MathF.Min(90f, centerLatitude + margin);
        float longitudeHalfSpan = margin / MathF.Max(0.05f, MathF.Cos(centerLatitude * MathF.PI / 180f));
        float longitudeMin = centerLongitude - longitudeHalfSpan;
        float longitudeMax = centerLongitude + longitudeHalfSpan;

        if (longitudeMax - longitudeMin >= 360f)
        {
            longitudeMin = -180f;
            longitudeMax = 180f;
        }

        float span = MathF.Max(latitudeMax - latitudeMin, longitudeMax - longitudeMin);
        float major = NiceAngularSpacing(span / 8f);
        float minor = major / 10f;

        while (EstimatedGlobeVertexCount(latitudeMin, latitudeMax, longitudeMin, longitudeMax, minor, major) > MaximumVertices &&
               minor < 10f)
        {
            major *= 2f;
            minor = major / 10f;
        }

        var vertices = new List<float>(16384);
        float sampleStep = MathF.Max(major / 8f, 0.02f);
        float surfaceRadius = radius + SurfaceOffsetMeters;

        // Meridians (constant longitude).
        AddLongitudeLines(
            vertices, latitudeMin, latitudeMax, longitudeMin, longitudeMax,
            minor, major, sampleStep, surfaceRadius);

        // Parallels (constant latitude).
        AddLatitudeLines(
            vertices, latitudeMin, latitudeMax, longitudeMin, longitudeMax,
            minor, major, sampleStep, surfaceRadius);

        return new GridGeometry(vertices.ToArray(), vertices.Count / 4);
    }

    private static void AddVerticalLines(
        List<float> vertices,
        float xMin,
        float xMax,
        float yMin,
        float yMax,
        float spacing,
        float alpha)
    {
        if (spacing <= 0f)
        {
            return;
        }

        int first = (int)MathF.Ceiling(xMin / spacing);
        int last = (int)MathF.Floor(xMax / spacing);

        for (int i = first; i <= last; i++)
        {
            float x = i * spacing;
            AddSegment(vertices, x, yMin, x, yMax, alpha);
        }
    }

    private static void AddHorizontalLines(
        List<float> vertices,
        float xMin,
        float xMax,
        float yMin,
        float yMax,
        float spacing,
        float alpha)
    {
        if (spacing <= 0f)
        {
            return;
        }

        int first = (int)MathF.Ceiling(yMin / spacing);
        int last = (int)MathF.Floor(yMax / spacing);

        for (int i = first; i <= last; i++)
        {
            float y = i * spacing;
            AddSegment(vertices, xMin, y, xMax, y, alpha);
        }
    }

    private static void AddLongitudeLines(
        List<float> vertices,
        float latitudeMin,
        float latitudeMax,
        float longitudeMin,
        float longitudeMax,
        float minor,
        float major,
        float sampleStep,
        float radius)
    {
        int first = (int)MathF.Ceiling(longitudeMin / minor);
        int last = (int)MathF.Floor(longitudeMax / minor);

        for (int i = first; i <= last; i++)
        {
            float longitude = i * minor;
            float alpha = IsMajor(longitude, major) ? MajorAlpha : MinorAlpha;
            AddMeridian(vertices, longitude, latitudeMin, latitudeMax, sampleStep, radius, alpha);
        }
    }

    private static void AddLatitudeLines(
        List<float> vertices,
        float latitudeMin,
        float latitudeMax,
        float longitudeMin,
        float longitudeMax,
        float minor,
        float major,
        float sampleStep,
        float radius)
    {
        int first = (int)MathF.Ceiling(latitudeMin / minor);
        int last = (int)MathF.Floor(latitudeMax / minor);

        for (int i = first; i <= last; i++)
        {
            float latitude = i * minor;
            float alpha = IsMajor(latitude, major) ? MajorAlpha : MinorAlpha;
            AddParallel(vertices, latitude, longitudeMin, longitudeMax, sampleStep, radius, alpha);
        }
    }

    private static void AddMeridian(
        List<float> vertices,
        float longitude,
        float latitudeMin,
        float latitudeMax,
        float sampleStep,
        float radius,
        float alpha)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling((latitudeMax - latitudeMin) / sampleStep));
        Vector3 previous = GlobePoint(latitudeMin, longitude, radius);

        for (int i = 1; i <= steps; i++)
        {
            float latitude = latitudeMin + (latitudeMax - latitudeMin) * i / steps;
            Vector3 current = GlobePoint(latitude, longitude, radius);
            AddSegment(vertices, previous, current, alpha);
            previous = current;
        }
    }

    private static void AddParallel(
        List<float> vertices,
        float latitude,
        float longitudeMin,
        float longitudeMax,
        float sampleStep,
        float radius,
        float alpha)
    {
        float longitudeStep = sampleStep / MathF.Max(0.1f, MathF.Cos(latitude * MathF.PI / 180f));
        int steps = Math.Max(1, (int)MathF.Ceiling((longitudeMax - longitudeMin) / longitudeStep));
        Vector3 previous = GlobePoint(latitude, longitudeMin, radius);

        for (int i = 1; i <= steps; i++)
        {
            float longitude = longitudeMin + (longitudeMax - longitudeMin) * i / steps;
            Vector3 current = GlobePoint(latitude, longitude, radius);
            AddSegment(vertices, previous, current, alpha);
            previous = current;
        }
    }

    private static Vector3 GlobePoint(float latitude, float longitude, float radius)
    {
        float latitudeRadians = latitude * MathF.PI / 180f;
        float longitudeRadians = longitude * MathF.PI / 180f;
        float cosLatitude = MathF.Cos(latitudeRadians);

        return new Vector3(
            cosLatitude * MathF.Cos(longitudeRadians),
            MathF.Sin(latitudeRadians),
            cosLatitude * MathF.Sin(longitudeRadians)) * radius;
    }

    private static bool IsMajor(float value, float major) =>
        MathF.Abs(value / major - MathF.Round(value / major)) < 0.001f;

    private static void AddSegment(List<float> vertices, float x0, float y0, float x1, float y1, float alpha)
    {
        vertices.Add(x0);
        vertices.Add(y0);
        vertices.Add(SurfaceOffsetMeters);
        vertices.Add(alpha);
        vertices.Add(x1);
        vertices.Add(y1);
        vertices.Add(SurfaceOffsetMeters);
        vertices.Add(alpha);
    }

    private static void AddSegment(List<float> vertices, Vector3 from, Vector3 to, float alpha)
    {
        vertices.Add(from.X);
        vertices.Add(from.Y);
        vertices.Add(from.Z);
        vertices.Add(alpha);
        vertices.Add(to.X);
        vertices.Add(to.Y);
        vertices.Add(to.Z);
        vertices.Add(alpha);
    }

    private static int EstimatedVertexCount(float xMin, float xMax, float yMin, float yMax, float minor)
    {
        if (minor <= 0f)
        {
            return int.MaxValue;
        }

        long vertical = (long)((xMax - xMin) / minor) + 1;
        long horizontal = (long)((yMax - yMin) / minor) + 1;
        return (int)Math.Min(int.MaxValue, (vertical + horizontal) * 4);
    }

    private static int EstimatedGlobeVertexCount(
        float latitudeMin,
        float latitudeMax,
        float longitudeMin,
        float longitudeMax,
        float minor,
        float major)
    {
        if (minor <= 0f)
        {
            return int.MaxValue;
        }

        long meridians = (long)((longitudeMax - longitudeMin) / minor) + 1;
        long parallels = (long)((latitudeMax - latitudeMin) / minor) + 1;
        long samplesPerLine = (long)(MathF.Max(8f, 180f / MathF.Max(major, 0.01f)));
        return (int)Math.Min(int.MaxValue, (meridians + parallels) * samplesPerLine * 4);
    }

    private static float NiceSpacing(float raw)
    {
        raw = MathF.Max(raw, 0.001f);
        double exponent = Math.Floor(Math.Log10(raw));
        double power = Math.Pow(10, exponent);
        double normalized = raw / power;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (float)(nice * power);
    }

    private static float NiceAngularSpacing(float raw)
    {
        raw = Math.Clamp(raw, 0.0005f, 30f);
        double exponent = Math.Floor(Math.Log10(raw));
        double power = Math.Pow(10, exponent);
        double normalized = raw / power;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (float)Math.Clamp(nice * power, 0.0005, 30.0);
    }
}
