using System.Numerics;

namespace WorldGen;

/// <summary>
/// Geomorphic character of a region. Provinces are not tectonic plates; they describe how the surface behaves
/// within and across plates (old and smooth, high and flat, wet and dissected, and so on).
/// </summary>
public enum ProvinceKind
{
    Craton,
    Plateau,
    Highlands,
    Basin,
    Volcanic,
    Karst,
    Glacial,
    PassiveMargin,
}

/// <summary>
/// Blended parameters that modulate the elevation model. Kept deliberately small: each field must have a
/// visible effect at world scale and a stable meaning under zoom.
/// </summary>
public readonly record struct ProvinceParameters(
    float ContinentalReliefScale,
    float MountainReliefScale,
    float HillReliefScale,
    float DetailReliefScale,
    float RidgeFrequencyScale,
    float BaseOffsetMeters,
    float Anisotropy,
    float Roughness,
    float Erodibility)
{
    public static ProvinceParameters Lerp(ProvinceParameters from, ProvinceParameters to, float t) => new(
        MathUtils.Lerp(from.ContinentalReliefScale, to.ContinentalReliefScale, t),
        MathUtils.Lerp(from.MountainReliefScale, to.MountainReliefScale, t),
        MathUtils.Lerp(from.HillReliefScale, to.HillReliefScale, t),
        MathUtils.Lerp(from.DetailReliefScale, to.DetailReliefScale, t),
        MathUtils.Lerp(from.RidgeFrequencyScale, to.RidgeFrequencyScale, t),
        MathUtils.Lerp(from.BaseOffsetMeters, to.BaseOffsetMeters, t),
        MathUtils.Lerp(from.Anisotropy, to.Anisotropy, t),
        MathUtils.Lerp(from.Roughness, to.Roughness, t),
        MathUtils.Lerp(from.Erodibility, to.Erodibility, t));
}

public readonly record struct ProvinceSample(
    ProvinceParameters Parameters,
    ProvinceKind DominantKind,
    float DominantWeight,
    float SecondaryWeight);

/// <summary>
/// A low-frequency, deterministic partition of the sphere into a small number of geomorphic provinces.
/// Boundaries are soft: the two nearest provinces are blended, so parameters change continuously and no
/// polygon edge is visible in the terrain.
/// </summary>
public sealed class ProvinceField
{
    private readonly Province[] _provinces;

    public ProvinceField(int seed, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);

        var random = new DeterministicRandom(MathUtils.DeriveSeed(seed, 41));
        ProvinceKind[] palette = BuildPalette(count);
        Shuffle(palette, random);

