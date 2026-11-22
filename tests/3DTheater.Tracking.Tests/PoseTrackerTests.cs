using System.Numerics;
using Theater3D.Tracking.Vmc;

namespace Theater3D.Tracking.Tests;

/// <summary>体のトラッキング（設計判断記録 100 章）。</summary>
[TestClass]
public sealed class PoseTrackerTests
{
    [TestMethod]
    public void AnchorsMatchThePoseDetector()
    {
        IReadOnlyList<Vector2> anchors = PoseDetectorMath.Anchors;
        Assert.HasCount(PoseDetectorMath.AnchorCount, anchors);
        // 28 × 28 の升に 2 つずつ・14 × 14 の升に 2 つずつ・7 × 7 の升に 6 つずつ
        Assert.AreEqual(new Vector2(0.5f / 28, 0.5f / 28), anchors[0]);
        Assert.AreEqual(anchors[0], anchors[1]);
        Assert.AreEqual(new Vector2(0.5f / 14, 0.5f / 14), anchors[28 * 28 * 2]);
        Assert.AreEqual(new Vector2(0.5f / 7, 0.5f / 7), anchors[(28 * 28 * 2) + (14 * 14 * 2)]);
        Assert.AreEqual(new Vector2(6.5f / 7, 6.5f / 7), anchors[^1]);
    }

    [TestMethod]
    public void BodyIsDecodedAndTheRegionCoversTheCircle()
    {
        var regressors = new float[PoseDetectorMath.AnchorCount * 12];
        var scores = Enumerable.Repeat(-10f, PoseDetectorMath.AnchorCount).ToArray();
        int i = 100;
        scores[i] = 5f;
        Vector2 a = PoseDetectorMath.Anchors[i];
        float[] r = [0, 0, 40, 80, 0, 20, 0, -40, 0, -10, 10, -30]; // 箱と 4 つの点（腰の真ん中・円の上・肩の真ん中・上半身の円の上）
        r.CopyTo(regressors, i * 12);
        List<FaceDetection> bodies = PoseDetectorMath.Decode(regressors, scores);
        Assert.HasCount(1, bodies);
        Assert.HasCount(4, bodies[0].Keypoints);
        Assert.AreEqual(a.X, bodies[0].Keypoints[0].X, 1e-5f);
        Assert.AreEqual(a.Y + (20f / 224f), bodies[0].Keypoints[0].Y, 1e-5f);

        // 腰の真ん中（画素）から、真上 100 画素の円の上の点: 一辺は直径の 1.25 倍、回さない
        FaceRegion region = PoseDetectorMath.RegionFromPoints(new Vector2(300, 500), new Vector2(300, 400));
        Assert.AreEqual(new Vector2(300, 500), region.Center);
        Assert.AreEqual(250f, region.Size, 1e-3f);
        Assert.AreEqual(0f, region.Rotation, 1e-5f);
        // 体が右へ倒れている（円の上の点が右）: 時計回りに 90°
        Assert.AreEqual(MathF.PI / 2, PoseDetectorMath.RegionFromPoints(new Vector2(300, 500), new Vector2(400, 500)).Rotation, 1e-5f);
    }

