using System;
using System.Numerics;

namespace WorldGen.App.Preview3D;

/// <summary>
/// Spherical orbit camera in the preview's local tangent frame: x east, y north, z up, metres.
/// </summary>
/// <remarks>
/// The camera orbits a point on the ground rather than the origin, because terrain inspection is about
/// "stand here and look at that ridge". Target elevation is snapped to the surface and the eye is pushed
/// above the surface after every change, so tilt and zoom cannot clip through a mountain.
/// </remarks>
public sealed class OrbitCamera : IPreviewCamera
{
    public float MinimumPitch { get; set; } = 0.05f;

    public float MaximumPitch { get; set; } = 1.5533f;

    public Vector3 Target { get; set; }

    public float Distance { get; set; } = 1_500_000f;

    public float Yaw { get; set; } = 0.55f;

    public float Pitch { get; set; } = 0.85f;

    public float FieldOfViewDegrees { get; set; } = 55f;

    public Vector3 DirectionFromTarget
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            return new Vector3(
                cosPitch * MathF.Cos(Yaw),
                cosPitch * MathF.Sin(Yaw),
                MathF.Sin(Pitch));
        }
    }

    public Vector3 Eye => Target + DirectionFromTarget * Distance;

    public Vector3 MeshFocus => Target;

    public float MeshHalfExtent => MathF.Max(1f, Distance * 0.85f);

    public float FogDistance => Distance;

    public Matrix4x4 GetViewMatrix(float worldDiagonal)
    {
        _ = worldDiagonal;
        return Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitZ);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio, float worldDiagonal)
    {
        float near = MathF.Max(1f, Distance * 0.01f);
        float far = MathF.Max(near * 1000f, Distance * 12f + worldDiagonal * 2f);
        return Matrix4x4.CreatePerspectiveFieldOfView(
            (float)MathUtils.ToRadians(FieldOfViewDegrees),
            MathF.Max(0.05f, aspectRatio),
            near,
            far);
    }

    public Vector3 CreateRayDirection(float normalizedX, float normalizedY, float aspectRatio, float worldDiagonal)
    {
        Matrix4x4 view = GetViewMatrix(worldDiagonal);
        Matrix4x4 projection = GetProjectionMatrix(aspectRatio, worldDiagonal);
        Matrix4x4 viewProjection = view * projection;

        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            return Vector3.Normalize(Target - Eye);
        }

        Vector4 nearPoint = Vector4.Transform(new Vector4(normalizedX, normalizedY, -1f, 1f), inverse);
        Vector4 farPoint = Vector4.Transform(new Vector4(normalizedX, normalizedY, 1f, 1f), inverse);

        Vector3 nearWorld = new(nearPoint.X / nearPoint.W, nearPoint.Y / nearPoint.W, nearPoint.Z / nearPoint.W);
        Vector3 farWorld = new(farPoint.X / farPoint.W, farPoint.Y / farPoint.W, farPoint.Z / farPoint.W);

        Vector3 direction = farWorld - nearWorld;
        float length = direction.Length();
        return length > 1e-6f ? direction / length : Vector3.Normalize(Target - Eye);
    }

    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, MinimumPitch, MaximumPitch);

        if (Yaw > MathF.PI * 2f || Yaw < -MathF.PI * 2f)
        {
            Yaw %= MathF.PI * 2f;
        }
    }

    /// <summary>
    /// Pans the target in the camera's ground plane. Screen-space pan is scaled by distance so the
    /// ground moves under the cursor at roughly one pixel per pixel.
    /// </summary>
    public void Pan(float deltaRight, float deltaForward, float worldUnitsPerPixel)
    {
        Vector3 forward = new(-MathF.Cos(Yaw), -MathF.Sin(Yaw), 0f);
        Vector3 right = new(-MathF.Sin(Yaw), MathF.Cos(Yaw), 0f);

        Target += right * (deltaRight * worldUnitsPerPixel);
        Target += forward * (deltaForward * worldUnitsPerPixel);
        Target = new Vector3(Target.X, Target.Y, Target.Z);
    }

    public void Zoom(float factor, Vector3? anchor, float minimumDistance, float maximumDistance)
    {
        float previous = Distance;
        float next = Math.Clamp(Distance * factor, minimumDistance, maximumDistance);

        if (anchor is { } point && previous > 1f)
        {
            // Move the target toward the anchor so zooming stays anchored to what is under the cursor.
            float shift = 1f - next / previous;
            Target += (point - Target) * Math.Clamp(shift, 0f, 1f);
        }

        Distance = next;
    }

    /// <summary>
    /// Snaps the target to the surface and lifts the eye above the terrain if a change would bury it.
    /// </summary>
    public void ClampToGround(Func<float, float, float> heightAt, float eyeClearanceMeters)
    {
        Target = new Vector3(Target.X, Target.Y, MathF.Max(0f, heightAt(Target.X, Target.Y)));

        for (int attempt = 0; attempt < 24; attempt++)
        {
            Vector3 eye = Eye;
            float ground = MathF.Max(0f, heightAt(eye.X, eye.Y));
            if (eye.Z > ground + eyeClearanceMeters)
            {
                return;
            }

            Pitch = MathF.Min(MaximumPitch, Pitch + 0.035f);
        }

        // At high exaggeration the eye can still be inside a peak at maximum pitch: back it out.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Vector3 eye = Eye;
            float ground = MathF.Max(0f, heightAt(eye.X, eye.Y));
            if (eye.Z > ground + eyeClearanceMeters)
            {
                return;
            }

            Distance *= 1.3f;
        }
    }

    public void Reset(Vector3 target, float distance)
    {
        Target = target;
        Distance = distance;
        Yaw = 0.55f;
        Pitch = 0.85f;
    }

    public IPreviewCamera Snapshot() => new OrbitCamera
    {
        Target = Target,
        Distance = Distance,
        Yaw = Yaw,
        Pitch = Pitch,
        FieldOfViewDegrees = FieldOfViewDegrees,
        MinimumPitch = MinimumPitch,
        MaximumPitch = MaximumPitch,
    };
}
