using System.Globalization;

namespace WorldGen;

internal sealed class Options
{
    public const string Usage = """
        Usage: WorldGen [options]

          --width <px>                 Output width in pixels (default 2048)
          --height <px>                Output height in pixels (default 1024)
          --seed <int>                 Planet seed (default 1337)
          --out <path>                 Output prefix without stage suffix (default "world");
                                       Stage 1 writes <path>.s1-base-height.tif, -height.png, -preview.png, -manifest.json
                                       Stage 2 writes <path>.s2-eroded-height.tif, -height.png, -preview.png, -manifest.json
                                       Stage 3 writes <path>.s3-climate-height.tif, -rainfall.tif, -height.png, -preview.png, -manifest.json
                                       Stage 4 writes <path>.s4-hydro-height.tif, -height.png, -preview.png, -manifest.json, -vectors.json
                                       Stage 5 writes <path>.s5-biome-height.tif, -ids.tif, -ids.png, -preview.png, -palette.json, -manifest.json
          --lat <north> <south>        Latitude bounds in degrees (default 90 -90)
          --lon <west> <east>          Longitude bounds in degrees (default -180 180)
          --check                      Report diagnostics for this view
          --erode                      Run Stage 2 erosion (writes .s2-eroded-*)
          --erode-from <tif>           Load an existing Stage 1 float32 GeoTIFF (read-only) and write .s2-eroded-*
          --erosion-steps <int>        Erosion solver iterations (default 25)
          --erosion-k <float>          Stream-power erodibility K (default 0.032)
          --erosion-diffusion <float>  Hillslope diffusivity D (default 0.15)
          --erosion-capacity <float>   Sediment transport capacity (default 0.55)
          --erosion-deposition <float> Basin/alluvial deposition rate (default 0.35)
          --erosion-uplift <float>     Active tectonic uplift scale during erosion (default 0.28)
          --erosion-halo <px>          Overlap halo pixels for regional erosion (default 16)
          --climate                    Run Stage 3 climate & rainfall (implies --erode unless --climate-from is used; writes .s3-climate-*)
          --climate-from <tif>         Load an existing Stage 2 eroded float32 GeoTIFF (read-only) and write .s3-climate-*
          --precip-scale <float>       Global precipitation multiplier (default 1.0)
          --orographic-lift <float>    Windward mountain orographic lift strength (default 1.15)
          --rain-shadow <float>        Leeward mountain rain-shadow drying strength (default 1.25)
          --hydro                      Run Stage 4 hydrology (implies --erode and --climate unless --hydro-from is used; writes .s4-hydro-*)
          --hydro-from <tif>           Load an existing Stage 2/3 float32 GeoTIFF (read-only) and write .s4-hydro-*
          --river-discharge <m3s>      Minimum volumetric river discharge in m³/s (default 220)
          --river-threshold <sqkm>     Minimum drainage area in km² for rivers (default 26000)
          --min-twig-cells <int>       Minimum pixel length for Order-1 headwater branches (default 8)
          --min-lake-cells <int>       Minimum connected cells to form a lake (default 12)
          --min-lake-depth <meters>    Minimum basin depth in meters to form a lake (default 5.0)
          --max-lake-depth <meters>    Maximum lake depth before partial sill breaching (default 65.0)
          --biome                      Run Stage 5 biomes (implies --hydro unless --biome-from is used; writes .s5-biome-*)
          --biome-from <tif>           Load an existing Stage 2/3 float32 GeoTIFF (read-only) and write .s5-biome-*
          --biome-set <id>             Biome set from biomes.json (default whittaker)
          --no-ecotone                 Disable the sphere-noise ecotone modulation
          --smooth-passes <int>        Majority-filter passes over land biomes (default 1)
          --no-continent-warp          Disable domain warp on continents
          --warp1 <float>              Continent warp strength 1 (default 0.45)
          --warp2 <float>              Continent warp strength 2 (default 0.22)
          --no-warp-rigidity           Disable rigidity-masked warping
          --warp-rigidity <float>      Warp rigidity scale (default 0.7)
          --orogen-width <meters>      Orogen width (default 850000)
          --cache-list                 List cached stage variants and exit
          --cache-prune                Prune cached stages (preview unless --cache-yes)
          --cache-dir <path>           Cache root (default under the user's local app data)
          --cache-stage <s1,s2,...>    Limit a prune to the listed stages
          --cache-older-than <days>    Only prune entries unused for this many days
          --cache-include-released     Allow pruning released models (default: dev only)
          --cache-yes                  Actually delete; without it, prune is a dry run
          --no-cache                   Do not read or write the stage cache
          --help                       Show this message
        """;

