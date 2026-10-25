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
    public float ContinentalReliefMeters { get; init; } = 6_000f;

    /// <summary>
    /// Average elevation of continental crust above sea level. Fades in from the coast so it never moves the
    /// shoreline, but it gives lowlands a realistic base height instead of letting the whole interior sit near 0 m.
    /// </summary>
    public float LandBaseMeters { get; init; } = 375f;

    /// <summary>Relief of the ridged mountain belts.</summary>
    public float MountainReliefMeters { get; init; } = 11_000f;

    /// <summary>
    /// Soft ceiling on land elevation above sea level, in meters. A single pixel where a strong collision
    /// meets a high-relief, high-offset province could otherwise stack past the 16-bit PNG window; this makes
    /// the tail asymptote instead of running away, so no seed clips and the distribution stays bounded. Only
    /// the extreme tail is compressed; ordinary terrain is unchanged. Set to 0 to disable.
    /// </summary>
    public float MountainCeilingMeters { get; init; } = 8_500f;

    /// <summary>Relief of the medium-scale hill layer.</summary>
    public float HillReliefMeters { get; init; } = 320f;

    /// <summary>Relief of the finest detail layer, which is only added when the render resolution warrants it.</summary>
    public float DetailReliefMeters { get; init; } = 70f;

    /// <summary>Number of kinematic plates in the tectonic sketch.</summary>
    public int PlateCount { get; init; } = 13;

    /// <summary>Number of geomorphic provinces used to vary terrain character.</summary>
    public int ProvinceCount { get; init; } = 9;

    /// <summary>Number of mantle hotspots used for volcanic chains.</summary>
    public int HotspotCount { get; init; } = 5;

    /// <summary>Budget for non-hotspot, non-arc landmarks (massifs and craters).</summary>
    public int LandmarkBudget { get; init; } = 20;

    /// <summary>Maximum incision of a continental rift, in meters.</summary>
    public float RiftDepthMeters { get; init; } = 650f;

    /// <summary>Relief of the uplifted shoulders beside a rift, in meters.</summary>
    public float RiftShoulderMeters { get; init; } = 350f;

    /// <summary>Depth added at a subduction trench, in meters.</summary>
    public float TrenchDepthMeters { get; init; } = 1_800f;

    /// <summary>Shallowing of the sea floor on a volcanic arc, in meters.</summary>
    public float ArcReliefMeters { get; init; } = 2_200f;

    // Tuning for iteration (CLI-driven)
    public float ContinentWarpStrength1 { get; init; } = 0.45f;
    public float ContinentWarpStrength2 { get; init; } = 0.22f;
    public float TectonicWarpStrength { get; init; } = 0.11f;
    public bool ContinentWarpEnabled { get; init; } = true;
    public float WarpRigidityScale { get; init; } = 0.7f;
    public bool WarpRigidityEnabled { get; init; } = true;
    public float OrogenWidthMeters { get; init; } = 850_000f;
    public float OrogenSpineWidthMeters { get; init; } = 180_000f;
    public float OrogenSpineHeightMeters { get; init; } = 4_500f;
    public bool OrogenSpineEnabled { get; init; } = false;
    public float ConvergenceThreshold { get; init; } = 0.12f;
    public float ConvergenceFull { get; init; } = 0.55f;
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
    private const float RidgeScale = 5.5f;
    private const float HillScale = 12f;
    private const float DetailScale = 40f;

    private readonly TerrainSettings _settings;
    private readonly PerlinNoise _continentNoise;
    private readonly PerlinNoise _warpNoise;
    private readonly PerlinNoise _ridgeNoise;
    private readonly PerlinNoise _hillNoise;
    private readonly PerlinNoise _detailNoise;
    private readonly PerlinNoise _oceanNoise;
    private readonly PerlinNoise _anisotropyNoise;
    private readonly TectonicSketch _tectonics;
    private readonly ProvinceField _provinces;
    private readonly LandmarkField _landmarks;
    private readonly float _landThreshold;
    private readonly PerlinNoise _rigidityNoise;

    public PlanetTerrain(TerrainSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.PlanetRadiusMeters, 1f);
        if (settings.TargetLandFraction is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings), settings.TargetLandFraction, "Target land fraction must be in [0, 1].");
        }

        _settings = settings;
        _continentNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 1));
        _warpNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 2));
        _ridgeNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 4));
        _hillNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 5));
        _detailNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 6));
        _oceanNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 7));
        _anisotropyNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 8));
        _rigidityNoise = new PerlinNoise(MathUtils.DeriveSeed(settings.Seed, 9));

        _landThreshold = ComputeLandThreshold();

        _tectonics = new TectonicSketch(
            settings.Seed,
            settings.PlateCount,
            settings.HotspotCount,
            settings.PlanetRadiusMeters,
            settings.OrogenWidthMeters);
        _provinces = new ProvinceField(settings.Seed, settings.ProvinceCount);
        _landmarks = LandmarkField.Build(
            settings.Seed,
            settings.LandmarkBudget,
            settings.PlanetRadiusMeters,
            _tectonics,
            direction => ContinentField(direction) - _landThreshold);
    }

    public TerrainSettings Settings => _settings;

    public TectonicSketch Tectonics => _tectonics;

    public ProvinceField Provinces => _provinces;

    public LandmarkField Landmarks => _landmarks;

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

        TectonicSample tectonics = _tectonics.Sample(point);
        float continentalness = ContinentField(point, tectonics) - _landThreshold;
        ProvinceSample province = _provinces.Sample(point);

        float elevation = continentalness <= 0f
            ? OceanElevation(point, continentalness, tectonics, province)
            : LandElevation(point, continentalness, tectonics, province, metersPerPixel);

        // Landmarks are additive. A seamount can raise the sea floor above zero and become a volcanic island;
        // a crater or caldera can deepen land, so clamp the land branch to the sea-level datum for now.
        elevation += _landmarks.Elevation(point);

        // Bound the positive tail after every layer has contributed, so no combination of a strong collision,
        // a high-offset province, and a stacked massif can exceed the encoding window. Depths are untouched.
        elevation = SoftCeiling(elevation, _settings.MountainCeilingMeters);

        if (continentalness > 0f && elevation < 0.1f)
        {
            // Until hydrology can represent enclosed basins below sea level, keep land above the datum. This is
            // a small, documented compromise; the alternative would make the coastline disagree with sea level.
            elevation = 0.1f;
        }

        return elevation;
    }

    /// <summary>
    /// Continental shape: heavily domain-warped low-frequency noise. Warping is what turns round noise blobs
    /// into elongated landmasses with peninsulas and inland seas.
    /// </summary>
    private float ContinentField(Vector3 point, TectonicSample? precomputedTectonics = null)
    {
        Vector3 warped = point;
        float warpScale = 1f;
        if (_settings.WarpRigidityEnabled && _tectonics != null)
        {
            // Cratons and thick continental interiors resist advection; boundaries and young crust warp more.
            TectonicSample t = precomputedTectonics ?? _tectonics.Sample(point);
            float boundaryProximity = MathF.Max(t.Orogeny, MathF.Max(t.Rift, MathF.Max(t.Trench, t.OceanRidge)));
            float rigidity = 1f - boundaryProximity;
            // Older cratons read as low-frequency smoothness
            float cratonMask = 1f - Math.Clamp(_rigidityNoise.Fractal(point * 0.8f, 3, 0.5f), 0f, 1f);
            rigidity = Math.Clamp(rigidity * 0.6f + cratonMask * 0.4f, 0f, 1f);
            warpScale = 1f - rigidity * _settings.WarpRigidityScale;
            warpScale = Math.Clamp(warpScale, 0.1f, 1f);
        }

        if (_settings.ContinentWarpEnabled && _settings.ContinentWarpStrength1 > 0f)
        {
            warped = Warped(warped, frequency: 2.4f, strength: _settings.ContinentWarpStrength1 * warpScale);
        }

        if (_settings.ContinentWarpEnabled && _settings.ContinentWarpStrength2 > 0f)
        {
            warped = Warped(warped, frequency: 5.1f, strength: _settings.ContinentWarpStrength2 * warpScale);
        }

        return _continentNoise.Fractal(warped * ContinentScale, octaves: 5, gain: 0.52f);
    }

    private float OceanElevation(
        Vector3 point,
        float continentalness,
        TectonicSample tectonics,
        ProvinceSample province)
    {
        // Continental shelf, then a steep slope into the abyss. Starting at zero depth keeps the shoreline
        // continuous with the coast instead of dropping off a 170 m cliff at the water's edge.
        float shelf = MathUtils.SmoothStep(0f, 0.012f, -continentalness);
        float slope = MathUtils.SmoothStep(0.012f, 0.09f, -continentalness);

        float depth = -_settings.ShelfDepthMeters * shelf
                      - (_settings.AbyssDepthMeters - _settings.ShelfDepthMeters) * slope;

        // Abyssal hills and seamount-scale bumps.
        float relief = (_oceanNoise.Fractal(point * 7f, octaves: 4) - 0.5f) * 2f;
        depth += relief * 380f * slope * (0.7f + 0.6f * province.Parameters.ContinentalReliefScale);

        // Mid-ocean ridges are broad swells; let them wander and vary in crest height along strike too.
        // Both ridges and trenches belong to the abyssal ocean: gating them below the shelf break stops a
        // boundary from painting a conspicuous straight channel across a shelf or coastline.
        float abyssal = MathUtils.SmoothStep(0.05f, 0.09f, -continentalness);
        float ridge = tectonics.OceanRidge * abyssal;
        float ridgeDepth = tectonics.OceanRidgeCrestMeters + relief * 320f;
        depth = MathUtils.Lerp(depth, ridgeDepth, ridge * 0.9f);

        depth -= tectonics.Trench * _settings.TrenchDepthMeters * abyssal;
        depth += tectonics.Arc * _settings.ArcReliefMeters * 0.55f;

        return depth;
    }

    private float LandElevation(
        Vector3 point,
        float continentalness,
        TectonicSample tectonics,
        ProvinceSample province,
        float metersPerPixel)
    {
        ProvinceParameters parameters = province.Parameters;
        float inland = MathUtils.SmoothStep(0f, 0.05f, continentalness);

        float elevation = _settings.LandBaseMeters * MathUtils.SmoothStep(0f, 0.03f, continentalness)
                          + _settings.ContinentalReliefMeters
                          * parameters.ContinentalReliefScale
                          * MathF.Pow(continentalness, 1.35f);

        // Plateaus are broad elevated surfaces; basins are shallow depressions. Both fade in from the coast.
        elevation += parameters.BaseOffsetMeters * MathUtils.SmoothStep(0.005f, 0.08f, continentalness);

        // Mountain belts follow tectonic boundaries. The orogeny mask is broad; the arc mask is a narrower
        // band on the overriding plate. Both are gated inland so the coastline stays controlled by sea level.
        float uplift = MathF.Max(tectonics.Orogeny, tectonics.Arc * 0.85f)
                       * MathUtils.SmoothStep(0.015f, 0.10f, continentalness);

        if (uplift > 0.002f)
        {
            float ridgeFrequency = RidgeScale * parameters.RidgeFrequencyScale;
            int ridgeOctaves = OctavesFor(metersPerPixel, ridgeFrequency, maxOctaves: 6, _settings.PlanetRadiusMeters);
            if (ridgeOctaves > 0)
            {
                float ridges = RidgedAnisotropic(
                    point,
                    ridgeFrequency,
                    ridgeOctaves,
                    tectonics.BoundaryTangent,
                    parameters.Anisotropy,
                    parameters.Roughness);

                elevation += uplift
                             * (0.20f + 1.05f * ridges)
                             * _settings.MountainReliefMeters
                             * parameters.MountainReliefScale;
            }
        }

        // Rift valleys incise; their shoulders rise. Keep them subtle so they don't form unnaturally long,
        // lake-like depressions visible from space.
        float rift = tectonics.Rift * inland;
        elevation -= rift * _settings.RiftDepthMeters;
        elevation += tectonics.RiftShoulder * inland * _settings.RiftShoulderMeters * 0.6f;
        elevation += tectonics.Transform * inland * 260f * (0.5f + parameters.Anisotropy);

        // Hills and fine detail vary by province. They fade out at the shoreline, so they never move the coast.
        int hillOctaves = OctavesFor(metersPerPixel, HillScale, maxOctaves: 5, _settings.PlanetRadiusMeters);
        if (hillOctaves > 0)
        {
            float hills = (_hillNoise.Fractal(point * HillScale, hillOctaves) - 0.5f) * 2f;
            elevation += hills * _settings.HillReliefMeters * parameters.HillReliefScale * inland;
        }

        int detailOctaves = OctavesFor(metersPerPixel, DetailScale, maxOctaves: 14, _settings.PlanetRadiusMeters);
        if (detailOctaves > 0)
        {
            float detail = (_detailNoise.Fractal(point * DetailScale, detailOctaves) - 0.5f) * 2f;
            elevation += detail * _settings.DetailReliefMeters * parameters.DetailReliefScale * inland;
        }

        return elevation;
    }

    /// <summary>
    /// Leaves <paramref name="value"/> unchanged up to a knee, then compresses it toward
    /// <paramref name="ceiling"/> without ever reaching it. A non-positive ceiling disables the cap.
    /// </summary>
    private static float SoftCeiling(float value, float ceiling)
    {
        if (ceiling <= 0f)
        {
            return value;
        }

        float knee = ceiling * 0.8f;
        if (value <= knee)
        {
            return value;
        }

        float over = value - knee;
        float range = ceiling - knee;
        return knee + range * over / (over + range);
    }

    /// <summary>
    /// Ridged noise with a shear along the local boundary tangent. The shear stretches crests along the belt
    /// instead of leaving them as isotropic blobs; it is a continuous world-space operation, so it is stable
    /// under zoom.
    /// </summary>
    private float RidgedAnisotropic(
        Vector3 point,
        float frequency,
        int octaves,
        Vector3 boundaryTangent,
        float anisotropy,
        float gain)
    {
        if (anisotropy <= 0.01f)
        {
            return _ridgeNoise.Ridged(point * frequency, octaves, gain);
        }

        Vector3 tangent = boundaryTangent - point * Vector3.Dot(point, boundaryTangent);
        if (tangent.LengthSquared() < 1e-6f)
        {
            return _ridgeNoise.Ridged(point * frequency, octaves, gain);
        }

        tangent = Vector3.Normalize(tangent);
        float shear = _anisotropyNoise.Noise(point * (frequency * 0.55f)) * anisotropy * 0.22f;
        Vector3 warped = Vector3.Normalize(point + tangent * shear);
        return _ridgeNoise.Ridged(warped * frequency, octaves, gain);
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
}
