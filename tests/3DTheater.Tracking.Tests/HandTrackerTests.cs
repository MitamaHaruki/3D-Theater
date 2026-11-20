using System.Numerics;
using Theater3D.Tracking.Vmc;

namespace Theater3D.Tracking.Tests;

/// <summary>手のトラッキングの決まり（MediaPipe の Hand Landmarker と同じ流れ）・指の角度・送る知らせ（設計判断記録 99 章）。</summary>
[TestClass]
public sealed class HandTrackerTests
{
    private const float Tolerance = 1e-3f;

    [TestMethod]
    public void AnchorsMatchThePalmDetector()
    {
        IReadOnlyList<Vector2> anchors = HandDetectorMath.Anchors;
        Assert.HasCount(2016, anchors, "24 × 24 × 2 + 12 × 12 × 6");
        Assert.AreEqual(new Vector2(0.5f / 24), anchors[0]);
        Assert.AreEqual(anchors[0], anchors[1], "1 つの升に 2 つ");
        Assert.AreEqual(new Vector2(0.5f / 12), anchors[1152], "12 × 12 の升の始まり");
        Assert.AreEqual(anchors[1152], anchors[1157], "12 × 12 の升は 6 つずつ");
        Assert.AreEqual(new Vector2(11.5f / 12), anchors[^1]);
    }

    [TestMethod]
    public void PalmsAreDecodedAndBoxesWithoutSizeAreDropped()
    {
        var regressors = new float[2016 * 18];
        var scores = Enumerable.Repeat(-20f, 2016).ToArray();
        void Put(int anchor, float size, float logit)
        {
            Span<float> r = regressors.AsSpan(anchor * 18, 18);
            r[2] = size * 192;
            r[3] = size * 192;
            for (int k = 0; k < 7; k++)
            {
                r[5 + (2 * k)] = -0.01f * k * 192; // 点は上へ並べる
            }
            scores[anchor] = logit;
        }
        Put(300, 0.2f, 3f);
        Put(900, 0f, 5f); // 大きさの無い箱（捨てる）
        Put(1500, -0.1f, 5f); // 大きさが負の箱（捨てる）
        List<FaceDetection> palms = HandDetectorMath.Decode(regressors, scores);
        Assert.HasCount(1, palms);
        Assert.HasCount(7, palms[0].Keypoints, "掌の点は 7 つ");
        Assert.AreEqual(HandDetectorMath.Anchors[300].X, (palms[0].Min.X + palms[0].Max.X) / 2, 1e-5f);
    }

    [TestMethod]
    public void SuppressionEndsEvenWithBoxesThatDoNotOverlapThemselves()
    {
        // 大きさが 0 の箱は自分とも重ならない。それでも 1 つずつ取り除いて終わる
        var zero = new FaceDetection(new Vector2(0.5f), new Vector2(0.5f), [Vector2.Zero], 0.9f);
        var box = new FaceDetection(new Vector2(0.1f), new Vector2(0.3f), [Vector2.Zero], 0.8f);
        List<FaceDetection> result = FaceDetectorMath.WeightedSuppression([zero, zero with { Score = 0.7f }, box], 0.3f);
        Assert.HasCount(3, result);
    }

    [TestMethod]
    public void PalmRegionPointsTheFingersUpAndShiftsTowardThem()
    {
        // 手首（下）→ 中指の付け根（上）のまっすぐな掌: 回さず、指の側（上）へ箱の高さの半分ずらして 2.6 倍
        var palm = new FaceDetection(new Vector2(100, 200), new Vector2(160, 260), [new(130, 250), new(120, 215), new(130, 210), new(140, 212), new(150, 215), new(105, 240), new(110, 230)], 0.9f);
        FaceRegion region = HandDetectorMath.RegionFromPalm(palm);
        Assert.AreEqual(0f, region.Rotation, Tolerance);
        Assert.AreEqual(130f, region.Center.X, Tolerance);
        Assert.AreEqual(230f - 30f, region.Center.Y, Tolerance);
        Assert.AreEqual(60f * 2.6f, region.Size, Tolerance);

        // 指が右を向く掌: 切り出した絵の上が、画像の右になるように回す（時計回りに 90°）
        var right = palm with { Keypoints = [new(110, 230), new(140, 220), new(150, 230), new(140, 240), new(135, 245), new(115, 215), new(120, 210)] };
        FaceRegion turned = HandDetectorMath.RegionFromPalm(right);
        Assert.AreEqual(MathF.PI / 2, turned.Rotation, Tolerance);
        Vector2 top = turned.ToImage(0.5f, 0f);
        Assert.IsGreaterThan(turned.Center.X + (turned.Size * 0.4f), top.X, "切り出した絵の上 = 画像の右");
        Assert.IsGreaterThan(130f, turned.Center.X, "指の側（右）へずらす");
    }

