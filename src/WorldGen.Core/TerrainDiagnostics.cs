using System.Numerics;

namespace WorldGen;

/// <summary>
/// Numeric checks that let terrain quality be judged without looking at an image: hypsometry, landmass
/// distribution, and agreement between world-scale and zoomed-in sampling.
/// </summary>
public static class TerrainDiagnostics
{
    private const float HistogramMinMeters = -8_000f;
    private const float HistogramMaxMeters = 9_000f;
    private const float HistogramBinMeters = 50f;

    public static void Print(PlanetTerrain terrain, Heightmap map, float worldMetersPerPixel)
        => Print(terrain, map, worldMetersPerPixel, Console.Out);

    public static string Format(
        PlanetTerrain terrain,
        Heightmap map,
        float worldMetersPerPixel,
        ErosionResult? erosion = null,
        ClimateResult? climate = null,
        HydrologyResult? hydrology = null)
    {
        using var writer = new StringWriter();
        Print(terrain, map, worldMetersPerPixel, writer, erosion, climate, hydrology);
        return writer.ToString().Trim();
    }

    public static void Print(
        PlanetTerrain terrain,
        Heightmap map,
        float worldMetersPerPixel,
        TextWriter output,
        ErosionResult? erosion = null,
        ClimateResult? climate = null,
        HydrologyResult? hydrology = null)
    {
        PrintConsistency(terrain, worldMetersPerPixel, output);
        PrintHypsometry(map, output);
        if (erosion is not null)
        {
            PrintErosion(erosion, output);
        }
        ClimateResult? effectiveClimate = climate ?? hydrology?.Climate;
        if (effectiveClimate is not null)
        {
            PrintClimate(effectiveClimate, output);
        }
        if (hydrology is not null)
        {
            PrintHydrology(hydrology, output);
        }

        // Whole-planet landmass analysis makes no sense for a cropped view.
        if (map.View == MapView.WholePlanet)
        {
            PrintVariety(terrain, output);
            PrintLandmasses(terrain, output);
        }
    }

