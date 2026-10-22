using System.Numerics;

namespace WorldGen;

/// <summary>
/// Tunable parameters for <see cref="PlanetTerrain"/>.
/// </summary>
public sealed record TerrainSettings
{
    /// <summary>Any seed produces a different planet, reproducibly.</summary>
    public int Seed { get; init; } = 1337;

    public float PlanetRadiusMeters { get; init; } = 6_371_000f;

    /// <summary>Fraction of the surface above sea level. Drives the sea-level threshold.</summary>
    public float TargetLandFraction { get; init; } = 0.29f;

    /// <summary>Depth of the continental shelf break, in meters below sea level.</summary>
    public float ShelfDepthMeters { get; init; } = 170f;

    /// <summary>Typical depth of the abyssal plains, in meters below sea level.</summary>
    public float AbyssDepthMeters { get; init; } = 5_900f;

    /// <summary>Relief of the broad continental surface, on top of which mountains and hills sit.</summary>
    public float ContinentalReliefMeters { get; init; } = 5_000f;

    /// <summary>Relief of the ridged mountain belts.</summary>
    public float MountainReliefMeters { get; init; } = 7_400f;

    /// <summary>Relief of the medium-scale hill layer.</summary>
    public float HillReliefMeters { get; init; } = 320f;

    /// <summary>Relief of the finest detail layer, which is only added when the render resolution warrants it.</summary>
    public float DetailReliefMeters { get; init; } = 70f;
}

/// <summary>
/// A planet's elevation field, defined as a continuous function of direction on the unit sphere.
/// </summary>
/// <remarks>
/// Elevations are in meters relative to sea level, so the later erosion and hydrology stages can work in real
/// units. Because the field is a function of direction rather than of image pixels, any region can be rendered at
/// any resolution: a zoomed map adds finer octaves without altering the world-scale shape.
/// </remarks>
public sealed class PlanetTerrain
{
    // Angular scale of the continent field on the unit sphere. Smaller values mean fewer, larger landmasses.
    private const float ContinentScale = 1.08f;
    private const float BeltScale = 2.6f;
    private const float RidgeScale = 5.5f;
    private const float HillScale = 12f;
    private const float DetailScale = 40f;

    private readonly TerrainSettings _settings;
    private readonly PerlinNoise _continentNoise;
    private readonly PerlinNoise _warpNoise;
    private readonly PerlinNoise _beltNoise;
    private readonly PerlinNoise _ridgeNoise;
    private readonly PerlinNoise _hillNoise;
    private readonly PerlinNoise _detailNoise;
    private readonly PerlinNoise _oceanNoise;
    private readonly float _landThreshold;