    [TestMethod]
    public void SamplerCanFillOutsideWithTheEdgeColor()
    {
        // 4 × 4 の絵。画素 (x, y) の赤 = x × 10 + 10
        var pixels = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                pixels[(((y * 4) + x) * 4) + 2] = (byte)((x * 10) + 10);
            }
        }
        var image = new BgraImage(pixels, 4, 4);
        var rgb = new float[4 * 4 * 3];
        // 右へ 4 画素ずらした範囲（全部、絵の外）
        var region = new FaceRegion(new Vector2(6, 2), 4, 0);
        ImageSampler.Sample(image, 4, region, rgb, 1, 0);
        Assert.AreEqual(0f, rgb[0], "外は 0 の色");
        ImageSampler.Sample(image, 4, region, rgb, 1, 0, clampEdges: true);
        Assert.AreEqual(40f, rgb[0], 1e-3f, "外は、いちばん近い端（右の端）の色");
        Assert.AreEqual(40f, rgb[((3 * 4) + 3) * 3], 1e-3f);
    }

    [TestMethod]
    public void ModelsLoadAndFindNoBodyInABlankImage()
    {
        using var tracker = new PoseTracker();
        var blank = new BgraImage(new byte[640 * 480 * 4], 640, 480);
        Assert.IsEmpty(tracker.Detect(blank));
        PoseTrackingResult result = tracker.Process(blank);
        Assert.IsFalse(result.Found);
        Assert.IsNull(result.Body);
        Assert.IsTrue(result.Detected, "初めの絵では探す");
        Assert.IsFalse(tracker.Process(blank).Detected, "体が無い間は、1 枚おきに探す（体を探すモデルは重い）");
        Assert.IsTrue(tracker.Process(blank).Detected);
        tracker.Reset();
        Assert.IsTrue(tracker.Process(blank).Detected, "探し直すときは、すぐ探す");
        Assert.IsFalse(tracker.IsTracking);
    }

    [TestMethod]
    public void BodyTravelsInTheFrameAndLostBodyIsCleared()
    {
        BodyPose body = BodyPose.Empty();
        for (int k = BodyPose.SentFirst; k <= BodyPose.SentLast; k++)
        {
            body.Points[k] = new Vector3(k * 0.01f, -k * 0.02f, k * 0.003f);
            body.Visibility[k] = k / 30f;
        }
        body.Points[0] = new Vector3(9, 9, 9); // 送らない点（鼻）
        var frame = new VmcFaceFrame(5, true, 0, 0, 0, Vector2.Zero, 0, new Dictionary<string, float>(), Quaternion.Identity, 1f, 0f)
        {
            BodyTracked = true,
            Body = body,
        };
        IReadOnlyList<OscMessage> messages = VmcProtocol.FrameMessages(frame);
        OscMessage sent = messages.Single(m => m.Address == VmcProtocol.Body);
        Assert.AreEqual(1, sent.Int(0), "体あり");
        Assert.AreEqual(VmcProtocol.Frame, messages[^1].Address, "番号は最後");

        var state = new VmcState();
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(messages)))
        {
            state.Apply(m);
        }
        BodyPose received = state.ToFrame()!.Body!;
        for (int k = BodyPose.SentFirst; k <= BodyPose.SentLast; k++)
        {
            Assert.AreEqual(body.Points[k], received.Points[k], $"{k} 番目の点");
            Assert.AreEqual(body.Visibility[k], received.Visibility[k], 1e-6f);
        }
        Assert.AreEqual(Vector3.Zero, received.Points[0], "送らない点は 0");
        Assert.IsTrue(received.IsVisible(BodyPose.RightHip));
        Assert.IsFalse(received.IsVisible(BodyPose.LeftShoulder));

        // 体を見失った（体なしを送る）
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(VmcProtocol.FrameMessages(frame with { Body = null }))))
        {
            state.Apply(m);
        }
        Assert.IsNull(state.ToFrame()!.Body);

        // 体を読んでいない送り手（体の知らせなし）
        Assert.IsFalse(VmcProtocol.FrameMessages(frame with { BodyTracked = false }).Any(m => m.Address == VmcProtocol.Body));
    }

    [TestMethod]
    public void HandDirectionTravelsWithTheHandAndOldSendersHaveNone()
    {
        float[] angles = new float[HandPose.ValueCount];
        var hand = new HandPose(true, angles) { Forward = new Vector3(0, -1, 0), Normal = new Vector3(0, 0, -1) };
        var frame = new VmcFaceFrame(1, true, 0, 0, 0, Vector2.Zero, 0, new Dictionary<string, float>(), Quaternion.Identity, 1f, 0f)
        {
            HandsTracked = true,
            Hands = [hand],
        };
        var state = new VmcState();
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(VmcProtocol.FrameMessages(frame))))
        {
            state.Apply(m);
        }
        HandPose received = state.ToFrame()!.Hands.Single();
        Assert.AreEqual(hand.Forward, received.Forward);
        Assert.AreEqual(hand.Normal, received.Normal);

        // 確かさだけを送る送り手（99 章の版）: 向きは分からない（0）
        object[] withQuality = [1, 1, .. angles.Cast<object>(), 0.7f];
        state.Apply(new OscMessage(VmcProtocol.Hand, withQuality));
        received = state.ToFrame()!.Hands.Single();
        Assert.AreEqual(0.7f, received.Quality, 1e-6f);
        Assert.AreEqual(Vector3.Zero, received.Forward);
        Assert.AreEqual(Vector3.Zero, received.Normal);
    }

    [TestMethod]
    public void HandPoseKnowsWhereTheHandPoints()
    {
        // 指を上へ伸ばし、掌をカメラへ向けた右手（HandTrackerTests と同じ形。カメラの決まりは x 右・y 下・z 奥）
        var p = new Vector3[21];
        p[0] = Vector3.Zero;
        for (int k = 1; k <= 4; k++)
        {
            p[k] = new Vector3(0.1f * k, -0.08f * k, 0);
        }
        float[] xs = [0.02f, 0f, -0.015f, -0.03f];
        for (int f = 0; f < 4; f++)
        {
            for (int k = 0; k < 4; k++)
            {
                p[5 + (f * 4) + k] = new Vector3(xs[f], -0.09f - (0.025f * k), 0);
            }
        }
        HandPose pose = HandPose.FromWorld(p, isRight: true);
        Assert.IsLessThan(0.01f, Vector3.Distance(new Vector3(0, -1, 0), pose.Forward), $"手首 → 中指の付け根は上（{pose.Forward}）");
        Assert.IsLessThan(0.01f, Vector3.Distance(new Vector3(0, 0, -1), pose.Normal), $"掌はカメラの方（{pose.Normal}）");
    }
}
