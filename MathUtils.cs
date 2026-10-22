using System.Numerics;

namespace WorldGen;

/// <summary>
/// A tiny, fully specified pseudo-random generator used for one-time structural placement (noise
/// permutation tables, plate/province/landmark seeding).
/// </summary>
/// <remarks>
/// This exists so that a given seed produces byte-identical worlds across .NET runtime versions.
/// <see cref="System.Random"/> only guarantees its seeded sequence within a runtime version; a major
/// upgrade may change it and silently move every mountain, province, and island. This generator is
/// defined entirely by the arithmetic below (a 64-bit SplitMix64 stream), so its output is fixed by
/// the code, not by the platform. See
/// <see href="../../docs/design/decisions.md#adr-012--fixed-prng-for-structural-generation">ADR-012</see>.
///
/// It is <em>not</em> cryptographic and must not be used for anything security related.
/// </remarks>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(int seed) => _state = unchecked((ulong)(uint)seed) * 0x9E3779B97F4A7C15UL;

    /// <summary>Returns a raw 64-bit value and advances the stream.</summary>
    public ulong NextUInt64()
    {
        // SplitMix64: increment then avalanche. Fixed constants, no platform dependence.
        _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
        ulong z = _state;
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    /// <summary>Uniform integer in <c>[0, maxExclusive)</c>. Throws for a non-positive bound.</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }

        // Lemire's unbiased multiply-shift rejection sampling.
        ulong bound = (ulong)maxExclusive;
        ulong threshold = unchecked((0UL - bound) % bound);
        while (true)
        {
            ulong value = NextUInt64();
            if (value >= threshold)
            {
                return (int)(((UInt128)value * (UInt128)bound) >> 64);
            }
        }
    }

    /// <summary>Uniform integer in <c>[minInclusive, maxExclusive)</c>.</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    /// <summary>Uniform float in <c>[0, 1)</c> using the top 24 bits, matching single precision.</summary>
    public float NextSingle() => (NextUInt64() >> 40) * (1f / (1 << 24));
}

public static class MathUtils
{
    /// <summary>Golden angle in radians, used to distribute points evenly on a sphere.</summary>
    public const float GoldenAngle = 2.39996323f;

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Mixes a world seed and a named stream into an independent deterministic seed.
    /// </summary>
    public static int DeriveSeed(int seed, int stream) => unchecked(seed * 1_000_003 + stream * 97_409 + 12_345);

    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    /// <summary>
    /// Returns the <paramref name="index"/>-th of <paramref name="count"/> points spread evenly over the unit sphere.
    /// </summary>
    public static Vector3 FibonacciSpherePoint(int index, int count)
    {
        float y = 1f - 2f * (index + 0.5f) / count;
        float radius = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        float angle = index * GoldenAngle;
        return new Vector3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius);
    }
}
