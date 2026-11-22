using System.Diagnostics;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace Theater3D.Tracking;

/// <summary>
/// 1 枚の絵の体のトラッキングの結果。<see cref="Landmarks"/> は 33 点の画素の座標（x 右・y 下。z は x と同じくらいの大きさの奥行き）、
/// <see cref="Body"/> はメートルの点と見えている度合い（体が無ければ null）。
/// </summary>
public sealed record PoseTrackingResult(bool Found, Vector3[] Landmarks, BodyPose? Body, float Presence, bool Detected, double Milliseconds)
{
    public static PoseTrackingResult None { get; } = new(false, [], null, 0f, false, 0);
}

/// <summary>
/// 体のトラッキング（MediaPipe の Pose Landmarker の軽い版と同じ流れ。設計判断記録 100 章）。
/// 1. 体を探すモデル（224 × 224。色は -1〜1。縦横比を保って収め、外は黒）で、腰の真ん中と体を囲む円を見つける
/// 2. 体の範囲（腰の真ん中を真ん中に、円の直径の 1.25 倍の正方形）を 256 × 256 に切り出し（色は 0〜1。画像の外は端の色）、
///    体の 39 点（33 点と、次の範囲を決める補いの点）の画素とメートルの値・見えている度合い・体らしさを求める
/// 3. 次の絵では、補いの点（33 = 真ん中・34 = 円の上の点）から範囲を決める（体らしさが 0.5 を下回ったら、探し直す）
/// 人は 1 人だけ追う。体を追っていない間は、<see cref="EmptySearchInterval"/> 枚ごとに探す（体を探すモデルは、点のモデルより重い）。
/// <see cref="Process"/> は 1 つのスレッドから呼ぶこと。
/// </summary>
public sealed class PoseTracker : IDisposable
{
    public const int LandmarkCount = BodyPose.PointCount;
    public const int ModelLandmarkCount = 39;
    public const int LandmarkInputSize = 256;
    public const float PresenceThreshold = 0.5f;

    /// <summary>体を追っていないとき、探す間隔（枚）。体を見せてから付くまでが、最大でこの枚数だけ遅れる。</summary>
    public const int EmptySearchInterval = 2;

    // 次の範囲を決める補いの点（真ん中と、円の上の点）
    private const int RegionCenterPoint = 33;
    private const int RegionCirclePoint = 34;

    private readonly InferenceSession _detector;
    private readonly InferenceSession _landmarker;
    private readonly float[] _detectorInput = new float[PoseDetectorMath.InputSize * PoseDetectorMath.InputSize * 3];
    private readonly float[] _landmarkInput = new float[LandmarkInputSize * LandmarkInputSize * 3];
    private readonly RunOptions _run = new();
    private readonly int _screenOutput;
    private readonly int _presenceOutput;
    private readonly int _worldOutput;
    private FaceRegion? _region;
    private int _sinceSearch = int.MaxValue / 2;

