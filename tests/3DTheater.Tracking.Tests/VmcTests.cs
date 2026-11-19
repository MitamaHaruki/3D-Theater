using System.Net;
using System.Numerics;
using Theater3D.Tracking.Vmc;

namespace Theater3D.Tracking.Tests;

/// <summary>VMC プロトコル（OSC）の読み書きと、3D Theater VCamera の送る形（設計判断記録 98 章）。</summary>
[TestClass]
public sealed class VmcTests
{
    private const float Tolerance = 1e-3f;

    [TestMethod]
    public void OscMessagesAreWrittenInTheStandardLayout()
    {
        // 宛名・型の並び・値（大きい桁から）。どれも 4 バイトの区切りまで 0 で埋める
        CollectionAssert.AreEqual(
            new byte[] { (byte)'/', (byte)'a', 0, 0, (byte)',', (byte)'i', 0, 0, 0, 0, 0, 1 },
            Osc.Write(new OscMessage("/a", 1)));
        byte[] text = Osc.Write(new OscMessage("/abc", "xyz"));
        Assert.HasCount(16, text, "「/abc」は 0 を足して 8、「,s」は 4、「xyz」は 4");

        // 書いて読むと同じ（バンドルの中の知らせも）
        var messages = new[]
        {
            new OscMessage(VmcProtocol.BlendVal, "EyeBlinkLeft", 0.25f),
            new OscMessage(VmcProtocol.Frame, 42, 7.5f),
            new OscMessage(VmcProtocol.BlendApply),
            new OscMessage("/日本語", "あいう"),
        };
        IReadOnlyList<OscMessage> read = Osc.Read(Osc.WriteBundle(messages));
        Assert.HasCount(4, read);
        Assert.AreEqual("EyeBlinkLeft", read[0].String(0));
        Assert.AreEqual(0.25f, read[0].Float(1));
        Assert.AreEqual(42, read[1].Int(0));
        Assert.AreEqual(7.5f, read[1].Float(1));
        Assert.AreEqual(VmcProtocol.BlendApply, read[2].Address);
        Assert.IsEmpty(read[2].Arguments);
        Assert.AreEqual("あいう", read[3].String(0), "文字は UTF-8");

        // 壊れたもの・途中で切れたものは、読めた所まで
        Assert.IsEmpty(Osc.Read([1, 2, 3, 4, 5]));
        byte[] bundle = Osc.WriteBundle(messages);
        Assert.IsLessThan(4, Osc.Read(bundle.AsSpan(0, bundle.Length - 6)).Count);
    }

    [TestMethod]
    public void HeadRotationFollowsThePersonWithoutMirroring()
    {
        // 本人が自分の右を向く（カメラの決まりでは左右が負）→ キャラクターも自分の右（Unity の +x）を向く
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, VmcProtocol.HeadRotation(-30f, 0f, 0f));
        Assert.IsGreaterThan(0.4f, forward.X);
        Assert.AreEqual(30f, VmcProtocol.ToUnityEuler(VmcProtocol.HeadRotation(-30f, 0f, 0f)).Y, Tolerance);

        // 上を向く → 前が上へ。本人の左へ傾ける（カメラの決まりでは傾きが正）→ 頭の上がキャラクターの左（-x）へ
        Assert.IsGreaterThan(0.3f, Vector3.Transform(Vector3.UnitZ, VmcProtocol.HeadRotation(0f, 20f, 0f)).Y);
        Assert.IsLessThan(-0.2f, Vector3.Transform(Vector3.UnitY, VmcProtocol.HeadRotation(0f, 0f, 15f)).X);