    [TestMethod]
    public void LandmarkRegionCoversTheHandForTheNextFrame()
    {
        Vector3[] hand = UprightRightHand(new Vector2(300, 400), 100f);
        FaceRegion region = HandDetectorMath.RegionFromLandmarks(hand);
        Assert.AreEqual(0f, region.Rotation, 0.05f, "指が上を向いた手は回さない");
        foreach (Vector3 p in hand)
        {
            Assert.IsLessThan(region.Size * 0.5f, MathF.Abs(p.X - region.Center.X), "点はすべて範囲の中");
            Assert.IsLessThan(region.Size * 0.5f, MathF.Abs(p.Y - region.Center.Y));
        }
        Assert.AreEqual(1f, HandDetectorMath.Overlap(region, region), Tolerance);
        Assert.AreEqual(0f, HandDetectorMath.Overlap(region, region with { Center = region.Center + new Vector2(region.Size * 2, 0) }), Tolerance);
    }

    [TestMethod]
    public void StraightHandHasNoBendAndCurledFingersBendTowardThePalm()
    {
        // 右手の掌をカメラへ向け、指を上へ伸ばした手（メートル。カメラから見た x 右・y 下・z 奥）
        Vector3[] world = UprightRightHand(Vector2.Zero, 0.1f);
        HandPose open = HandPose.FromWorld(world, isRight: true);
        for (int f = 1; f < 5; f++)
        {
            for (int j = 0; j < 3; j++)
            {
                Assert.AreEqual(0f, open.Bend(f, j), 0.5f, $"{HandPose.FingerNames[f]} の {j} 番目の関節は伸びている");
            }
        }
        Assert.AreEqual(0f, open.Spread(2), 0.5f, "中指は開いていない");
        Assert.IsGreaterThan(5f, open.Spread(1), "人差し指は親指の側へ少し開いている");
        Assert.IsLessThan(-5f, open.Spread(4), "小指は反対の側へ開いている");

        // 人差し指の中の関節から先を、掌の側（カメラの方 = -z）へ 90° 曲げる
        Vector3[] bent = [.. world];
        Vector3 pip = bent[6];
        float segment = Vector3.Distance(world[6], world[7]);
        bent[7] = pip + new Vector3(0, 0, -segment);
        bent[8] = pip + new Vector3(0, 0, -2 * segment);
        HandPose curled = HandPose.FromWorld(bent, isRight: true);
        Assert.AreEqual(90f, curled.Bend(1, 1), 1f, "中の関節が掌の側へ 90°");
        Assert.AreEqual(0f, curled.Bend(1, 2), 1f);

        // 左手（左右を映した形）でも同じ角度になる
        Vector3[] left = [.. bent.Select(p => p with { X = -p.X })];
        HandPose leftPose = HandPose.FromWorld(left, isRight: false);
        for (int i = 0; i < HandPose.ValueCount; i++)
        {
            Assert.AreEqual(curled.Angles[i], leftPose.Angles[i], 0.01f, $"{i} 番目");
        }
    }

