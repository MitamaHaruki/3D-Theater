using System.Numerics;

namespace Theater3D.Domain;

/// <summary>
/// キーとキーの間の動き方を表す曲線（3 次ベジェ）。始点 (0,0) と終点 (1,1) は決まっていて、2 つの制御点だけを持つ。
/// 横軸が時間の進み、縦軸が値の進み（どちらも 0〜1）。MMD の補間曲線と同じ考え方。
/// </summary>
public readonly record struct BezierCurve(Vector2 Control1, Vector2 Control2)
{
    /// <summary>直線（MMD が新しいキーに入れる 20・107 の制御点）。</summary>
    public static BezierCurve Linear { get; } = FromMmd(20, 20, 107, 107);

    /// <summary>直線か（制御点がどちらも対角線の上にある）。</summary>
    public bool IsLinear => MathF.Abs(Control1.X - Control1.Y) < 1e-6f && MathF.Abs(Control2.X - Control2.Y) < 1e-6f;

    /// <summary>MMD の書き方（0〜127 の整数）から作る。</summary>
    public static BezierCurve FromMmd(byte x1, byte y1, byte x2, byte y2) =>
        new(new Vector2(Math.Min(x1, (byte)127) / 127f, Math.Min(y1, (byte)127) / 127f), new Vector2(Math.Min(x2, (byte)127) / 127f, Math.Min(y2, (byte)127) / 127f));

    /// <summary>時間の進み <paramref name="x"/>（0〜1）での値の進み（0〜1）。</summary>
    public float Evaluate(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        if (IsLinear)
        {
            return x;
        }
        // 曲線の横の位置が x になる t を二分法で探す（横の位置は t について単調に増える）
        float lo = 0f, hi = 1f, t = x;
        for (int i = 0; i < 32; i++)
        {
            float bx = Bezier(t, Control1.X, Control2.X);
            if (MathF.Abs(bx - x) < 1e-6f)
            {
                break;
            }
            if (bx < x)
            {
                lo = t;
            }
            else
            {
                hi = t;
            }
            t = (lo + hi) * 0.5f;
        }
        return Bezier(t, Control1.Y, Control2.Y);
    }

    private static float Bezier(float t, float p1, float p2)
    {
        float u = 1f - t;
        return (3f * u * u * t * p1) + (3f * u * t * t * p2) + (t * t * t);
    }
}

/// <summary>ボーンのキーの動き方（移動の X・Y・Z と回転で別々の曲線）。</summary>
public readonly record struct BoneInterpolation(BezierCurve X, BezierCurve Y, BezierCurve Z, BezierCurve Rotation)
{
    public static BoneInterpolation Linear { get; } = new(BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear);
}

/// <summary>キーの種類による動き方の違い。</summary>
public enum KeyInterpolationKind
{
    /// <summary>曲線で次のキーへ近づける（直線なら線形補間）。</summary>
    Curve,

    /// <summary>次のキーまで値を変えない（段階）。</summary>
    Step,
}

/// <summary>カメラのキーの動き方（注視点の X・Y・Z、回転、距離、視野角で別々の曲線。MMD と同じ 6 本）。</summary>
public readonly record struct CameraInterpolation(BezierCurve X, BezierCurve Y, BezierCurve Z, BezierCurve Rotation, BezierCurve Distance, BezierCurve FieldOfView)
{
    public static CameraInterpolation Linear { get; } = new(BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear,
        BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear);

    public bool IsLinear => X.IsLinear && Y.IsLinear && Z.IsLinear && Rotation.IsLinear && Distance.IsLinear && FieldOfView.IsLinear;
}

/// <summary>カメラの置き方（MMD と同じく、注視点・回転・注視点からの距離・視野角で表す）。</summary>
/// <param name="Rotation">回転（度、シーンの項目と同じく Z → X → Y の順。X が正なら見下ろす）。補間では成分ごとに直線で動くので、1 周を超える回転も表せる。</param>
/// <param name="Distance">注視点から手前へ下がる距離（正の数）。</param>
public readonly record struct CameraPose(Vector3 Target, Vector3 Rotation, float Distance, float FieldOfView)
{
    /// <summary>カメラの位置（注視点から、見ている向きの逆へ距離だけ下がった所）。</summary>
    public Vector3 Position => Target - (Forward * Distance);

    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, SceneRotation.ToQuaternion(Rotation));
}

