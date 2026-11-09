using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// グラフに描く値の線 1 本。<see cref="Values"/> は <see cref="FirstFrame"/> から 1 フレームごとの値。
/// <see cref="CurveChannel"/> は、この値の動き方を決める補間曲線の番号（<see cref="CurveEditing.ChannelNames"/>）。
/// </summary>
public sealed record GraphSeries(string Name, int CurveChannel, int FirstFrame, float[] Values);

/// <summary>
/// 補間曲線の編集と、グラフエディタに描く値。補間曲線はキーごとに持ち、「前のキーからこのキーまで」の動き方（MMD と同じ）。
/// 曲線の番号: ボーン = 移動 X・移動 Y・移動 Z・回転、モーフ = 値、カメラ = 注視点 X・Y・Z・回転・距離・視野角（MMD と同じ本数）。
/// キーの並びを書き換えるので、画面からは <see cref="EditService"/> の中で呼び、元に戻せるようにする。
/// </summary>
public static class CurveEditing
{
    /// <summary>項目の補間曲線の名前（キーを持てない項目は空）。</summary>
    public static IReadOnlyList<string> ChannelNames(SceneNode node) => node switch
    {
        BoneNode => ["移動 X", "移動 Y", "移動 Z", "回転"],
        MorphNode => ["値"],
        CameraNode => ["注視点 X", "注視点 Y", "注視点 Z", "回転", "距離", "視野角"],
        _ => [],
    };

