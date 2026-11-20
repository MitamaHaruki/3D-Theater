using System.Numerics;

namespace Theater3D.Tracking;

/// <summary>BGRA 8 ビット・上の行からの絵（1 行は幅 × 4 バイト）。</summary>
public readonly record struct BgraImage(byte[] Pixels, int Width, int Height)
{
    public int Stride => Width * 4;
}

/// <summary>
/// 顔の点を求めるために切り出す範囲（画素の座標）。真ん中・一辺・回転（ラジアン。画像の上で、時計回りが正）。
/// MediaPipe と同じく、顔を囲む箱の長いほうの辺を 1.5 倍にした正方形を、両目を結ぶ線が水平になるよう回して切り出す。
/// </summary>
public readonly record struct FaceRegion(Vector2 Center, float Size, float Rotation)
{
    /// <summary>切り出す範囲の広げ方（MediaPipe の顔の点の決まり）。</summary>
    public const float Scale = 1.5f;

    /// <summary>
    /// 顔を囲む箱（<paramref name="min"/>〜<paramref name="max"/>）と、回転を決める 2 点（右目 → 左目のように、水平になってほしい向き）から作る。
    /// </summary>
    public static FaceRegion From(Vector2 min, Vector2 max, Vector2 rotationStart, Vector2 rotationEnd)
    {
        float rotation = NormalizeAngle(-MathF.Atan2(-(rotationEnd.Y - rotationStart.Y), rotationEnd.X - rotationStart.X));
        Vector2 size = max - min;
        return new FaceRegion((min + max) * 0.5f, MathF.Max(size.X, size.Y) * Scale, rotation);
    }

    /// <summary>切り出した絵の中の場所（0〜1）を、元の画像の画素の座標にする。</summary>
    public Vector2 ToImage(float u, float v)
    {
        float x = u - 0.5f;
        float y = v - 0.5f;
        float cos = MathF.Cos(Rotation);
        float sin = MathF.Sin(Rotation);
        return Center + new Vector2((cos * x) - (sin * y), (sin * x) + (cos * y)) * Size;
    }

    public static float NormalizeAngle(float angle) => angle - (2 * MathF.PI * MathF.Floor((angle + MathF.PI) / (2 * MathF.PI)));
}

/// <summary>
/// 顔を探すモデルが見つけた顔（画素の座標）。点は右目・左目・鼻先・口・右耳・左耳の 6 つ（本人から見た左右）。
/// 掌を探すモデルの結果にも使う（点は 7 つ。<see cref="HandDetectorMath"/>）。
/// </summary>
public sealed record FaceDetection(Vector2 Min, Vector2 Max, Vector2[] Keypoints, float Score)
{
    /// <summary>顔の点を求める範囲（右目 → 左目を水平にする）。</summary>
    public FaceRegion Region => FaceRegion.From(Min, Max, Keypoints[0], Keypoints[1]);
}

