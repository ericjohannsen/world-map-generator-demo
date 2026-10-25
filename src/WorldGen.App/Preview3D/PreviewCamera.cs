using System;
using System.Numerics;

namespace WorldGen.App.Preview3D;

/// <summary>Common contract for the orbit and free-flight preview cameras.</summary>
public interface IPreviewCamera
{
    Vector3 Eye { get; }
    Vector3 MeshFocus { get; }
    float MeshHalfExtent { get; }
    float FogDistance { get; }
    Matrix4x4 GetViewMatrix(float worldDiagonal);
    Matrix4x4 GetProjectionMatrix(float aspectRatio, float worldDiagonal);
    Vector3 CreateRayDirection(float normalizedX, float normalizedY, float aspectRatio, float worldDiagonal);
    IPreviewCamera Snapshot();
}

/// <summary>Movement request for one free-flight frame, in camera-local axes.</summary>
public struct FlightInput
{
    public float Forward;
    public float Right;
    public float Up;
    public float Turn;
    public bool Boost;
    public bool Precision;
}

/// <summary>
/// First-person flight camera using quaternion orientation to avoid gimbal lock at poles.
/// </summary>
public sealed class FreeFlightCamera : IPreviewCamera
{
    private readonly Terrain3DViewMode _mode;
    private Vector3 _velocity;
    private float _speedMultiplier = 1f;
    private Quaternion _orientation = Quaternion.Identity;

    private const float MinimumPitch = -1.53f;
    private const float MaximumPitch = 1.53f;

    public FreeFlightCamera(Terrain3DViewMode mode)
    {
        _mode = mode;
        FieldOfViewDegrees = 62f;
    }

    public Vector3 Position { get; set; }
    public Quaternion Orientation { get => _orientation; set => _orientation = Quaternion.Normalize(value); }
    public float FieldOfViewDegrees { get; set; }
    public float PlanetRadiusMeters { get; set; } = 6_371_000f;
    public float ClearanceMeters { get; set; } = 1.8f;
    public float GroundElevationMeters { get; set; }
    public bool HasState { get; set; }

    private float _yaw;
    private float _pitch;