    [TestMethod]
    public void HandsTravelInTheFrameAndLostHandsAreCleared()
    {
        float[] angles = [.. Enumerable.Range(0, HandPose.ValueCount).Select(i => i * 1.5f)];
        var hand = new HandPose(true, angles);
        var frame = new VmcFaceFrame(3, true, 0, 0, 0, Vector2.Zero, 0, new Dictionary<string, float>(), Quaternion.Identity, 1f, 0f)
        {
            HandsTracked = true,
            Hands = [hand],
        };
        IReadOnlyList<OscMessage> messages = VmcProtocol.FrameMessages(frame);
        OscMessage[] hands = [.. messages.Where(m => m.Address == VmcProtocol.Hand)];
        Assert.HasCount(2, hands, "見つからない手も「手なし」として送る");
        Assert.AreEqual(0, hands[0].Int(0), "本人の左手");
        Assert.AreEqual(0, hands[0].Int(1), "左手なし");
        Assert.AreEqual(1, hands[1].Int(1), "右手あり");
        Assert.AreEqual(VmcProtocol.Frame, messages[^1].Address, "番号は最後");

        var state = new VmcState();
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(messages)))
        {
            state.Apply(m);
        }
        PhoneFrame received = state.ToFrame()!;
        Assert.HasCount(1, received.Hands);
        Assert.IsTrue(received.Hands[0].IsRight);
        CollectionAssert.AreEqual(angles, received.Hands[0].Angles);
        Assert.AreEqual(1f, received.Hands[0].Quality, "確かさも届く");

        // 確かさの低い手。古い送り手（確かさを送らない）は 1 とみなす
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(VmcProtocol.FrameMessages(frame with { Hands = [hand with { Quality = 0.4f }] }))))
        {
            state.Apply(m);
        }
        Assert.AreEqual(0.4f, state.ToFrame()!.Hands[0].Quality, 1e-6f);
        object[] old = [1, 1, .. angles.Cast<object>()];
        state.Apply(new OscMessage(VmcProtocol.Hand, old));
        Assert.AreEqual(1f, state.ToFrame()!.Hands[0].Quality);

        // 右手を見失った
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(VmcProtocol.FrameMessages(frame with { Hands = [] }))))
        {
            state.Apply(m);
        }
        Assert.IsEmpty(state.ToFrame()!.Hands);

        // 手を読んでいない送り手（手の知らせなし）
        Assert.IsFalse(VmcProtocol.FrameMessages(frame with { HandsTracked = false }).Any(m => m.Address == VmcProtocol.Hand));
    }

    [TestMethod]
    public void ModelsLoadAndFindNoHandInABlankImage()
    {
        using var tracker = new HandTracker();
        var blank = new BgraImage(new byte[640 * 480 * 4], 640, 480);
        Assert.IsEmpty(tracker.Detect(blank));
        HandTrackingResult result = tracker.Process(blank);
        Assert.IsEmpty(result.Hands);
        Assert.IsTrue(result.Detected, "初めの絵では探す");
        Assert.AreEqual(0, tracker.TrackingCount);
        Assert.IsFalse(tracker.Process(blank).Detected, "手が無い間は、1 枚おきに探す（掌を探すモデルは重い）");
        Assert.IsTrue(tracker.Process(blank).Detected);
        tracker.Reset();
        Assert.IsTrue(tracker.Process(blank).Detected, "探し直すときは、すぐ探す");
    }

    [TestMethod]
    public void HandednessStaysUntilTheModelClearlyDisagrees()
    {
        var state = HandednessFilter.Next(null, 0.8f);
        Assert.IsTrue(state.IsRight, "新しく見つけた手は、答えのまま");
        state = HandednessFilter.Next(state, 0.1f);
        Assert.IsTrue(state.IsRight, "1 枚だけ反対の答えが出ても、変えない");
        for (int i = 0; i < 3; i++)
        {
            state = HandednessFilter.Next(state, 0.05f);
        }
        Assert.IsFalse(state.IsRight, "続けて反対なら変える");

        // 2 つの手が同じ側: あいまいなほうを反対にする
        Vector3[] none = new Vector3[21];
        List<TrackedHand> hands =
        [
            new(none, none, true, 0.9f, 1f, default),
            new(none, none, true, 0.6f, 1f, default),
        ];
        HandednessFilter.Separate(hands);
        Assert.IsTrue(hands[0].IsRight);
        Assert.IsFalse(hands[1].IsRight);
        Assert.AreEqual(0.4f, hands[1].Handedness, 1e-6f, "ならした答えも反対の側へ写す");
    }

    [TestMethod]
    public void DepthReadBackwardIsMirroredBack()
    {
        // 4 本の指を掌の側（カメラの方 = -z）へ曲げた右手。奥行きを裏返して読むと、指は手の甲の側へ曲がって見える
        Vector3[] curled = UprightRightHand(Vector2.Zero, 0.1f);
        for (int b = 5; b <= 17; b += 4)
        {
            float segment = Vector3.Distance(curled[b], curled[b + 1]);
            curled[b + 2] = curled[b + 1] + new Vector3(0, 0, -segment);
            curled[b + 3] = curled[b + 1] + new Vector3(0, 0, -2 * segment);
        }
        HandPose straight = HandPose.FromWorld(curled, isRight: true);
        Assert.IsFalse(straight.DepthMirrored);
        Assert.AreEqual(90f, straight.Bend(2, 1), 1f);

        Vector3[] backward = [.. curled.Select(p => p with { Z = -p.Z })];
        Assert.IsLessThan(HandPose.MirroredBelow, HandPose.CurlToPalm(backward, isRight: true), "指が手の甲の側へ曲がっている");
        HandPose fixedPose = HandPose.FromWorld(backward, isRight: true);
        Assert.IsTrue(fixedPose.DepthMirrored);
        for (int i = 0; i < HandPose.ValueCount; i++)
        {
            Assert.AreEqual(straight.Angles[i], fixedPose.Angles[i], 0.01f, $"{i} 番目");
        }
    }

    [TestMethod]
    public void QualityIsHighWhenThePalmFacesTheCamera()
    {
        Vector3[] facing = UprightRightHand(Vector2.Zero, 0.1f);
        Assert.AreEqual(1f, HandPose.FromWorld(facing, true).Quality, 1e-4f, "掌がカメラを向いている");
        // 縦の軸で 90° 回す（掌が横を向く）
        Vector3[] sideways = [.. facing.Select(p => new Vector3(p.Z, p.Y, -p.X))];
        Assert.AreEqual(0f, HandPose.FromWorld(sideways, true).Quality, 1e-4f, "掌が横を向いている");
        // 横の軸で 70° 倒す（指がカメラの方を向く）
        float c = MathF.Cos(70 * MathF.PI / 180), s = MathF.Sin(70 * MathF.PI / 180);
        Vector3[] pointing = [.. facing.Select(p => new Vector3(p.X, (p.Y * c) - (p.Z * s), (p.Y * s) + (p.Z * c)))];
        Assert.IsLessThan(0.3f, HandPose.FromWorld(pointing, true).Quality, "指がカメラの方を向いている");
    }

    [TestMethod]
    public void SamplerCopiesRotatesAndLetterboxes()
    {
        // 8 × 8 の絵。画素 (x, y) の赤 = x × 10、緑 = y × 10
        var pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int i = ((y * 8) + x) * 4;
                pixels[i + 2] = (byte)(x * 10);
                pixels[i + 1] = (byte)(y * 10);
                pixels[i + 3] = 255;
            }
        }
        var image = new BgraImage(pixels, 8, 8);
        var rgb = new float[8 * 8 * 3];
        float R(int u, int v) => rgb[((v * 8) + u) * 3];
        float G(int u, int v) => rgb[(((v * 8) + u) * 3) + 1];

        ImageSampler.Sample(image, 8, new FaceRegion(new Vector2(4, 4), 8, 0), rgb, 1, 0);
        Assert.AreEqual(30f, R(3, 5), 1e-3f, "そのまま写す");
        Assert.AreEqual(50f, G(3, 5), 1e-3f);

        // 時計回りに 90° 回した範囲: 出力の (u, v) は、元の (7 − v, u)
        ImageSampler.Sample(image, 8, new FaceRegion(new Vector2(4, 4), 8, MathF.PI / 2), rgb, 1, 0);
        Assert.AreEqual((7 - 5) * 10f, R(3, 5), 1e-3f);
        Assert.AreEqual(3 * 10f, G(3, 5), 1e-3f);

        // 横長の 8 × 4 を 8 × 8 に収める: 上下 2 画素ずつ余白（0 の色）
        var wide = new BgraImage(pixels[..(8 * 4 * 4)], 8, 4);
        (float scale, Vector2 pad) = ImageSampler.SampleLetterbox(wide, 8, rgb, 1, 0);
        Assert.AreEqual(1f, scale);
        Assert.AreEqual(new Vector2(0, 2), pad);
        Assert.AreEqual(0f, G(4, 0));
        Assert.AreEqual(0f, R(4, 7));
        Assert.AreEqual(10f, G(4, 3), 1e-3f, "元の 1 行目");
        Assert.AreEqual(40f, R(4, 3), 1e-3f);
    }

    // 指を上へ伸ばした右手（掌はカメラの方。親指は画像の右）。手首が origin、手の長さが scale ほど
    private static Vector3[] UprightRightHand(Vector2 origin, float scale)
    {
        var p = new Vector3[21];
        Vector3 O(float x, float y) => new(origin.X + (x * scale), origin.Y + (y * scale), 0);
        p[0] = O(0, 0);
        // 親指: 付け根から右上へ
        p[1] = O(0.15f, -0.1f);
        p[2] = O(0.3f, -0.2f);
        p[3] = O(0.4f, -0.3f);
        p[4] = O(0.5f, -0.4f);
        // 人差し指〜小指: 付け根を右から左へ並べ、人差し指は少し右へ、小指は左へ開く
        (float X, float Lean)[] fingers = [(0.2f, 0.15f), (0f, 0f), (-0.15f, -0.05f), (-0.3f, -0.2f)];
        for (int f = 0; f < 4; f++)
        {
            int b = 5 + (f * 4);
            var basePoint = new Vector2(fingers[f].X, -0.9f);
            var direction = Vector2.Normalize(new Vector2(fingers[f].Lean, -1f));
            for (int k = 0; k < 4; k++)
            {
                Vector2 q = basePoint + (direction * (0.25f * k));
                p[b + k] = O(q.X, q.Y);
            }
        }
        return p;
    }
}
