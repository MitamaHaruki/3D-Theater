using System.Diagnostics;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace Theater3D.Tracking;

/// <summary>1 枚の絵の顔のトラッキングの結果。点は画素の座標（x 右・y 下・z 奥。z は x と同じくらいの大きさ）。</summary>
public sealed record FaceTrackingResult(
    bool Found,
    Vector3[] Landmarks,
    float[] Blendshapes,
    HeadPose Pose,
    FaceRegion Region,
    float Presence,
    bool Detected,
    double Milliseconds)
{
    public static FaceTrackingResult NotFound(float presence, double milliseconds) =>
        new(false, [], [], default, default, presence, false, milliseconds);

    /// <summary>名前で表情の値を読む（見つからなければ 0）。</summary>
    public float Blendshape(string name)
    {
        int i = FaceBlendshapes.IndexOf(name);
        return i >= 0 && i < Blendshapes.Length ? Blendshapes[i] : 0f;
    }

    /// <summary>顔の真ん中（鼻先。画素）。顔が無ければ 0。</summary>
    public Vector2 Center => Found && Landmarks.Length > HeadPose.NoseTip ? new Vector2(Landmarks[HeadPose.NoseTip].X, Landmarks[HeadPose.NoseTip].Y) : Vector2.Zero;

    /// <summary>顔の大きさ（右目尻から左目尻までの 3 倍。画素。顔の範囲とほぼ同じで、回しても変わりにくい）。顔が無ければ 0。</summary>
    public float Size
    {
        get
        {
            if (!Found || Landmarks.Length <= Math.Max(HeadPose.RightEyeOuter, HeadPose.LeftEyeOuter))
            {
                return 0f;
            }
            Vector3 r = Landmarks[HeadPose.RightEyeOuter];
            Vector3 l = Landmarks[HeadPose.LeftEyeOuter];
            return Vector2.Distance(new Vector2(r.X, r.Y), new Vector2(l.X, l.Y)) * 3f;
        }
    }

    /// <summary>表情の値を名前で引けるようにする（ARKit の名前。MediaPipe の「_neutral」も入る）。</summary>
    public Dictionary<string, float> BlendshapeMap()
    {
        var map = new Dictionary<string, float>(FaceBlendshapes.Count, StringComparer.Ordinal);
        for (int i = 0; i < FaceBlendshapes.Count && i < Blendshapes.Length; i++)
        {
            map[FaceBlendshapes.Names[i]] = Blendshapes[i];
        }
        return map;
    }
}

/// <summary>
/// 顔のトラッキング（MediaPipe の Face Landmarker と同じ流れ）。
/// 1. 顔を探すモデル（128 × 128。見失っているときだけ）で顔と両目の位置を見つける
/// 2. 顔の範囲（1.5 倍・両目が水平）を 256 × 256 に切り出し、顔の点（478 個）と顔らしさを求める
/// 3. 146 個の点から表情の値（52 個）を求め、点から頭の向きを求める
/// 4. 次の絵では、今の点から顔の範囲を決める（顔らしさが 0.5 を下回ったら、次は探し直す）
/// 1 つの顔だけを追う。<see cref="Process"/> は 1 つのスレッドから呼ぶこと。
/// </summary>
public sealed class FaceTracker : IDisposable
{
    public const int LandmarkCount = 478;
    public const int LandmarkInputSize = 256;
    public const float PresenceThreshold = 0.5f;

    private readonly InferenceSession _detector;
    private readonly InferenceSession _landmarker;
    private readonly InferenceSession _blendshapes;
    private readonly float[] _detectorInput = new float[FaceDetectorMath.InputSize * FaceDetectorMath.InputSize * 3];
    private readonly float[] _landmarkInput = new float[LandmarkInputSize * LandmarkInputSize * 3];
    private readonly float[] _blendshapeInput = new float[FaceBlendshapes.LandmarkSubset.Count * 2];
    private readonly RunOptions _run = new();
    private FaceRegion? _tracked;

