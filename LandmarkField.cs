using System.Numerics;

namespace WorldGen;

public enum LandmarkKind
{
    Volcano,
    Caldera,
    Massif,
    Crater,
    Seamount,
}

/// <summary>
/// A sparse, named-scale landform placed in world space. Landmarks are not noise octaves: they are individual
/// features with a position, a footprint, and a profile, so they survive zooming and can be named or moved.
/// </summary>
public readonly record struct Landmark(
    Vector3 Position,
    float RadiusMeters,
    float HeightMeters,
    float DepthMeters,
    LandmarkKind Kind,
    float CosineOuterRadius);

/// <summary>
/// A small set of placed landforms: hotspot tracks, volcanic-arc cones, massifs, and impact craters. Features
/// are deterministic for a seed and additive on top of the tectonic and province fields.
/// </summary>
public sealed class LandmarkField
{
    private readonly Landmark[] _landmarks;
    private readonly float _radius;

    private LandmarkField(Landmark[] landmarks, float radius)
    {
        _landmarks = landmarks;
        _radius = radius;
    }

    public int Count => _landmarks.Length;

    public int CountOf(LandmarkKind kind)
    {
        int count = 0;
        foreach (Landmark landmark in _landmarks)
        {
            if (landmark.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Builds the landmark set. <paramref name="continentalness"/> is the signed continentalness value
    /// (positive on land), used only to choose plausible feature types and placements.
    /// </summary>
    public static LandmarkField Build(
        int seed,
        int budget,
        float planetRadiusMeters,
        TectonicSketch tectonics,
        Func<Vector3, float> continentalness)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 0);

        var random = new Random(MathUtils.DeriveSeed(seed, 51));
        var landmarks = new List<Landmark>(budget * 4);

        void Add(
            Vector3 position,
            float radiusMeters,
            float heightMeters,
            float depthMeters,
            LandmarkKind kind,
            float minimumSeparationMeters = 0f)
        {
            position = Vector3.Normalize(position);
            if (minimumSeparationMeters > 0f && IsTooClose(position, minimumSeparationMeters))
            {
                return;
            }

            float outerAngle = Math.Min(MathF.PI, 2f * radiusMeters / planetRadiusMeters);
            landmarks.Add(new Landmark(
                position,
                radiusMeters,
                heightMeters,
                depthMeters,
                kind,
                MathF.Cos(outerAngle)));
        }

        bool IsTooClose(Vector3 position, float minimumSeparationMeters)
        {
            float minimumDot = MathF.Cos(minimumSeparationMeters / planetRadiusMeters);
            foreach (Landmark landmark in landmarks)
            {
                if (Vector3.Dot(position, landmark.Position) > minimumDot)
                {
                    return true;
                }
            }

            return false;
        }

        // Hotspot tracks: a young volcano at the hotspot, older seamounts trailing behind the plate motion.
        foreach (TectonicHotspot hotspot in tectonics.Hotspots)
        {
            Vector3 velocity = hotspot.Velocity;
            Vector3 direction;
            if (velocity.LengthSquared() < 1e-8f)
            {
                direction = TectonicSketch.RandomUnit(random);
            }
            else
            {
                direction = Vector3.Normalize(velocity);
            }

            int links = 5 + random.Next(3);
            for (int i = 0; i < links; i++)
            {
                float fade = 1f - i / (float)(links + 1);
                Vector3 position = Vector3.Normalize(
                    hotspot.Position - direction * (i * 260_000f / planetRadiusMeters));
                float radius = hotspot.RadiusMeters * (0.45f + 0.55f * fade);
                float height = 350f + 2_300f * hotspot.Strength * fade;

                LandmarkKind kind = i == 0
                    ? (random.NextSingle() < 0.5f ? LandmarkKind.Caldera : LandmarkKind.Volcano)
                    : LandmarkKind.Seamount;

                Add(position, radius, height, kind == LandmarkKind.Caldera ? 650f : 0f, kind);
            }
        }

        // Volcanic arcs: sample the tectonic field and keep sites on the arc band.
        int arcTarget = Math.Max(6, budget);
        int arcPlaced = 0;
        int attempts = 0;
        while (arcPlaced < arcTarget && attempts < arcTarget * 300)
        {
            attempts++;
            Vector3 position = TectonicSketch.RandomUnit(random);
            TectonicSample sample = tectonics.Sample(position);
            if (sample.Arc < 0.55f || random.NextSingle() > 0.10f)
            {
                continue;
            }

            bool onLand = continentalness(position) > 0.02f;
            float radius = 55_000f + random.NextSingle() * 95_000f;
            float height = 800f + random.NextSingle() * 2_800f;

            Add(
                position,
                radius,
                height,
                random.NextSingle() < 0.15f ? 300f : 0f,
                onLand ? LandmarkKind.Volcano : LandmarkKind.Seamount,
                minimumSeparationMeters: 220_000f);
            arcPlaced++;
        }

        // A handful of broad massifs, calderas, and impact craters on land.
        int extraPlaced = 0;
        attempts = 0;
        while (extraPlaced < budget && attempts < budget * 120)
        {
            attempts++;
            Vector3 position = TectonicSketch.RandomUnit(random);
            if (continentalness(position) < 0.02f)
            {
                continue;
            }

            if (random.NextSingle() < 0.35f)
            {
                float radius = 60_000f + random.NextSingle() * 140_000f;
                float rim = 200f + random.NextSingle() * 500f;
                float depth = 250f + random.NextSingle() * 700f;
                Add(position, radius, rim, depth, LandmarkKind.Crater, minimumSeparationMeters: 260_000f);
            }
            else
            {
                float radius = 170_000f + random.NextSingle() * 280_000f;
                float height = extraPlaced < 2
                    ? 1_800f + random.NextSingle() * 1_800f
                    : 500f + random.NextSingle() * 1_400f;
                Add(position, radius, height, 0f, LandmarkKind.Massif, minimumSeparationMeters: 340_000f);
            }

            extraPlaced++;
        }

        return new LandmarkField(landmarks.ToArray(), planetRadiusMeters);
    }

    /// <summary>
    /// Additive elevation contribution of all landmarks at a direction. The caller decides whether the result
    /// is land or ocean; a seamount may raise the sea floor but does not redefine the continental coastline.
    /// </summary>
    public float Elevation(Vector3 direction)
    {
        Vector3 point = Vector3.Normalize(direction);
        float total = 0f;

        foreach (Landmark landmark in _landmarks)
        {
            float dot = Vector3.Dot(point, landmark.Position);
            if (dot < landmark.CosineOuterRadius)
            {
                continue;
            }

            float chord = MathF.Sqrt(MathF.Max(0f, 2f - 2f * dot)) * _radius;
            float t = chord / landmark.RadiusMeters;

            total += landmark.Kind switch
            {
                LandmarkKind.Volcano => Cone(landmark.HeightMeters, t),
                LandmarkKind.Seamount => 0.8f * Cone(landmark.HeightMeters, t),
                LandmarkKind.Caldera => Caldera(landmark.HeightMeters, landmark.DepthMeters, t),
                LandmarkKind.Massif => landmark.HeightMeters * MathF.Exp(-2.4f * t * t),
                LandmarkKind.Crater => Crater(landmark.HeightMeters, landmark.DepthMeters, t),
                _ => 0f,
            };
        }

        return total;
    }

    private static float Cone(float height, float t)
    {
        if (t >= 1f)
        {
            return 0f;
        }

        float f = 1f - t * t;
        return height * f * f;
    }

    private static float Caldera(float height, float depth, float t) =>
        height * MathF.Exp(-2.2f * t * t) - depth * MathF.Exp(-8f * t * t);

    private static float Crater(float rimHeight, float depth, float t)
    {
        float rim = MathF.Exp(-((t - 0.85f) / 0.28f) * ((t - 0.85f) / 0.28f));
        float bowl = MathF.Exp(-3f * t * t);
        return rimHeight * rim - depth * bowl;
    }
}