public interface IKeyframe
{
    int Frame { get; }
}

/// <summary>
/// ボーンのキー 1 つ。移動・回転は <see cref="BoneNode"/> と同じく親から見た向き。
/// <see cref="Interpolation"/> と <see cref="Kind"/> は「前のキーからこのキーまで」の動き方（MMD と同じ決め）。
/// </summary>
public readonly record struct BoneKey(int Frame, Vector3 Translation, Quaternion Rotation, BoneInterpolation Interpolation, KeyInterpolationKind Kind = KeyInterpolationKind.Curve)
    : IKeyframe;

/// <summary>
/// カメラのキー 1 つ。注視点・回転・距離・視野角を、それぞれの曲線で補間する（位置を直線で補間しないので、注視点のまわりを回る動きが崩れない）。
/// <see cref="Interpolation"/> と <see cref="Kind"/> は前のキーからこのキーまでの動き方。<see cref="Orthographic"/> は MMD の「パース OFF」（今は使わず、書き戻すために持つ）。
/// </summary>
public readonly record struct CameraKey(int Frame, Vector3 Target, Vector3 Rotation, float Distance, float FieldOfView,
    CameraInterpolation Interpolation, KeyInterpolationKind Kind = KeyInterpolationKind.Curve, bool Orthographic = false) : IKeyframe
{
    public CameraPose Pose => new(Target, Rotation, Distance, FieldOfView);
}

/// <summary>モーフのキー 1 つ。<see cref="Curve"/> と <see cref="Kind"/> は前のキーからこのキーまでの動き方。</summary>
public readonly record struct MorphKey(int Frame, float Weight, BezierCurve Curve, KeyInterpolationKind Kind = KeyInterpolationKind.Curve) : IKeyframe;

/// <summary>
/// 照明の値（キーで動かすもの）。<see cref="Rotation"/> は度（シーンの項目と同じ。補間では成分ごとに直線で動く）、
/// <see cref="Color"/> は各 0〜1。届く距離・広がり・ぼかしは、点光源・スポットライトだけが使う。
/// </summary>
public readonly record struct LightPose(Vector3 Position, Vector3 Rotation, Vector3 Color, float Intensity, float Range, float SpotAngle,
    float SpotSoftness);

/// <summary>
/// 照明のキー 1 つ（MMD の照明と足した照明）。<see cref="Curve"/> と <see cref="Kind"/> は前のキーからこのキーまでの動き方で、
/// すべての値に同じ曲線を使う（MMD の照明のキーは直線だけ）。
/// </summary>
public readonly record struct LightKey(int Frame, LightPose Pose, BezierCurve Curve, KeyInterpolationKind Kind = KeyInterpolationKind.Curve)
    : IKeyframe;

/// <summary>
/// 1 つの項目のキーの並び（フレーム順、同じフレームには 1 つだけ）。変わると <see cref="Changed"/> を出す。
/// </summary>
public sealed class KeyTrack<TKey> where TKey : struct, IKeyframe
{
    private readonly List<TKey> _keys = [];

    public event EventHandler? Changed;

    public IReadOnlyList<TKey> Keys => _keys;

    public int Count => _keys.Count;

    public int FirstFrame => _keys.Count > 0 ? _keys[0].Frame : 0;

    public int LastFrame => _keys.Count > 0 ? _keys[^1].Frame : 0;

