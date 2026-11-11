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
/// 曲線の番号: ボーン = 移動 X・移動 Y・移動 Z・回転、モーフ = 値、カメラ = 注視点 X・Y・Z・回転・距離・視野角（MMD と同じ本数）、
/// 照明・映像効果 = すべての値（1 本）。
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
        LightNode or EffectsNode => ["すべての値"],
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
            case LightNode light when light.Keys.TryGet(key.Frame, out LightKey k) && channel == 0:
                curve = k.Curve;
                kind = k.Kind;
                return true;
            case EffectsNode effects when effects.Keys.TryGet(key.Frame, out EffectKey k) && channel == 0:
                curve = k.Curve;
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
                case LightNode light:
                    light.Keys.ReplaceAll(light.Keys.Keys.Select(k => frames.Contains(k.Frame) && channel is null or 0
                        ? k with { Curve = curve, Kind = KeyInterpolationKind.Curve }
                        : k));
                    break;
                case EffectsNode effects:
                    effects.Keys.ReplaceAll(effects.Keys.Keys.Select(k => frames.Contains(k.Frame) && channel is null or 0
                        ? k with { Curve = curve, Kind = KeyInterpolationKind.Curve }
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
            LightNode light => light.Keys.Keys.Select(k => k.Frame),
            ModelDisplayNode display => display.Keys.Keys.Select(k => k.Frame),
            EffectsNode effects => effects.Keys.Keys.Select(k => k.Frame),
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
        LightNode light => [.. light.Keys.Keys.Select(k => k.Frame)],
        ModelDisplayNode display => [.. display.Keys.Keys.Select(k => k.Frame)],
        EffectsNode effects => [.. effects.Keys.Keys.Select(k => k.Frame)],
        _ => [],
    };

    /// <summary>
    /// グラフに描く値を、<paramref name="first"/>〜<paramref name="last"/> の各フレームで求める（キーが無ければ今の値の平らな線）。
    /// ボーン: 移動 X・Y・Z と回転 X・Y・Z（度。前のフレームから 180° を超えて飛ばないようにつなぐ）。
    /// モーフ: 値。カメラ: 注視点 X・Y・Z、回転 X・Y・Z、距離、視野角。照明: 回転 X・Y・Z、色 R・G・B、明るさ（点光源・スポットライトは
    /// 位置 X・Y・Z と届く距離、スポットライトは広がりとぼかしも）。シーンの値は書き換えない。
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
            case LightNode light:
            {
                var values = Enumerable.Range(0, 14).Select(_ => new float[count]).ToArray();
                for (int i = 0; i < count; i++)
                {
                    LightPose pose = light.Keys.Count > 0 && KeyframeEvaluator.TryEvaluate(light.Keys, first + i, out LightPose p) ? p : light.Pose;
                    values[0][i] = pose.Position.X;
                    values[1][i] = pose.Position.Y;
                    values[2][i] = pose.Position.Z;
                    values[3][i] = pose.Rotation.X;
                    values[4][i] = pose.Rotation.Y;
                    values[5][i] = pose.Rotation.Z;
                    values[6][i] = pose.Color.X;
                    values[7][i] = pose.Color.Y;
                    values[8][i] = pose.Color.Z;
                    values[9][i] = pose.Intensity;
                    values[10][i] = pose.Range;
                    values[11][i] = pose.SpotAngle;
                    values[12][i] = pose.SpotSoftness;
                }
                var series = new List<GraphSeries>();
                bool ranged = light.Type != LightType.Directional;
                if (ranged)
                {
                    series.AddRange([new("位置 X", 0, first, values[0]), new("位置 Y", 0, first, values[1]), new("位置 Z", 0, first, values[2])]);
                }
                series.AddRange(
                [
                    new("回転 X", 0, first, values[3]),
                    new("回転 Y", 0, first, values[4]),
                    new("回転 Z", 0, first, values[5]),
                    new("色 R", 0, first, values[6]),
                    new("色 G", 0, first, values[7]),
                    new("色 B", 0, first, values[8]),
                    new("明るさ", 0, first, values[9]),
                ]);
                if (ranged)
                {
                    series.Add(new("届く距離", 0, first, values[10]));
                }
                if (light.Type == LightType.Spot)
                {
                    series.AddRange([new("広がり", 0, first, values[11]), new("縁のぼかし", 0, first, values[12])]);
                }
                return series;
            }
            case EffectsNode effects when effects.Keys.Count > 0:
            {
                // 値は描き方の設定にあるので、キーのある間だけ描く
                string[] names = ["明るさ", "コントラスト", "彩度", "色温度", "ブルームの強さ", "光り始める明るさ", "ブルームの広がり", "周辺減光",
                    "ピントの距離", "ピントの合う幅", "ぼけの大きさ", "霧の始まり", "見通せる距離", "霧の色 R", "霧の色 G", "霧の色 B"];
                var values = names.Select(_ => new float[count]).ToArray();
                for (int i = 0; i < count; i++)
                {
                    KeyframeEvaluator.TryEvaluate(effects.Keys, first + i, out EffectPose p);
                    float[] row = [p.Exposure, p.Contrast, p.Saturation, p.Temperature, p.BloomIntensity, p.BloomThreshold, p.BloomSpread,
                        p.Vignette, p.DofFocusDistance, p.DofFocusRange, p.DofBlur, p.FogStart, p.FogVisibility, p.FogColor.X, p.FogColor.Y,
                        p.FogColor.Z];
                    for (int c = 0; c < row.Length; c++)
                    {
                        values[c][i] = row[c];
                    }
                }
                return [.. names.Select((name, c) => new GraphSeries(name, 0, first, values[c]))];
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
