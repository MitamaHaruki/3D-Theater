using System.Numerics;
using System.Text.Json;

namespace Theater3D.Tracking.Tests;

/// <summary>顔のトラッキングの決まり（MediaPipe の Face Landmarker と同じ流れ）と、モデルの読み込み。</summary>
[TestClass]
public sealed class FaceTrackerTests
{
    [TestMethod]
    public void AnchorsMatchTheShortRangeDetector()
    {
        IReadOnlyList<Vector2> anchors = FaceDetectorMath.Anchors;
        Assert.HasCount(896, anchors, "16 × 16 × 2 + 8 × 8 × 6");
        Assert.AreEqual(new Vector2(0.5f / 16), anchors[0]);
        Assert.AreEqual(anchors[0], anchors[1], "1 つの升に 2 つ");
        Assert.AreEqual(new Vector2(1.5f / 16, 0.5f / 16), anchors[2]);
        Assert.AreEqual(new Vector2(0.5f / 8), anchors[512], "8 × 8 の升の始まり");
        Assert.AreEqual(anchors[512], anchors[517], "8 × 8 の升は 6 つずつ");
        Assert.AreEqual(new Vector2(7.5f / 8), anchors[^1]);
    }

    [TestMethod]
    public void OverlappingCandidatesAreAveragedByScore()
    {
        var regressors = new float[896 * 16];
        var scores = Enumerable.Repeat(-20f, 896).ToArray();
        // 隣り合う 2 つの候補（同じ升）に、少しずらした同じくらいの箱を置く。離れた所にもう 1 つ
        void Put(int anchor, float dx, float logit)
        {
            Span<float> r = regressors.AsSpan(anchor * 16, 16);
            r[0] = dx * 128;
            r[2] = 0.2f * 128;
            r[3] = 0.2f * 128;
            for (int k = 0; k < 6; k++)
            {
                r[4 + (2 * k)] = dx * 128;
            }
            scores[anchor] = logit;
        }
        Put(200, 0, 3f);
        Put(201, 0.02f, 1f);
        Put(10, 0, 2f);
        List<FaceDetection> faces = FaceDetectorMath.Decode(regressors, scores);
        Assert.HasCount(2, faces, "重なった 2 つは 1 つにまとめる");
        FaceDetection merged = faces[0];
        Assert.AreEqual(FaceDetectorMath.Sigmoid(3f), merged.Score, 1e-6f, "顔らしさは高いほう");
        float w1 = FaceDetectorMath.Sigmoid(3f) / (FaceDetectorMath.Sigmoid(3f) + FaceDetectorMath.Sigmoid(1f));
        float expectedX = FaceDetectorMath.Anchors[200].X + ((1 - w1) * 0.02f);
        Assert.AreEqual(expectedX, (merged.Min.X + merged.Max.X) / 2, 1e-5f, "顔らしさで重みを付けた平均");
        Assert.AreEqual(0.2f, merged.Max.Y - merged.Min.Y, 1e-5f);
        Assert.AreEqual(0f, FaceDetectorMath.Overlap(faces[0], faces[1]), "離れた顔は残す");
        Assert.IsEmpty(FaceDetectorMath.Decode(new float[896 * 16], Enumerable.Repeat(-5f, 896).ToArray()), "顔らしさが低ければ何も無い");
    }

    [TestMethod]
    public void RegionsAreSquareScaledAndLevelTheEyes()
    {
        // 右目 (100,100) → 左目 (200,200)：画像の上で 45° 下がっている
        FaceRegion region = FaceRegion.From(new Vector2(80, 60), new Vector2(220, 260), new Vector2(100, 100), new Vector2(200, 200));
        Assert.AreEqual(new Vector2(150, 160), region.Center);
        Assert.AreEqual(300f, region.Size, 1e-4f, "長いほうの辺（200）の 1.5 倍の正方形");
        Assert.AreEqual(MathF.PI / 4, region.Rotation, 1e-5f, "両目の線が水平になるよう回す");
        Assert.AreEqual(region.Center, region.ToImage(0.5f, 0.5f));
        Vector2 right = region.ToImage(1, 0.5f) - region.Center;
        Assert.AreEqual(Vector2.Normalize(new Vector2(1, 1)).X, Vector2.Normalize(right).X, 1e-5f, "切り出しの右は、右目 → 左目の向き");
        Assert.AreEqual(150f, right.Length(), 1e-3f);
        Assert.AreEqual(-MathF.PI + 0.1f, FaceRegion.NormalizeAngle(MathF.PI + 0.1f), 1e-5f);
    }

    [TestMethod]
    public void BlendshapeConstantsMatchMediaPipe()
    {
        Assert.HasCount(52, FaceBlendshapes.Names);
        Assert.AreEqual(52, FaceBlendshapes.Names.Distinct().Count());
        Assert.AreEqual(9, FaceBlendshapes.IndexOf("eyeBlinkLeft"));
        Assert.AreEqual(25, FaceBlendshapes.IndexOf("jawOpen"));
        Assert.AreEqual(-1, FaceBlendshapes.IndexOf("tongueOut"));
        Assert.HasCount(146, FaceBlendshapes.LandmarkSubset);
        Assert.AreEqual(146, FaceBlendshapes.LandmarkSubset.Distinct().Count());
        Assert.AreEqual(477, FaceBlendshapes.LandmarkSubset.Max(), "瞳の点も使う");
    }

