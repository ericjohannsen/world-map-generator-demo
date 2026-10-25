using System.Numerics;
using System.Runtime.CompilerServices;

namespace WorldGen;

/// <summary>
/// Improved Perlin noise (Ken Perlin, 2002) in three dimensions, with a seedable permutation table.
/// </summary>
/// <remarks>
/// The noise is sampled in 3D space, which lets terrain be evaluated on the surface of a sphere with no
/// poles, seams, or latitude distortion.
/// </remarks>
public sealed class PerlinNoise
{
    private readonly int[] _permutation = new int[512];

    public PerlinNoise(int seed)
    {
        var values = new int[256];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i;
        }

        var random = new DeterministicRandom(seed);
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        for (int i = 0; i < _permutation.Length; i++)
        {
            _permutation[i] = values[i & 255];
        }
    }

    /// <summary>
    /// Returns noise in the range [-1, 1] for the given position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Noise(Vector3 point) => Noise(point.X, point.Y, point.Z);

    /// <summary>
    /// Returns noise in the range [-1, 1] for the given position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public float Noise(float x, float y, float z)
    {
        int xi = (int)MathF.Floor(x);
        int yi = (int)MathF.Floor(y);
        int zi = (int)MathF.Floor(z);

        x -= xi;
        y -= yi;
        z -= zi;

        xi &= 255;
        yi &= 255;
        zi &= 255;

        float u = Fade(x);
        float v = Fade(y);
        float w = Fade(z);

        int a = _permutation[xi] + yi;
        int aa = _permutation[a] + zi;
        int ab = _permutation[a + 1] + zi;

        int b = _permutation[xi + 1] + yi;
        int ba = _permutation[b] + zi;
        int bb = _permutation[b + 1] + zi;

        return Lerp(
            Lerp(
                Lerp(Gradient(_permutation[aa], x, y, z), Gradient(_permutation[ba], x - 1f, y, z), u),
                Lerp(Gradient(_permutation[ab], x, y - 1f, z), Gradient(_permutation[bb], x - 1f, y - 1f, z), u),
                v),
            Lerp(
                Lerp(Gradient(_permutation[aa + 1], x, y, z - 1f), Gradient(_permutation[ba + 1], x - 1f, y, z - 1f), u),
                Lerp(Gradient(_permutation[ab + 1], x, y - 1f, z - 1f), Gradient(_permutation[bb + 1], x - 1f, y - 1f, z - 1f), u),
                v),
            w);
    }

    /// <summary>
    /// Sums octaves of noise and remaps the result to [0, 1].
    /// </summary>
    /// <remarks>
    /// Amplitudes are normalized against the infinite geometric series rather than the octave count actually
    /// used. That keeps the low-frequency shape identical at every level of detail, so a zoomed-in tile is
    /// consistent with a world-scale render that simply stops adding octaves earlier.
    /// </remarks>
    public float Fractal(Vector3 point, int octaves, float gain = 0.5f, float lacunarity = 2f)
    {
        ValidateOctaves(octaves, gain);

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;

        for (int i = 0; i < octaves; i++)
        {
            total += Noise(point * frequency) * amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return Math.Clamp(total * (1f - gain) * 0.5f + 0.5f, 0f, 1f);
    }

    /// <summary>
    /// Ridged fractal noise: sharp crests where the underlying noise crosses zero, used for mountain chains.
    /// </summary>
    /// <returns>Values in [0, 1], concentrated toward the low end with narrow ridges near 1.</returns>
    public float Ridged(Vector3 point, int octaves, float gain = 0.5f, float lacunarity = 2f)
    {
        ValidateOctaves(octaves, gain);

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;

        for (int i = 0; i < octaves; i++)
        {
            float signal = 1f - MathF.Abs(Noise(point * frequency));
            total += signal * signal * amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return Math.Clamp(total * (1f - gain), 0f, 1f);
    }

    private static void ValidateOctaves(int octaves, float gain)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(octaves, 0);
        if (gain is < 0f or >= 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Gain must be in [0, 1).");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Gradient(int hash, float x, float y, float z)
    {
        // Twelve gradient directions pointing at the edges of a cube.
        int h = hash & 15;
        float u = h < 8 ? x : y;
        float v = h < 4 ? y : h is 12 or 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
