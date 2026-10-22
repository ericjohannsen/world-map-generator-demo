using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using WorldGen;

// Fixed 16-bit encoding range, in meters. Shared by every tile so overlapping renders agree.
const float HeightmapMinMeters = -6_500f;
const float HeightmapMaxMeters = 9_000f;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 1;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

var terrain = new PlanetTerrain(new TerrainSettings { Seed = options.Seed });

var stopwatch = Stopwatch.StartNew();
Heightmap map = HeightmapRenderer.Render(terrain, options.View, options.Width, options.Height);
var (min, max, mean, landFraction) = map.ComputeStatistics();
stopwatch.Stop();

float radius = terrain.Settings.PlanetRadiusMeters;
double metersPerPixel = radius * MathUtils.ToRadians(options.View.LongitudeSpan) / options.Width;

string heightPngPath = Path.GetFullPath(options.Output + "-height.png");
string previewPath = Path.GetFullPath(options.Output + "-preview.png");
string tiffPath = Path.GetFullPath(options.Output + "-height.tif");
string manifestPath = Path.GetFullPath(options.Output + "-manifest.json");

SaveGeoTiff(map, radius, tiffPath);
SaveHeightmap(map, heightPngPath);
SavePreview(map, radius, previewPath);
SaveManifest(map, terrain, options, radius, metersPerPixel, tiffPath, heightPngPath, previewPath, manifestPath);

Console.WriteLine($"{map.Width}x{map.Height} equirectangular map, {metersPerPixel / 1000.0:F1} km/px at the equator ({stopwatch.Elapsed.TotalSeconds:F1}s)");
Console.WriteLine($"Land {landFraction * 100.0:F1}% of surface, elevation {min:F0} .. {max:F0} m (mean {mean:F0} m)");
Console.WriteLine($"Heightmap (GeoTIFF): {tiffPath}");
Console.WriteLine($"Heightmap (PNG):     {heightPngPath}");
Console.WriteLine($"Preview:             {previewPath}");
Console.WriteLine($"Manifest:            {manifestPath}");

if (options.Check)
{
    TerrainDiagnostics.Print(terrain, map, (float)metersPerPixel);
}

return 0;

static void EnsureDirectory(string path)
{
    string? directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
}

static void SaveGeoTiff(Heightmap map, float planetRadiusMeters, string path)
{
    EnsureDirectory(path);

    var tiffOptions = new GeoTiffOptions
    {
        WestLongitude = map.View.WestLongitude,
        NorthLatitude = map.View.NorthLatitude,
        LongitudeSpan = map.View.LongitudeSpan,
        LatitudeSpan = map.View.LatitudeSpan,
        PlanetRadiusMeters = planetRadiusMeters,
        Description = "WorldGen base topology; elevations in metres above sea level",
    };

    GeoTiffWriter.WriteFloat32(path, map.Elevations, map.Width, map.Height, tiffOptions);
}

static void SaveHeightmap(Heightmap map, string path)
{
    EnsureDirectory(path);

    var samples = new ushort[map.Elevations.Length];
    const float scale = 65_535f / (HeightmapMaxMeters - HeightmapMinMeters);

    for (int i = 0; i < samples.Length; i++)
    {
        float normalized = (map.Elevations[i] - HeightmapMinMeters) * scale;
        samples[i] = (ushort)Math.Clamp(MathF.Round(normalized), 0f, 65_535f);
    }

    PngWriter.WriteGrayscale16(path, samples, map.Width, map.Height);
}

static void SavePreview(Heightmap map, float planetRadiusMeters, string path)
{
    EnsureDirectory(path);

    byte[] pixels = TerrainPreview.Render(map, planetRadiusMeters);
    PngWriter.WriteRgb(path, pixels, map.Width, map.Height);
}