    [TestMethod]
    public void HeadPoseFollowsTheFaceAxes()
    {
        Vector3[] frontal = FaceAt(Matrix4x4.Identity);
        HeadPose straight = HeadPose.FromLandmarks(frontal);
        Assert.AreEqual(0f, straight.Yaw, 1e-3f);
        Assert.AreEqual(0f, straight.Pitch, 1e-3f);
        Assert.AreEqual(0f, straight.Roll, 1e-3f);

        // カメラの座標（y 上・z 手前）で 20° 右（画像の右）を向く・10° 上を向く・15° 傾ける
        HeadPose turned = HeadPose.FromLandmarks(FaceAt(Matrix4x4.CreateRotationY(20f * MathF.PI / 180)));
        Assert.AreEqual(20f, turned.Yaw, 0.01f, "画像の右を向くと正");
        HeadPose up = HeadPose.FromLandmarks(FaceAt(Matrix4x4.CreateRotationX(-10f * MathF.PI / 180)));
        Assert.AreEqual(10f, up.Pitch, 0.01f, "上を向くと正");
        HeadPose tilted = HeadPose.FromLandmarks(FaceAt(Matrix4x4.CreateRotationZ(-15f * MathF.PI / 180)));
        Assert.AreEqual(15f, tilted.Roll, 0.01f, "画像の上で時計回りに傾けると正");
    }

    // カメラの座標で顔の 4 点（右目尻・左目尻・額・あご）を回し、画素の座標（y 下・z 奥）にする
    private static Vector3[] FaceAt(Matrix4x4 rotation)
    {
        var points = new Vector3[FaceTracker.LandmarkCount];
        void Set(int index, Vector3 camera)
        {
            Vector3 p = Vector3.Transform(camera, rotation);
            points[index] = new Vector3(320 + p.X, 240 - p.Y, -p.Z);
        }
        Set(HeadPose.RightEyeOuter, new Vector3(-40, 20, 0));
        Set(HeadPose.LeftEyeOuter, new Vector3(40, 20, 0));
        Set(HeadPose.Forehead, new Vector3(0, 70, 5));
        Set(HeadPose.Chin, new Vector3(0, -80, 5));
        return points;
    }

    [TestMethod]
    public void ModelsLoadAndFindNoFaceInABlankImage()
    {
        using var tracker = new FaceTracker();
        var blank = new BgraImage(new byte[640 * 480 * 4], 640, 480);
        Assert.IsEmpty(tracker.Detect(blank));
        FaceTrackingResult result = tracker.Process(blank);
        Assert.IsFalse(result.Found);
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsGreaterThan(0, result.Milliseconds);
    }

    /// <summary>
    /// 公式の MediaPipe（Python）の答えと比べる。写真と答えはリポジトリに入れないので、THEATER_FACE_REFERENCE に
    /// portrait.bgra（BGRA の生のデータ）と portrait_reference.json（MediaPipe の答え）のあるフォルダーを入れたときだけ確かめる。
    /// </summary>
    [TestMethod]
    public void MatchesMediaPipeOnAReferencePhoto()
    {
        string? folder = Environment.GetEnvironmentVariable("THEATER_FACE_REFERENCE");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "portrait_reference.json")))
        {
            Assert.Inconclusive("比べる写真が無いため、確かめませんでした（THEATER_FACE_REFERENCE）");
            return;
        }
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "portrait_reference.json")));
        int width = json.RootElement.GetProperty("width").GetInt32();
        int height = json.RootElement.GetProperty("height").GetInt32();
        var image = new BgraImage(File.ReadAllBytes(Path.Combine(folder, "portrait.bgra")), width, height);
        using var tracker = new FaceTracker();
        FaceTrackingResult result = tracker.Process(image);
        Assert.IsTrue(result.Found);
        Assert.IsTrue(result.Detected, "初めは顔を探す");

        float[][] expected = [.. json.RootElement.GetProperty("landmarks").EnumerateArray().Select(p => p.EnumerateArray().Select(v => v.GetSingle()).ToArray())];
        double sum = 0;
        double worst = 0;
        for (int i = 0; i < FaceTracker.LandmarkCount; i++)
        {
            double d = Vector2.Distance(new Vector2(result.Landmarks[i].X, result.Landmarks[i].Y), new Vector2(expected[i][0] * width, expected[i][1] * height));
            sum += d;
            worst = Math.Max(worst, d);
        }
        double mean = sum / FaceTracker.LandmarkCount;
        Console.WriteLine($"点のずれ: 平均 {mean:F2} 画素・最大 {worst:F2} 画素（顔の大きさ {result.Region.Size:F0} 画素）");
        Assert.IsLessThan(result.Region.Size * 0.01, mean, "点のずれは、平均で顔の範囲の 1 % まで");

        JsonElement blend = json.RootElement.GetProperty("blendshapes");
        double maxDiff = 0;
        foreach (JsonProperty p in blend.EnumerateObject())
        {
            double diff = Math.Abs(result.Blendshape(p.Name) - p.Value.GetSingle());
            maxDiff = Math.Max(maxDiff, diff);
        }
        Console.WriteLine($"表情の値の差の最大: {maxDiff:F3}・笑顔 {result.Blendshape("mouthSmileLeft"):F3}（MediaPipe {blend.GetProperty("mouthSmileLeft").GetSingle():F3}）");
        Assert.IsLessThan(0.1, maxDiff, "表情の値の差は 0.1 まで");

        // 2 枚目は、1 枚目の点から範囲を決めて追う（探し直さない）
        FaceTrackingResult second = tracker.Process(image);
        Assert.IsTrue(second.Found);
        Assert.IsFalse(second.Detected);
        Console.WriteLine($"かかった時間: 1 枚目 {result.Milliseconds:F1} ms・2 枚目 {second.Milliseconds:F1} ms・頭の向き 左右 {result.Pose.Yaw:F1}° 上下 {result.Pose.Pitch:F1}° 傾き {result.Pose.Roll:F1}°");
    }
}