/// <summary>
/// 絵の切り出し（双一次補間）と、モデルへ渡す数の並びへの書き込み。切り出し方は、元の画像の座標 = 始まり + u × 横の向き + v × 縦の向き
/// （u・v は出力の 0〜1）の形だけ（回した範囲も、縦横比を保った縮小も、この形）。画素ごとに関数を呼ばず、足していくだけにして速くした（設計判断記録 99 章）。
/// </summary>
internal static class ImageSampler
{
    /// <summary>
    /// 元の画像の <c>origin + u × axisU + v × axisV</c>（u・v は出力の 0〜1）を <paramref name="size"/> × <paramref name="size"/> に切り出し、
    /// RGB の順に <c>値 × scale + offset</c> を並べる（NHWC）。外は 0 の色。
    /// </summary>
    public static void Sample(BgraImage image, int size, Vector2 origin, Vector2 axisU, Vector2 axisV, Span<float> rgb, float scale, float offset)
    {
        byte[] p = image.Pixels;
        int stride = image.Stride;
        int width = image.Width;
        int height = image.Height;
        Vector2 du = axisU / size;
        Vector2 dv = axisV / size;
        // 出力の画素の真ん中に当たる場所（画素の真ん中が整数＋0.5 なので、0.5 引いて補間の位置にする）
        Vector2 first = origin + ((du + dv) * 0.5f) - new Vector2(0.5f);
        int index = 0;
        for (int v = 0; v < size; v++)
        {
            Vector2 s = first + (dv * v);
            for (int u = 0; u < size; u++, s += du)
            {
                int x0 = (int)MathF.Floor(s.X);
                int y0 = (int)MathF.Floor(s.Y);
                float fx = s.X - x0;
                float fy = s.Y - y0;
                float r, g, b;
                if ((uint)x0 < (uint)(width - 1) && (uint)y0 < (uint)(height - 1))
                {
                    // 4 つとも画像の中（ほとんどの画素）
                    int i = (y0 * stride) + (x0 * 4);
                    int j = i + stride;
                    float w00 = (1 - fx) * (1 - fy);
                    float w10 = fx * (1 - fy);
                    float w01 = (1 - fx) * fy;
                    float w11 = fx * fy;
                    b = (p[i] * w00) + (p[i + 4] * w10) + (p[j] * w01) + (p[j + 4] * w11);
                    g = (p[i + 1] * w00) + (p[i + 5] * w10) + (p[j + 1] * w01) + (p[j + 5] * w11);
                    r = (p[i + 2] * w00) + (p[i + 6] * w10) + (p[j + 2] * w01) + (p[j + 6] * w11);
                }
                else
                {
                    r = g = b = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int x = x0 + (k & 1);
                        int y = y0 + (k >> 1);
                        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                        {
                            continue;
                        }
                        float w = ((k & 1) == 0 ? 1 - fx : fx) * ((k >> 1) == 0 ? 1 - fy : fy);
                        int i = (y * stride) + (x * 4);
                        b += p[i] * w;
                        g += p[i + 1] * w;
                        r += p[i + 2] * w;
                    }
                }
                rgb[index++] = (r * scale) + offset;
                rgb[index++] = (g * scale) + offset;
                rgb[index++] = (b * scale) + offset;
            }
        }
    }

    /// <summary>回した範囲（<see cref="FaceRegion"/>）を切り出す。</summary>
    public static void Sample(BgraImage image, int size, FaceRegion region, Span<float> rgb, float scale, float offset)
    {
        Vector2 origin = region.ToImage(0, 0);
        Sample(image, size, origin, region.ToImage(1, 0) - origin, region.ToImage(0, 1) - origin, rgb, scale, offset);
    }

    /// <summary>
    /// 画像全体を、縦横比を保って <paramref name="size"/> × <paramref name="size"/> に収める（足りない所は 0 の色）。
    /// 縮めた倍率と、上下・左右の余白（出力の画素）を返す（出力の座標 p は、元の画像の (p − pad) ÷ scale）。
    /// </summary>
    public static (float Scale, Vector2 Pad) SampleLetterbox(BgraImage image, int size, Span<float> rgb, float scale, float offset)
    {
        float factor = Math.Min(size / (float)image.Width, size / (float)image.Height);
        var pad = new Vector2((size - (image.Width * factor)) * 0.5f, (size - (image.Height * factor)) * 0.5f);
        Sample(image, size, -pad / factor, new Vector2(size / factor, 0), new Vector2(0, size / factor), rgb, scale, offset);
        return (factor, pad);
    }
}

/// <summary>
/// 顔を探すモデル（BlazeFace の近距離用。128 × 128）の、位置の候補（アンカー）と結果の読み方（MediaPipe の face_detection_short_range と同じ）。
/// </summary>
public static class FaceDetectorMath
{
    public const int InputSize = 128;
    public const int AnchorCount = 896;
    public const float ScoreThreshold = 0.5f;
    public const float OverlapThreshold = 0.3f;

    /// <summary>位置の候補（0〜1）。16 × 16 の升に 2 つずつ、8 × 8 の升に 6 つずつ。</summary>
    public static IReadOnlyList<Vector2> Anchors { get; } = CreateAnchors();

    private static Vector2[] CreateAnchors() => SsdAnchors(InputSize, [8, 16, 16, 16]);

