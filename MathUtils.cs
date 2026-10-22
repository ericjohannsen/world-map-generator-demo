using System.Numerics;

namespace WorldGen;

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
