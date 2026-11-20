using System.Diagnostics;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;

namespace Theater3D.Tracking;

/// <summary>
/// 見つけた手 1 つ。<see cref="Landmarks"/> は画素の座標（x 右・y 下・z 奥。z は x と同じくらいの大きさ）、
/// <see cref="World"/> はメートル（手の真ん中の辺りが 0。向きは画像と同じ）。左右は本人から見た左右。
/// <see cref="Handedness"/> は、追っている間ならした「本人の右手らしさ」（0〜1）。
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
/// 左右の見分けのならし方（設計判断記録 99 章）。モデルの左右の答えは、手を横から見たときなどに 1 枚だけ入れ替わることがあるので、
/// 追っている手は、答えをならし、はっきり反対になったときだけ左右を変える。2 つの手が同じ側になったら、答えのあいまいなほうを反対にする。
/// </summary>
public static class HandednessFilter
{
    /// <summary>ならす強さ（新しい答えを足す割合）。</summary>
    public const float Rate = 0.25f;

    /// <summary>右手とみなしている手を、左手に変える境（これより下）。左手を右手に変える境は 1 − これ。</summary>
    public const float SwitchBelow = 0.3f;

    /// <summary>
    /// 次の左右（<paramref name="previous"/> が null なら、新しく見つけた手で、答えのまま）。
    /// <paramref name="raw"/> は今の絵の「本人の右手らしさ」。
    /// </summary>
    public static (float Score, bool IsRight) Next((float Score, bool IsRight)? previous, float raw)
    {
        if (previous is not { } p)
        {
            return (raw, raw >= 0.5f);
        }
        float score = p.Score + (Rate * (raw - p.Score));
        bool right = p.IsRight ? score >= SwitchBelow : score > 1f - SwitchBelow;
        return (score, right);
    }

    /// <summary>2 つの手が同じ側なら、答えのあいまいな（0.5 に近い）ほうを反対の側にする（ならした答えも反対の側へ写す）。</summary>
    public static void Separate(IList<TrackedHand> hands)
    {
        ArgumentNullException.ThrowIfNull(hands);
        if (hands.Count != 2 || hands[0].IsRight != hands[1].IsRight)
        {
            return;
        }
        int weaker = MathF.Abs(hands[0].Handedness - 0.5f) < MathF.Abs(hands[1].Handedness - 0.5f) ? 0 : 1;
        hands[weaker] = hands[weaker] with { IsRight = !hands[weaker].IsRight, Handedness = 1f - hands[weaker].Handedness };
    }
}