    /// <summary>キーを置く。同じフレームにキーがあれば置き換える。</summary>
    public void Set(TKey key)
    {
        if (key.Frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "フレームは 0 以上にしてください");
        }
        int index = IndexOf(key.Frame);
        if (index >= 0)
        {
            _keys[index] = key;
        }
        else
        {
            _keys.Insert(~index, key);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>まとめて置く（通知は 1 回）。同じフレームのキーは後ろのものを使う。</summary>
    public void SetRange(IEnumerable<TKey> keys)
    {
        bool any = false;
        foreach (TKey key in keys)
        {
            if (key.Frame < 0)
            {
                continue;
            }
            any = true;
            int index = IndexOf(key.Frame);
            if (index >= 0)
            {
                _keys[index] = key;
            }
            else
            {
                _keys.Insert(~index, key);
            }
        }
        if (any)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Remove(int frame)
    {
        int index = IndexOf(frame);
        if (index < 0)
        {
            return false;
        }
        _keys.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_keys.Count == 0)
        {
            return;
        }
        _keys.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>すべてのキーを置き換える（通知は 1 回）。同じフレームのキーは後ろのものを使う。</summary>
    public void ReplaceAll(IEnumerable<TKey> keys)
    {
        var sorted = new SortedDictionary<int, TKey>();
        foreach (TKey key in keys)
        {
            if (key.Frame >= 0)
            {
                sorted[key.Frame] = key;
            }
        }
        if (sorted.Count == _keys.Count && sorted.Values.SequenceEqual(_keys))
        {
            return;
        }
        _keys.Clear();
        _keys.AddRange(sorted.Values);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TKey[] ToArray() => [.. _keys];

    /// <summary>フレームのキーを探す。</summary>
    public bool TryGet(int frame, out TKey key)
    {
        int index = IndexOf(frame);
        key = index >= 0 ? _keys[index] : default;
        return index >= 0;
    }

    public bool HasKeyAt(int frame) => IndexOf(frame) >= 0;

    /// <summary>
    /// <paramref name="frame"/> の前後のキーの番号。キーちょうどなら <c>before == after</c>。
    /// 最初のキーより前・最後のキーより後ろなら、端のキーを両方に返す。キーが無ければ false。
    /// </summary>
    public bool FindSegment(float frame, out int before, out int after)
    {
        before = after = -1;
        if (_keys.Count == 0)
        {
            return false;
        }
        if (frame <= _keys[0].Frame)
        {
            before = after = 0;
            return true;
        }
        if (frame >= _keys[^1].Frame)
        {
            before = after = _keys.Count - 1;
            return true;
        }
        int lo = 0, hi = _keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_keys[mid].Frame <= frame)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        if (_keys[lo].Frame == frame)
        {
            before = after = lo;
        }
        else
        {
            before = lo;
            after = hi;
        }
        return true;
    }

    /// <summary>フレームが <paramref name="first"/>〜<paramref name="last"/> にあるキーの番号の範囲（描くときに使う）。</summary>
    public (int Start, int End) Range(int first, int last)
    {
        int start = IndexOf(first);
        start = start < 0 ? ~start : start;
        int end = IndexOf(last);
        end = end < 0 ? ~end : end + 1;
        return (start, Math.Max(start, end));
    }

    private int IndexOf(int frame)
    {
        int lo = 0, hi = _keys.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int f = _keys[mid].Frame;
            if (f == frame)
            {
                return mid;
            }
            if (f < frame)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ~lo;
    }
}

/// <summary>キーの並びから、あるフレームでの値を求める（再生・停止・書き出しで同じ計算を使う。指示書 3.4）。</summary>
public static class KeyframeEvaluator
{
    public static bool TryEvaluate(KeyTrack<BoneKey> track, float frame, out Vector3 translation, out Quaternion rotation)
    {
        translation = Vector3.Zero;
        rotation = Quaternion.Identity;
        if (!track.FindSegment(frame, out int before, out int after))
        {
            return false;
        }
        BoneKey a = track.Keys[before];
        if (before == after)
        {
            translation = a.Translation;
            rotation = a.Rotation;
            return true;
        }
        BoneKey b = track.Keys[after];
        if (b.Kind == KeyInterpolationKind.Step)
        {
            translation = a.Translation;
            rotation = a.Rotation;
            return true;
        }
        float t = (frame - a.Frame) / (b.Frame - a.Frame);
        BoneInterpolation ip = b.Interpolation;
        translation = new Vector3(
            float.Lerp(a.Translation.X, b.Translation.X, ip.X.Evaluate(t)),
            float.Lerp(a.Translation.Y, b.Translation.Y, ip.Y.Evaluate(t)),
            float.Lerp(a.Translation.Z, b.Translation.Z, ip.Z.Evaluate(t)));
        rotation = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, ip.Rotation.Evaluate(t)));
        return true;
    }