    /// <summary>キーの補間曲線と種類（段階か）。キーが無い・番号が範囲外なら false。</summary>
    public static bool TryGetCurve(KeyRef key, int channel, out BezierCurve curve, out KeyInterpolationKind kind)
    {
        curve = BezierCurve.Linear;
        kind = KeyInterpolationKind.Curve;
        switch (key.Node)
        {
            case BoneNode bone when bone.Keys.TryGet(key.Frame, out BoneKey k) && (uint)channel < 4:
                curve = channel switch { 0 => k.Interpolation.X, 1 => k.Interpolation.Y, 2 => k.Interpolation.Z, _ => k.Interpolation.Rotation };
                kind = k.Kind;
                return true;
            case MorphNode morph when morph.Keys.TryGet(key.Frame, out MorphKey k) && channel == 0:
                curve = k.Curve;
                kind = k.Kind;
                return true;
            case CameraNode camera when camera.Keys.TryGet(key.Frame, out CameraKey k) && (uint)channel < 6:
                curve = channel switch
                {
                    0 => k.Interpolation.X,
                    1 => k.Interpolation.Y,
                    2 => k.Interpolation.Z,
                    3 => k.Interpolation.Rotation,
                    4 => k.Interpolation.Distance,
                    _ => k.Interpolation.FieldOfView,
                };
                kind = k.Kind;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// キーの補間曲線を変える。<paramref name="channel"/> が null なら全部の曲線を同じにする。段階のキーは曲線に戻す。
    /// 変えたキーの数を返す。
    /// </summary>
    public static int SetCurve(IEnumerable<KeyRef> keys, int? channel, BezierCurve curve)
    {
        int count = 0;
        foreach (var group in keys.Where(KeyEditing.Exists).GroupBy(k => k.Node))
        {
            var frames = group.Select(k => k.Frame).ToHashSet();
            switch (group.Key)
            {
                case BoneNode bone:
                    bone.Keys.ReplaceAll(bone.Keys.Keys.Select(k => frames.Contains(k.Frame)
                        ? k with { Interpolation = With(k.Interpolation, channel, curve), Kind = KeyInterpolationKind.Curve }
                        : k));
                    break;
                case MorphNode morph:
                    morph.Keys.ReplaceAll(morph.Keys.Keys.Select(k => frames.Contains(k.Frame) && channel is null or 0
                        ? k with { Curve = curve, Kind = KeyInterpolationKind.Curve }
                        : k));
                    break;
                case CameraNode camera:
                    camera.Keys.ReplaceAll(camera.Keys.Keys.Select(k => frames.Contains(k.Frame)
                        ? k with { Interpolation = With(k.Interpolation, channel, curve), Kind = KeyInterpolationKind.Curve }
                        : k));
                    break;
                default:
                    continue;
            }
            count += frames.Count;
        }
        return count;
    }

    /// <summary>このキーの前のキーのフレーム（このキーの補間曲線が始まる所）。無ければ null。</summary>
    public static int? PreviousKeyFrame(KeyRef key)
    {
        IEnumerable<int> frames = key.Node switch
        {
            BoneNode bone => bone.Keys.Keys.Select(k => k.Frame),
            MorphNode morph => morph.Keys.Keys.Select(k => k.Frame),
            CameraNode camera => camera.Keys.Keys.Select(k => k.Frame),
            _ => [],
        };
        int? previous = null;
        foreach (int frame in frames)
        {
            if (frame >= key.Frame)
            {
                break;
            }
            previous = frame;
        }
        return previous;
    }

    /// <summary>項目のキーのフレーム（キーを持てない項目は空）。</summary>
    public static IReadOnlyList<int> KeyFrames(SceneNode node) => node switch
    {
        BoneNode bone => [.. bone.Keys.Keys.Select(k => k.Frame)],
        MorphNode morph => [.. morph.Keys.Keys.Select(k => k.Frame)],
        CameraNode camera => [.. camera.Keys.Keys.Select(k => k.Frame)],
        _ => [],
    };

    /// <summary>
    /// グラフに描く値を、<paramref name="first"/>〜<paramref name="last"/> の各フレームで求める（キーが無ければ今の値の平らな線）。
    /// ボーン: 移動 X・Y・Z と回転 X・Y・Z（度。前のフレームから 180° を超えて飛ばないようにつなぐ）。
    /// モーフ: 値。カメラ: 注視点 X・Y・Z、回転 X・Y・Z、距離、視野角。シーンの値は書き換えない。
    /// </summary>
    public static IReadOnlyList<GraphSeries> Sample(SceneNode node, int first, int last)
    {
        int count = Math.Max(0, last - first + 1);
        switch (node)
        {
            case BoneNode bone:
            {
                var values = Enumerable.Range(0, 6).Select(_ => new float[count]).ToArray();
                Vector3 previous = default;
                for (int i = 0; i < count; i++)
                {
                    Vector3 t = bone.Position;
                    Quaternion r = bone.Orientation;
                    if (bone.Keys.Count > 0 && KeyframeEvaluator.TryEvaluate(bone.Keys, first + i, out Vector3 kt, out Quaternion kr))
                    {
                        (t, r) = (kt, kr);
                    }
                    Vector3 degrees = SceneRotation.ToDegrees(Quaternion.Normalize(r));
                    if (i > 0)
                    {
                        degrees = new Vector3(Unwrap(degrees.X, previous.X), Unwrap(degrees.Y, previous.Y), Unwrap(degrees.Z, previous.Z));
                    }
                    previous = degrees;
                    values[0][i] = t.X;
                    values[1][i] = t.Y;
                    values[2][i] = t.Z;
                    values[3][i] = degrees.X;
                    values[4][i] = degrees.Y;
                    values[5][i] = degrees.Z;
                }
                return
                [
                    new("移動 X", 0, first, values[0]),
                    new("移動 Y", 1, first, values[1]),
                    new("移動 Z", 2, first, values[2]),
                    new("回転 X", 3, first, values[3]),
                    new("回転 Y", 3, first, values[4]),
                    new("回転 Z", 3, first, values[5]),
                ];
            }
            case MorphNode morph:
            {
                var values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = morph.Keys.Count > 0 && KeyframeEvaluator.TryEvaluate(morph.Keys, first + i, out float w) ? w : morph.Weight;
                }
                return [new("値", 0, first, values)];
            }
            case CameraNode camera:
            {
                var values = Enumerable.Range(0, 8).Select(_ => new float[count]).ToArray();
                for (int i = 0; i < count; i++)
                {
                    CameraPose pose = camera.Keys.Count > 0 && KeyframeEvaluator.TryEvaluate(camera.Keys, first + i, out CameraPose p) ? p : camera.Pose;
                    values[0][i] = pose.Target.X;
                    values[1][i] = pose.Target.Y;
                    values[2][i] = pose.Target.Z;
                    values[3][i] = pose.Rotation.X;
                    values[4][i] = pose.Rotation.Y;
                    values[5][i] = pose.Rotation.Z;
                    values[6][i] = pose.Distance;
                    values[7][i] = pose.FieldOfView;
                }
                return
                [
                    new("注視点 X", 0, first, values[0]),
                    new("注視点 Y", 1, first, values[1]),
                    new("注視点 Z", 2, first, values[2]),
                    new("回転 X", 3, first, values[3]),
                    new("回転 Y", 3, first, values[4]),
                    new("回転 Z", 3, first, values[5]),
                    new("距離", 4, first, values[6]),
                    new("視野角", 5, first, values[7]),
                ];
            }
            default:
                return [];
        }
    }

    /// <summary>MMD の書き方（0〜127 の整数）に丸めた曲線（MMD と同じ細かさにそろえる）。</summary>
    public static BezierCurve RoundToMmd(BezierCurve curve) => BezierCurve.FromMmd(Byte(curve.Control1.X), Byte(curve.Control1.Y), Byte(curve.Control2.X), Byte(curve.Control2.Y));

    /// <summary>曲線の制御点を MMD の書き方（0〜127）で返す。</summary>
    public static (int X1, int Y1, int X2, int Y2) ToMmd(BezierCurve curve) =>
        (Byte(curve.Control1.X), Byte(curve.Control1.Y), Byte(curve.Control2.X), Byte(curve.Control2.Y));

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 127f), 0, 127);

    private static float Unwrap(float value, float previous)
    {
        while (value - previous > 180f)
        {
            value -= 360f;
        }
        while (value - previous < -180f)
        {
            value += 360f;
        }
        return value;
    }

    private static BoneInterpolation With(BoneInterpolation i, int? channel, BezierCurve c) => channel switch
    {
        null => new BoneInterpolation(c, c, c, c),
        0 => i with { X = c },
        1 => i with { Y = c },
        2 => i with { Z = c },
        3 => i with { Rotation = c },
        _ => i,
    };

    private static CameraInterpolation With(CameraInterpolation i, int? channel, BezierCurve c) => channel switch
    {
        null => new CameraInterpolation(c, c, c, c, c, c),
        0 => i with { X = c },
        1 => i with { Y = c },
        2 => i with { Z = c },
        3 => i with { Rotation = c },
        4 => i with { Distance = c },
        5 => i with { FieldOfView = c },
        _ => i,
    };
}
