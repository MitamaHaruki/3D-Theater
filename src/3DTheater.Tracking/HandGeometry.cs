using System.Numerics;

namespace Theater3D.Tracking;

/// <summary>
/// 掌を探すモデル（192 × 192）の、位置の候補と結果の読み方、手の点を求める範囲の決め方（MediaPipe の Hand Landmarker と同じ。設計判断記録 99 章）。
/// 範囲は <see cref="FaceRegion"/>（真ん中・一辺・回転）で表す。手首から中指の付け根へ向かう線が上を向くように回す。
/// </summary>
public static class HandDetectorMath
{
    public const int InputSize = 192;
    public const int AnchorCount = 2016;
    public const float ScoreThreshold = 0.5f;
    public const float OverlapThreshold = 0.3f;

    /// <summary>掌の箱から手の範囲への広げ方と、指の側へのずらし方（箱の高さに対して）。</summary>
    public const float PalmScale = 2.6f;
    public const float PalmShift = -0.5f;

    /// <summary>手の点から次の絵の範囲への広げ方と、ずらし方。</summary>
    public const float LandmarkScale = 2.0f;
    public const float LandmarkShift = -0.1f;

    /// <summary>この重なりより大きい範囲は、同じ手とみなす（新しく見つけた掌と、追っている手）。</summary>
    public const float SameHandOverlap = 0.5f;

    /// <summary>位置の候補（0〜1）。24 × 24 の升に 2 つずつ、12 × 12 の升に 6 つずつ。</summary>
    public static IReadOnlyList<Vector2> Anchors { get; } = FaceDetectorMath.SsdAnchors(InputSize, [8, 16, 16, 16]);

    // 範囲を決める点（手首・親指の付け根から第 1 関節・4 本の指の付け根と第 2 関節。指先より絵ごとのぶれが少ない）
    private static readonly int[] RegionPoints = [0, 1, 2, 3, 5, 6, 9, 10, 13, 14, 17, 18];

    /// <summary>
    /// モデルの出力（候補ごとに 18 個の数と、掌らしさ）から、掌を取り出す（座標は 192 × 192 の入力の 0〜1）。
    /// 点は 7 つ（0 = 手首、2 = 中指の付け根。ほかは指の付け根の辺り）。重なった候補は、掌らしさで重みを付けて平均する。
    /// </summary>
    public static List<FaceDetection> Decode(ReadOnlySpan<float> regressors, ReadOnlySpan<float> scores)
    {
        const int values = 18;
        var candidates = new List<FaceDetection>();
        for (int i = 0; i < AnchorCount; i++)
        {
            float score = FaceDetectorMath.Sigmoid(Math.Clamp(scores[i], -100f, 100f));
            if (score < ScoreThreshold)
            {
                continue;
            }
            Vector2 a = Anchors[i];
            ReadOnlySpan<float> r = regressors.Slice(i * values, values);
            var center = new Vector2((r[0] / InputSize) + a.X, (r[1] / InputSize) + a.Y);
            var half = new Vector2(r[2] / InputSize, r[3] / InputSize) * 0.5f;
            if (!(half.X > 0 && half.Y > 0 && float.IsFinite(center.X) && float.IsFinite(center.Y)))
            {
                continue; // 大きさの無い箱・数でない値は捨てる
            }
            var keypoints = new Vector2[7];
            for (int k = 0; k < 7; k++)
            {
                keypoints[k] = new Vector2((r[4 + (2 * k)] / InputSize) + a.X, (r[5 + (2 * k)] / InputSize) + a.Y);
            }
            candidates.Add(new FaceDetection(center - half, center + half, keypoints, score));
        }
        return FaceDetectorMath.WeightedSuppression(candidates, OverlapThreshold);
    }

    /// <summary>掌（画素の座標）から、手の点を求める範囲（手首 → 中指の付け根を上に向け、指の側へずらして 2.6 倍）。</summary>
    public static FaceRegion RegionFromPalm(FaceDetection palm)
    {
        ArgumentNullException.ThrowIfNull(palm);
        float rotation = Upward(palm.Keypoints[0], palm.Keypoints[2]);
        return Transform((palm.Min + palm.Max) * 0.5f, palm.Max - palm.Min, rotation, PalmScale, PalmShift);
    }

    /// <summary>今の手の点（画素の座標）から、次の絵の範囲（回した向きで点を囲む箱を、2 倍にして少し指の側へ）。</summary>
    public static FaceRegion RegionFromLandmarks(ReadOnlySpan<Vector3> landmarks)
    {
        var points = new Vector2[landmarks.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Vector2(landmarks[i].X, landmarks[i].Y);
        }
        Vector2 P(int i) => points[i];
        // 向き: 手首 → 人差し指・中指・薬指の付け根の辺り
        Vector2 target = (((P(5) + P(13)) * 0.5f) + P(9)) * 0.5f;
        float rotation = Upward(P(0), target);

        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (int i in RegionPoints)
        {
            min = Vector2.Min(min, P(i));
            max = Vector2.Max(max, P(i));
        }
        Vector2 axisCenter = (min + max) * 0.5f;
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        var rmin = new Vector2(float.MaxValue);
        var rmax = new Vector2(float.MinValue);
        foreach (int i in RegionPoints)
        {
            Vector2 d = P(i) - axisCenter;
            var projected = new Vector2((d.X * cos) + (d.Y * sin), (-d.X * sin) + (d.Y * cos)); // 回した向きから見た位置
            rmin = Vector2.Min(rmin, projected);
            rmax = Vector2.Max(rmax, projected);
        }
        Vector2 c = (rmin + rmax) * 0.5f;
        Vector2 center = axisCenter + new Vector2((c.X * cos) - (c.Y * sin), (c.X * sin) + (c.Y * cos));
        return Transform(center, rmax - rmin, rotation, LandmarkScale, LandmarkShift);
    }

