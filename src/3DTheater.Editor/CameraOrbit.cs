using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// ライブモードの、キャラクターの周りを回るカメラの動かし方（タッチパッド向け。設計判断記録 78 章の 7 段目）。
/// 2 本指でなでると、キャラクターの縦の軸の周りを回る（左右）・見ている高さを中心に上下に回る。指を広げる・つまむと、近づく・離れる。
/// どれも、中心（<see cref="Pivot"/>）が画面の同じ所に映ったままになるように動かす。
/// 角度は度（X = 上下で、正で見下ろす。Y = 左右。Z = 傾き。シーンのカメラと同じ決まり）。
/// </summary>
public static class CameraOrbit
{
    /// <summary>ホイール 1 段（120）で回る角度（度）。タッチパッドの 2 本指は、なでた長さに合わせて細かく届く。</summary>
    public const float DegreesPerWheelStep = 12f;

    /// <summary>ホイール 1 段（120）で、中心までの距離を何倍にするか（1 段で 15% ほど近づく）。</summary>
    public const float DollyPerWheelStep = 0.85f;

    /// <summary>ホイール 1 段（120）を、マウスで何画素ずらしたのと同じにするか（上下左右にずらすとき）。</summary>
    public const float PixelsPerWheelStep = 40f;

    /// <summary>中心に近づける限り（MMD の単位。キャラクターの背の高さは 15〜20 ほど）。</summary>
    public const float MinDistance = 1f;

    public const float MaxDistance = 2000f;

    /// <summary>
    /// 回る中心。キャラクターの縦の軸（<paramref name="axis"/> を通る、上下の線）の上で、カメラの視線にいちばん近い所
    /// （高さは <paramref name="minY"/>〜<paramref name="maxY"/> に収める）。真上・真下を見ているときは、高さの真ん中。
    /// </summary>
    public static Vector3 Pivot(Vector3 position, Vector3 rotation, Vector3 axis, float minY, float maxY)
    {
        if (maxY < minY)
        {
            (minY, maxY) = (maxY, minY);
        }
        Vector3 forward = Forward(rotation);
        // 視線 position + s·forward（forward の長さは 1）と、縦の線 (axis.X, t, axis.Z) のいちばん近い所
        var w = new Vector3(position.X - axis.X, position.Y, position.Z - axis.Z);
        float b = forward.Y;
        float denominator = 1f - (b * b);
        float t;
        if (denominator < 1e-4f)
        {
            t = (minY + maxY) * 0.5f;
        }
        else
        {
            float d = Vector3.Dot(forward, w);
            float e = w.Y;
            float s = ((b * e) - d) / denominator;
            t = s < 0f ? position.Y : (e - (b * d)) / denominator; // 軸がカメラの後ろなら、カメラの高さ
        }
        return new Vector3(axis.X, Math.Clamp(t, minY, maxY), axis.Z);
    }

    /// <summary>中心が無いとき（キャラクターがいない）の中心: 注視点（見ている向きへ <paramref name="targetDistance"/> 進んだ所。近すぎれば 10）。</summary>
    public static Vector3 PivotAhead(Vector3 position, Vector3 rotation, float targetDistance) =>
        position + (Forward(rotation) * (MathF.Abs(targetDistance) >= 1f ? MathF.Abs(targetDistance) : 10f));

    /// <summary>
    /// 中心の周りを回る（<paramref name="yawDegrees"/> は左右、<paramref name="pitchDegrees"/> は上下で、正で見下ろす側へ）。
    /// 向きも同じだけ回すので、中心は画面の同じ所に映ったまま。上下は ±89 度まで。傾きはそのまま。
    /// </summary>
    public static (Vector3 Position, Vector3 Rotation) Orbit(Vector3 position, Vector3 rotation, Vector3 pivot, float yawDegrees, float pitchDegrees)
    {
        var turned = new Vector3(
            Math.Clamp(rotation.X + pitchDegrees, -CameraFly.MaxPitch, CameraFly.MaxPitch),
            WrapDegrees(rotation.Y + yawDegrees),
            rotation.Z);
        // 傾きを除いた向きの変わり分（傾きは、向きの中でいちばん先に回すので、変わり分には入らない）
        Quaternion delta = Quaternion.Normalize(Level(turned) * Quaternion.Inverse(Level(rotation)));
        Vector3 moved = pivot + Vector3.Transform(position - pivot, delta);
        return (moved, turned);
    }

    /// <summary>中心へ近づく（<paramref name="factor"/> 倍の距離にする。1 より小さいと近づく）。中心までの距離は <see cref="MinDistance"/>〜<see cref="MaxDistance"/>。</summary>
    public static Vector3 Dolly(Vector3 position, Vector3 pivot, float factor)
    {
        Vector3 offset = position - pivot;
        float distance = offset.Length();
        if (!(distance > 1e-4f) || !(factor > 0f) || !float.IsFinite(factor))
        {
            return position;
        }
        float wanted = Math.Clamp(distance * factor, Math.Min(MinDistance, distance), Math.Max(MaxDistance, distance));
        return pivot + (offset * (wanted / distance));
    }

    /// <summary>ホイールの量（120 で 1 段）を、回る角度にする。</summary>
    public static float Degrees(float wheelDelta) => wheelDelta / 120f * DegreesPerWheelStep;

    /// <summary>ホイールの量を、近づく倍率にする（奥へ回す・指を広げる = 正で近づく）。</summary>
    public static float DollyFactor(float wheelDelta) => MathF.Pow(DollyPerWheelStep, wheelDelta / 120f);

    /// <summary>ホイールの量を、マウスでずらした画素にする。</summary>
    public static float Pixels(float wheelDelta) => wheelDelta / 120f * PixelsPerWheelStep;

    /// <summary>
    /// 2 本指でなでた向きを、画面の上で指が動いた向き（右・下が正）にする。Windows の初めの決まり（指の動きに中身がついて来る）では、
    /// 指を下へ動かすと上下のホイールが正、右へ動かすと左右のホイールが負で届く。<paramref name="reverse"/> なら逆にする。
    /// </summary>
    public static Vector2 FingerMotion(int horizontalDelta, int verticalDelta, bool reverse)
    {
        var motion = new Vector2(-horizontalDelta, verticalDelta);
        return reverse ? -motion : motion;
    }

    private static Vector3 Forward(Vector3 rotation) => Vector3.Transform(Vector3.UnitZ, SceneRotation.ToQuaternion(rotation));

    private static Quaternion Level(Vector3 rotation) => SceneRotation.ToQuaternion(new Vector3(rotation.X, rotation.Y, 0f));

    // -180〜180 度に収める
    private static float WrapDegrees(float degrees)
    {
        float wrapped = MathF.IEEERemainder(degrees, 360f);
        return wrapped <= -180f ? wrapped + 360f : wrapped;
    }
}

/// <summary>ライブモードのプレビューの操作の、利用者ごとの設定（設計判断記録 78 章の 7 段目。PC ごとに違うので、シーンには入れない）。</summary>
public sealed class CameraControlSettings
{
    private bool _touchpad;
    private bool _reverseTouchpad;

    public event EventHandler? Changed;

    /// <summary>タッチパッドで操作する（2 本指でなでると、キャラクターの周りを回る。ホイールで視野角を変えない）。</summary>
    public bool Touchpad { get => _touchpad; set => Set(ref _touchpad, value); }

    /// <summary>2 本指でなでたときの向きを逆にする。</summary>
    public bool ReverseTouchpad { get => _reverseTouchpad; set => Set(ref _reverseTouchpad, value); }

    private void Set(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