        _provinces = new Province[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 anchor = MathUtils.FibonacciSpherePoint(i, count);
            Vector3 jitter = TectonicSketch.RandomUnit(random) * (0.08f + random.NextSingle() * 0.12f);
            _provinces[i] = new Province(
                Vector3.Normalize(anchor + jitter),
                (random.NextSingle() * 2f - 1f) * 0.045f,
                palette[i],
                Preset(palette[i]));
        }
    }

    public int Count => _provinces.Length;

    public ProvinceSample Sample(Vector3 direction)
    {
        Vector3 point = Vector3.Normalize(direction);

        int first = 0;
        int second = 1;
        float firstScore = float.NegativeInfinity;
        float secondScore = float.NegativeInfinity;

        for (int i = 0; i < _provinces.Length; i++)
        {
            float score = Vector3.Dot(point, _provinces[i].Seed) + _provinces[i].Weight;
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

        // Near the boundary the two provinces blend 50/50; away from it the nearest one dominates. The result
        // depends only on direction, so it is identical at every render resolution and on every tile.
        float dominance = firstScore - secondScore;
        float blend = 0.5f + 0.5f * MathUtils.SmoothStep(0f, 0.14f, dominance);
        ProvinceParameters parameters = ProvinceParameters.Lerp(
            _provinces[second].Parameters,
            _provinces[first].Parameters,
            blend);

        return new ProvinceSample(
            parameters,
            _provinces[first].Kind,
            blend,
            1f - blend);
    }

    public ProvinceKind DominantKind(Vector3 direction)
    {
        int nearest = 0;
        float best = float.NegativeInfinity;
        for (int i = 0; i < _provinces.Length; i++)
        {
            float score = Vector3.Dot(Vector3.Normalize(direction), _provinces[i].Seed) + _provinces[i].Weight;
            if (score > best)
            {
                best = score;
                nearest = i;
            }
        }

        return _provinces[nearest].Kind;
    }

    private static ProvinceKind[] BuildPalette(int count)
    {
        ProvinceKind[] cycle =
        [
            ProvinceKind.Craton,
            ProvinceKind.Craton,
            ProvinceKind.Plateau,
            ProvinceKind.Highlands,
            ProvinceKind.Basin,
            ProvinceKind.Volcanic,
            ProvinceKind.Karst,
            ProvinceKind.Glacial,
            ProvinceKind.PassiveMargin,
        ];

        var palette = new ProvinceKind[count];
        for (int i = 0; i < count; i++)
        {
            palette[i] = cycle[i % cycle.Length];
        }

        return palette;
    }

    private static void Shuffle<T>(T[] values, DeterministicRandom random)
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static ProvinceParameters Preset(ProvinceKind kind) => kind switch
    {
        ProvinceKind.Craton => new ProvinceParameters(
            ContinentalReliefScale: 0.55f,
            MountainReliefScale: 0.35f,
            HillReliefScale: 0.45f,
            DetailReliefScale: 0.70f,
            RidgeFrequencyScale: 0.75f,
            BaseOffsetMeters: 0f,
            Anisotropy: 0.00f,
            Roughness: 0.50f,
            Erodibility: 0.45f),
        ProvinceKind.Plateau => new ProvinceParameters(
            ContinentalReliefScale: 0.85f,
            MountainReliefScale: 0.55f,
            HillReliefScale: 0.40f,
            DetailReliefScale: 0.65f,
            RidgeFrequencyScale: 0.72f,
            BaseOffsetMeters: 1_850f,
            Anisotropy: 0.10f,
            Roughness: 0.50f,
            Erodibility: 0.50f),
        ProvinceKind.Highlands => new ProvinceParameters(
            ContinentalReliefScale: 1.05f,
            MountainReliefScale: 1.10f,
            HillReliefScale: 1.20f,
            DetailReliefScale: 1.00f,
            RidgeFrequencyScale: 1.00f,
            BaseOffsetMeters: 450f,
            Anisotropy: 0.35f,
            Roughness: 0.52f,
            Erodibility: 0.90f),
        ProvinceKind.Basin => new ProvinceParameters(
            ContinentalReliefScale: 0.42f,
            MountainReliefScale: 0.30f,
            HillReliefScale: 0.50f,
            DetailReliefScale: 0.80f,
            RidgeFrequencyScale: 0.90f,
            BaseOffsetMeters: -360f,
            Anisotropy: 0.00f,
            Roughness: 0.48f,
            Erodibility: 1.20f),
        ProvinceKind.Volcanic => new ProvinceParameters(
            ContinentalReliefScale: 0.95f,
            MountainReliefScale: 1.25f,
            HillReliefScale: 1.30f,
            DetailReliefScale: 1.15f,
            RidgeFrequencyScale: 1.20f,
            BaseOffsetMeters: 320f,
            Anisotropy: 0.50f,
            Roughness: 0.55f,
            Erodibility: 1.00f),
        ProvinceKind.Karst => new ProvinceParameters(
            ContinentalReliefScale: 0.70f,
            MountainReliefScale: 0.55f,
            HillReliefScale: 0.80f,
            DetailReliefScale: 1.30f,
            RidgeFrequencyScale: 1.40f,
            BaseOffsetMeters: 100f,
            Anisotropy: 0.00f,
            Roughness: 0.58f,
            Erodibility: 0.80f),
        ProvinceKind.Glacial => new ProvinceParameters(
            ContinentalReliefScale: 0.82f,
            MountainReliefScale: 0.85f,
            HillReliefScale: 1.40f,
            DetailReliefScale: 1.40f,
            RidgeFrequencyScale: 1.30f,
            BaseOffsetMeters: 0f,
            Anisotropy: 0.20f,
            Roughness: 0.54f,
            Erodibility: 1.30f),
        ProvinceKind.PassiveMargin => new ProvinceParameters(
            ContinentalReliefScale: 0.62f,
            MountainReliefScale: 0.25f,
            HillReliefScale: 0.60f,
            DetailReliefScale: 0.90f,
            RidgeFrequencyScale: 0.80f,
            BaseOffsetMeters: -120f,
            Anisotropy: 0.15f,
            Roughness: 0.50f,
            Erodibility: 1.10f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private readonly record struct Province(
        Vector3 Seed,
        float Weight,
        ProvinceKind Kind,
        ProvinceParameters Parameters);
}