    /// <summary>2 つの範囲の重なり（回転は見ず、真ん中と一辺の正方形で比べる。共通部分 ÷ 合わせた範囲）。</summary>
    public static float Overlap(FaceRegion a, FaceRegion b)
    {
        Vector2 min = Vector2.Max(a.Center - new Vector2(a.Size * 0.5f), b.Center - new Vector2(b.Size * 0.5f));
        Vector2 max = Vector2.Min(a.Center + new Vector2(a.Size * 0.5f), b.Center + new Vector2(b.Size * 0.5f));
        Vector2 size = Vector2.Max(max - min, Vector2.Zero);
        float intersection = size.X * size.Y;
        float union = (a.Size * a.Size) + (b.Size * b.Size) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    // start → end が画像の上を向くように回す角度（画像の上で、時計回りが正）
    private static float Upward(Vector2 start, Vector2 end) =>
        FaceRegion.NormalizeAngle((MathF.PI * 0.5f) - MathF.Atan2(-(end.Y - start.Y), end.X - start.X));

    // MediaPipe の RectTransformationCalculator（画素で）。ずらす向きは、回した範囲の上下
    private static FaceRegion Transform(Vector2 center, Vector2 size, float rotation, float scale, float shiftY)
    {
        float dy = size.Y * shiftY;
        Vector2 shifted = center + new Vector2(-MathF.Sin(rotation) * dy, MathF.Cos(rotation) * dy);
        return new FaceRegion(shifted, MathF.Max(size.X, size.Y) * scale, rotation);
    }
}

/// <summary>
/// 手の形（指の曲がりと開き。度）。手の 21 点（メートル。カメラから見た x 右・y 下・z 奥。映像は左右反転していない）から求める。
/// 指ごと（親指・人差し指・中指・薬指・小指）に 4 つ: 根元・中・先の曲がり（掌の側へ曲げると正）と、開き（中指の向きから親指の側へ開くと正）。
/// 親指は、根元 = 付け根の骨（中手骨）が掌の面から掌の側へ出た角度、中 = 付け根の関節、先 = 第 1 関節、開き = 付け根の骨の掌の面の中での向き。
/// 左右（<see cref="IsRight"/>）は本人から見た左右。設計判断記録 99 章。
/// </summary>
public sealed record HandPose(bool IsRight, float[] Angles)
{
    public const int FingerCount = 5;
    public const int ValuesPerFinger = 4;
    public const int ValueCount = FingerCount * ValuesPerFinger;

    /// <summary>
    /// 指が掌と反対の側へ曲がっている度合いが、これより強ければ、奥行きが裏返って読めたとみなす（指の先・親指の先が、平均して掌の向きと
    /// 逆を向く。手の甲の側へ大きく反る指はほとんど無い）。
    /// </summary>
    public const float MirroredBelow = -0.25f;

    /// <summary>
    /// 読み取りの確かさ（0〜1）。掌か手の甲がカメラを向いているほど高く、掌を横に向けた・指がカメラの方を向いたときは低い
    /// （そういう向きでは、モデルの読んだ角度が 2〜4 倍揺れる。確かめ用の動画で測った）。
    /// </summary>
    public float Quality { get; init; } = 1f;

    /// <summary>奥行きが裏返って読めたので、直したか。</summary>
    public bool DepthMirrored { get; init; }

    public static IReadOnlyList<string> FingerNames { get; } = ["親指", "人差し指", "中指", "薬指", "小指"];

    /// <summary>曲がり（<paramref name="joint"/> は 0 = 根元・1 = 中・2 = 先）。</summary>
    public float Bend(int finger, int joint) => Angles[(finger * ValuesPerFinger) + joint];

    /// <summary>開き。</summary>
    public float Spread(int finger) => Angles[(finger * ValuesPerFinger) + 3];

