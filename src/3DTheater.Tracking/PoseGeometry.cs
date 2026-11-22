using System.Numerics;

namespace Theater3D.Tracking;

/// <summary>
/// 体を探すモデル（BlazePose の体を探すモデル。224 × 224）の、位置の候補と結果の読み方、体の点を求める範囲の決め方
/// （MediaPipe の Pose Landmarker と同じ。設計判断記録 100 章）。
/// 見つけた体の点は 4 つ: 0 = 腰の真ん中、1 = 体全体を囲む円の上の点（腰の真ん中からの向きと長さで、範囲の回りと大きさを決める）、
/// 2 = 肩の真ん中、3 = 上半身を囲む円の上の点。範囲は、腰の真ん中を真ん中に、円の直径の 1.25 倍の正方形を、0 → 1 が上を向くように回す。
/// </summary>
public static class PoseDetectorMath
{
    public const int InputSize = 224;
    public const int AnchorCount = 2254;
    public const float ScoreThreshold = 0.5f;
    public const float OverlapThreshold = 0.3f;

    /// <summary>範囲の広げ方（円の直径に対して。見つけたときも、点から次の範囲を決めるときも同じ）。</summary>
    public const float RegionScale = 1.25f;

    /// <summary>位置の候補（0〜1）。28 × 28 の升に 2 つずつ、14 × 14 の升に 2 つずつ、7 × 7 の升に 6 つずつ。</summary>
    public static IReadOnlyList<Vector2> Anchors { get; } = FaceDetectorMath.SsdAnchors(InputSize, [8, 16, 32, 32, 32]);

    /// <summary>
    /// モデルの出力（候補ごとに 12 個の数と、体らしさ）から、体を取り出す（座標は 224 × 224 の入力の 0〜1）。
    /// 重なった候補は、体らしさで重みを付けて平均する。
    /// </summary>
    public static List<FaceDetection> Decode(ReadOnlySpan<float> regressors, ReadOnlySpan<float> scores)
    {
        const int values = 12;
        const int keypointCount = 4;
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
            var keypoints = new Vector2[keypointCount];
            for (int k = 0; k < keypointCount; k++)
            {
                keypoints[k] = new Vector2((r[4 + (2 * k)] / InputSize) + a.X, (r[5 + (2 * k)] / InputSize) + a.Y);
            }
            candidates.Add(new FaceDetection(center - half, center + half, keypoints, score));
        }
        return FaceDetectorMath.WeightedSuppression(candidates, OverlapThreshold);
    }

    /// <summary>見つけた体（画素の座標）から、体の点を求める範囲。</summary>
    public static FaceRegion RegionFromDetection(FaceDetection body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return RegionFromPoints(body.Keypoints[0], body.Keypoints[1]);
    }

    /// <summary>
    /// 真ん中の点と、円の上の点（画素の座標）から、範囲（円の直径の 1.25 倍の正方形。真ん中 → 円の上の点が上を向くように回す）。
    /// 見つけた体の点 0・1 と、体の点のモデルの補いの点（33・34）に使う。
    /// </summary>
    public static FaceRegion RegionFromPoints(Vector2 center, Vector2 onCircle)
    {
        float size = 2f * Vector2.Distance(center, onCircle) * RegionScale;
        return new FaceRegion(center, size, HandDetectorMath.Upward(center, onCircle));
    }
}

/// <summary>
/// 体の点（MediaPipe の 33 点。メートル。カメラから見た x 右・y 下・z 奥。腰の真ん中が 0）と、それぞれが絵に見えている度合い（0〜1）。
/// 左右は本人から見た左右。3D Theater VCamera は、肩から腰までの <see cref="SentFirst"/>〜<see cref="SentLast"/> を送る（設計判断記録 100 章）。
/// </summary>
public sealed record BodyPose(Vector3[] Points, float[] Visibility)
{
    public const int PointCount = 33;

    public const int LeftShoulder = 11;
    public const int RightShoulder = 12;
    public const int LeftElbow = 13;
    public const int RightElbow = 14;
    public const int LeftWrist = 15;
    public const int RightWrist = 16;
    public const int LeftPinky = 17;
    public const int RightPinky = 18;
    public const int LeftIndex = 19;
    public const int RightIndex = 20;
    public const int LeftThumb = 21;
    public const int RightThumb = 22;
    public const int LeftHip = 23;
    public const int RightHip = 24;

    /// <summary>送る点の範囲（肩・ひじ・手首・手の 3 点・腰）。</summary>
    public const int SentFirst = LeftShoulder;
    public const int SentLast = RightHip;

    /// <summary>見えているとみなす度合い（これ以上）。</summary>
    public const float VisibleThreshold = 0.5f;

    /// <summary>空の形（点はすべて 0・見えていない）。</summary>
    public static BodyPose Empty() => new(new Vector3[PointCount], new float[PointCount]);

    /// <summary>点が見えているか。</summary>
    public bool IsVisible(int point) => (uint)point < (uint)Visibility.Length && Visibility[point] >= VisibleThreshold;
}