    /// <summary>
    /// MediaPipe の SSD の位置の候補（0〜1）。同じ間隔の層はまとめ、1 つの升に層ごとに 2 つ（縦横比 1 と、その間の大きさ。大きさは決まった 1）。
    /// 顔（128 × 128）と掌（192 × 192。設計判断記録 99 章）で使う。
    /// </summary>
    internal static Vector2[] SsdAnchors(int inputSize, int[] strides)
    {
        var anchors = new List<Vector2>();
        for (int layer = 0; layer < strides.Length;)
        {
            int stride = strides[layer];
            int perCell = 0;
            int last = layer;
            while (last < strides.Length && strides[last] == stride)
            {
                perCell += 2; // 縦横比 1 と、その間の大きさの 2 つ（大きさは決まった 1）
                last++;
            }
            int cells = (int)Math.Ceiling(inputSize / (double)stride);
            for (int y = 0; y < cells; y++)
            {
                for (int x = 0; x < cells; x++)
                {
                    for (int k = 0; k < perCell; k++)
                    {
                        anchors.Add(new Vector2((x + 0.5f) / cells, (y + 0.5f) / cells));
                    }
                }
            }
            layer = last;
        }
        return [.. anchors];
    }

    /// <summary>
    /// モデルの出力（候補ごとに 16 個の数と、顔らしさ）から、顔を取り出す（座標は 128 × 128 の入力の 0〜1）。
    /// 重なった候補は、顔らしさで重みを付けて平均する（MediaPipe の WEIGHTED）。
    /// </summary>
    public static List<FaceDetection> Decode(ReadOnlySpan<float> regressors, ReadOnlySpan<float> scores)
    {
        var candidates = new List<FaceDetection>();
        for (int i = 0; i < AnchorCount; i++)
        {
            float score = Sigmoid(Math.Clamp(scores[i], -100f, 100f));
            if (score < ScoreThreshold)
            {
                continue;
            }
            Vector2 a = Anchors[i];
            ReadOnlySpan<float> r = regressors.Slice(i * 16, 16);
            var center = new Vector2((r[0] / InputSize) + a.X, (r[1] / InputSize) + a.Y);
            var half = new Vector2(r[2] / InputSize, r[3] / InputSize) * 0.5f;
            if (!(half.X > 0 && half.Y > 0 && float.IsFinite(center.X) && float.IsFinite(center.Y)))
            {
                continue; // 大きさの無い箱・数でない値は捨てる
            }
            var keypoints = new Vector2[6];
            for (int k = 0; k < 6; k++)
            {
                keypoints[k] = new Vector2((r[4 + (2 * k)] / InputSize) + a.X, (r[5 + (2 * k)] / InputSize) + a.Y);
            }
            candidates.Add(new FaceDetection(center - half, center + half, keypoints, score));
        }
        return WeightedSuppression(candidates);
    }

    public static List<FaceDetection> WeightedSuppression(List<FaceDetection> candidates) => WeightedSuppression(candidates, OverlapThreshold);

    /// <summary>重なりが <paramref name="threshold"/> を超える候補を、点数で重みを付けて平均する（MediaPipe の WEIGHTED）。</summary>
    public static List<FaceDetection> WeightedSuppression(List<FaceDetection> candidates, float threshold)
    {
        var remaining = candidates.OrderByDescending(c => c.Score).ToList();
        var result = new List<FaceDetection>();
        while (remaining.Count > 0)
        {
            FaceDetection best = remaining[0];
            // いちばん良い候補は必ず取り除く（大きさが 0 の箱は自分とも重ならないので、重なりだけで選ぶと終わらない）
            List<FaceDetection> cluster = [best, .. remaining.Skip(1).Where(c => Overlap(best, c) > threshold)];
            remaining.RemoveAll(c => cluster.Contains(c));
            float total = cluster.Sum(c => c.Score);
            Vector2 min = Vector2.Zero;
            Vector2 max = Vector2.Zero;
            var keypoints = new Vector2[best.Keypoints.Length];
            foreach (FaceDetection c in cluster)
            {
                float w = c.Score / total;
                min += c.Min * w;
                max += c.Max * w;
                for (int k = 0; k < keypoints.Length; k++)
                {
                    keypoints[k] += c.Keypoints[k] * w;
                }
            }
            result.Add(new FaceDetection(min, max, keypoints, best.Score));
        }
        return result;
    }

    /// <summary>2 つの箱の重なり（共通部分 ÷ 合わせた範囲）。</summary>
    public static float Overlap(FaceDetection a, FaceDetection b)
    {
        Vector2 min = Vector2.Max(a.Min, b.Min);
        Vector2 max = Vector2.Min(a.Max, b.Max);
        Vector2 size = Vector2.Max(max - min, Vector2.Zero);
        float intersection = size.X * size.Y;
        float areaA = (a.Max.X - a.Min.X) * (a.Max.Y - a.Min.Y);
        float areaB = (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y);
        float union = areaA + areaB - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    public static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
}