    public PlanetTerrain(TerrainSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.PlanetRadiusMeters, 1f);
        if (settings.TargetLandFraction is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings), settings.TargetLandFraction, "Target land fraction must be in [0, 1].");
        }

        _settings = settings;
        _continentNoise = new PerlinNoise(DeriveSeed(settings.Seed, 1));
        _warpNoise = new PerlinNoise(DeriveSeed(settings.Seed, 2));
        _beltNoise = new PerlinNoise(DeriveSeed(settings.Seed, 3));
        _ridgeNoise = new PerlinNoise(DeriveSeed(settings.Seed, 4));
        _hillNoise = new PerlinNoise(DeriveSeed(settings.Seed, 5));
        _detailNoise = new PerlinNoise(DeriveSeed(settings.Seed, 6));
        _oceanNoise = new PerlinNoise(DeriveSeed(settings.Seed, 7));

        _landThreshold = ComputeLandThreshold();
    }

    public TerrainSettings Settings => _settings;

    /// <summary>
    /// Continentalness value above which a point is land. Constant for a given seed and land fraction, so
    /// coastlines do not shift between renders at different resolutions.
    /// </summary>
    public float LandThreshold => _landThreshold;

    /// <summary>
    /// Samples the elevation in meters relative to sea level.
    /// </summary>
    /// <param name="direction">Any non-zero vector; only its direction matters.</param>
    /// <param name="metersPerPixel">
    /// Ground size of one map pixel at this location. Finer values enable additional detail octaves. Pass
    /// <see cref="float.PositiveInfinity"/> (the default) for the fully detailed field.
    /// </param>
    public float Elevation(Vector3 direction, float metersPerPixel = float.PositiveInfinity)
    {
        Vector3 point = Vector3.Normalize(direction);

        float continentalness = ContinentField(point) - _landThreshold;
        float belt = BeltField(point);

        return continentalness <= 0f
            ? OceanElevation(point, continentalness, belt)
            : LandElevation(point, continentalness, belt, metersPerPixel);
    }

    /// <summary>
    /// Continental shape: heavily domain-warped low-frequency noise. Warping is what turns round noise blobs
    /// into elongated landmasses with peninsulas and inland seas.
    /// </summary>
    private float ContinentField(Vector3 point)
    {
        Vector3 warped = Warped(point, frequency: 2.4f, strength: 0.45f);
        warped = Warped(warped, frequency: 5.1f, strength: 0.22f);
        return _continentNoise.Fractal(warped * ContinentScale, octaves: 5, gain: 0.52f);
    }

    /// <summary>
    /// Broad bands that decide where mountain building happens. On land these are orogenic belts; under water
    /// they become mid-ocean ridges.
    /// </summary>
    private float BeltField(Vector3 point)
    {
        Vector3 warped = Warped(point, frequency: 3.1f, strength: 0.35f);
        return _beltNoise.Fractal(warped * BeltScale, octaves: 3);
    }

    private float OceanElevation(Vector3 point, float continentalness, float belt)
    {
        // Continental shelf, then a steep slope into the abyss. Starting at zero depth keeps the shoreline
        // continuous with the coast instead of dropping off a 170 m cliff at the water's edge.
        float shelf = MathUtils.SmoothStep(0f, 0.012f, -continentalness);
        float slope = MathUtils.SmoothStep(0.012f, 0.09f, -continentalness);

        float depth = -_settings.ShelfDepthMeters * shelf
                      - (_settings.AbyssDepthMeters - _settings.ShelfDepthMeters) * slope;

        // Abyssal hills and seamount-scale bumps.
        float relief = (_oceanNoise.Fractal(point * 7f, octaves: 4) - 0.5f) * 2f;
        depth += relief * 380f * slope;

        // Mid-ocean ridges: only the strongest belts rise toward a shallow crest.
        float ridge = MathUtils.SmoothStep(0.74f, 0.92f, belt);
        float ridgeDepth = -2_500f + relief * 320f;
        depth = MathUtils.Lerp(depth, ridgeDepth, ridge * 0.9f);

        return depth;
    }

    private float LandElevation(Vector3 point, float continentalness, float belt, float metersPerPixel)
    {
        float inland = MathUtils.SmoothStep(0f, 0.05f, continentalness);

        float elevation = _settings.ContinentalReliefMeters * MathF.Pow(continentalness, 1.35f);

        // Mountain belts: gated by both the belt field and distance from the coast.
        float uplift = MathUtils.SmoothStep(0.30f, 0.72f, belt)
                       * MathUtils.SmoothStep(0.015f, 0.10f, continentalness);

        if (uplift > 0f)
        {
            int ridgeOctaves = OctavesFor(metersPerPixel, RidgeScale, maxOctaves: 6, _settings.PlanetRadiusMeters);
            if (ridgeOctaves > 0)
            {
                float ridges = _ridgeNoise.Ridged(point * RidgeScale, ridgeOctaves);
                elevation += uplift * (0.2f + 0.8f * ridges) * _settings.MountainReliefMeters;
            }
        }

        // All noise terms fade out at the shoreline, so the terrain never dips below sea level on land and the
        // coastline stays exactly where the land fraction says it should be.
        int hillOctaves = OctavesFor(metersPerPixel, HillScale, maxOctaves: 5, _settings.PlanetRadiusMeters);
        if (hillOctaves > 0)
        {
            float hills = (_hillNoise.Fractal(point * HillScale, hillOctaves) - 0.5f) * 2f;
            elevation += hills * _settings.HillReliefMeters * inland;
        }

        int detailOctaves = OctavesFor(metersPerPixel, DetailScale, maxOctaves: 14, _settings.PlanetRadiusMeters);
        if (detailOctaves > 0)
        {
            float detail = (_detailNoise.Fractal(point * DetailScale, detailOctaves) - 0.5f) * 2f;
            elevation += detail * _settings.DetailReliefMeters * inland;
        }

        return elevation;
    }

    /// <summary>
    /// Domain warping: displace the sample point by a noise vector before evaluating the field. Keeps the point
    /// on the sphere so results stay seamless.
    /// </summary>
    private Vector3 Warped(Vector3 point, float frequency, float strength)
    {
        Vector3 offset = new(
            _warpNoise.Noise(point * frequency + new Vector3(11.3f, 0f, 0f)),
            _warpNoise.Noise(point * frequency + new Vector3(0f, 23.7f, 0f)),
            _warpNoise.Noise(point * frequency + new Vector3(0f, 0f, 37.1f)));

        return Vector3.Normalize(point + offset * strength);
    }

    /// <summary>
    /// Picks the continentalness quantile that puts the requested fraction of the surface above sea level.
    /// </summary>
    /// <remarks>
    /// Evaluated over a fixed set of evenly distributed points, not over the output image, so every zoom level
    /// and tile uses the same coastline.
    /// </remarks>
    private float ComputeLandThreshold()
    {
        const int sampleCount = 32_768;
        var values = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            values[i] = ContinentField(MathUtils.FibonacciSpherePoint(i, sampleCount));
        }

        Array.Sort(values);

        int index = (int)MathF.Round((1f - _settings.TargetLandFraction) * (sampleCount - 1));
        return values[index];
    }

    /// <summary>
    /// Number of octaves worth adding at a given ground resolution, so detail stops just short of aliasing.
    /// </summary>
    private static int OctavesFor(float metersPerPixel, float baseFrequency, int maxOctaves, float planetRadius)
    {
        if (!float.IsFinite(metersPerPixel) || metersPerPixel <= 0f)
        {
            return maxOctaves;
        }

        double circumference = 2.0 * Math.PI * planetRadius;
        double finestFrequency = circumference / (4.0 * metersPerPixel);
        if (finestFrequency <= baseFrequency)
        {
            return 0;
        }

        int octaves = (int)Math.Ceiling(Math.Log2(finestFrequency / baseFrequency)) + 1;
        return Math.Clamp(octaves, 0, maxOctaves);
    }

    private static int DeriveSeed(int seed, int stream) => unchecked(seed * 1_000_003 + stream * 97_409 + 12_345);
}