    /// <summary>
    /// Legacy geographic heading, kept in sync from the authoritative quaternion. Assigning it rebuilds the
    /// orientation so older call sites that seed the camera with Yaw/Pitch still work.
    /// </summary>
    public float Yaw
    {
        get => _yaw;
        set
        {
            _yaw = value;
            RebuildOrientationFromAngles();
        }
    }

    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = Math.Clamp(value, MinimumPitch, MaximumPitch);
            RebuildOrientationFromAngles();
        }
    }

    public float SpeedMultiplier
    {
        get => _speedMultiplier;
        set => _speedMultiplier = Math.Clamp(value, 0f, 20f);
    }

    public Vector3 Eye => Position;

    // Local axes: +X = screen right, +Y = view direction, +Z = screen up. With the identity quaternion
    // that is Forward = +Y, Up = +Z, Right = +X, and Right = Forward x Up.
    public Vector3 Up => Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, _orientation));
    public Vector3 Forward => Vector3.Normalize(Vector3.Transform(Vector3.UnitY, _orientation));
    public Vector3 Right => Vector3.Normalize(Vector3.Transform(Vector3.UnitX, _orientation));

    public Vector3 MeshFocus
    {
        get
        {
            if (_mode == Terrain3DViewMode.Globe)
                return Position;

            Vector3 up = Up;
            Vector3 forward = Forward;
            Vector3 groundForward = forward - up * Vector3.Dot(forward, up);
            if (groundForward.LengthSquared() < 1e-6f) groundForward = Vector3.UnitY;
            groundForward = Vector3.Normalize(groundForward);
            Vector3 focus = Position + groundForward * (MeshHalfExtent * 0.35f);
            return new Vector3(focus.X, focus.Y, 0f);
        }
    }

    public float MeshHalfExtent
    {
        get
        {
            if (_mode == Terrain3DViewMode.Globe) return 1f;
            float altitude = AltitudeAboveGround;
            return Math.Clamp(altitude * 8f + 4_000f, 4_000f, 4_000_000f);
        }
    }

    public float CurrentSpeed => _velocity.Length();
    public float FogDistance => MathF.Max(1_000f, AltitudeAboveGround * 6f + 5_000f);
    public float CruiseSpeed => Math.Clamp(AltitudeAboveGround * 0.2f, 3f, 50_000f) * SpeedMultiplier;
    public float AltitudeAboveGround => MathF.Max(0f, Altitude - GroundElevationMeters);

    public float Altitude
    {
        get
        {
            if (_mode == Terrain3DViewMode.Globe) return MathF.Max(0f, Position.Length() - PlanetRadiusMeters);
            return MathF.Max(0f, Position.Z);
        }
    }

    public void Update(float deltaSeconds, in FlightInput input)
    {
        if (deltaSeconds <= 0f) return;

        // Q/E rotate around the local vertical (the radial direction). Rotating about the camera's own up
        // would tilt the horizon when already pitched; rotating about the radial is gimbal-lock free because
        // that axis comes from the position, not from a geographic basis.
        if (MathF.Abs(input.Turn) > 1e-6f)
        {
            Vector3 turnUp = _mode == Terrain3DViewMode.Globe
                ? SafeNormalize(Position, Vector3.UnitZ)
                : Vector3.UnitZ;
            // Positive input.Turn means "turn toward screen right". A positive rotation about the local
            // vertical moves the view left, so negate.
            float angle = -input.Turn * 1.5f * deltaSeconds;
            Quaternion yawRotation = Quaternion.CreateFromAxisAngle(turnUp, angle);
            _orientation = Quaternion.Normalize(yawRotation * _orientation);
        }

        float speed = CruiseSpeed;
        if (input.Boost) speed *= 4f;
        if (input.Precision) speed *= 0.25f;

        Vector3 oldPos = Position;
        Vector3 radialUp = _mode == Terrain3DViewMode.Globe ? SafeNormalize(oldPos, Vector3.UnitZ) : Vector3.UnitZ;
        Vector3 forward = Forward;
        Vector3 forwardBeforeMove = forward;
        float pitchBeforeMove = PitchOf(_orientation, radialUp);

        Vector3 tangentForward = forward - radialUp * Vector3.Dot(forward, radialUp);
        if (tangentForward.LengthSquared() < 1e-6f)
            tangentForward = SafeNormalize(Vector3.Cross(radialUp, Right), Vector3.UnitY);
        tangentForward = SafeNormalize(tangentForward, Vector3.UnitY);
        Vector3 right = SafeNormalize(Vector3.Cross(tangentForward, radialUp), Vector3.UnitX);

        Vector3 moveDir = (tangentForward * input.Forward) + (right * input.Right) + (radialUp * input.Up);
        if (moveDir.LengthSquared() > 1f) moveDir = Vector3.Normalize(moveDir);

        _velocity = moveDir * speed;

        if (_mode == Terrain3DViewMode.Globe)
        {
            float radius = oldPos.Length();
            Vector3 radial = SafeNormalize(oldPos, Vector3.UnitZ);
            float radialSpeed = Vector3.Dot(_velocity, radial);
            Vector3 tangentVel = _velocity - radial * radialSpeed;
            float newRadius = MathF.Max(PlanetRadiusMeters + 0.1f, radius + radialSpeed * deltaSeconds);

            Vector3 tangentStep = tangentVel * deltaSeconds;
            Vector3 moved = oldPos + tangentStep;
            if (moved.LengthSquared() > 1e-8f)
                Position = Vector3.Normalize(moved) * newRadius;
            else
                Position = oldPos * (newRadius / MathF.Max(1e-6f, radius));

            Vector3 newRadialUp = SafeNormalize(Position, Vector3.UnitZ);

            // Keep the world heading continuous while preserving pitch relative to the new surface. This is
            // what keeps a level flight level and a pitched flight pitched as the camera crosses the globe.
            Vector3 horizontal = forwardBeforeMove - newRadialUp * Vector3.Dot(forwardBeforeMove, newRadialUp);
            if (horizontal.LengthSquared() < 1e-8f)
            {
                horizontal = Vector3.Cross(newRadialUp, Vector3.UnitY);
            }
            if (horizontal.LengthSquared() < 1e-8f)
            {
                horizontal = Vector3.Cross(newRadialUp, Vector3.UnitX);
            }

            horizontal = SafeNormalize(horizontal, Vector3.UnitY);
            Vector3 desiredForward = SafeNormalize(
                MathF.Cos(pitchBeforeMove) * horizontal + MathF.Sin(pitchBeforeMove) * newRadialUp,
                horizontal);
            SetLookDirection(desiredForward);

            _velocity = tangentVel + newRadialUp * Vector3.Dot(_velocity, newRadialUp);
        }
        else
        {
            Position += _velocity * deltaSeconds;
        }

        SyncYawPitchFromOrientation();
    }

    public void TurnBy(float radians)
    {
        if (MathF.Abs(radians) < 1e-6f) return;

        // Positive radians rotate the view toward screen right, matching the sign of OnFlyTick's Turn
        // input. The axis is the local vertical, so a pitched view keeps its pitch instead of tilting.
        Vector3 turnUp = _mode == Terrain3DViewMode.Globe
            ? SafeNormalize(Position, Vector3.UnitZ)
            : Vector3.UnitZ;
        Quaternion yawRotation = Quaternion.CreateFromAxisAngle(turnUp, -radians);
        _orientation = Quaternion.Normalize(yawRotation * _orientation);
        SyncYawPitchFromOrientation();
    }

    public void Stop() => _velocity = Vector3.Zero;

    public void Look(float deltaYaw, float deltaPitch)
    {
        Vector3 radial = _mode == Terrain3DViewMode.Globe
            ? SafeNormalize(Position, Vector3.UnitZ)
            : Vector3.UnitZ;

        Quaternion afterYaw = _orientation;
        if (MathF.Abs(deltaYaw) > 1e-6f)
        {
            // World-space rotation about the local vertical keeps the horizon level when pitched. Negate so
            // a positive deltaYaw (mouse dragged right) turns toward screen right.
            afterYaw = Quaternion.Normalize(
                Quaternion.CreateFromAxisAngle(radial, -deltaYaw) * afterYaw);
        }

        // Local +Y is the view direction and local +Z is up, so a positive rotation about local X pitches
        // up. Clamp the resulting pitch by solving for the allowed delta.
        float pitchBefore = PitchOf(afterYaw, radial);
        float targetPitch = Math.Clamp(pitchBefore + deltaPitch, MinimumPitch, MaximumPitch);
        Quaternion orientation = Quaternion.Normalize(
            afterYaw * Quaternion.CreateFromAxisAngle(Vector3.UnitX, targetPitch - pitchBefore));

        _orientation = orientation;
        SyncYawPitchFromOrientation();
    }

    /// <summary>Seed the orientation from a geographic heading and pitch. Used by reset and by restore.</summary>
    public void SetYawPitch(float yaw, float pitch)
    {
        (Vector3 up, Vector3 north, Vector3 east) = GetBasis();
        float cosPitch = MathF.Cos(pitch);
        Vector3 horizontal = MathF.Cos(yaw) * north + MathF.Sin(yaw) * east;
        Vector3 forward = SafeNormalize(cosPitch * horizontal + MathF.Sin(pitch) * up, north);
        SetLookDirection(forward);
    }

    public void SetLookDirection(Vector3 direction)
    {
        if (direction.LengthSquared() < 1e-8f) return;

        Vector3 forward = Vector3.Normalize(direction);
        Vector3 radial = _mode == Terrain3DViewMode.Globe
            ? SafeNormalize(Position, Vector3.UnitZ)
            : Vector3.UnitZ;

        // Build an orthonormal camera frame with right = forward x upHint and up = right x forward. Fall back
        // to another reference axis if the look direction is parallel to the local vertical.
        Vector3 right = Vector3.Cross(forward, radial);
        if (right.LengthSquared() < 1e-8f) right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-8f) right = Vector3.Cross(forward, Vector3.UnitX);
        right = SafeNormalize(right, Vector3.UnitX);
        Vector3 up = SafeNormalize(Vector3.Cross(right, forward), radial);

        // System.Numerics transforms row vectors, so the rotation matrix rows are the world images of the
        // local X/Y/Z axes. Local X = right, local Y = forward, local Z = up.
        Matrix4x4 matrix = new(
            right.X, right.Y, right.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            0f, 0f, 0f, 1f);

        _orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
        SyncYawPitchFromOrientation();
    }

    public void AdjustSpeed(float wheelDelta)
    {
        if (SpeedMultiplier <= 0f) { if (wheelDelta > 0f) SpeedMultiplier = 0.1f; return; }
        SpeedMultiplier *= MathF.Exp(wheelDelta * 0.15f);
    }

    public void AdjustSpeedStep(int direction) => SpeedMultiplier = Math.Clamp(SpeedMultiplier + direction * 0.1f, 0f, 20f);

    public void Reset(Vector3 position, Vector3 lookDirection, float speedMultiplier = 1f)
    {
        Position = position;
        SetLookDirection(lookDirection);
        SpeedMultiplier = speedMultiplier;
        HasState = true;
        Stop();
    }

    public IPreviewCamera Snapshot() => new FreeFlightCamera(_mode)
    {
        Position = Position,
        Yaw = _yaw,
        Pitch = _pitch,
        Orientation = Orientation,
        FieldOfViewDegrees = FieldOfViewDegrees,
        PlanetRadiusMeters = PlanetRadiusMeters,
        ClearanceMeters = ClearanceMeters,
        SpeedMultiplier = SpeedMultiplier,
        HasState = HasState,
        GroundElevationMeters = GroundElevationMeters
    };

    public Matrix4x4 GetViewMatrix(float worldDiagonal)
    {
        _ = worldDiagonal;
        return Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio, float worldDiagonal)
    {
        float near = 0.5f;
        float far = MathF.Max(20_000f, worldDiagonal * 2f);
        return Matrix4x4.CreatePerspectiveFieldOfView((float)(FieldOfViewDegrees * Math.PI / 180.0), MathF.Max(0.05f, aspectRatio), near, far);
    }

    public Vector3 CreateRayDirection(float normalizedX, float normalizedY, float aspectRatio, float worldDiagonal)
    {
        Matrix4x4 viewProjection = GetViewMatrix(worldDiagonal) * GetProjectionMatrix(aspectRatio, worldDiagonal);
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse)) return Forward;
        Vector4 nearPoint = Vector4.Transform(new Vector4(normalizedX, normalizedY, -1f, 1f), inverse);
        Vector4 farPoint = Vector4.Transform(new Vector4(normalizedX, normalizedY, 1f, 1f), inverse);
        Vector3 nearWorld = new(nearPoint.X / nearPoint.W, nearPoint.Y / nearPoint.W, nearPoint.Z / nearPoint.W);
        Vector3 farWorld = new(farPoint.X / farPoint.W, farPoint.Y / farPoint.W, farPoint.Z / farPoint.W);
        Vector3 direction = farWorld - nearWorld;
        return direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Forward;
    }

    /// <summary>
    /// Keep the legacy Yaw/Pitch scalars in step with the authoritative quaternion so persistence and the
    /// HUD are meaningful. This never feeds back into the orientation, so polar instability here cannot move
    /// the camera.
    /// </summary>
    private void SyncYawPitchFromOrientation()
    {
        (Vector3 up, Vector3 north, Vector3 east) = GetBasis();
        Vector3 forward = Forward;
        _pitch = Math.Clamp(
            MathF.Asin(Math.Clamp(Vector3.Dot(forward, up), -1f, 1f)),
            MinimumPitch,
            MaximumPitch);

        Vector3 horizontal = forward - up * Vector3.Dot(forward, up);
        if (horizontal.LengthSquared() > 1e-8f)
        {
            horizontal = Vector3.Normalize(horizontal);
            _yaw = MathF.Atan2(
                Vector3.Dot(horizontal, east),
                Vector3.Dot(horizontal, north));
        }
    }

    private void RebuildOrientationFromAngles()
    {
        // Position must be meaningful to build the geographic basis; ignore zero positions so construction
        // and regional cameras that never touch globe mode stay on the identity frame.
        if (!HasState && Position.LengthSquared() < 1e-8f)
        {
            return;
        }

        SetYawPitch(_yaw, _pitch);
    }

    private static float PitchOf(Quaternion orientation, Vector3 radial)
    {
        Vector3 forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orientation));
        return MathF.Asin(Math.Clamp(Vector3.Dot(forward, radial), -1f, 1f));
    }

    private (Vector3 Up, Vector3 North, Vector3 East) GetBasis()
    {
        if (_mode == Terrain3DViewMode.Regional)
        {
            return (Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        }

        Vector3 up = SafeNormalize(Position, Vector3.UnitZ);
        float r = MathF.Sqrt(Position.X * Position.X + Position.Z * Position.Z);
        Vector3 east;
        if (r < 1e-4f)
        {
            east = Vector3.Cross(Vector3.UnitY, up);
            if (east.LengthSquared() < 1e-6f)
            {
                east = Vector3.UnitX;
            }
        }
        else
        {
            float longitude = MathF.Atan2(Position.Z, Position.X);
            east = new Vector3(-MathF.Sin(longitude), 0f, MathF.Cos(longitude));
        }

        east = SafeNormalize(east - up * Vector3.Dot(east, up), Vector3.UnitX);
        Vector3 north = SafeNormalize(Vector3.Cross(east, up), Vector3.UnitY);
        return (up, north, east);
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-10f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }

}
