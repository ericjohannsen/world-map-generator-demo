using System.Numerics;

namespace WorldGen;

/// <summary>
/// A volcanic centre fixed in the mantle frame. <see cref="Velocity"/> is the local plate velocity, used to
/// grow a hotspot track behind the centre.
/// </summary>
public readonly record struct TectonicHotspot(
    Vector3 Position,
    float Strength,
    float RadiusMeters,
    Vector3 Velocity);

/// <summary>
/// Low-frequency tectonic fields sampled at a direction. All strengths are normalised to [0, 1]; the caller
/// converts them into metres.
/// </summary>
public readonly record struct TectonicSample(
    float Influence,
    float SignedDistanceMeters,
    float Convergence,
    float Divergence,
    float Orogeny,
    float Arc,
    float Trench,
    float Rift,
    float RiftShoulder,
    float OceanRidge,
    float Transform,
    Vector3 BoundaryTangent,
    int PlateA,
    int PlateB)
{
    public static TectonicSample Empty => new(
        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, Vector3.UnitX, -1, -1);
}

/// <summary>
/// A deliberately small kinematic plate model: a spherical Voronoi mosaic with Euler poles, boundary types,
/// and a handful of hotspots. It is a structural sketch, not a geodynamic simulation. It answers "where are
/// the convergent, divergent, and transform boundaries?" so mountains, arcs, rifts, and ridges are globally
/// coherent instead of being independent noise blobs.
/// </summary>
public sealed class TectonicSketch
{
    // Widths are half-widths of the smooth influence band, in metres.
    private const float OrogenWidthMeters = 850_000f;
    private const float ArcOffsetMeters = 180_000f;
    private const float ArcWidthMeters = 220_000f;
    private const float TrenchWidthMeters = 90_000f;
    private const float RiftWidthMeters = 320_000f;
    private const float RiftShoulderOffsetMeters = 240_000f;
    private const float RiftShoulderWidthMeters = 150_000f;
    private const float OceanRidgeWidthMeters = 420_000f;
    private const float TransformWidthMeters = 130_000f;
    private const float MaximumInfluenceMeters = 900_000f;

    private readonly Plate[] _plates;
    private readonly TectonicHotspot[] _hotspots;
    private readonly PerlinNoise _warpNoise;
    private readonly float _radius;