    public int Width { get; private set; } = 2048;
    public int Height { get; private set; } = 1024;
    public int Seed { get; private set; } = 1337;
    public string Output { get; private set; } = "world";
    public bool OutputExplicitlySet { get; private set; }
    public MapView View { get; private set; } = MapView.WholePlanet;
    public bool Check { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool Erode { get; private set; }
    public string? ErodeFromTiff { get; private set; }
    public int ErosionSteps { get; private set; } = 25;
    public float ErosionK { get; private set; } = 0.032f;
    public float ErosionDiffusion { get; private set; } = 0.15f;
    public float ErosionCapacity { get; private set; } = 0.55f;
    public float ErosionDeposition { get; private set; } = 0.35f;
    public float ErosionUplift { get; private set; } = 0.28f;
    public int ErosionHalo { get; private set; } = 16;
    public bool Climate { get; private set; }
    public string? ClimateFromTiff { get; private set; }
    public float PrecipitationScale { get; private set; } = 1.0f;
    public float OrographicLiftStrength { get; private set; } = 1.15f;
    public float RainShadowStrength { get; private set; } = 1.25f;
    public bool Hydro { get; private set; }
    public string? HydroFromTiff { get; private set; }
    public float MinRiverDischargeM3s { get; private set; } = 220f;
    public float RiverThresholdSqKm { get; private set; } = 26_000f;
    public int MinHeadwaterReachCells { get; private set; } = 8;
    public int MinLakeCells { get; private set; } = 12;
    public float MinLakeDepthMeters { get; private set; } = 5.0f;
    public float MaxLakeDepthMeters { get; private set; } = 65.0f;
    public bool Biome { get; private set; }
    public string? BiomeFromTiff { get; private set; }
    public string BiomeSetId { get; private set; } = "whittaker";
    public bool EcotoneEnabled { get; private set; } = true;
    public int SmoothingPasses { get; private set; } = 1;
    public bool ContinentWarpEnabled { get; private set; } = true;
    public float ContinentWarpStrength1 { get; private set; } = 0.45f;
    public float ContinentWarpStrength2 { get; private set; } = 0.22f;
    public bool WarpRigidityEnabled { get; private set; } = true;
    public float WarpRigidityScale { get; private set; } = 0.7f;
    public float OrogenWidthMeters { get; private set; } = 850_000f;
    public bool CacheList { get; private set; }
    public bool CachePrune { get; private set; }
    public string CacheDirectory { get; private set; } = StageCache.DefaultRoot;
    public string? CacheStageFilter { get; private set; }
    public int CacheOlderThanDays { get; private set; }
    public bool CacheIncludeReleased { get; private set; }
    public bool CacheYes { get; private set; }
    public bool NoCache { get; private set; }

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
                    options.OutputExplicitlySet = true;
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
                case "--erode":
                    options.Erode = true;
                    break;
                case "--erode-from":
                    options.ErodeFromTiff = ParseString(args, ref i);
                    options.Erode = true;
                    break;
                case "--erosion-steps":
                    options.ErosionSteps = ParseInt(args, ref i);
                    break;
                case "--erosion-k":
                    options.ErosionK = (float)ParseDouble(args, ref i);
                    break;
                case "--erosion-diffusion":
                    options.ErosionDiffusion = (float)ParseDouble(args, ref i);
                    break;
                case "--erosion-capacity":
                    options.ErosionCapacity = (float)ParseDouble(args, ref i);
                    break;
                case "--erosion-deposition":
                    options.ErosionDeposition = (float)ParseDouble(args, ref i);
                    break;
                case "--erosion-uplift":
                    options.ErosionUplift = (float)ParseDouble(args, ref i);
                    break;
                case "--erosion-halo":
                    options.ErosionHalo = ParseInt(args, ref i);
                    break;
                case "--climate":
                    options.Climate = true;
                    if (string.IsNullOrWhiteSpace(options.ClimateFromTiff))
                    {
                        options.Erode = true;
                    }
                    break;
                case "--climate-from":
                    options.ClimateFromTiff = ParseString(args, ref i);
                    options.Climate = true;
                    break;
                case "--precip-scale":
                    options.PrecipitationScale = (float)ParseDouble(args, ref i);
                    break;
                case "--orographic-lift":
                    options.OrographicLiftStrength = (float)ParseDouble(args, ref i);
                    break;
                case "--rain-shadow":
                    options.RainShadowStrength = (float)ParseDouble(args, ref i);
                    break;
                case "--hydro":
                    options.Hydro = true;
                    if (string.IsNullOrWhiteSpace(options.HydroFromTiff))
                    {
                        options.Climate = true;
                        options.Erode = true;
                    }
                    break;
                case "--hydro-from":
                    options.HydroFromTiff = ParseString(args, ref i);
                    options.Hydro = true;
                    break;
                case "--river-discharge":
                    options.MinRiverDischargeM3s = (float)ParseDouble(args, ref i);
                    break;
                case "--river-threshold":
                    options.RiverThresholdSqKm = (float)ParseDouble(args, ref i);
                    break;
                case "--min-twig-cells":
                    options.MinHeadwaterReachCells = ParseInt(args, ref i);
                    break;
                case "--min-lake-cells":
                    options.MinLakeCells = ParseInt(args, ref i);
                    break;
                case "--min-lake-depth":
                    options.MinLakeDepthMeters = (float)ParseDouble(args, ref i);
                    break;
                case "--max-lake-depth":
                    options.MaxLakeDepthMeters = (float)ParseDouble(args, ref i);
                    break;
                case "--biome":
                    options.Biome = true;
                    if (string.IsNullOrWhiteSpace(options.BiomeFromTiff))
                    {
                        options.Hydro = true;
                        options.Climate = true;
                        options.Erode = true;
                    }
                    break;
                case "--biome-from":
                    options.BiomeFromTiff = ParseString(args, ref i);
                    options.Biome = true;
                    break;
                case "--biome-set":
                    options.BiomeSetId = ParseString(args, ref i);
                    break;
                case "--no-ecotone":
                    options.EcotoneEnabled = false;
                    break;
                case "--smooth-passes":
                    options.SmoothingPasses = ParseInt(args, ref i);
                    break;
                case "--no-continent-warp":
                    options.ContinentWarpEnabled = false;
                    break;
                case "--warp1":
                    options.ContinentWarpStrength1 = (float)ParseDouble(args, ref i);
                    break;
                case "--warp2":
                    options.ContinentWarpStrength2 = (float)ParseDouble(args, ref i);
                    break;
                case "--no-warp-rigidity":
                    options.WarpRigidityEnabled = false;
                    break;
                case "--warp-rigidity":
                    options.WarpRigidityScale = (float)ParseDouble(args, ref i);
                    break;
                case "--orogen-width":
                    options.OrogenWidthMeters = (float)ParseDouble(args, ref i);
                    break;
                case "--cache-list":
                    options.CacheList = true;
                    break;
                case "--cache-prune":
                    options.CachePrune = true;
                    break;
                case "--cache-dir":
                    options.CacheDirectory = ParseString(args, ref i);
                    break;
                case "--cache-stage":
                    options.CacheStageFilter = ParseString(args, ref i);
                    break;
                case "--cache-older-than":
                    options.CacheOlderThanDays = ParseInt(args, ref i);
                    break;
                case "--cache-include-released":
                    options.CacheIncludeReleased = true;
                    break;
                case "--cache-yes":
                    options.CacheYes = true;
                    break;
                case "--no-cache":
                    options.NoCache = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options.ShowHelp = true;
                    return options;
                default:
                    throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        if (options.Width < 1 || options.Height < 1)
            throw new ArgumentException("Width and height must be positive.");

        options.View = new MapView(north, south, west, east);
        options.View.Validate();
        return options;
    }

    private static string ParseString(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
        return args[++i];
    }

    private static int ParseInt(string[] args, ref int i)
    {
        string value = ParseString(args, ref i);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw new ArgumentException($"Expected an integer, got '{value}'.");
        return result;
    }

    private static double ParseDouble(string[] args, ref int i)
    {
        string value = ParseString(args, ref i);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            throw new ArgumentException($"Expected a number, got '{value}'.");
        return result;
    }
}
