using System.Diagnostics;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace Theater3D.Tracking;

/// <summary>
/// 見つけた手 1 つ。<see cref="Landmarks"/> は画素の座標（x 右・y 下・z 奥。z は x と同じくらいの大きさ）、
/// <see cref="World"/> はメートル（手の真ん中の辺りが 0。向きは画像と同じ）。左右は本人から見た左右。
/// </summary>
public sealed record TrackedHand(Vector3[] Landmarks, Vector3[] World, bool IsRight, float Handedness, float Presence, FaceRegion Region)
{
    /// <summary>指の曲がりと開き。</summary>
    public HandPose Pose => HandPose.FromWorld(World, IsRight);
}

/// <summary>1 枚の絵の手のトラッキングの結果（0〜2 つの手）。</summary>
public sealed record HandTrackingResult(IReadOnlyList<TrackedHand> Hands, bool Detected, double Milliseconds)
{
    public static HandTrackingResult None { get; } = new([], false, 0);

    /// <summary>本人の左手（無ければ null）。</summary>
    public TrackedHand? Left => Hands.FirstOrDefault(h => !h.IsRight);

    /// <summary>本人の右手（無ければ null）。</summary>
    public TrackedHand? Right => Hands.FirstOrDefault(h => h.IsRight);
}

/// <summary>
/// 手のトラッキング（MediaPipe の Hand Landmarker と同じ流れ。設計判断記録 99 章）。
/// 1. 掌を探すモデル（192 × 192）で掌と手首・指の付け根の位置を見つける
/// 2. 手の範囲（手首 → 中指の付け根を上に向けて 2.6 倍）を 224 × 224 に切り出し、手の 21 点（画素とメートル）・手らしさ・左右を求める
/// 3. 次の絵では、今の点から手の範囲を決める（手らしさが 0.5 を下回ったら、その手は探し直す）
/// 手は 2 つまで追う。1 つも追っていなければ毎枚探し、1 つだけなら <see cref="SearchInterval"/> 枚ごとにもう 1 つを探す（毎枚探すと重いため）。
/// <see cref="Process"/> は 1 つのスレッドから呼ぶこと。
/// </summary>
public sealed class HandTracker : IDisposable
{
    public const int LandmarkCount = 21;
    public const int LandmarkInputSize = 224;
    public const float PresenceThreshold = 0.5f;
    public const int MaxHands = 2;

    /// <summary>手を 1 つだけ追っているとき、もう 1 つを探す間隔（枚）。</summary>
    public const int SearchInterval = 5;

    private readonly InferenceSession _detector;
    private readonly InferenceSession _landmarker;
    private readonly float[] _detectorInput = new float[HandDetectorMath.InputSize * HandDetectorMath.InputSize * 3];
    private readonly float[] _landmarkInput = new float[LandmarkInputSize * LandmarkInputSize * 3];
    private readonly RunOptions _run = new();
    private readonly List<FaceRegion> _tracked = [];
    private readonly int _screenOutput;
    private readonly int _presenceOutput;
    private readonly int _handednessOutput;
    private readonly int _worldOutput;
    private int _sinceSearch;