    public static void PrintClimate(ClimateResult climate, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"Climate & Rainfall (Stage 3, {climate.ElapsedSeconds:F2}s):");
        output.WriteLine($"  temperature:   land mean {climate.MeanLandTemperatureCelsius:F1} °C (equator {climate.Settings.EquatorialTempCelsius:F0} °C, poles {climate.Settings.PolarTempCelsius:F0} °C, lapse {climate.Settings.LapseRateCPerKm:F1} °C/km)");
        output.WriteLine($"  precipitation: land mean {climate.MeanLandPrecipitationMm:F0} mm/yr, max {climate.MaxLandPrecipitationMm:F0} mm/yr, mean runoff {climate.MeanLandRunoffMm:F0} mm/yr");
        output.WriteLine($"  moisture zones: arid (<250 mm/yr) {climate.AridLandFraction * 100.0:F1}% of land, humid (≥1000 mm/yr) {climate.HumidLandFraction * 100.0:F1}% of land");
    }

    public static void PrintHydrology(HydrologyResult hydrology, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"Hydrology (Stage 4, {hydrology.ElapsedSeconds:F2}s):");
        output.WriteLine(
            $"  lakes:  {hydrology.Lakes.Count} basins ({hydrology.TotalLakeAreaSqKm:N0} km² total), "
            + $"{hydrology.BreachedDepressionCount} minor/arid depressions breached");
        output.WriteLine(
            $"  rivers: {hydrology.Rivers.Count} reaches ({hydrology.TotalRiverLengthKm:N0} km total length, "
            + $"{hydrology.RiverCellCount} channel cells, max Strahler order {hydrology.MaxStrahlerOrder})");

        var largestLakes = hydrology.Lakes.OrderByDescending(l => l.AreaSqKm).Take(5).ToList();
        for (int i = 0; i < largestLakes.Count; i++)
        {
            LakeFeature lake = largestLakes[i];
            string lat = $"{Math.Abs(lake.CentroidLatitude):F1}{(lake.CentroidLatitude >= 0 ? "N" : "S")}";
            string lon = $"{Math.Abs(lake.CentroidLongitude):F1}{(lake.CentroidLongitude >= 0 ? "E" : "W")}";
            output.WriteLine(
                $"    lake #{lake.Id}: {lake.AreaSqKm:N0} km² ({lake.CellCount} px), surface {lake.SurfaceElevationMeters:F0} m, "
                + $"max depth {lake.MaxDepthMeters:F0} m near {lat} {lon}");
        }
    }

    public static void PrintErosion(ErosionResult erosion, TextWriter output)
    {
        double maxDrainage = 0.0;
        int activeValleys = 0;
        int landCells = 0;

        for (int i = 0; i < erosion.Eroded.Elevations.Length; i++)
        {
            if (erosion.DrainageAreaSqKm[i] > maxDrainage)
            {
                maxDrainage = erosion.DrainageAreaSqKm[i];
            }

            if (erosion.Eroded.Elevations[i] > 0f)
            {
                landCells++;
                if (erosion.ErosionDepthMeters[i] >= 5.0f)
                {
                    activeValleys++;
                }
            }
        }

        double valleyShare = landCells > 0 ? activeValleys * 100.0 / landCells : 0.0;

        output.WriteLine();
        output.WriteLine($"Erosion (Stage 2, {erosion.Settings.Iterations} iterations, {erosion.ElapsedSeconds:F2}s):");
        output.WriteLine($"  incision:   mean {erosion.MeanErosionMeters:F1} m, max {erosion.MaxErosionMeters:F0} m ({valleyShare:F1}% of land incised ≥5 m)");
        output.WriteLine($"  deposition: mean {erosion.MeanDepositionMeters:F1} m, max {erosion.MaxDepositionMeters:F0} m");
        output.WriteLine($"  drainage:   max catchment {maxDrainage:N0} km², coastal delta cells prograded: {erosion.CoastlineProgradedCells}");
    }

    /// <summary>
    /// The same direction sampled at world-map and zoomed-in resolution must agree on every shared octave.
    /// Any deviation comes from detail that only the finer render can represent.
    /// </summary>
    private static void PrintConsistency(PlanetTerrain terrain, float worldMetersPerPixel, TextWriter output)
    {
        const int sampleCount = 20_000;
        double sumSquares = 0.0;
        double maxDifference = 0.0;
        int coastFlips = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            Vector3 direction = MathUtils.FibonacciSpherePoint(i, sampleCount);
            float coarse = terrain.Elevation(direction, worldMetersPerPixel);
            float fine = terrain.Elevation(direction, worldMetersPerPixel / 64f);

            double difference = Math.Abs(coarse - fine);
            sumSquares += difference * difference;
            maxDifference = Math.Max(maxDifference, difference);
            if (coarse > 0f != fine > 0f)
            {
                coastFlips++;
            }
        }

        output.WriteLine();
        output.WriteLine($"Consistency ({worldMetersPerPixel / 1000.0:F1} km/px vs {worldMetersPerPixel / 64f:F0} m/px):");
        output.WriteLine($"  elevation deviation: max {maxDifference:F0} m, RMS {Math.Sqrt(sumSquares / sampleCount):F0} m");
        output.WriteLine($"  coastline classification changed for {coastFlips * 100.0 / sampleCount:F3}% of samples");
    }

    /// <summary>
    /// Area-weighted elevation distribution, split into land and ocean. Real Earth: land median ~400 m,
    /// 90th percentile ~1,900 m, ocean median ~-4,000 m.
    /// </summary>
    private static void PrintHypsometry(Heightmap map, TextWriter output)
    {
        int binCount = (int)((HistogramMaxMeters - HistogramMinMeters) / HistogramBinMeters) + 1;
        var landBins = new double[binCount];
        var oceanBins = new double[binCount];
        double totalWeight = 0.0;

        for (int y = 0; y < map.Height; y++)
        {
            double weight = Math.Cos(MathUtils.ToRadians(map.LatitudeAt(y)));

            for (int x = 0; x < map.Width; x++)
            {
                float elevation = map[x, y];
                int bin = Math.Clamp((int)((elevation - HistogramMinMeters) / HistogramBinMeters), 0, binCount - 1);

                if (elevation > 0f)
                {
                    landBins[bin] += weight;
                }
                else
                {
                    oceanBins[bin] += weight;
                }

                totalWeight += weight;
            }
        }

        double landWeight = landBins.Sum();
        double oceanWeight = oceanBins.Sum();

        output.WriteLine();
        output.WriteLine("Hypsometry (area weighted):");
        output.WriteLine(
            $"  land: p50 {Percentile(landBins, 0.50, HistogramMinMeters, HistogramBinMeters):F0} m, "
            + $"p90 {Percentile(landBins, 0.90, HistogramMinMeters, HistogramBinMeters):F0} m, "
            + $"p99 {Percentile(landBins, 0.99, HistogramMinMeters, HistogramBinMeters):F0} m");
        output.WriteLine(
            $"  ocean: p50 {Percentile(oceanBins, 0.50, HistogramMinMeters, HistogramBinMeters):F0} m, "
            + $"p90 {Percentile(oceanBins, 0.90, HistogramMinMeters, HistogramBinMeters):F0} m");
        output.WriteLine($"  surface: {landWeight * 100.0 / totalWeight:F1}% land");
    }

    /// <summary>
    /// Counts the structural ingredients that make one part of the world look unlike another: province
    /// distribution, how much land is near a plate boundary, and which tectonic masks are actually active.
    /// A world with every province at the same frequency, or with almost no boundary influence, is too uniform.
    /// </summary>
    private static void PrintVariety(PlanetTerrain terrain, TextWriter output)
    {
        const int sampleCount = 20_000;
        var landProvinceCounts = new Dictionary<ProvinceKind, int>();
        int landSamples = 0;
        int boundarySamples = 0;
        double orogeny = 0.0;
        double arc = 0.0;
        double rift = 0.0;
        double trench = 0.0;
        double oceanRidge = 0.0;
        double transform = 0.0;

        for (int i = 0; i < sampleCount; i++)
        {
            Vector3 direction = MathUtils.FibonacciSpherePoint(i, sampleCount);
            float elevation = terrain.Elevation(direction, 111_000f);
            TectonicSample sample = terrain.Tectonics.Sample(direction);

            if (sample.Influence > 0.5f)
            {
                boundarySamples++;
            }

            orogeny += sample.Orogeny;
            arc += sample.Arc;
            rift += sample.Rift;
            trench += sample.Trench;
            oceanRidge += sample.OceanRidge;
            transform += sample.Transform;

            if (elevation > 0f)
            {
                landSamples++;
                ProvinceKind kind = terrain.Provinces.DominantKind(direction);
                landProvinceCounts[kind] = landProvinceCounts.GetValueOrDefault(kind) + 1;
            }
        }

        output.WriteLine();
        output.WriteLine(
            $"Variety: {terrain.Tectonics.PlateCount} plates, {terrain.Tectonics.Hotspots.Count} hotspots, "
            + $"{terrain.Provinces.Count} provinces, {terrain.Landmarks.Count} landmarks");
        output.WriteLine(
            $"  boundary influence >0.5: {boundarySamples * 100.0 / sampleCount:F1}% of surface; "
            + $"mean orogeny {orogeny / sampleCount:F3}, arc {arc / sampleCount:F3}, "
            + $"rift {rift / sampleCount:F3}");
        output.WriteLine(
            $"  mean trench {trench / sampleCount:F3}, ocean ridge {oceanRidge / sampleCount:F3}, "
            + $"transform {transform / sampleCount:F3}");
        output.WriteLine(
            "  landmarks: "
            + $"volcano {terrain.Landmarks.CountOf(LandmarkKind.Volcano)}, "
            + $"caldera {terrain.Landmarks.CountOf(LandmarkKind.Caldera)}, "
            + $"massif {terrain.Landmarks.CountOf(LandmarkKind.Massif)}, "
            + $"crater {terrain.Landmarks.CountOf(LandmarkKind.Crater)}, "
            + $"seamount {terrain.Landmarks.CountOf(LandmarkKind.Seamount)}");

        if (landSamples > 0)
        {
            string provinces = string.Join(
                ", ",
                landProvinceCounts
                    .OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key} {pair.Value * 100.0 / landSamples:F0}%"));
            output.WriteLine($"  land provinces: {provinces}");
        }
    }

    /// <summary>
    /// Coarse connected-component analysis on a one-degree grid: the view from space is mostly about having a
    /// few large, well-shaped landmasses rather than thousands of specks or one supercontinent.
    /// </summary>
    private static void PrintLandmasses(PlanetTerrain terrain, TextWriter output)
    {
        const int columns = 360;
        const int rows = 180;
        var land = new bool[columns * rows];
        var rowWeights = new double[rows];

        for (int row = 0; row < rows; row++)
        {
            double latitude = 89.5 - row;
            rowWeights[row] = Math.Cos(MathUtils.ToRadians(latitude));
            double longitudeOffset = -179.5;

            for (int column = 0; column < columns; column++)
            {
                double longitude = longitudeOffset + column;
                Vector3 direction = Direction(latitude, longitude);
                land[row * columns + column] = terrain.Elevation(direction, 111_000f) > 0f;
            }
        }

        var visited = new bool[land.Length];
        var components = new List<(double Area, double Latitude, double Longitude)>();
        var stack = new Stack<int>();
        double totalWeight = rowWeights.Sum() * columns;

        for (int start = 0; start < land.Length; start++)
        {
            if (visited[start] || !land[start])
            {
                continue;
            }

            double area = 0.0;
            Vector3 moment = Vector3.Zero;
            stack.Push(start);
            visited[start] = true;

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int row = index / columns;
                int column = index % columns;
                double weight = rowWeights[row];

                area += weight;
                moment += Direction(89.5 - row, -179.5 + column) * (float)weight;

                Visit(row, (column + 1) % columns);
                Visit(row, (column - 1 + columns) % columns);
                if (row > 0)
                {
                    Visit(row - 1, column);
                }

                if (row < rows - 1)
                {
                    Visit(row + 1, column);
                }
            }

            if (moment.LengthSquared() > 1e-6f)
            {
                moment = Vector3.Normalize(moment);
                double latitude = MathUtils.ToDegrees(Math.Asin(moment.Y));
                double longitude = MathUtils.ToDegrees(Math.Atan2(moment.Z, moment.X));
                components.Add((area / totalWeight, latitude, longitude));
            }
        }

        var largest = components.OrderByDescending(component => component.Area).Take(5).ToList();
        int significant = components.Count(component => component.Area > 0.005);

        output.WriteLine();
        output.WriteLine($"Landmasses (1 degree grid): {components.Count} total, {significant} larger than 0.5% of surface");
        for (int i = 0; i < largest.Count; i++)
        {
            var component = largest[i];
            string latitude = $"{Math.Abs(component.Latitude):F0}{(component.Latitude >= 0 ? "N" : "S")}";
            string longitude = $"{Math.Abs(component.Longitude):F0}{(component.Longitude >= 0 ? "E" : "W")}";
            output.WriteLine($"  {i + 1}. {component.Area * 100.0:F1}% centred near {latitude} {longitude}");
        }

        return;

        void Visit(int row, int column)
        {
            int next = row * columns + column;
            if (!visited[next] && land[next])
            {
                visited[next] = true;
                stack.Push(next);
            }
        }
    }

    private static Vector3 Direction(double latitudeDegrees, double longitudeDegrees)
    {
        double latitude = MathUtils.ToRadians(latitudeDegrees);
        double longitude = MathUtils.ToRadians(longitudeDegrees);
        double cosLatitude = Math.Cos(latitude);

        return new Vector3(
            (float)(cosLatitude * Math.Cos(longitude)),
            (float)Math.Sin(latitude),
            (float)(cosLatitude * Math.Sin(longitude)));
    }

    private static double Percentile(double[] bins, double fraction, float origin, float binSize)
    {
        double total = bins.Sum();
        if (total <= 0.0)
        {
            return double.NaN;
        }

        double target = total * fraction;
        double cumulative = 0.0;

        for (int i = 0; i < bins.Length; i++)
        {
            cumulative += bins[i];
            if (cumulative >= target)
            {
                return origin + (i + 0.5) * binSize;
            }
        }

        return origin + bins.Length * binSize;
    }
}