    public TectonicSketch(int seed, int plateCount, int hotspotCount, float planetRadiusMeters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plateCount, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(hotspotCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(planetRadiusMeters, 1f);

        _radius = planetRadiusMeters;
        _warpNoise = new PerlinNoise(MathUtils.DeriveSeed(seed, 11));

        var random = new DeterministicRandom(MathUtils.DeriveSeed(seed, 12));
        _plates = new Plate[plateCount];

        for (int i = 0; i < plateCount; i++)
        {
            Vector3 anchor = MathUtils.FibonacciSpherePoint(i, plateCount);
            Vector3 jitter = RandomUnit(random) * (0.10f + random.NextSingle() * 0.12f);
            Vector3 plateSeed = Vector3.Normalize(anchor + jitter);

            _plates[i] = new Plate(
                plateSeed,
                (random.NextSingle() * 2f - 1f) * 0.055f,
                RandomUnit(random),
                0.025f + random.NextSingle() * 0.065f,
                ((char)('A' + i)).ToString());
        }

        _hotspots = new TectonicHotspot[hotspotCount];
        for (int i = 0; i < hotspotCount; i++)
        {
            Vector3 position;
            int attempts = 0;
            do
            {
                position = RandomUnit(random);
                attempts++;
            }
            while (attempts < 100 && IsTooCloseToHotspot(position, i, minimumAngle: 0.55f));

            _hotspots[i] = new TectonicHotspot(
                position,
                0.55f + random.NextSingle() * 0.45f,
                180_000f + random.NextSingle() * 320_000f,
                VelocityAt(position));
        }
    }

    public int PlateCount => _plates.Length;

    public IReadOnlyList<TectonicHotspot> Hotspots => _hotspots;

    public string PlateName(int index) => index >= 0 && index < _plates.Length ? _plates[index].Name : "?";

    /// <summary>
    /// Samples the plate mosaic and derives boundary masks at a direction.
    /// </summary>
    public TectonicSample Sample(Vector3 direction)
    {
        Vector3 point = Vector3.Normalize(direction);
        Vector3 warped = Warp(point);

        FindNearestTwo(warped, out int a, out int b, out float scoreA, out float scoreB);
        if (a > b)
        {
            (a, b) = (b, a);
            (scoreA, scoreB) = (scoreB, scoreA);
        }

        // The nearest boundary is the bisector between plates a and b. Positive signed distance is on b's
        // side, negative on a's side, independent of which happened to be nearest.
        Vector3 normal = _plates[a].Seed - _plates[b].Seed;
        float normalLength = normal.Length();
        if (normalLength < 1e-5f)
        {
            return TectonicSample.Empty;
        }

        normal /= normalLength;
        float signedDistance = -(scoreA - scoreB) / normalLength;
        float absoluteDistance = MathF.Abs(signedDistance);

        Vector3 tangent = Vector3.Cross(normal, warped);
        if (tangent.LengthSquared() < 1e-6f)
        {
            tangent = Vector3.Cross(normal, Vector3.UnitY);
            if (tangent.LengthSquared() < 1e-6f)
            {
                tangent = Vector3.Cross(normal, Vector3.UnitX);
            }
        }

        tangent = Vector3.Normalize(tangent);

        Vector3 velocityA = Vector3.Cross(_plates[a].EulerAxis * _plates[a].AngularRate, point);
        Vector3 velocityB = Vector3.Cross(_plates[b].EulerAxis * _plates[b].AngularRate, point);
        Vector3 relativeVelocity = velocityA - velocityB;
        float relativeSpeed = relativeVelocity.Length();
        float convergence = relativeSpeed < 1e-5f
            ? 0f
            : Math.Clamp(Vector3.Dot(relativeVelocity, normal) / relativeSpeed, -1f, 1f);
        float speedFactor = Math.Clamp(relativeSpeed / 0.07f, 0f, 1f);
        // A moderately convergent boundary already builds mountains; requiring near-head-on collision left too
        // little land in belts, so the p99 land elevation depended almost entirely on non-tectonic base relief.
        float converging = MathUtils.SmoothStep(0.12f, 0.55f, convergence) * speedFactor;
        float diverging = MathUtils.SmoothStep(0.12f, 0.55f, -convergence) * speedFactor;
        float transforming = Math.Clamp(1f - converging - diverging, 0f, 1f) * speedFactor;

        float influence = 1f - MathUtils.SmoothStep(0f, MaximumInfluenceMeters / _radius, absoluteDistance);
        float orogeny = influenceBand(absoluteDistance, OrogenWidthMeters) * converging;
        float trench = influenceBand(absoluteDistance, TrenchWidthMeters) * converging;
        float arc = gaussianBand(signedDistance * _radius - ArcOffsetMeters, ArcWidthMeters) * converging;
        float rift = influenceBand(absoluteDistance, RiftWidthMeters) * diverging;
        float shoulderDistance = MathF.Abs(absoluteDistance * _radius - RiftShoulderOffsetMeters);
        float riftShoulder = gaussianBand(shoulderDistance, RiftShoulderWidthMeters) * diverging;
        float oceanRidge = influenceBand(absoluteDistance, OceanRidgeWidthMeters) * diverging;
        float transform = influenceBand(absoluteDistance, TransformWidthMeters) * transforming;

        return new TectonicSample(
            influence,
            signedDistance * _radius,
            convergence,
            diverging,
            orogeny,
            arc,
            trench,
            rift,
            riftShoulder,
            oceanRidge,
            transform,
            tangent,
            a,
            b);

        float influenceBand(float distance, float width) =>
            1f - MathUtils.SmoothStep(0f, width / _radius, distance);
    }

    /// <summary>
    /// Local plate velocity at a direction, in arbitrary but consistent angular units.
    /// </summary>
    public Vector3 VelocityAt(Vector3 direction)
    {
        Vector3 point = Vector3.Normalize(direction);
        int nearest = NearestPlate(point);
        return Vector3.Cross(_plates[nearest].EulerAxis * _plates[nearest].AngularRate, point);
    }

    private Vector3 Warp(Vector3 point)
    {
        Vector3 offset = new(
            _warpNoise.Noise(point * 2.2f + new Vector3(17.1f, 0f, 0f)),
            _warpNoise.Noise(point * 2.2f + new Vector3(0f, 31.7f, 0f)),
            _warpNoise.Noise(point * 2.2f + new Vector3(0f, 0f, 47.3f)));

        return Vector3.Normalize(point + offset * 0.11f);
    }

    private void FindNearestTwo(Vector3 point, out int first, out int second, out float firstScore, out float secondScore)
    {
        first = 0;
        second = 1;
        firstScore = float.NegativeInfinity;
        secondScore = float.NegativeInfinity;

        for (int i = 0; i < _plates.Length; i++)
        {
            float score = Vector3.Dot(point, _plates[i].Seed) + _plates[i].Weight;
            if (score > firstScore)
            {
                secondScore = firstScore;
                second = first;
                firstScore = score;
                first = i;
            }
            else if (score > secondScore)
            {
                secondScore = score;
                second = i;
            }
        }
    }

    private int NearestPlate(Vector3 point)
    {
        int nearest = 0;
        float best = float.NegativeInfinity;
        for (int i = 0; i < _plates.Length; i++)
        {
            float score = Vector3.Dot(point, _plates[i].Seed) + _plates[i].Weight;
            if (score > best)
            {
                best = score;
                nearest = i;
            }
        }

        return nearest;
    }

    private static float gaussianBand(float delta, float width) => MathF.Exp(-(delta / width) * (delta / width));

    private bool IsTooCloseToHotspot(Vector3 position, int count, float minimumAngle)
    {
        float minimumDot = MathF.Cos(minimumAngle);
        for (int i = 0; i < count; i++)
        {
            if (Vector3.Dot(position, _hotspots[i].Position) > minimumDot)
            {
                return true;
            }
        }

        return false;
    }

    internal static Vector3 RandomUnit(DeterministicRandom random)
    {
        while (true)
        {
            Vector3 value = new(
                random.NextSingle() * 2f - 1f,
                random.NextSingle() * 2f - 1f,
                random.NextSingle() * 2f - 1f);

            float lengthSquared = value.LengthSquared();
            if (lengthSquared is > 0.01f and <= 1f)
            {
                return value / MathF.Sqrt(lengthSquared);
            }
        }
    }

    private readonly record struct Plate(Vector3 Seed, float Weight, Vector3 EulerAxis, float AngularRate, string Name);
}
