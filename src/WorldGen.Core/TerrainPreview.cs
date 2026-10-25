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

        Parallel.For(0, map.Height, y =>
        {
            double latitude = map.LatitudeAt(y);
            double eastWestMetersPerPixel = Math.Max(200.0, equatorMetersPerPixel * Math.Cos(MathUtils.ToRadians(latitude)));
            float polar = MathUtils.SmoothStep(62f, 80f, (float)Math.Abs(latitude));

            for (int x = 0; x < map.Width; x++)
            {
                float height = map[x, y];

                Vector3 color = height < 0f ? OceanColor(height, polar) : LandColor(height, latitude, polar);

                int left = (x - 1 + map.Width) % map.Width;
                int right = (x + 1) % map.Width;
                int up = Math.Max(y - 1, 0);
                int down = Math.Min(y + 1, map.Height - 1);

                float dzdx = (map[right, y] - map[left, y]) / (2.0f * (float)eastWestMetersPerPixel);
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
        });

        return pixels;
    }

    /// <summary>
    /// Renders an unshaded albedo image using the same land and ocean palette as <see cref="Render"/>.
    /// A 3D view lights the surface itself, so feeding it the hillshaded preview would shade every
    /// slope twice; this is the colour source for the 3D preview.
    /// </summary>
    public static byte[] RenderAlbedo(Heightmap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var pixels = new byte[map.Width * map.Height * 3];

        Parallel.For(0, map.Height, y =>
        {
            double latitude = map.LatitudeAt(y);
            float polar = MathUtils.SmoothStep(62f, 80f, (float)Math.Abs(latitude));
            int row = y * map.Width;

            for (int x = 0; x < map.Width; x++)
            {
                float height = map[x, y];
                Vector3 color = height < 0f ? OceanColor(height, polar) : LandColor(height, latitude, polar);

                int offset = (row + x) * 3;
                pixels[offset] = ToByte(color.X);
                pixels[offset + 1] = ToByte(color.Y);
                pixels[offset + 2] = ToByte(color.Z);
            }
        });

        return pixels;
    }

    /// <summary>
    /// Renders an erosion diagnostic overlay on top of the hillshaded eroded terrain:
    /// bedrock incision in warm terracotta/crimson, sediment deposition in golden amber, and major drainage trunks in cyan-blue.
    /// </summary>
    public static byte[] RenderErosionOverlay(ErosionResult erosion, float planetRadiusMeters)
    {
        ArgumentNullException.ThrowIfNull(erosion);

        Heightmap map = erosion.Eroded;
        byte[] basePixels = Render(map, planetRadiusMeters);

        Vector3 incisionTint = Rgb(220, 68, 45);
        Vector3 depositionTint = Rgb(235, 190, 55);
        Vector3 drainageTint = Rgb(48, 165, 225);

        int count = map.Width * map.Height;
        for (int i = 0; i < count; i++)
        {
            if (map.Elevations[i] <= 0f && erosion.SedimentThicknessMeters[i] <= 0.01f)
                continue;

            int offset = i * 3;
            var color = new Vector3(
                basePixels[offset] / 255f,
                basePixels[offset + 1] / 255f,
                basePixels[offset + 2] / 255f);

            float inc = Math.Clamp(erosion.ErosionDepthMeters[i] / 220f, 0f, 1f);
            float dep = Math.Clamp(erosion.SedimentThicknessMeters[i] / 80f, 0f, 1f);
            float drain = MathUtils.SmoothStep(15_000f, 180_000f, erosion.DrainageAreaSqKm[i]);

            if (inc > 0.01f)
            {
                color = Vector3.Lerp(color, incisionTint, MathF.Pow(inc, 0.65f) * 0.72f);
            }

            if (dep > 0.01f)
            {
                color = Vector3.Lerp(color, depositionTint, MathF.Pow(dep, 0.60f) * 0.78f);
            }

            if (drain > 0.05f && map.Elevations[i] > 0f)
            {
                color = Vector3.Lerp(color, drainageTint, drain * 0.55f);
            }

            basePixels[offset] = ToByte(color.X);
            basePixels[offset + 1] = ToByte(color.Y);
            basePixels[offset + 2] = ToByte(color.Z);
        }

        return basePixels;
    }

    /// <summary>
    /// Renders a Stage 3 climate &amp; precipitation preview: hillshaded terrain colored by annual precipitation
    /// and aridity (hyper-arid desert ochre/sand → semi-arid olive → humid forest green → wet monsoon/orographic teal).
    /// </summary>
    public static byte[] RenderClimate(ClimateResult climate, float planetRadiusMeters)
    {
        ArgumentNullException.ThrowIfNull(climate);

        Heightmap map = climate.Source;
        byte[] pixels = Render(map, planetRadiusMeters);

        Vector3 hyperArid = Rgb(212, 182, 124);
        Vector3 semiArid = Rgb(168, 162, 92);
        Vector3 subHumid = Rgb(92, 134, 64);
        Vector3 humid = Rgb(42, 108, 62);
        Vector3 perHumid = Rgb(24, 118, 136);

        int count = map.Width * map.Height;
        for (int i = 0; i < count; i++)
        {
            if (map.Elevations[i] <= 0f)
                continue;

            float p = climate.PrecipitationMmPerYear[i];
            Vector3 pColor = p switch
            {
                < 250f => Vector3.Lerp(hyperArid, semiArid, MathUtils.SmoothStep(40f, 250f, p)),
                < 650f => Vector3.Lerp(semiArid, subHumid, MathUtils.SmoothStep(250f, 650f, p)),
                < 1400f => Vector3.Lerp(subHumid, humid, MathUtils.SmoothStep(650f, 1400f, p)),
                _ => Vector3.Lerp(humid, perHumid, MathUtils.SmoothStep(1400f, 2800f, p)),
            };

            BlendPixel(pixels, i, pColor, 0.58f);
        }

        return pixels;
    }

    /// <summary>
    /// Renders a Stage 4 hydrological preview: moisture-tinted hillshaded terrain overlaid with depth-shaded lake basins
    /// and crisp 1-pixel discharge-scaled river courses (HY-3, HY-4, HY-5).
    /// </summary>
    public static byte[] RenderHydrology(HydrologyResult hydrology, float planetRadiusMeters)
    {
        ArgumentNullException.ThrowIfNull(hydrology);

        Heightmap map = hydrology.HydrologicalHeightmap;
        byte[] pixels = Render(map, planetRadiusMeters);

        Vector3 desertTint = Rgb(204, 178, 126);
        Vector3 wetTint = Rgb(46, 102, 56);
        Vector3 shallowLake = Rgb(48, 134, 168);
        Vector3 deepLake = Rgb(18, 66, 114);
        Vector3 riverHeadwater = Rgb(46, 152, 214);
        Vector3 riverTrunk = Rgb(14, 92, 172);

        int width = map.Width;
        int height = map.Height;
        ReadOnlySpan<float> precip = hydrology.Climate.PrecipitationMmPerYear;

        // 1. Subtly tint lowland/mid-elevation terrain by Stage 3 aridity/humidity so deserts and wet zones read clearly.
        for (int y = 0; y < height; y++)
        {
            double latitude = map.LatitudeAt(y);
            float polar = MathUtils.SmoothStep(62f, 80f, (float)Math.Abs(latitude));
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                float h = map.Elevations[i];
                if (h <= 0f || hydrology.LakeId[i] > 0)
                    continue;

                float p = precip[i];
                float elevFade = (1f - MathUtils.SmoothStep(1800f, 3400f, h)) * (1f - polar);
                if (p < 320f && elevFade > 0.01f)
                {
                    float dryT = (1f - MathUtils.SmoothStep(60f, 320f, p)) * 0.48f * elevFade;
                    BlendPixel(pixels, i, desertTint, dryT);
                }
                else if (p > 1100f && elevFade > 0.01f)
                {
                    float wetT = MathUtils.SmoothStep(1100f, 2400f, p) * 0.32f * elevFade;
                    BlendPixel(pixels, i, wetTint, wetT);
                }
            }
        }

        // 2. Render lakes with basin depth shading and polar ice tint.
        for (int y = 0; y < height; y++)
        {
            double latitude = map.LatitudeAt(y);
            float polar = MathUtils.SmoothStep(62f, 80f, (float)Math.Abs(latitude));
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int i = rowOffset + x;
                if (hydrology.LakeId[i] <= 0)
                    continue;

                float depth = Math.Max(0f, map.Elevations[i] - hydrology.Source.Elevations[i]);
                float depthT = Math.Clamp(depth / 85f, 0f, 1f);
                Vector3 lakeColor = Vector3.Lerp(shallowLake, deepLake, MathF.Pow(depthT, 0.6f));
                lakeColor = Vector3.Lerp(lakeColor, SeaIce, polar * 0.85f);

                int offset = i * 3;
                pixels[offset] = ToByte(lakeColor.X);
                pixels[offset + 1] = ToByte(lakeColor.Y);
                pixels[offset + 2] = ToByte(lakeColor.Z);
            }
        }

        // 3. Render river network as crisp 1-pixel channels scaled by log(discharge) and Strahler order.
        float minQ = MathF.Max(10f, hydrology.Settings.MinRiverDischargeM3s);
        for (int i = 0; i < width * height; i++)
        {
            byte order = hydrology.RiverOrder[i];
            if (order == 0 || hydrology.LakeId[i] > 0 || map.Elevations[i] <= 0f)
                continue;

            float q = MathF.Max(minQ, hydrology.DischargeM3s[i]);
            float logScale = Math.Clamp(MathF.Log10(q / minQ) / 2.2f, 0f, 1f);
            float orderScale = Math.Clamp((order - 1f) / 4f, 0f, 1f);
            float strength = Math.Clamp(0.55f * logScale + 0.45f * orderScale, 0f, 1f);

            Vector3 rColor = Vector3.Lerp(riverHeadwater, riverTrunk, strength);
            float alpha = Math.Clamp(0.58f + 0.40f * strength, 0.58f, 0.98f);

            BlendPixel(pixels, i, rColor, alpha);
        }

        return pixels;
    }

    /// <summary>
    /// Renders an unshaded categorical biome-id image from the palette. This is the 2D biome-id map and the
    /// albedo source for a 3D biome view; water and land both come from the same id → colour lookup (BI-2).
    /// </summary>
    public static byte[] RenderBiomeAlbedo(BiomeResult biomes, BiomePalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        palette ??= biomes.Palette;

        Heightmap map = biomes.Elevation;
        var pixels = new byte[map.Width * map.Height * 3];

        Parallel.For(0, map.Height, y =>
        {
            int row = y * map.Width;
            for (int x = 0; x < map.Width; x++)
            {
                int i = row + x;
                Vector3 color = ToVector(palette.ColorOf(biomes.BiomeId[i]));
                int offset = i * 3;
                pixels[offset] = ToByte(color.X);
                pixels[offset + 1] = ToByte(color.Y);
                pixels[offset + 2] = ToByte(color.Z);
            }
        });

        return pixels;
    }

    /// <summary>
    /// Renders a Stage 5 biome preview: biome colours over hillshaded terrain. Water is kept nearly flat and
    /// matte, land is shaded strongly so relief reads through the classification.
    /// </summary>
    public static byte[] RenderBiomes(BiomeResult biomes, float planetRadiusMeters, BiomePalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        palette ??= biomes.Palette;

        Heightmap map = biomes.Elevation;
        byte[] pixels = new byte[map.Width * map.Height * 3];

        double northSouthMetersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height;
        double equatorMetersPerPixel = planetRadiusMeters * MathUtils.ToRadians(map.View.LongitudeSpan) / map.Width;
        Vector3 light = Vector3.Normalize(new Vector3(-0.55f, -0.55f, 0.85f));

        Parallel.For(0, map.Height, y =>
        {
            double latitude = map.LatitudeAt(y);
            double eastWestMetersPerPixel = Math.Max(200.0, equatorMetersPerPixel * Math.Cos(MathUtils.ToRadians(latitude)));
            int row = y * map.Width;

            for (int x = 0; x < map.Width; x++)
            {
                int i = row + x;
                bool water = biomes.BiomeId[i] is >= 1 and <= 4;
                Vector3 color = ToVector(palette.ColorOf(biomes.BiomeId[i]));

                int left = (x - 1 + map.Width) % map.Width;
                int right = (x + 1) % map.Width;
                int up = Math.Max(y - 1, 0);
                int down = Math.Min(y + 1, map.Height - 1);

                float dzdx = (map[right, y] - map[left, y]) / (2.0f * (float)eastWestMetersPerPixel);
                float dzdy = (map[x, down] - map[x, up]) / ((down - up) * (float)northSouthMetersPerPixel);
                Vector3 normal = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
                float lambert = Math.Clamp(Vector3.Dot(normal, light), 0f, 1f);

                float shade = water ? 0.94f + 0.06f * lambert : 0.50f + 0.72f * lambert;
                color *= shade;

                int offset = i * 3;
                pixels[offset] = ToByte(color.X);
                pixels[offset + 1] = ToByte(color.Y);
                pixels[offset + 2] = ToByte(color.Z);
            }
        });

        return pixels;
    }

    private static Vector3 ToVector(BiomeColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

    private static void BlendPixel(byte[] pixels, int cellIndex, Vector3 targetColor, float alpha)
    {
        int offset = cellIndex * 3;
        var existing = new Vector3(
            pixels[offset] / 255f,
            pixels[offset + 1] / 255f,
            pixels[offset + 2] / 255f);
        Vector3 blended = Vector3.Lerp(existing, targetColor, alpha);
        pixels[offset] = ToByte(blended.X);
        pixels[offset + 1] = ToByte(blended.Y);
        pixels[offset + 2] = ToByte(blended.Z);
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