    /// <param name="modelDirectory">モデルのフォルダー（face_detector.onnx・face_landmarks_detector.onnx・face_blendshapes.onnx）。</param>
    public FaceTracker(string? modelDirectory = null, int threads = 2)
    {
        string directory = modelDirectory ?? DefaultModelDirectory;
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, threads),
            InterOpNumThreads = 1,
        };
        _detector = new InferenceSession(Path.Combine(directory, "face_detector.onnx"), options);
        _landmarker = new InferenceSession(Path.Combine(directory, "face_landmarks_detector.onnx"), options);
        _blendshapes = new InferenceSession(Path.Combine(directory, "face_blendshapes.onnx"), options);
    }

    /// <summary>アプリと一緒に置くモデルのフォルダー。</summary>
    public static string DefaultModelDirectory => Path.Combine(AppContext.BaseDirectory, "Models", "FaceLandmarker");

    /// <summary>今、顔を追っているか（次の絵で探し直さずに済むか）。</summary>
    public bool IsTracking => _tracked is not null;

    /// <summary>追うのをやめ、次の絵で顔を探し直す。</summary>
    public void Reset() => _tracked = null;

    public FaceTrackingResult Process(BgraImage image)
    {
        long start = Stopwatch.GetTimestamp();
        bool detected = false;
        FaceRegion? region = _tracked;
        if (region is null)
        {
            if (Detect(image).FirstOrDefault() is not { } face)
            {
                return FaceTrackingResult.NotFound(0, Elapsed(start));
            }
            region = face.Region;
            detected = true;
        }

        (Vector3[] landmarks, float presence) = Landmarks(image, region.Value);
        if (presence < PresenceThreshold && !detected)
        {
            // 追っていた範囲から外れた: すぐに探し直す
            _tracked = null;
            if (Detect(image).FirstOrDefault() is { } again)
            {
                region = again.Region;
                detected = true;
                (landmarks, presence) = Landmarks(image, region.Value);
            }
        }
        if (presence < PresenceThreshold)
        {
            _tracked = null;
            return FaceTrackingResult.NotFound(presence, Elapsed(start));
        }

        float[] blendshapes = Blendshapes(landmarks);
        HeadPose pose = HeadPose.FromLandmarks(landmarks);
        _tracked = RegionFromLandmarks(landmarks);
        return new FaceTrackingResult(true, landmarks, blendshapes, pose, region.Value, presence, detected, Elapsed(start));
    }

    /// <summary>顔を探す（顔らしさの高い順。座標は画像の画素）。</summary>
    public List<FaceDetection> Detect(BgraImage image)
    {
        const int size = FaceDetectorMath.InputSize;
        // 縦横比を保って 128 × 128 に収める（足りない所は 0）
        (float scale, Vector2 pad) = ImageSampler.SampleLetterbox(image, size, _detectorInput, 2f / 255f, -1f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_detectorInput, [1, size, size, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _detector.Run(_run, [_detector.InputNames[0]], [input], _detector.OutputNames);
        // 出力の並びはモデルの書き方しだいなので、大きさで見分ける（箱は 896 × 16、顔らしさは 896）
        bool swapped = outputs[0].GetTensorDataAsSpan<float>().Length < outputs[1].GetTensorDataAsSpan<float>().Length;
        ReadOnlySpan<float> regressors = outputs[swapped ? 1 : 0].GetTensorDataAsSpan<float>();
        ReadOnlySpan<float> scores = outputs[swapped ? 0 : 1].GetTensorDataAsSpan<float>();
        Vector2 ToImage(Vector2 p) => ((p * size) - pad) / scale;
        return [.. FaceDetectorMath.Decode(regressors, scores)
            .Select(d => d with { Min = ToImage(d.Min), Max = ToImage(d.Max), Keypoints = [.. d.Keypoints.Select(ToImage)] })];
    }

    private (Vector3[] Landmarks, float Presence) Landmarks(BgraImage image, FaceRegion region)
    {
        ImageSampler.Sample(image, LandmarkInputSize, region, _landmarkInput, 1f / 255f, 0f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_landmarkInput, [1, LandmarkInputSize, LandmarkInputSize, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _landmarker.Run(_run, [_landmarker.InputNames[0]], [input], _landmarker.OutputNames);
        ReadOnlySpan<float> raw = default;
        float presence = 0;
        for (int i = 0; i < outputs.Count; i++)
        {
            ReadOnlySpan<float> data = outputs[i].GetTensorDataAsSpan<float>();
            if (data.Length == LandmarkCount * 3)
            {
                raw = data;
            }
            else if (data.Length == 1 && _landmarker.OutputNames[i] == "Identity_1")
            {
                presence = FaceDetectorMath.Sigmoid(data[0]); // 顔らしさ（face flag）
            }
        }
        var landmarks = new Vector3[LandmarkCount];
        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector2 p = region.ToImage(raw[3 * i] / LandmarkInputSize, raw[(3 * i) + 1] / LandmarkInputSize);
            landmarks[i] = new Vector3(p, raw[(3 * i) + 2] / LandmarkInputSize * region.Size);
        }
        return (landmarks, presence);
    }

    private float[] Blendshapes(Vector3[] landmarks)
    {
        FaceBlendshapes.WriteInput(landmarks, _blendshapeInput);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_blendshapeInput, [1, FaceBlendshapes.LandmarkSubset.Count, 2]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _blendshapes.Run(_run, [_blendshapes.InputNames[0]], [input], _blendshapes.OutputNames);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>今の点から、次の絵の顔の範囲（点を囲む箱・右目尻 → 左目尻を水平に）。</summary>
    public static FaceRegion RegionFromLandmarks(ReadOnlySpan<Vector3> landmarks)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (Vector3 p in landmarks)
        {
            var q = new Vector2(p.X, p.Y);
            min = Vector2.Min(min, q);
            max = Vector2.Max(max, q);
        }
        Vector3 start = landmarks[HeadPose.RightEyeOuter];
        Vector3 end = landmarks[HeadPose.LeftEyeOuter];
        return FaceRegion.From(min, max, new Vector2(start.X, start.Y), new Vector2(end.X, end.Y));
    }

    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    public void Dispose()
    {
        _detector.Dispose();
        _landmarker.Dispose();
        _blendshapes.Dispose();
        _run.Dispose();
    }
}