        // 回転からカメラの決まりへ戻せる（向きを組み合わせても）
        foreach (var (yaw, pitch, roll) in new[] { (25f, -10f, 5f), (-40f, 15f, -12f), (0f, 0f, 0f), (10f, 30f, 20f) })
        {
            var (y, p, r) = VmcProtocol.CameraAngles(VmcProtocol.HeadRotation(yaw, pitch, roll));
            Assert.AreEqual(yaw, y, Tolerance, "左右");
            Assert.AreEqual(pitch, p, Tolerance, "上下");
            Assert.AreEqual(roll, r, Tolerance, "傾き");
        }
        Assert.AreEqual("EyeBlinkLeft", VmcProtocol.PerfectSyncName("eyeBlinkLeft"));
        Assert.AreEqual("JawOpen", VmcProtocol.PerfectSyncName("JawOpen"));
    }

    [TestMethod]
    public void FaceFrameCarriesBonesBlendshapesAndTheTheaterValues()
    {
        var blend = new Dictionary<string, float> { ["_neutral"] = 0.9f, ["eyeBlinkLeft"] = 0.8f, ["jawOpen"] = 0.5f, ["mouthPucker"] = 2f };
        Quaternion head = VmcProtocol.HeadRotation(10f, 5f, 0f);
        var frame = new VmcFaceFrame(7, true, 12f, -3f, 4f, new Vector2(640, 360), 300f, blend, head, 9.5f, 1.25f);
        IReadOnlyList<OscMessage> messages = VmcProtocol.FaceMessages(frame);

        OscMessage bone = messages.Single(m => m.Address == VmcProtocol.BonePos);
        Assert.AreEqual("Head", bone.String(0));
        Assert.AreEqual(head.W, bone.Float(7)!.Value, Tolerance);
        string[] names = [.. messages.Where(m => m.Address == VmcProtocol.BlendVal).Select(m => m.String(0)!)];
        CollectionAssert.Contains(names, "EyeBlinkLeft", "パーフェクトシンクの名前");
        CollectionAssert.DoesNotContain(names, "_neutral", "MediaPipe の「_neutral」は送らない");
        CollectionAssert.Contains(names, "Blink_L", "VRM の基本の表情も");
        Assert.AreEqual(1f, messages.Single(m => m.String(0) == "MouthPucker").Float(1), "0〜1 に収める");
        int apply = messages.ToList().FindIndex(m => m.Address == VmcProtocol.BlendApply);
        Assert.IsGreaterThan(messages.ToList().FindLastIndex(m => m.Address == VmcProtocol.BlendVal), apply, "表情を全部送ってから当てる");
        Assert.AreEqual(VmcProtocol.Frame, messages[^1].Address);
        Assert.AreEqual(7, messages[^1].Int(0));

        // 顔が無い枚は、ボーンと表情を送らない（受け手は前の形のまま）。顔なしの知らせと番号は送る
        IReadOnlyList<OscMessage> lost = VmcProtocol.FaceMessages(frame with { Found = false });
        Assert.IsFalse(lost.Any(m => m.Address is VmcProtocol.BonePos or VmcProtocol.BlendVal));
        Assert.AreEqual(0, lost.Single(m => m.Address == VmcProtocol.Face).Int(0));
    }

    [TestMethod]
    public void StateRebuildsTheFaceFromTheaterValuesOrFromOtherApps()
    {
        // 3D Theater VCamera から: 顔の知らせの値（カメラの決まり・合わせる前）をそのまま使う
        var state = new VmcState();
        var blend = new Dictionary<string, float> { ["eyeBlinkLeft"] = 0.8f, ["jawOpen"] = 0.5f };
        var frame = new VmcFaceFrame(1, true, 12f, -3f, 4f, new Vector2(640, 360), 300f, blend, Quaternion.Identity, 5f, 0f);
        foreach (OscMessage m in Osc.Read(Osc.WriteBundle(VmcProtocol.FaceMessages(frame))))
        {
            state.Apply(m);
        }
        PhoneFrame face = state.ToFrame()!;
        Assert.AreEqual(PhoneProtocol.Vmc, face.Protocol);
        Assert.IsTrue(face.FromFaceTracker);
        Assert.AreEqual(new Vector3(12f, -3f, 4f), face.Rotation);
        Assert.AreEqual(new Vector3(640, 360, 300), face.Position);
        Assert.AreEqual(0.8f, face.Blendshape("eyeBlinkLeft"), "名前は ARKit に戻す");
        Assert.AreEqual(1, state.LastSequence);
        PhonePose pose = PhonePoseMapping.ToCameraConvention(face, false, false, false);
        Assert.AreEqual(12f, pose.Yaw);
        Assert.AreEqual(300f, pose.Size);

        // ほかのアプリから: 首と頭のボーンの回転を掛けて向きを求める。表情は Apply が届いてから当てる。VRM の基本の表情も使う
        var other = new VmcState();
        Quaternion neck = VmcProtocol.HeadRotation(-10f, 0f, 0f);
        Quaternion head = VmcProtocol.HeadRotation(-15f, 0f, 0f);
        other.Apply(new OscMessage(VmcProtocol.BonePos, "Neck", 0f, 0.1f, 0f, neck.X, neck.Y, neck.Z, neck.W));
        other.Apply(new OscMessage(VmcProtocol.BonePos, "Head", 0f, 0.1f, 0f, head.X, head.Y, head.Z, head.W));
        other.Apply(new OscMessage(VmcProtocol.BlendVal, "Blink_L", 1f));
        other.Apply(new OscMessage(VmcProtocol.BlendVal, "A", 0.6f));
        Assert.AreEqual(0f, other.ToFrame()!.Blendshape("eyeBlinkLeft"), "Apply の前は当てない");
        other.Apply(new OscMessage(VmcProtocol.BlendApply));
        PhoneFrame fromOther = other.ToFrame()!;
        Assert.IsFalse(fromOther.FromFaceTracker);
        Assert.AreEqual(-25f, fromOther.Rotation.X, 0.01f, "首と頭を合わせた向き（自分の右へ 25°）");
        Assert.AreEqual(1f, fromOther.Blendshape("eyeBlinkLeft"), "Blink_L → eyeBlinkLeft");
        Assert.AreEqual(0.6f, fromOther.Blendshape("jawOpen"), "A → jawOpen");
        Assert.AreEqual(Vector3.Zero, fromOther.Position, "位置は無い（体はずらさない）");

        // ARKit の値があれば、VRM の基本の表情より優先する
        Dictionary<string, float> both = VmcState.Normalize(new Dictionary<string, float> { ["JawOpen"] = 0.2f, ["A"] = 0.9f });
        Assert.AreEqual(0.2f, both["jawOpen"]);
        Assert.IsNull(new VmcState().ToFrame(), "何も届いていなければ形は無い");
    }

    [TestMethod]
    public void EncoderSendsRawValuesAndCalibratedHeadBone()
    {
        var encoder = new VmcFaceEncoder();
        VmcFaceFrame first = encoder.Encode(Face(20f, 0f, 0f), 6f, 0f);
        Assert.IsTrue(first.Found);
        Assert.AreEqual(20f, first.Yaw, "本アプリ向けは合わせる前のまま");
        Assert.AreEqual(Quaternion.Identity, first.Head, "正面を覚えるまでは、まっすぐ前");
        for (int i = 1; i < VmcFaceEncoder.CalibrationFrames; i++)
        {
            encoder.Encode(Face(20f, 0f, 0f), 6f, i);
        }
        Assert.IsTrue(encoder.IsCalibrated);
        VmcFaceFrame turned = encoder.Encode(Face(-10f, 0f, 0f), 6f, 1f);
        Assert.AreEqual(-30f, VmcProtocol.CameraAngles(turned.Head).Yaw, 0.01f, "覚えた正面（20°）から、自分の右へ 30°");
        Assert.AreEqual(VmcFaceEncoder.CalibrationFrames, turned.Sequence, "番号は 0 から 1 枚ごとに");
        Assert.AreEqual(300f, turned.Size, 0.01f, "顔の大きさは目尻の間の 3 倍");

        VmcFaceFrame lost = encoder.Encode(FaceTrackingResult.NotFound(0.1f, 3), 3f, 2f);
        Assert.IsFalse(lost.Found);
        encoder.Recalibrate();
        Assert.IsFalse(encoder.IsCalibrated);
    }

    [TestMethod]
    public void SenderAndReceiverMeasureTheRoundTripOverUdp()
    {
        int port = 40000 + Random.Shared.Next(2000);
        using var receiver = new VmcReceiver(port, IPAddress.Loopback);
        using var got = new ManualResetEventSlim(false);
        PhoneFrame? received = null;
        receiver.FrameReceived += (frame, _) =>
        {
            received = frame;
            got.Set();
        };
        receiver.Start();
        using var sender = new VmcSender(new IPEndPoint(IPAddress.Loopback, port));
        var encoder = new VmcFaceEncoder();
        for (int i = 0; i < 5; i++)
        {
            Assert.IsTrue(sender.Send(encoder.Encode(Face(5f, 2f, 1f), 6f, i / 30f)));
            Thread.Sleep(20);
        }
        Assert.IsTrue(got.Wait(TimeSpan.FromSeconds(3)), "受け手に届く");
        Assert.AreEqual(5f, received!.Rotation.X, Tolerance);
        SpinWait.SpinUntil(() => sender.IsAnswered, TimeSpan.FromSeconds(3));
        Thread.Sleep(100);
        RoundTripSummary summary = sender.TakeSummary();
        Assert.AreEqual(5, summary.Sent);
        Assert.IsGreaterThan(0, summary.Replied, "受け手が番号を返す");
        Assert.IsLessThan(100.0, summary.Max, "同じ PC の中なら、とても短い");
        Assert.AreEqual(0, sender.TakeSummary().Sent, "読むと数え直す");
    }

    // 顔の結果（向きと、鼻先・目尻の点だけ）
    private static FaceTrackingResult Face(float yaw, float pitch, float roll)
    {
        var landmarks = new Vector3[FaceTracker.LandmarkCount];
        landmarks[HeadPose.NoseTip] = new Vector3(640, 360, 0);
        landmarks[HeadPose.RightEyeOuter] = new Vector3(590, 330, 0);
        landmarks[HeadPose.LeftEyeOuter] = new Vector3(690, 330, 0);
        var blend = new float[FaceBlendshapes.Count];
        blend[FaceBlendshapes.IndexOf("eyeBlinkLeft")] = 0.3f;
        return new FaceTrackingResult(true, landmarks, blend, new HeadPose(yaw, pitch, roll, Matrix4x4.Identity), default, 0.99f, false, 6);
    }
}