    /// <param name="modelDirectory">モデルのフォルダー（hand_detector.onnx・hand_landmarks_detector.onnx）。</param>
    public HandTracker(string? modelDirectory = null, int threads = 2)
    {
        string directory = modelDirectory ?? DefaultModelDirectory;
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, threads),
            InterOpNumThreads = 1,
        };
        _detector = new InferenceSession(Path.Combine(directory, "hand_detector.onnx"), options);
        _landmarker = new InferenceSession(Path.Combine(directory, "hand_landmarks_detector.onnx"), options);
        // 出力は名前で見分ける（画素の点・手らしさ・左右・メートルの点。変換したモデルの名前のまま）
        List<string> names = [.. _landmarker.OutputNames];
        _screenOutput = names.IndexOf("Identity");
        _presenceOutput = names.IndexOf("Identity_1");
        _handednessOutput = names.IndexOf("Identity_2");
        _worldOutput = names.IndexOf("Identity_3");
        if (_screenOutput < 0 || _presenceOutput < 0 || _handednessOutput < 0 || _worldOutput < 0)
        {
            throw new InvalidDataException($"手の点のモデルの出力が思っていた形ではありません（{string.Join("・", names)}）");
        }
    }

    /// <summary>アプリと一緒に置くモデルのフォルダー。</summary>
    public static string DefaultModelDirectory => Path.Combine(AppContext.BaseDirectory, "Models", "HandLandmarker");

    /// <summary>今追っている手の数。</summary>
    public int TrackingCount => _tracked.Count;

    /// <summary>追うのをやめ、次の絵で手を探し直す。</summary>
    public void Reset()
    {
        _tracked.Clear();
        _sinceSearch = 0;
    }

    public HandTrackingResult Process(BgraImage image)
    {
        long start = Stopwatch.GetTimestamp();
        var hands = new List<TrackedHand>(MaxHands);
        var next = new List<FaceRegion>(MaxHands);
        foreach (FaceRegion region in _tracked)
        {
            if (Landmarks(image, region) is { } hand)
            {
                FaceRegion following = HandDetectorMath.RegionFromLandmarks(hand.Landmarks);
                if (!next.Any(r => HandDetectorMath.Overlap(r, following) > HandDetectorMath.SameHandOverlap))
                {
                    hands.Add(hand);
                    next.Add(following); // 2 つの範囲が同じ手に寄ってきたら、1 つにする
                }
            }
        }

        bool detected = false;
        _sinceSearch++;
        if (hands.Count < MaxHands && (hands.Count == 0 || _sinceSearch >= SearchInterval))
        {
            _sinceSearch = 0;
            detected = true;
            foreach (FaceDetection palm in Detect(image))
            {
                if (hands.Count >= MaxHands)
                {
                    break;
                }
                FaceRegion region = HandDetectorMath.RegionFromPalm(palm);
                if (next.Any(r => HandDetectorMath.Overlap(r, region) > HandDetectorMath.SameHandOverlap))
                {
                    continue; // もう追っている手
                }
                if (Landmarks(image, region) is { } hand)
                {
                    FaceRegion following = HandDetectorMath.RegionFromLandmarks(hand.Landmarks);
                    if (!next.Any(r => HandDetectorMath.Overlap(r, following) > HandDetectorMath.SameHandOverlap))
                    {
                        hands.Add(hand);
                        next.Add(following);
                    }
                }
            }
        }
        _tracked.Clear();
        _tracked.AddRange(next);
        return new HandTrackingResult(hands, detected, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>掌を探す（掌らしさの高い順。座標は画像の画素）。</summary>
    public List<FaceDetection> Detect(BgraImage image)
    {
        const int size = HandDetectorMath.InputSize;
        // 縦横比を保って 192 × 192 に収める（足りない所は 0）。色は 0〜1
        float scale = Math.Min(size / (float)image.Width, size / (float)image.Height);
        var pad = new Vector2((size - (image.Width * scale)) * 0.5f, (size - (image.Height * scale)) * 0.5f);
        ImageSampler.Sample(image, size, (u, v) => (new Vector2(u * size, v * size) - pad) / scale, _detectorInput, 1f / 255f, 0f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_detectorInput, [1, size, size, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _detector.Run(_run, [_detector.InputNames[0]], [input], _detector.OutputNames);
        // 出力の並びはモデルの書き方しだいなので、大きさで見分ける（箱は 2016 × 18、掌らしさは 2016）
        bool swapped = outputs[0].GetTensorDataAsSpan<float>().Length < outputs[1].GetTensorDataAsSpan<float>().Length;
        ReadOnlySpan<float> regressors = outputs[swapped ? 1 : 0].GetTensorDataAsSpan<float>();
        ReadOnlySpan<float> scores = outputs[swapped ? 0 : 1].GetTensorDataAsSpan<float>();
        Vector2 ToImage(Vector2 p) => ((p * size) - pad) / scale;
        return [.. HandDetectorMath.Decode(regressors, scores)
            .Select(d => d with { Min = ToImage(d.Min), Max = ToImage(d.Max), Keypoints = [.. d.Keypoints.Select(ToImage)] })];
    }

    // 範囲を切り出して手の点を求める（手らしさが足りなければ null）
    private TrackedHand? Landmarks(BgraImage image, FaceRegion region)
    {
        ImageSampler.Sample(image, LandmarkInputSize, region.ToImage, _landmarkInput, 1f / 255f, 0f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_landmarkInput, [1, LandmarkInputSize, LandmarkInputSize, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _landmarker.Run(_run, [_landmarker.InputNames[0]], [input], _landmarker.OutputNames);
        float presence = outputs[_presenceOutput].GetTensorDataAsSpan<float>()[0];
        if (presence < PresenceThreshold)
        {
            return null;
        }
        float handedness = outputs[_handednessOutput].GetTensorDataAsSpan<float>()[0];
        ReadOnlySpan<float> screen = outputs[_screenOutput].GetTensorDataAsSpan<float>();
        ReadOnlySpan<float> world = outputs[_worldOutput].GetTensorDataAsSpan<float>();
        var landmarks = new Vector3[LandmarkCount];
        var meters = new Vector3[LandmarkCount];
        float cos = MathF.Cos(region.Rotation);
        float sin = MathF.Sin(region.Rotation);
        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector2 p = region.ToImage(screen[3 * i] / LandmarkInputSize, screen[(3 * i) + 1] / LandmarkInputSize);
            landmarks[i] = new Vector3(p, screen[(3 * i) + 2] / LandmarkInputSize * region.Size);
            // メートルの点は切り出した向きのままなので、画像の向きへ回し戻す
            float x = world[3 * i];
            float y = world[(3 * i) + 1];
            meters[i] = new Vector3((cos * x) - (sin * y), (sin * x) + (cos * y), world[(3 * i) + 2]);
        }
        // 左右のモデルは左右反転した映像（自撮り）を前提に「左手らしさ」を返す（MediaPipe の「Left」）。反転していないカメラの映像では、
        // それが本人の右手になる（点の 3D の形の向きと、顔から見た位置で確かめた。設計判断記録 99 章）
        return new TrackedHand(landmarks, meters, handedness >= 0.5f, handedness, presence, region);
    }

    public void Dispose()
    {
        _detector.Dispose();
        _landmarker.Dispose();
        _run.Dispose();
    }
}
