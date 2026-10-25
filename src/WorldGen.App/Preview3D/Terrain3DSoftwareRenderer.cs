using System;
using System.Numerics;
using WorldGen;

namespace WorldGen.App.Preview3D;

/// <summary>
/// Software z-buffered rasteriser of the same mesh, camera, and albedo as the GPU path.
/// </summary>
/// <remarks>
/// This exists so the preview still works when Avalonia cannot create an OpenGL context, and so there is
/// an implementation to compare the GPU image against. It is deliberately simple: whole-triangle near
/// rejection, perspective-correct UVs, one directional light, distance fog. It is not a competitor to the
/// GPU path and should not grow effects the GPU path does not have.
/// </remarks>
public sealed class Terrain3DSoftwareRenderer
{
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.55f, -0.38f, 0.74f));
    private static readonly Vector3 SkyColor = new(0.045f, 0.062f, 0.085f);
    private static readonly Vector3 SpaceColor = new(0.012f, 0.016f, 0.028f);

    private float[] _screenX = Array.Empty<float>();
    private float[] _screenY = Array.Empty<float>();
    private float[] _inverseW = Array.Empty<float>();
    private float[] _uvU = Array.Empty<float>();
    private float[] _uvV = Array.Empty<float>();
    private float[] _normalX = Array.Empty<float>();
    private float[] _normalY = Array.Empty<float>();
    private float[] _normalZ = Array.Empty<float>();
    private float[] _depth = Array.Empty<float>();

    public void Render(
        TerrainScene scene,
        IPreviewCamera camera,
        byte[] targetBgra,
        int width,
        int height,
        TerrainMeshData? mesh = null,
        bool globe = false,
        GridGeometry? grid = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(targetBgra);

        Vector3 background = globe ? SpaceColor : SkyColor;
        FillSky(targetBgra, width, height, background);
        EnsureDepth(width * height);

        mesh ??= scene.CpuMeshes.Get(camera);
        if (mesh is null)
        {
            return;
        }

        float aspect = width / (float)height;
        Matrix4x4 viewProjection = camera.GetViewMatrix(scene.WorldDiagonal) *
                                   camera.GetProjectionMatrix(aspect, scene.WorldDiagonal);

        int vertexCount = mesh.VertexCount;
        EnsureVertices(vertexCount);
        TransformVertices(mesh, viewProjection, width, height);

        float fogScale = globe
            ? 0f
            : 1f / MathF.Max(500f, camera.FogDistance * 4.5f + scene.WorldDiagonal * 0.02f);
        RasteriseTriangles(scene, mesh, width, height, targetBgra, fogScale, background);

        if (grid is { VertexCount: > 1 })
        {
            RasteriseGrid(grid, viewProjection, targetBgra, width, height);
        }
    }

    private void RasteriseGrid(
        GridGeometry grid,
        Matrix4x4 viewProjection,
        byte[] target,
        int width,
        int height)
    {
        float[] vertices = grid.Vertices;
        int vertexCount = grid.VertexCount;

        for (int i = 0; i + 1 < vertexCount; i += 2)
        {
            int o0 = i * 4;
            int o1 = o0 + 4;

            Vector4 c0 = Vector4.Transform(
                new Vector4(vertices[o0], vertices[o0 + 1], vertices[o0 + 2], 1f), viewProjection);
            Vector4 c1 = Vector4.Transform(
                new Vector4(vertices[o1], vertices[o1 + 1], vertices[o1 + 2], 1f), viewProjection);

            if (c0.W <= 1e-4f || c1.W <= 1e-4f)
            {
                continue;
            }

            float iw0 = 1f / c0.W;
            float iw1 = 1f / c1.W;

            float x0 = (c0.X * iw0 * 0.5f + 0.5f) * width;
            float y0 = (0.5f - c0.Y * iw0 * 0.5f) * height;
            float x1 = (c1.X * iw1 * 0.5f + 0.5f) * width;
            float y1 = (0.5f - c1.Y * iw1 * 0.5f) * height;

            int steps = (int)MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
            if (steps <= 0)
            {
                continue;
            }

            steps = Math.Min(steps, 2048);
            float a0 = vertices[o0 + 3];
            float a1 = vertices[o1 + 3];

            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                int px = (int)(x0 + (x1 - x0) * t);
                int py = (int)(y0 + (y1 - y0) * t);

                if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                {
                    continue;
                }

                int index = py * width + px;
                float invW = iw0 + (iw1 - iw0) * t;
                if (invW * 1.0005f <= _depth[index])
                {
                    continue;
                }

                float alpha = a0 + (a1 - a0) * t;
                if (alpha <= 0.001f)
                {
                    continue;
                }

                int pixel = index * 4;
                target[pixel] = Blend(target[pixel], 255, alpha);
                target[pixel + 1] = Blend(target[pixel + 1], 199, alpha);
                target[pixel + 2] = Blend(target[pixel + 2], 140, alpha);
            }
        }
    }

    private static byte Blend(byte existing, byte target, float alpha) =>
        (byte)Math.Clamp(existing + (target - existing) * alpha, 0f, 255f);

    private void EnsureDepth(int count)
    {
        if (_depth.Length != count)
        {
            _depth = new float[count];
        }
        else
        {
            Array.Clear(_depth);
        }
    }

    private void EnsureVertices(int count)
    {
        if (_screenX.Length >= count)
        {
            return;
        }

        _screenX = new float[count];
        _screenY = new float[count];
        _inverseW = new float[count];
        _uvU = new float[count];
        _uvV = new float[count];
        _normalX = new float[count];
        _normalY = new float[count];
        _normalZ = new float[count];
    }

    private void TransformVertices(TerrainMeshData mesh, Matrix4x4 viewProjection, int width, int height)
    {
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            int p = i * 3;
            var position = new Vector4(
                mesh.Positions[p],
                mesh.Positions[p + 1],
                mesh.Positions[p + 2],
                1f);

            Vector4 clip = Vector4.Transform(position, viewProjection);
            float inverseW = clip.W > 1e-4f ? 1f / clip.W : 0f;
            _inverseW[i] = inverseW;

            if (inverseW > 0f)
            {
                _screenX[i] = (clip.X * inverseW * 0.5f + 0.5f) * width;
                _screenY[i] = (0.5f - clip.Y * inverseW * 0.5f) * height;
            }

            int uv = i * 2;
            _uvU[i] = mesh.Uvs[uv];
            _uvV[i] = mesh.Uvs[uv + 1];
            _normalX[i] = mesh.Normals[p];
            _normalY[i] = mesh.Normals[p + 1];
            _normalZ[i] = mesh.Normals[p + 2];
        }

    }

    private void RasteriseTriangles(
        TerrainScene scene,
        TerrainMeshData mesh,
        int width,
        int height,
        byte[] targetBgra,
        float fogScale,
        Vector3 background)
    {
        byte[] albedo = scene.AlbedoRgb;
        int mapWidth = scene.Map.Width;
        int mapHeight = scene.Map.Height;

        int[] indices = mesh.Indices;
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            int ia = indices[triangle];
            int ib = indices[triangle + 1];
            int ic = indices[triangle + 2];

            float wA = _inverseW[ia];
            float wB = _inverseW[ib];
            float wC = _inverseW[ic];
            if (wA <= 0f || wB <= 0f || wC <= 0f)
            {
                continue;
            }

            float ax = _screenX[ia];
            float ay = _screenY[ia];
            float bx = _screenX[ib];
            float by = _screenY[ib];
            float cx = _screenX[ic];
            float cy = _screenY[ic];

            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (MathF.Abs(area) < 1e-4f)
            {
                continue;
            }

            int minX = (int)MathF.Max(0f, MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            int maxX = (int)MathF.Min(width - 1, MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int minY = (int)MathF.Max(0f, MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            int maxY = (int)MathF.Min(height - 1, MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            if (minX > maxX || minY > maxY)
            {
                continue;
            }

            float inverseArea = 1f / area;

            float uA = _uvU[ia];
            float uB = _uvU[ib];
            float uC = _uvU[ic];
            float vA = _uvV[ia];
            float vB = _uvV[ib];
            float vC = _uvV[ic];

            float nax = _normalX[ia];
            float nay = _normalY[ia];
            float naz = _normalZ[ia];
            float nbx = _normalX[ib];
            float nby = _normalY[ib];
            float nbz = _normalZ[ib];
            float ncx = _normalX[ic];
            float ncy = _normalY[ic];
            float ncz = _normalZ[ic];

            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                int rowOffset = y * width;

                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;

                    float e0 = ((cx - bx) * (py - by) - (cy - by) * (px - bx)) * inverseArea;
                    float e1 = ((ax - cx) * (py - cy) - (ay - cy) * (px - cx)) * inverseArea;
                    float e2 = ((bx - ax) * (py - ay) - (by - ay) * (px - ax)) * inverseArea;

                    if (e0 < 0f || e1 < 0f || e2 < 0f)
                    {
                        continue;
                    }

                    float inverseW = wA * e0 + wB * e1 + wC * e2;
                    int depthIndex = rowOffset + x;
                    if (inverseW <= _depth[depthIndex])
                    {
                        continue;
                    }

                    float u = (uA * wA * e0 + uB * wB * e1 + uC * wC * e2) / inverseW;
                    float v = (vA * wA * e0 + vB * wB * e1 + vC * wC * e2) / inverseW;
                    u -= MathF.Floor(u);
                    v = Math.Clamp(v, 0f, 1f);

                    float nx = nax * e0 + nbx * e1 + ncx * e2;
                    float ny = nay * e0 + nby * e1 + ncy * e2;
                    float nz = naz * e0 + nbz * e1 + ncz * e2;
                    float normalLength = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (normalLength < 1e-6f)
                    {
                        continue;
                    }

                    nx /= normalLength;
                    ny /= normalLength;
                    nz /= normalLength;

                    SampleAlbedo(albedo, mapWidth, mapHeight, u, v, out byte red, out byte green, out byte blue);

                    float diffuse = MathF.Max(0f, nx * SunDirection.X + ny * SunDirection.Y + nz * SunDirection.Z);
                    float shade = 0.38f + 0.78f * diffuse;

                    float colorRed = red / 255f * shade;
                    float colorGreen = green / 255f * shade;
                    float colorBlue = blue / 255f * shade;

                    float fog = Math.Clamp((1f / inverseW) * fogScale, 0f, 1f);
                    fog *= fog * 0.92f;
                    colorRed = colorRed + (background.X - colorRed) * fog;
                    colorGreen = colorGreen + (background.Y - colorGreen) * fog;
                    colorBlue = colorBlue + (background.Z - colorBlue) * fog;

                    int pixel = depthIndex * 4;
                    targetBgra[pixel] = ToByte(colorBlue);
                    targetBgra[pixel + 1] = ToByte(colorGreen);
                    targetBgra[pixel + 2] = ToByte(colorRed);
                    targetBgra[pixel + 3] = 255;

                    _depth[depthIndex] = inverseW;
                }
            }
        }
    }

    private static void SampleAlbedo(
        byte[] albedo,
        int mapWidth,
        int mapHeight,
        float u,
        float v,
        out byte red,
        out byte green,
        out byte blue)
    {
        float fx = u * mapWidth - 0.5f;
        float fy = v * mapHeight - 0.5f;

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        int x1 = x0 + 1;
        x0 = TerrainProjection.Wrap(x0, mapWidth);
        x1 = TerrainProjection.Wrap(x1, mapWidth);
        int yc0 = Math.Clamp(y0, 0, mapHeight - 1);
        int yc1 = Math.Clamp(y0 + 1, 0, mapHeight - 1);

        int i00 = (yc0 * mapWidth + x0) * 3;
        int i10 = (yc0 * mapWidth + x1) * 3;
        int i01 = (yc1 * mapWidth + x0) * 3;
        int i11 = (yc1 * mapWidth + x1) * 3;

        red = Interpolate(albedo[i00], albedo[i10], albedo[i01], albedo[i11], tx, ty);
        green = Interpolate(albedo[i00 + 1], albedo[i10 + 1], albedo[i01 + 1], albedo[i11 + 1], tx, ty);
        blue = Interpolate(albedo[i00 + 2], albedo[i10 + 2], albedo[i01 + 2], albedo[i11 + 2], tx, ty);
    }

    private static byte Interpolate(byte v00, byte v10, byte v01, byte v11, float tx, float ty)
    {
        float top = v00 + (v10 - v00) * tx;
        float bottom = v01 + (v11 - v01) * tx;
        return ToByte((top + (bottom - top) * ty) / 255f);
    }

    private static void FillSky(byte[] targetBgra, int width, int height, Vector3 background)
    {
        byte blue = ToByte(background.Z);
        byte green = ToByte(background.Y);
        byte red = ToByte(background.X);

        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x * 4;
                targetBgra[pixel] = blue;
                targetBgra[pixel + 1] = green;
                targetBgra[pixel + 2] = red;
                targetBgra[pixel + 3] = 255;
            }
        }
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}