    public static bool TryEvaluate(KeyTrack<CameraKey> track, float frame, out CameraPose pose)
    {
        pose = default;
        if (!track.FindSegment(frame, out int before, out int after))
        {
            return false;
        }
        CameraKey a = track.Keys[before];
        if (before == after)
        {
            pose = a.Pose;
            return true;
        }
        CameraKey b = track.Keys[after];
        if (b.Kind == KeyInterpolationKind.Step)
        {
            pose = a.Pose;
            return true;
        }
        float t = (frame - a.Frame) / (b.Frame - a.Frame);
        CameraInterpolation ip = b.Interpolation;
        var target = new Vector3(
            float.Lerp(a.Target.X, b.Target.X, ip.X.Evaluate(t)),
            float.Lerp(a.Target.Y, b.Target.Y, ip.Y.Evaluate(t)),
            float.Lerp(a.Target.Z, b.Target.Z, ip.Z.Evaluate(t)));
        // 回転は四元数の球面補間ではなく、角度を成分ごとに直線で動かす（MMD と同じ。1 周を超える回転も表せる）
        Vector3 rotation = Vector3.Lerp(a.Rotation, b.Rotation, ip.Rotation.Evaluate(t));
        pose = new CameraPose(target, rotation,
            float.Lerp(a.Distance, b.Distance, ip.Distance.Evaluate(t)),
            float.Lerp(a.FieldOfView, b.FieldOfView, ip.FieldOfView.Evaluate(t)));
        return true;
    }

    public static bool TryEvaluate(KeyTrack<LightKey> track, float frame, out LightPose pose)
    {
        pose = default;
        if (!track.FindSegment(frame, out int before, out int after))
        {
            return false;
        }
        LightKey a = track.Keys[before];
        if (before == after)
        {
            pose = a.Pose;
            return true;
        }
        LightKey b = track.Keys[after];
        if (b.Kind == KeyInterpolationKind.Step)
        {
            pose = a.Pose;
            return true;
        }
        float t = b.Curve.Evaluate((frame - a.Frame) / (b.Frame - a.Frame));
        LightPose p = a.Pose, q = b.Pose;
        pose = new LightPose(
            Vector3.Lerp(p.Position, q.Position, t),
            Vector3.Lerp(p.Rotation, q.Rotation, t), // カメラと同じく、角度を成分ごとに直線で動かす
            Vector3.Lerp(p.Color, q.Color, t),
            float.Lerp(p.Intensity, q.Intensity, t),
            float.Lerp(p.Range, q.Range, t),
            float.Lerp(p.SpotAngle, q.SpotAngle, t),
            float.Lerp(p.SpotSoftness, q.SpotSoftness, t));
        return true;
    }

    public static bool TryEvaluate(KeyTrack<MorphKey> track, float frame, out float weight)
    {
        weight = 0;
        if (!track.FindSegment(frame, out int before, out int after))
        {
            return false;
        }
        MorphKey a = track.Keys[before];
        if (before == after)
        {
            weight = a.Weight;
            return true;
        }
        MorphKey b = track.Keys[after];
        if (b.Kind == KeyInterpolationKind.Step)
        {
            weight = a.Weight;
            return true;
        }
        float t = (frame - a.Frame) / (b.Frame - a.Frame);
        weight = float.Lerp(a.Weight, b.Weight, b.Curve.Evaluate(t));
        return true;
    }
}

/// <summary>シーンの回転の表し方（度、Z → X → Y の順）と四元数の行き来。</summary>
public static class SceneRotation
{
    private const float ToRadians = MathF.PI / 180f;
    private const float ToDegreesFactor = 180f / MathF.PI;

    public static Quaternion ToQuaternion(Vector3 degrees)
    {
        Vector3 r = degrees * ToRadians;
        return Quaternion.CreateFromYawPitchRoll(r.Y, r.X, r.Z);
    }

    /// <summary>四元数を度に分ける（X = −90〜90、Y・Z = −180〜180）。X が ±90 のときは Z を 0 にする。</summary>
    public static Vector3 ToDegrees(Quaternion q)
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        // 行ベクトルの行列で M = Rz × Rx × Ry（Z から順に回す）のとき:
        //   M32 = -sin(X)、M31 = cos(X)sin(Y)、M33 = cos(X)cos(Y)、M12 = sin(Z)cos(X)、M22 = cos(Z)cos(X)
        float sinX = Math.Clamp(-m.M32, -1f, 1f);
        float x = MathF.Asin(sinX);
        float y, z;
        if (MathF.Abs(sinX) > 0.99999f)
        {
            z = 0;
            y = MathF.Atan2(-m.M13, m.M11);
        }
        else
        {
            y = MathF.Atan2(m.M31, m.M33);
            z = MathF.Atan2(m.M12, m.M22);
        }
        return new Vector3(x, y, z) * ToDegreesFactor;
    }
}