/// <summary>
/// 手のトラッキング（MediaPipe の Hand Landmarker と同じ流れ。設計判断記録 99 章）。
/// 1. 掌を探すモデル（192 × 192）で掌と手首・指の付け根の位置を見つける
/// 2. 手の範囲（手首 → 中指の付け根を上に向けて 2.6 倍）を 224 × 224 に切り出し、手の 21 点（画素とメートル）・手らしさ・左右を求める
/// 3. 次の絵では、今の点から手の範囲を決める（手らしさが 0.5 を下回ったら、その手は探し直す）
/// 手は 2 つまで追う。軽くするため:
/// <list type="bullet">
/// <item>2 つの手の点は、別々のスレッドで同時に求める（モデルを 2 つ持つ）。掌を探すときも、追っている手と同時に探す。</item>
/// <item>1 つも追っていなければ <see cref="EmptySearchInterval"/> 枚ごと、1 つだけなら <see cref="SearchInterval"/> 枚ごとに探す（掌を探すモデルは、手の点のモデルより重い）。</item>
/// </list>
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

    /// <summary>手を 1 つも追っていないとき、探す間隔（枚）。手を見せてから付くまでが、最大でこの枚数だけ遅れる。</summary>
    public const int EmptySearchInterval = 2;

    private readonly InferenceSession _detector;
    private readonly InferenceSession[] _landmarkers = new InferenceSession[MaxHands];
    private readonly float[] _detectorInput = new float[HandDetectorMath.InputSize * HandDetectorMath.InputSize * 3];
    private readonly float[][] _landmarkInputs = [new float[LandmarkInputSize * LandmarkInputSize * 3], new float[LandmarkInputSize * LandmarkInputSize * 3]];
    private readonly RunOptions[] _runs = [new(), new(), new()]; // 手の点 2 つと、掌を探す分
    private readonly List<Track> _tracked = [];
    private readonly int _screenOutput;
    private readonly int _presenceOutput;
    private readonly int _handednessOutput;
    private readonly int _worldOutput;
    private int _sinceSearch = int.MaxValue / 2;

    // 追っている手: 次の絵の範囲と、ならした左右
    private readonly record struct Track(FaceRegion Region, float Score, bool IsRight);

    /// <param name="modelDirectory">モデルのフォルダー（hand_detector.onnx・hand_landmarks_detector.onnx）。</param>
    /// <param name="threads">1 つのモデルが使うスレッドの数（同時に動くのは、顔のほかに最大 3 つ）。</param>
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
        for (int i = 0; i < MaxHands; i++)
        {
            _landmarkers[i] = new InferenceSession(Path.Combine(directory, "hand_landmarks_detector.onnx"), options);
        }
        // 出力は名前で見分ける（画素の点・手らしさ・左右・メートルの点。変換したモデルの名前のまま）
        List<string> names = [.. _landmarkers[0].OutputNames];
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
        _sinceSearch = int.MaxValue / 2;
    }

    public HandTrackingResult Process(BgraImage image)
    {
        long start = Stopwatch.GetTimestamp();
        Track[] tracked = [.. _tracked];
        _sinceSearch++;
        bool search = tracked.Length < MaxHands && _sinceSearch >= (tracked.Length == 0 ? EmptySearchInterval : SearchInterval);

        // 追っている手の点と、掌を探すのを同時にする
        var followed = new TrackedHand?[tracked.Length];
        List<FaceDetection> palms = [];
        var work = new List<Action>(MaxHands + 1);
        for (int i = 0; i < tracked.Length; i++)
        {
            int slot = i;
            work.Add(() => followed[slot] = Landmarks(image, tracked[slot].Region, slot, (tracked[slot].Score, tracked[slot].IsRight)));
        }
        if (search)
        {
            _sinceSearch = 0;
            work.Add(() => palms = Detect(image));
        }
        RunAll(work);

        var hands = new List<TrackedHand>(MaxHands);
        var next = new List<FaceRegion>(MaxHands);
        void Keep(TrackedHand hand)
        {
            FaceRegion following = HandDetectorMath.RegionFromLandmarks(hand.Landmarks);
            if (hands.Count < MaxHands && !next.Any(r => HandDetectorMath.Overlap(r, following) > HandDetectorMath.SameHandOverlap))
            {
                hands.Add(hand);
                next.Add(following); // 2 つの範囲が同じ手に寄ってきたら、1 つにする
            }
        }
        foreach (TrackedHand? hand in followed)
        {
            if (hand is not null)
            {
                Keep(hand);
            }
        }

        // 新しく見つけた掌（もう追っている手は除く）の点を、同時に求める
        FaceRegion[] fresh = [.. palms.Select(HandDetectorMath.RegionFromPalm)
            .Where(r => !next.Any(n => HandDetectorMath.Overlap(n, r) > HandDetectorMath.SameHandOverlap))
            .Take(MaxHands - hands.Count)];
        var found = new TrackedHand?[fresh.Length];
        RunAll([.. Enumerable.Range(0, fresh.Length).Select(i => (Action)(() => found[i] = Landmarks(image, fresh[i], i, null)))]);
        foreach (TrackedHand? hand in found)
        {
            if (hand is not null)
            {
                Keep(hand);
            }
        }

        HandednessFilter.Separate(hands);
        _tracked.Clear();
        for (int i = 0; i < hands.Count; i++)
        {
            _tracked.Add(new Track(next[i], hands[i].Handedness, hands[i].IsRight));
        }
        if (_tracked.Count == 0 && tracked.Length > 0)
        {
            _sinceSearch = EmptySearchInterval; // 見失ったら、次の絵ですぐ探す
        }
        return new HandTrackingResult(hands, search, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>掌を探す（掌らしさの高い順。座標は画像の画素）。</summary>
    public List<FaceDetection> Detect(BgraImage image)
    {
        const int size = HandDetectorMath.InputSize;
        // 縦横比を保って 192 × 192 に収める（足りない所は 0）。色は 0〜1
        (float scale, Vector2 pad) = ImageSampler.SampleLetterbox(image, size, _detectorInput, 1f / 255f, 0f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(_detectorInput, [1, size, size, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _detector.Run(_runs[MaxHands], [_detector.InputNames[0]], [input], _detector.OutputNames);
        // 出力の並びはモデルの書き方しだいなので、大きさで見分ける（箱は 2016 × 18、掌らしさは 2016）
        bool swapped = outputs[0].GetTensorDataAsSpan<float>().Length < outputs[1].GetTensorDataAsSpan<float>().Length;
        ReadOnlySpan<float> regressors = outputs[swapped ? 1 : 0].GetTensorDataAsSpan<float>();
        ReadOnlySpan<float> scores = outputs[swapped ? 0 : 1].GetTensorDataAsSpan<float>();
        Vector2 ToImage(Vector2 p) => ((p * size) - pad) / scale;
        return [.. HandDetectorMath.Decode(regressors, scores)
            .Select(d => d with { Min = ToImage(d.Min), Max = ToImage(d.Max), Keypoints = [.. d.Keypoints.Select(ToImage)] })];
    }

    // 1 つなら、そのまま。2 つ以上なら、別々のスレッドで同時に（呼んだスレッドも 1 つ受け持つ）
    private static void RunAll(IReadOnlyList<Action> work)
    {
        if (work.Count == 1)
        {
            work[0]();
        }
        else if (work.Count > 1)
        {
            Parallel.Invoke([.. work]);
        }
    }

    // 範囲を切り出して手の点を求める（手らしさが足りなければ null）。slot は使うモデルと入れ物（同時に動かす手ごとに別）
    private TrackedHand? Landmarks(BgraImage image, FaceRegion region, int slot, (float Score, bool IsRight)? previous)
    {
        float[] buffer = _landmarkInputs[slot];
        InferenceSession landmarker = _landmarkers[slot];
        ImageSampler.Sample(image, LandmarkInputSize, region, buffer, 1f / 255f, 0f);
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(buffer, [1, LandmarkInputSize, LandmarkInputSize, 3]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = landmarker.Run(_runs[slot], [landmarker.InputNames[0]], [input], landmarker.OutputNames);
        float presence = outputs[_presenceOutput].GetTensorDataAsSpan<float>()[0];
        if (presence < PresenceThreshold)
        {
            return null;
        }
        // 左右のモデルは左右反転した映像（自撮り）を前提に「左手らしさ」を返す（MediaPipe の「Left」）。反転していないカメラの映像では、
        // それが本人の右手になる（点の 3D の形の向きと、顔から見た位置で確かめた。設計判断記録 99 章）
        float rightness = outputs[_handednessOutput].GetTensorDataAsSpan<float>()[0];
        (float score, bool isRight) = HandednessFilter.Next(previous, rightness);
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
        return new TrackedHand(landmarks, meters, isRight, score, presence, region);
    }

    public void Dispose()
    {
        _detector.Dispose();
        foreach (InferenceSession session in _landmarkers)
        {
            session.Dispose();
        }
        foreach (RunOptions run in _runs)
        {
            run.Dispose();
        }
    }
}
