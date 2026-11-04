using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>カメラの向きを表す 3 本の軸（前・右・上）。</summary>
public readonly record struct CameraBasis(Vector3 Forward, Vector3 Right, Vector3 Up)
{
    /// <summary>シーンの項目と同じ回転の決め（Z → X → Y の順）で軸を作る。角度はラジアン。</summary>
    public static CameraBasis FromAngles(float yaw, float pitch, float roll = 0f)
    {
        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        return new CameraBasis(
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, rotation)),
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, rotation)),
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rotation)));
    }

    /// <summary>左手系のビュー行列（行ベクトル × 行列。DirectXMath と同じ並び）。</summary>
    public Matrix4x4 CreateViewMatrix(Vector3 eye) => new(
        Right.X, Up.X, Forward.X, 0f,
        Right.Y, Up.Y, Forward.Y, 0f,
        Right.Z, Up.Z, Forward.Z, 0f,
        -Vector3.Dot(Right, eye), -Vector3.Dot(Up, eye), -Vector3.Dot(Forward, eye), 1f);
}

public readonly record struct ViewMatrices(Matrix4x4 View, Matrix4x4 Projection, CameraBasis Basis, Vector3 Eye, bool IsOrthographic);

/// <summary>
/// プレビューの 1 区画の視点。注視点のまわりを回る形（Blender と同じ考え方）。
/// 固定視点で回転させると自由視点（透視投影）へ移る。平行移動とズームでは固定視点のまま。
/// カメラ視点では、どの操作でもカメラの位置から自由視点へ移る（カメラそのものは動かさない）。
/// </summary>
public sealed class ViewCamera
{
    public const float DefaultFieldOfViewDegrees = 45f;
    private const float OrbitRadiansPerPixel = 0.008f;
    private const float ZoomFactorPerStep = 0.85f;
    private const float MinDistance = 0.05f;
    private const float MaxDistance = 5000f;
    private const float OrthographicEyeDistance = 1000f;

    private static readonly Vector3 DefaultTarget = new(0f, 0.5f, 0f);

    public ViewCamera(ViewKind kind = ViewKind.Free)
    {
        Reset();
        SetKind(kind);
    }

    public event EventHandler? Changed;

    public ViewKind Kind { get; private set; } = ViewKind.Free;

    public Vector3 Target { get; private set; }

    public float Distance { get; private set; }

    /// <summary>ラジアン。</summary>
    public float Yaw { get; private set; }

    /// <summary>ラジアン。正で見下ろす。</summary>
    public float Pitch { get; private set; }

    public bool IsOrthographic { get; private set; }

    public float FieldOfView { get; } = DefaultFieldOfViewDegrees * MathF.PI / 180f;

    /// <summary>注視点の位置で画面の縦に収まる長さ。平行移動とズームの量の基準。</summary>
    public float VisibleHeight => 2f * Distance * MathF.Tan(FieldOfView / 2f);

    public void Reset()
    {
        Target = DefaultTarget;
        Distance = 8f;
        Yaw = -30f * MathF.PI / 180f;
        Pitch = 20f * MathF.PI / 180f;
        if (Kind.IsFixed())
        {
            ApplyFixedAngles(Kind);
        }
        RaiseChanged();
    }

    public void SetKind(ViewKind kind)
    {
        if (kind.IsFixed())
        {
            ApplyFixedAngles(kind);
            IsOrthographic = true;
        }
        else if (kind == ViewKind.Free && Kind != ViewKind.Free)
        {
            IsOrthographic = false;
        }
        Kind = kind;
        RaiseChanged();
    }

    /// <summary>透視投影と平行投影を切り替える。カメラ視点ではシーンのカメラに従うので何もしない。</summary>
    public void ToggleProjection()
    {
        if (Kind == ViewKind.Camera)
        {
            return;
        }
        IsOrthographic = !IsOrthographic;
        RaiseChanged();
    }

    public void Orbit(Vector2 deltaPixels, CameraNode? sceneCamera = null)
    {
        LeaveCameraView(sceneCamera);
        if (Kind.IsFixed())
        {
            Kind = ViewKind.Free;
            IsOrthographic = false;
        }
        Yaw = WrapAngle(Yaw + (deltaPixels.X * OrbitRadiansPerPixel));
        Pitch = Math.Clamp(Pitch + (deltaPixels.Y * OrbitRadiansPerPixel), -MathF.PI / 2f, MathF.PI / 2f);
        RaiseChanged();
    }

