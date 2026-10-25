using System;
using System.Numerics;

namespace WorldGen;

/// <summary>
/// Builds a displaced UV sphere for the globe preview: a unit sphere with land pushed radially outward by
/// its elevation, textured with the same albedo as the map.
/// </summary>
/// <remarks>
/// Oceans stay at the sea-level sphere. Displacing bathymetry would make the water surface a rugged shell,
/// which reads as terrain rather than water; the ocean floor is still visible through the albedo colour.
/// The seam column is duplicated with <c>u = 1</c>, and the pole rows collapse to a point, which is the
/// standard UV-sphere trade-off: no seam, mild pole pinching.
/// </remarks>
public static class TerrainGlobeMesh
{
    public static TerrainMeshData Build(
        Heightmap source,
        float planetRadiusMeters,
        int longitudeSegments,
        int latitudeSegments,
        float verticalExaggeration)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(planetRadiusMeters, 1f);

        longitudeSegments = Math.Clamp(longitudeSegments, 16, 2048);
        latitudeSegments = Math.Clamp(latitudeSegments, 8, 1024);
        verticalExaggeration = Math.Clamp(verticalExaggeration, 0.05f, 64f);

        int columns = longitudeSegments + 1;
        int rows = latitudeSegments + 1;
        int vertexCount = columns * rows;

        var positions = new float[vertexCount * 3];
        var normals = new float[vertexCount * 3];
        var uvs = new float[vertexCount * 2];
        var directions = new float[vertexCount * 3];

        MapView view = source.View;
        float north = (float)view.NorthLatitude;
        float south = (float)view.SouthLatitude;
        float west = (float)view.WestLongitude;
        float east = (float)view.EastLongitude;

        float minimumElevation = 0f;
        float maximumElevation = 0f;

        Parallel.For(0, rows, j =>
        {
            float v = j / (float)latitudeSegments;
            double latitude = north + (south - north) * v;
            float latitudeRadians = (float)MathUtils.ToRadians(latitude);
            float cosLatitude = MathF.Cos(latitudeRadians);
            float sinLatitude = MathF.Sin(latitudeRadians);
            int row = j * columns;

            for (int i = 0; i < columns; i++)
            {
                float u = i / (float)longitudeSegments;
                double longitude = west + (east - west) * u;
                float longitudeRadians = (float)MathUtils.ToRadians(longitude);
                float cosLongitude = MathF.Cos(longitudeRadians);
                float sinLongitude = MathF.Sin(longitudeRadians);

                float dx = cosLatitude * cosLongitude;
                float dy = sinLatitude;
                float dz = cosLatitude * sinLongitude;

                float elevation = TerrainProjection.SampleBilinear(source, latitude, longitude);
                float landElevation = MathF.Max(0f, elevation);
                float radius = planetRadiusMeters + landElevation * verticalExaggeration;

                int index = (row + i) * 3;
                positions[index] = dx * radius;
                positions[index + 1] = dy * radius;
                positions[index + 2] = dz * radius;
                directions[index] = dx;
                directions[index + 1] = dy;
                directions[index + 2] = dz;

                int uvIndex = (row + i) * 2;
                uvs[uvIndex] = u;
                uvs[uvIndex + 1] = v;
            }
        });

        // Land-only range: the globe clamps water to sea level.
        for (int i = 0; i < vertexCount; i++)
        {
            int index = i * 3;
            float radius = MathF.Sqrt(
                positions[index] * positions[index] +
                positions[index + 1] * positions[index + 1] +
                positions[index + 2] * positions[index + 2]);
            float elevation = (radius - planetRadiusMeters) / verticalExaggeration;
            if (elevation > maximumElevation)
            {
                maximumElevation = elevation;
            }

            if (elevation < minimumElevation)
            {
                minimumElevation = elevation;
            }
        }

        Parallel.For(0, rows, j =>
        {
            int row = j * columns;
            int rowAbove = Math.Max(0, j - 1) * columns;
            int rowBelow = Math.Min(latitudeSegments, j + 1) * columns;

            for (int i = 0; i < columns; i++)
            {
                int columnBefore = (i - 1 + longitudeSegments) % longitudeSegments;
                int columnAfter = (i + 1) % longitudeSegments;
                if (i == longitudeSegments)
                {
                    columnBefore = longitudeSegments - 1;
                    columnAfter = 1;
                }

                int index = (row + i) * 3;

                if (j == 0 || j == latitudeSegments)
                {
                    normals[index] = directions[index];
                    normals[index + 1] = directions[index + 1];
                    normals[index + 2] = directions[index + 2];
                    continue;
                }

                int eastIndex = (row + columnAfter) * 3;
                int westIndex = (row + columnBefore) * 3;
                int northIndex = (rowAbove + i) * 3;
                int southIndex = (rowBelow + i) * 3;

                float eastX = positions[eastIndex] - positions[westIndex];
                float eastY = positions[eastIndex + 1] - positions[westIndex + 1];
                float eastZ = positions[eastIndex + 2] - positions[westIndex + 2];

                float southX = positions[southIndex] - positions[northIndex];
                float southY = positions[southIndex + 1] - positions[northIndex + 1];
                float southZ = positions[southIndex + 2] - positions[northIndex + 2];

                // east x south points away from the centre for this winding.
                float nx = eastY * southZ - eastZ * southY;
                float ny = eastZ * southX - eastX * southZ;
                float nz = eastX * southY - eastY * southX;

                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-9f)
                {
                    nx = directions[index];
                    ny = directions[index + 1];
                    nz = directions[index + 2];
                }
                else
                {
                    nx /= length;
                    ny /= length;
                    nz /= length;
                }

                normals[index] = nx;
                normals[index + 1] = ny;
                normals[index + 2] = nz;
            }
        });

        var indices = new int[longitudeSegments * latitudeSegments * 6];
        int cursor = 0;
        for (int j = 0; j < latitudeSegments; j++)
        {
            int row = j * columns;
            int nextRow = (j + 1) * columns;
            for (int i = 0; i < longitudeSegments; i++)
            {
                int a = row + i;
                int b = row + i + 1;
                int c = nextRow + i;
                int d = nextRow + i + 1;

                // a to b is east, a to c is south; east cross south faces outward.
                indices[cursor++] = a;
                indices[cursor++] = b;
                indices[cursor++] = c;

                indices[cursor++] = b;
                indices[cursor++] = c;
                indices[cursor++] = d;
            }
        }

        return new TerrainMeshData(
            view,
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

