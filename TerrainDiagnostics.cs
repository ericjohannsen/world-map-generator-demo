using System.Numerics;

namespace WorldGen;

/// <summary>
/// Numeric checks that let terrain quality be judged without looking at an image: hypsometry, landmass
/// distribution, and agreement between world-scale and zoomed-in sampling.
/// </summary>
public static class TerrainDiagnostics
{
    private const float HistogramMinMeters = -6_500f;
    private const float HistogramMaxMeters = 9_000f;
    private const float HistogramBinMeters = 50f;

    public static void Print(PlanetTerrain terrain, Heightmap map, float worldMetersPerPixel)
    {
        PrintConsistency(terrain, worldMetersPerPixel);
        PrintHypsometry(map);

        // Whole-planet landmass analysis makes no sense for a cropped view.
        if (map.View == MapView.WholePlanet)
        {
            PrintLandmasses(terrain);
        }
    }

    /// <summary>
    /// The same direction sampled at world-map and zoomed-in resolution must agree on every shared octave.
    /// Any deviation comes from detail that only the finer render can represent.
    /// </summary>
    private static void PrintConsistency(PlanetTerrain terrain, float worldMetersPerPixel)
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

        Console.WriteLine();
        Console.WriteLine($"Consistency ({worldMetersPerPixel / 1000.0:F1} km/px vs {worldMetersPerPixel / 64f:F0} m/px):");
        Console.WriteLine($"  elevation deviation: max {maxDifference:F0} m, RMS {Math.Sqrt(sumSquares / sampleCount):F0} m");
        Console.WriteLine($"  coastline classification changed for {coastFlips * 100.0 / sampleCount:F3}% of samples");
    }

    /// <summary>
    /// Area-weighted elevation distribution, split into land and ocean. Real Earth: land median ~400 m,
    /// 90th percentile ~1,900 m, ocean median ~-4,000 m.
    /// </summary>
    private static void PrintHypsometry(Heightmap map)
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

        Console.WriteLine();
        Console.WriteLine("Hypsometry (area weighted):");
        Console.WriteLine(
            $"  land: p50 {Percentile(landBins, 0.50, HistogramMinMeters, HistogramBinMeters):F0} m, "
            + $"p90 {Percentile(landBins, 0.90, HistogramMinMeters, HistogramBinMeters):F0} m, "
            + $"p99 {Percentile(landBins, 0.99, HistogramMinMeters, HistogramBinMeters):F0} m");
        Console.WriteLine(
            $"  ocean: p50 {Percentile(oceanBins, 0.50, HistogramMinMeters, HistogramBinMeters):F0} m, "
            + $"p90 {Percentile(oceanBins, 0.90, HistogramMinMeters, HistogramBinMeters):F0} m");
        Console.WriteLine($"  surface: {landWeight * 100.0 / totalWeight:F1}% land");
    }

    /// <summary>
    /// Coarse connected-component analysis on a one-degree grid: the view from space is mostly about having a
    /// few large, well-shaped landmasses rather than thousands of specks or one supercontinent.
    /// </summary>
    private static void PrintLandmasses(PlanetTerrain terrain)
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

        Console.WriteLine();
        Console.WriteLine($"Landmasses (1 degree grid): {components.Count} total, {significant} larger than 0.5% of surface");
        for (int i = 0; i < largest.Count; i++)
        {
            var component = largest[i];
            string latitude = $"{Math.Abs(component.Latitude):F0}{(component.Latitude >= 0 ? "N" : "S")}";
            string longitude = $"{Math.Abs(component.Longitude):F0}{(component.Longitude >= 0 ? "E" : "W")}";
            Console.WriteLine($"  {i + 1}. {component.Area * 100.0:F1}% centred near {latitude} {longitude}");
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