    public void Pan(Vector2 deltaPixels, float viewportHeightPixels, CameraNode? sceneCamera = null)
    {
        if (viewportHeightPixels <= 0f)
        {
            return;
        }
        LeaveCameraView(sceneCamera);
        CameraBasis basis = CameraBasis.FromAngles(Yaw, Pitch);
        float unitsPerPixel = VisibleHeight / viewportHeightPixels;
        Target += (-basis.Right * deltaPixels.X * unitsPerPixel) + (basis.Up * deltaPixels.Y * unitsPerPixel);
        RaiseChanged();
    }

    /// <summary>ホイールの 1 目盛りを 1 段として近づく（正）・離れる（負）。</summary>
    public void Zoom(float steps, CameraNode? sceneCamera = null)
    {
        LeaveCameraView(sceneCamera);
        Distance = Math.Clamp(Distance * MathF.Pow(ZoomFactorPerStep, steps), MinDistance, MaxDistance);
        RaiseChanged();
    }

    /// <summary>指定した球が収まるよう注視点と距離を合わせる。向きは変えない。</summary>
    public void Focus(Vector3 center, float radius, CameraNode? sceneCamera = null)
    {
        LeaveCameraView(sceneCamera);
        Target = center;
        Distance = Math.Clamp(Math.Max(radius, 0.01f) / MathF.Sin(FieldOfView / 2f) * 1.2f, MinDistance, MaxDistance);
        RaiseChanged();
    }

    public ViewMatrices GetMatrices(float aspectRatio, CameraNode? sceneCamera)
    {
        float aspect = aspectRatio > 0f && float.IsFinite(aspectRatio) ? aspectRatio : 1f;

        if (Kind == ViewKind.Camera && sceneCamera is not null)
        {
            Vector3 radians = sceneCamera.Rotation * (MathF.PI / 180f);
            CameraBasis cameraBasis = CameraBasis.FromAngles(radians.Y, radians.X, radians.Z);
            Matrix4x4 cameraProjection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
                sceneCamera.FieldOfView * MathF.PI / 180f, aspect, sceneCamera.NearClip, sceneCamera.FarClip);
            return new ViewMatrices(
                cameraBasis.CreateViewMatrix(sceneCamera.Position), cameraProjection, cameraBasis, sceneCamera.Position, false);
        }

        CameraBasis basis = CameraBasis.FromAngles(Yaw, Pitch);
        if (IsOrthographic)
        {
            // 平行投影では距離で見え方が変わらないので、手前の物が切れないよう目の位置を十分に引く
            Vector3 orthoEye = Target - (basis.Forward * OrthographicEyeDistance);
            float height = VisibleHeight;
            Matrix4x4 ortho = Matrix4x4.CreateOrthographicLeftHanded(height * aspect, height, 0.1f, OrthographicEyeDistance * 2f);
            return new ViewMatrices(basis.CreateViewMatrix(orthoEye), ortho, basis, orthoEye, true);
        }

        Vector3 eye = Target - (basis.Forward * Distance);
        float near = Math.Max(0.01f, Distance * 0.005f);
        float far = Math.Max(1000f, Distance * 200f);
        Matrix4x4 perspective = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(FieldOfView, aspect, near, far);
        return new ViewMatrices(basis.CreateViewMatrix(eye), perspective, basis, eye, false);
    }

    private void LeaveCameraView(CameraNode? sceneCamera)
    {
        if (Kind != ViewKind.Camera)
        {
            return;
        }
        Kind = ViewKind.Free;
        IsOrthographic = false;
        if (sceneCamera is null)
        {
            return;
        }

        // カメラの位置と向きから見え方が変わらないように自由視点を合わせる
        Vector3 radians = sceneCamera.Rotation * (MathF.PI / 180f);
        Yaw = WrapAngle(radians.Y);
        Pitch = Math.Clamp(radians.X, -MathF.PI / 2f, MathF.PI / 2f);
        CameraBasis basis = CameraBasis.FromAngles(Yaw, Pitch);
        Target = sceneCamera.Position + (basis.Forward * Distance);
    }

    private void ApplyFixedAngles(ViewKind kind)
    {
        var (yaw, pitch) = kind.FixedAngles();
        Yaw = yaw * MathF.PI / 180f;
        Pitch = pitch * MathF.PI / 180f;
    }

    private static float WrapAngle(float radians)
    {
        float wrapped = MathF.IEEERemainder(radians, MathF.Tau);
        return wrapped;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
