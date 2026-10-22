using System.Numerics;

namespace WorldGen;

/// <summary>
/// Renders a <see cref="Heightmap"/> as an approximate "view from space" colour image with simple hillshading.
/// This is a diagnostic for terrain structure only; biomes belong to a later pipeline stage.
/// </summary>
public static class TerrainPreview
{
    private static readonly Vector3 DeepOcean = Rgb(7, 24, 58);
    private static readonly Vector3 ShallowOcean = Rgb(26, 79, 124);
    private static readonly Vector3 Shelf = Rgb(52, 121, 145);
    private static readonly Vector3 Beach = Rgb(199, 186, 146);
    private static readonly Vector3 Lowland = Rgb(79, 105, 58);
    private static readonly Vector3 Highland = Rgb(88, 94, 52);
    private static readonly Vector3 Rock = Rgb(116, 108, 92);
    private static readonly Vector3 BareRock = Rgb(150, 142, 128);
    private static readonly Vector3 Snow = Rgb(240, 244, 248);
    private static readonly Vector3 SeaIce = Rgb(205, 224, 236);

    public static byte[] Render(Heightmap map, float planetRadiusMeters)
    {
        var pixels = new byte[map.Width * map.Height * 3];

        double northSouthMetersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height;
        double equatorMetersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;

        // Sun from the north-west, fairly high in the sky.
        Vector3 light = Vector3.Normalize(new Vector3(-0.55f, -0.55f, 0.85f));

        for (int y = 0; y < map.Height; y++)
        {
            double latitude = map.LatitudeAt(y);
            double eastWestMetersPerPixel = Math.Max(200.0, equatorMetersPerPixel * Math.Cos(MathUtils.ToRadians(latitude)));
            float polar = MathUtils.SmoothStep(62f, 80f, (float)Math.Abs(latitude));

            for (int x = 0; x < map.Width; x++)
            {
                float height = map[x, y];

                Vector3 color = height < 0f ? OceanColor(height, polar) : LandColor(height, latitude, polar);

                int left = Math.Max(x - 1, 0);
                int right = Math.Min(x + 1, map.Width - 1);
                int up = Math.Max(y - 1, 0);
                int down = Math.Min(y + 1, map.Height - 1);

                float dzdx = (map[right, y] - map[left, y]) / ((right - left) * (float)eastWestMetersPerPixel);
                float dzdy = (map[x, down] - map[x, up]) / ((down - up) * (float)northSouthMetersPerPixel);

                Vector3 normal = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
                float lambert = Math.Clamp(Vector3.Dot(normal, light), 0f, 1f);

                // Shade land strongly so ridges read; keep the ocean nearly flat and matte.
                float shade = height < 0f ? 0.93f + 0.07f * lambert : 0.45f + 0.75f * lambert;
                color *= shade;

                int offset = (y * map.Width + x) * 3;
                pixels[offset] = ToByte(color.X);
                pixels[offset + 1] = ToByte(color.Y);
                pixels[offset + 2] = ToByte(color.Z);
            }
        }

        return pixels;
    }

    private static Vector3 OceanColor(float height, float polar)
    {
        float depth = Math.Clamp(-height / 5_400f, 0f, 1f);
        Vector3 color = Vector3.Lerp(Shelf, ShallowOcean, MathUtils.SmoothStep(0f, 0.12f, depth));
        color = Vector3.Lerp(color, DeepOcean, MathF.Pow(depth, 0.55f));

        return Vector3.Lerp(color, SeaIce, polar * 0.9f);
    }

    private static Vector3 LandColor(float height, double latitude, float polar)
    {
        Vector3 color = height switch
        {
            < 20f => Beach,
            < 250f => Vector3.Lerp(Beach, Lowland, MathUtils.SmoothStep(0f, 250f, height)),
            < 1_100f => Vector3.Lerp(Lowland, Highland, MathUtils.SmoothStep(250f, 1_100f, height)),
            < 2_200f => Vector3.Lerp(Highland, Rock, MathUtils.SmoothStep(1_100f, 2_200f, height)),
            _ => Vector3.Lerp(Rock, BareRock, MathUtils.SmoothStep(2_200f, 3_600f, height)),
        };

        // Snow line falls with latitude; poles end up ice-covered at sea level.
        float snowLine = 5_200f * MathF.Pow(MathF.Max(0f, MathF.Cos((float)MathUtils.ToRadians(latitude))), 1.25f);
        color = Vector3.Lerp(color, Snow, MathUtils.SmoothStep(snowLine, snowLine + 600f, height));
        color = Vector3.Lerp(color, Snow, polar * 0.85f);

        return color;
    }

    private static Vector3 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    private static byte ToByte(float channel) => (byte)Math.Clamp(MathF.Round(channel * 255f), 0f, 255f);
}
