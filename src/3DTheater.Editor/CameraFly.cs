using System.Numerics;

namespace Theater3D.Editor;

/// <summary>右ボタンを押している間に押しているキー（ライブモードのカメラの動かし方）。</summary>
[Flags]
public enum FlyKeys
{
    None = 0,
    Forward = 1,  // W
    Back = 2,     // S
    Left = 4,     // A
    Right = 8,    // D
    Up = 16,      // E
    Down = 32,    // Q
    Fast = 64,    // Shift
}

/// <summary>
/// ライブモードのカメラの動かし方（設計判断記録 78 章の 3 段目。利用者と相談して決めた）。
/// プレビューで右ボタンを押している間だけ、マウスで向きを変え、W・A・S・D で前後左右、Q・E で下・上に動く（Shift で速く）。
/// ホイールで視野角を変える（本当のズーム）。中ボタンのドラッグで、カメラを上下左右にずらす。
/// 角度は度（X = 上下で、正で見下ろす。Y = 左右。Z = 傾き。シーンのカメラと同じ決まり）。
/// </summary>
public static class CameraFly
{
    /// <summary>マウスを 1 画素動かしたときに回る角度（度）。</summary>
    public const float DegreesPerPixel = 0.15f;

    /// <summary>キーで進む速さ（1 秒あたり。MMD の単位。キャラクターの背の高さは 15〜20 ほど）。</summary>
    public const float UnitsPerSecond = 10f;

    /// <summary>Shift を押している間の速さの倍率。</summary>
    public const float FastFactor = 4f;

    /// <summary>ホイール 1 段で変わる視野角（度）。</summary>
    public const float DegreesPerWheelStep = 2f;

    public const float MinFieldOfView = 5f;

    public const float MaxFieldOfView = 120f;

    /// <summary>見上げ・見下ろしの限り（真上・真下を越えて裏返らないように）。</summary>
    public const float MaxPitch = 89f;

    /// <summary>マウスで向きを変える（右へ動かすと右を向き、下へ動かすと下を向く）。傾きはそのまま。</summary>
    public static Vector3 Look(Vector3 rotation, float pixelsX, float pixelsY)
    {
        float pitch = Math.Clamp(rotation.X + (pixelsY * DegreesPerPixel), -MaxPitch, MaxPitch);
        float yaw = WrapDegrees(rotation.Y + (pixelsX * DegreesPerPixel));
        return new Vector3(pitch, yaw, rotation.Z);
    }

    /// <summary>キーで動かす（前後は向いている方へ。上下は世界の上下）。<paramref name="seconds"/> は前の回からの時間。</summary>
    public static Vector3 Move(Vector3 position, Vector3 rotation, FlyKeys keys, float seconds)
    {
        if (!(seconds > 0f) || !float.IsFinite(seconds))
        {
            return position;
        }
        Vector3 radians = rotation * (MathF.PI / 180f);
        CameraBasis basis = CameraBasis.FromAngles(radians.Y, radians.X, radians.Z);
        Vector3 direction = Vector3.Zero;
        direction += Axis(keys, FlyKeys.Forward, FlyKeys.Back) * basis.Forward;
        direction += Axis(keys, FlyKeys.Right, FlyKeys.Left) * basis.Right;
        direction += Axis(keys, FlyKeys.Up, FlyKeys.Down) * Vector3.UnitY;
        if (direction.LengthSquared() < 1e-8f)
        {
            return position;
        }
        float speed = UnitsPerSecond * (keys.HasFlag(FlyKeys.Fast) ? FastFactor : 1f);
        return position + (Vector3.Normalize(direction) * speed * seconds);
    }

    /// <summary>ホイールで視野角を変える（奥へ回す = 正で寄る = 視野角を狭く）。<paramref name="wheelDelta"/> は 120 で 1 段。</summary>
    public static float Zoom(float fieldOfView, int wheelDelta) =>
        Math.Clamp(fieldOfView - (wheelDelta / 120f * DegreesPerWheelStep), MinFieldOfView, MaxFieldOfView);

    /// <summary>中ボタンのドラッグで、カメラを右・上へずらす（画面の中の物がマウスについて来るように、マウスと逆へ）。</summary>
    public static Vector3 Pan(Vector3 position, Vector3 rotation, float pixelsX, float pixelsY, float unitsPerPixel)
    {
        Vector3 radians = rotation * (MathF.PI / 180f);
        CameraBasis basis = CameraBasis.FromAngles(radians.Y, radians.X, radians.Z);
        return position - (basis.Right * pixelsX * unitsPerPixel) + (basis.Up * pixelsY * unitsPerPixel);
    }

    private static float Axis(FlyKeys keys, FlyKeys plus, FlyKeys minus) =>
        (keys.HasFlag(plus) ? 1f : 0f) - (keys.HasFlag(minus) ? 1f : 0f);

    // -180〜180 度に収める
    private static float WrapDegrees(float degrees)
    {
        float wrapped = MathF.IEEERemainder(degrees, 360f);
        return wrapped <= -180f ? wrapped + 360f : wrapped;
    }
}