static void SaveManifest(
    Heightmap map,
    PlanetTerrain terrain,
    Options options,
    float planetRadiusMeters,
    double metersPerPixelEquator,
    string tiffPath,
    string heightPngPath,
    string previewPath,
    string manifestPath)
{
    EnsureDirectory(manifestPath);

    var (min, max, mean, landFraction) = map.ComputeStatistics();
    var settings = terrain.Settings;

    var manifest = new WorldManifest(
        FormatVersion: 1,
        Tool: "WorldGen",
        Stage: "base-topology",
        Seed: options.Seed,
        PlanetRadiusMeters: planetRadiusMeters,
        Datum: "sea-level",
        View: new ManifestView(
            map.View.NorthLatitude,
            map.View.SouthLatitude,
            map.View.WestLongitude,
            map.View.EastLongitude),
        Width: map.Width,
        Height: map.Height,
        MetersPerPixelEquator: metersPerPixelEquator,
        MetersPerPixelNorthSouth: planetRadiusMeters * MathUtils.ToRadians(map.View.LatitudeSpan) / map.Height,
        PngEncodingRange: new ManifestElevationRange(HeightmapMinMeters, HeightmapMaxMeters),
        Encoding: "height: geotiff-float32-le-deflate; png: grayscale-16",
        Terrain: new ManifestTerrainParameters(
            Math.Round((double)settings.TargetLandFraction, 6),
            Math.Round((double)terrain.LandThreshold, 6),
            settings.ShelfDepthMeters,
            settings.AbyssDepthMeters,
            settings.ContinentalReliefMeters,
            settings.MountainReliefMeters,
            settings.HillReliefMeters,
            settings.DetailReliefMeters),
        Files: new ManifestFiles(
            Path.GetFileName(tiffPath),
            Path.GetFileName(heightPngPath),
            Path.GetFileName(previewPath)),
        Observed: new ManifestStatistics(
            landFraction,
            min,
            max,
            mean,
            "Land fraction and mean are area-weighted by cos(latitude); min/max are exact raster extremes."));

    ManifestWriter.Write(manifestPath, manifest);
}

internal sealed class Options
{
    public const string Usage = """
        Usage: WorldGen [options]

          --width <px>          Output width in pixels (default 2048)
          --height <px>         Output height in pixels (default 1024)
          --seed <int>          Planet seed (default 1337)
          --out <path>          Output path without extension (default "world");
                                writes <path>-height.tif, <path>-height.png,
                                <path>-preview.png, and <path>-manifest.json
          --lat <north> <south> Latitude bounds in degrees (default 90 -90)
          --lon <west> <east>   Longitude bounds in degrees (default -180 180)
          --check               Report world-vs-zoom consistency for this view
          --help                Show this message
        """;

    public int Width { get; private set; } = 2048;

    public int Height { get; private set; } = 1024;

    public int Seed { get; private set; } = 1337;

    public string Output { get; private set; } = "world";

    public MapView View { get; private set; } = MapView.WholePlanet;

    public bool Check { get; private set; }

    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        double north = 90.0, south = -90.0, west = -180.0, east = 180.0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--width":
                    options.Width = ParseInt(args, ref i);
                    break;
                case "--height":
                    options.Height = ParseInt(args, ref i);
                    break;
                case "--seed":
                    options.Seed = ParseInt(args, ref i);
                    break;
                case "--out":
                    options.Output = ParseString(args, ref i);
                    break;
                case "--lat":
                    north = ParseDouble(args, ref i);
                    south = ParseDouble(args, ref i);
                    break;
                case "--lon":
                    west = ParseDouble(args, ref i);
                    east = ParseDouble(args, ref i);
                    break;
                case "--check":
                    options.Check = true;
                    break;
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    return options;
                default:
                    throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        if (options.Width < 1 || options.Height < 1)
        {
            throw new ArgumentException("Width and height must be positive.");
        }

        options.View = new MapView(north, south, west, east);
        options.View.Validate();
        return options;
    }

    private static string ParseString(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[i]}.");
        }

        return args[++i];
    }

    private static int ParseInt(string[] args, ref int i)
    {
        string value = ParseString(args, ref i);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"Expected an integer, got '{value}'.");
        }

        return result;
    }

    private static double ParseDouble(string[] args, ref int i)
    {
        string value = ParseString(args, ref i);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
        {
            throw new ArgumentException($"Expected a number, got '{value}'.");
        }

        return result;
    }
}