    /// <summary>
    /// 手の 21 点（メートル）から求める。<paramref name="isRight"/> は本人の右手か（掌の向きを決める）。
    /// 1 枚のカメラの絵では、手の奥行きが前後に裏返って読めることがある（掌の側と手の甲の側の取り違え。左右の見分けとも食い違う）。
    /// 指が掌と反対の側へ大きく曲がって読めたら、奥行きを手首の所で裏返してから求める。
    /// </summary>
    public static HandPose FromWorld(ReadOnlySpan<Vector3> points, bool isRight)
    {
        if (points.Length < HandTracker.LandmarkCount)
        {
            throw new ArgumentException($"手の点は {HandTracker.LandmarkCount} 個要ります", nameof(points));
        }
        Vector3[] p = points[..HandTracker.LandmarkCount].ToArray();
        bool mirrored = CurlToPalm(p, isRight) < MirroredBelow;
        if (mirrored)
        {
            float z = p[0].Z;
            for (int k = 0; k < p.Length; k++)
            {
                p[k].Z = (2 * z) - p[k].Z;
            }
        }
        Vector3 wrist = p[0];
        Vector3 forward = Unit(p[9] - wrist); // 手首 → 中指の付け根
        // 掌の向き（右手なら、人差し指の付け根 × 小指の付け根 が掌の側を向く）
        Vector3 normal = Unit(Vector3.Cross(p[5] - wrist, p[17] - wrist));
        if (!isRight)
        {
            normal = -normal;
        }
        normal = Unit(normal - (forward * Vector3.Dot(normal, forward)));
        Vector3 side = Unit(Vector3.Cross(normal, forward));
        if (Vector3.Dot(side, p[5] - p[17]) < 0)
        {
            side = -side; // 親指の側
        }
        Vector3 axis = Vector3.Cross(forward, normal); // 曲げる軸（この軸で正に回すと、掌の側へ曲がる）

        var angles = new float[ValueCount];
        // 親指
        Vector3 metacarpal = Unit(p[2] - p[1]);
        Vector3 proximal = Unit(p[3] - p[2]);
        Vector3 distal = Unit(p[4] - p[3]);
        float outOfPlane = Vector3.Dot(metacarpal, normal);
        Vector3 inPlane = metacarpal - (normal * outOfPlane);
        angles[0] = Degrees(MathF.Atan2(outOfPlane, inPlane.Length()));
        angles[1] = Degrees(Angle(metacarpal, proximal));
        angles[2] = Degrees(Angle(proximal, distal));
        angles[3] = Degrees(MathF.Atan2(Vector3.Dot(inPlane, side), Vector3.Dot(inPlane, forward)));
        // 人差し指〜小指
        for (int f = 1; f < FingerCount; f++)
        {
            int b = 1 + (f * 4); // 付け根（5・9・13・17）
            Vector3 d1 = Unit(p[b + 1] - p[b]);
            Vector3 d2 = Unit(p[b + 2] - p[b + 1]);
            Vector3 d3 = Unit(p[b + 3] - p[b + 2]);
            int i = f * ValuesPerFinger;
            float bend = MathF.Atan2(Vector3.Dot(d1, normal), Vector3.Dot(d1, forward));
            angles[i] = Degrees(bend);
            angles[i + 1] = Degrees(Signed(d1, d2, axis));
            angles[i + 2] = Degrees(Signed(d2, d3, axis));
            // 開き: 掌の面に映した向きで測る（根元を大きく曲げると、あてにならない。使う側で弱める）
            Vector3 flat = d1 - (normal * Vector3.Dot(d1, normal));
            angles[i + 3] = flat.LengthSquared() < 1e-6f ? 0f : Degrees(MathF.Atan2(Vector3.Dot(flat, side), Vector3.Dot(flat, forward)));
        }
        for (int i = 0; i < angles.Length; i++)
        {
            if (!float.IsFinite(angles[i]))
            {
                angles[i] = 0f;
            }
        }
        // 確かさ: 掌の向きがカメラの向き（z）に近いほど、指の向きがカメラの向きから離れているほど高い
        float facing = Math.Clamp((MathF.Abs(normal.Z) - 0.3f) / 0.55f, 0f, 1f);
        float across = Math.Clamp((0.85f - MathF.Abs(forward.Z)) / 0.45f, 0f, 1f);
        return new HandPose(isRight, angles) { Quality = facing * across, DepthMirrored = mirrored };
    }

    /// <summary>
    /// 指の先（と親指の先）が、掌の側へ向いている度合い（-1〜1 の平均）。正なら、左右の見分けと点の 3D の形が合っている。
    /// </summary>
    public static float CurlToPalm(ReadOnlySpan<Vector3> p, bool isRight)
    {
        Vector3 normal = Unit(Vector3.Cross(p[5] - p[0], p[17] - p[0]));
        if (!isRight)
        {
            normal = -normal;
        }
        float sum = Vector3.Dot(Unit(p[4] - p[2]), normal);
        for (int b = 5; b <= 17; b += 4)
        {
            sum += Vector3.Dot(Unit(p[b + 3] - p[b]), normal);
        }
        return sum / 5f;
    }

    private static Vector3 Unit(Vector3 v)
    {
        float length = v.Length();
        return length > 1e-9f ? v / length : Vector3.Zero;
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));

    // a から b への曲がり（axis の向きに回ると正）
    private static float Signed(Vector3 a, Vector3 b, Vector3 axis)
    {
        float angle = Angle(a, b);
        return Vector3.Dot(Vector3.Cross(a, b), axis) < 0 ? -angle : angle;
    }

    private static float Degrees(float radians) => radians * (180f / MathF.PI);
}