    /// <param name="modelDirectory">モデルのフォルダー（pose_detector.onnx・pose_landmarks_detector.onnx）。</param>
    /// <param name="threads">1 つのモデルが使うスレッドの数。</param>
    public PoseTracker(string? modelDirectory = null, int threads = 2)
    {
        string directory = modelDirectory ?? DefaultModelDirectory;
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, threads),
            InterOpNumThreads = 1,
        };
        _detector = new InferenceSession(Path.Combine(directory, "pose_detector.onnx"), options);
        _landmarker = new InferenceSession(Path.Combine(directory, "pose_landmarks_detector.onnx"), options);
        // 出力は名前で見分ける（画素の点・体らしさ・メートルの点。変換したモデルの名前のまま。使わない出力は変換で外した）
        List<string> names = [.. _landmarker.OutputNames];
        _screenOutput = names.IndexOf("Identity");
        _presenceOutput = names.IndexOf("Identity_1");
        _worldOutput = names.IndexOf("Identity_4");
        if (_screenOutput < 0 || _presenceOutput < 0 || _worldOutput < 0)
        {
            throw new InvalidDataException($"体の点のモデルの出力が思っていた形ではありません（{string.Join("・", names)}）");
        }
    }

    /// <summary>アプリと一緒に置くモデルのフォルダー。</summary>
    public static string DefaultModelDirectory => Path.Combine(AppContext.BaseDirectory, "Models", "PoseLandmarker");

    /// <summary>体を追っているか。</summary>
    public bool IsTracking => _region is not null;

    /// <summary>追うのをやめ、次の絵で体を探し直す。</summary>
    public void Reset()
    {
        _region = null;
        _sinceSearch = int.MaxValue / 2;
    }

    public PoseTrackingResult Process(BgraImage image)
    {
        long start = Stopwatch.GetTimestamp();
        bool detected = false;
        FaceRegion? region = _region;
        if (region is null)
        {
            _sinceSearch++;
            if (_sinceSearch >= EmptySearchInterval)
            {
                _sinceSearch = 0;
                detected = true;
                region = Detect(image) is [var best, ..] ? PoseDetectorMath.RegionFromDetection(best) : null;
            }
        }
        if (region is not { } r || Landmarks(image, r) is not { } found)
        {
            if (_region is not null)
            {
                _sinceSearch = EmptySearchInterval; // 見失ったら、次の絵ですぐ探す
            }
            _region = null;
            return new PoseTrackingResult(false, [], null, 0f, detected, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        _region = found.Next;
        return new PoseTrackingResult(true, found.Landmarks, found.Body, found.Presence, detected, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>体を探す（体らしさの高い順。座標は画像の画素）。</summary>
    public List<FaceDetection> Detect(BgraImage image)
    {
        const int size = PoseDetectorMath.InputSize;
        // 縦横比を保って 224 × 224 に収める（足りない所は黒）。色は -1〜1
        (float scale, Vector2 pad) = ImageSampler.SampleLetterbox(image, size, _detectorInput, 2f / 255f, -1f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_detectorInput, [1, size, size, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _detector.Run(_run, [_detector.InputNames[0]], [input], _detector.OutputNames);
        // 出力の並びはモデルの書き方しだいなので、大きさで見分ける（箱は 2254 × 12、体らしさは 2254）
        bool swapped = outputs[0].GetTensorDataAsSpan<float>().Length < outputs[1].GetTensorDataAsSpan<float>().Length;
        ReadOnlySpan<float> regressors = outputs[swapped ? 1 : 0].GetTensorDataAsSpan<float>();
        ReadOnlySpan<float> scores = outputs[swapped ? 0 : 1].GetTensorDataAsSpan<float>();
        Vector2 ToImage(Vector2 p) => ((p * size) - pad) / scale;
        return [.. PoseDetectorMath.Decode(regressors, scores)
            .Select(d => d with { Min = ToImage(d.Min), Max = ToImage(d.Max), Keypoints = [.. d.Keypoints.Select(ToImage)] })];
    }

    // 範囲を切り出して体の点を求める（体らしさが足りなければ null）
    private (Vector3[] Landmarks, BodyPose Body, float Presence, FaceRegion Next)? Landmarks(BgraImage image, FaceRegion region)
    {
        // 体の範囲は絵の外（腰より下など）へ大きくはみ出すので、外は端の色にする（MediaPipe と同じ。黒にすると、腕の点が大きくずれる）
        ImageSampler.Sample(image, LandmarkInputSize, region, _landmarkInput, 1f / 255f, 0f, clampEdges: true);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_landmarkInput, [1, LandmarkInputSize, LandmarkInputSize, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _landmarker.Run(_run, [_landmarker.InputNames[0]], [input], _landmarker.OutputNames);
        float presence = outputs[_presenceOutput].GetTensorDataAsSpan<float>()[0];
        if (!(presence >= PresenceThreshold))
        {
            return null;
        }
        ReadOnlySpan<float> screen = outputs[_screenOutput].GetTensorDataAsSpan<float>();
        ReadOnlySpan<float> world = outputs[_worldOutput].GetTensorDataAsSpan<float>();
        float cos = MathF.Cos(region.Rotation);
        float sin = MathF.Sin(region.Rotation);
        var landmarks = new Vector3[LandmarkCount];
        BodyPose body = BodyPose.Empty();
        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector2 p = region.ToImage(screen[5 * i] / LandmarkInputSize, screen[(5 * i) + 1] / LandmarkInputSize);
            landmarks[i] = new Vector3(p, screen[(5 * i) + 2] / LandmarkInputSize * region.Size);
            body.Visibility[i] = FaceDetectorMath.Sigmoid(screen[(5 * i) + 3]);
            // メートルの点は切り出した向きのままなので、画像の向きへ回し戻す
            float x = world[3 * i];
            float y = world[(3 * i) + 1];
            body.Points[i] = new Vector3((cos * x) - (sin * y), (sin * x) + (cos * y), world[(3 * i) + 2]);
        }
        Vector2 center = region.ToImage(screen[5 * RegionCenterPoint] / LandmarkInputSize, screen[(5 * RegionCenterPoint) + 1] / LandmarkInputSize);
        Vector2 circle = region.ToImage(screen[5 * RegionCirclePoint] / LandmarkInputSize, screen[(5 * RegionCirclePoint) + 1] / LandmarkInputSize);
        FaceRegion next = PoseDetectorMath.RegionFromPoints(center, circle);
        return (landmarks, body, presence, next);
    }

    public void Dispose()
    {
        _detector.Dispose();
        _landmarker.Dispose();
        _run.Dispose();
    }
}
