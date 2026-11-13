using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Theater3D.Tracking.Tests;

/// <summary>
/// スマホの顔のアプリから受け取る: 頼み方・2 つの形式の読み方・向きの合わせ方・受け取り（この PC の中だけで送り合う）。
/// </summary>
[TestClass]
[DoNotParallelize] // ポートを使うので、1 つずつ確かめる
public sealed class PhoneTrackingTests
{
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [TestMethod]
    public void VTubeStudioRequestAsksForSecondsOfDataOnOurPorts()
    {
        using JsonDocument request = JsonDocument.Parse(PhoneMessages.VTubeStudioRequest([21413], 30));
        Assert.AreEqual("iOSTrackingDataRequest", request.RootElement.GetProperty("messageType").GetString());
        Assert.AreEqual(10.0, request.RootElement.GetProperty("time").GetDouble(), "10 秒まで");
        Assert.AreEqual("3D Theater", request.RootElement.GetProperty("sentBy").GetString());
        Assert.AreEqual(21413, request.RootElement.GetProperty("ports")[0].GetInt32());
    }

    [TestMethod]
    public void VTubeStudioJsonIsRead()
    {
        const string Json = """
            {"Timestamp":1700000000000,"Hotkey":-1,"FaceFound":true,
             "Rotation":{"x":-5.5,"y":12.0,"z":3.0},"Position":{"x":0.01,"y":-0.02,"z":-0.4},
             "EyeLeft":{"x":1,"y":2,"z":0},"EyeRight":{"x":1,"y":2,"z":0},
             "BlendShapes":[{"k":"EyeBlinkLeft","v":0.8},{"k":"jawOpen","v":0.5},{"k":"browInnerUpLeft","v":0.2},{"k":"browInnerUpRight","v":0.6},
                            {"k":"mouthSmileRight","v":1.7},{"k":"NewThing","v":0.1}],
             "SomethingNew":{"a":1}}
            """;
        PhoneFrame frame = PhoneMessages.ParseVTubeStudio(Json) ?? throw new AssertFailedException("読めません");
        Assert.AreEqual(PhoneProtocol.VTubeStudio, frame.Protocol);
        Assert.IsTrue(frame.FaceFound);
        Assert.AreEqual(new Vector3(-5.5f, 12f, 3f), frame.Rotation);
        Assert.AreEqual(new Vector3(0.01f, -0.02f, -0.4f), frame.Position);
        Assert.AreEqual(0.8f, frame.Blendshape("eyeBlinkLeft"), "大文字で始まる名前も ARKit の名前にそろえる");
        Assert.AreEqual(0.5f, frame.Blendshape("jawOpen"));
        Assert.AreEqual(0.6f, frame.Blendshape("browInnerUp"), "MeowFace の左右の眉は、大きいほうにまとめる");
        Assert.AreEqual(1f, frame.Blendshape("mouthSmileRight"), "0〜1 に収める");
        Assert.AreEqual(0.1f, frame.Blendshape("newThing"), "知らない名前もそのまま持つ（使わないだけ）");

        Assert.IsFalse(PhoneMessages.ParseVTubeStudio("""{"FaceFound":false,"BlendShapes":[]}""")!.FaceFound);
        Assert.IsNull(PhoneMessages.ParseVTubeStudio("{壊れた"));
        Assert.IsNull(PhoneMessages.ParseVTubeStudio("[1,2]"));
    }

    [TestMethod]
    public void IFacialMocapTextIsRead()
    {
        const string Text = "mouthSmile_R-35|eyeBlink_L-80|jawOpen&50|browInnerUp-12|cheekPuff-0|"
            + "=head#-21.5,-6.0,-6.6,-0.03,-0.10,-0.66|rightEye#6.0,2.4,0.3|leftEye#6.0,-1.7,-0.2|";
        PhoneFrame frame = PhoneMessages.ParseIFacialMocap(Text) ?? throw new AssertFailedException("読めません");
        Assert.AreEqual(PhoneProtocol.IFacialMocap, frame.Protocol);
        Assert.IsTrue(frame.FaceFound);
        Assert.AreEqual(0.35f, frame.Blendshape("mouthSmileRight"), 1e-5f, "_R は Right、値は 0〜100");
        Assert.AreEqual(0.8f, frame.Blendshape("eyeBlinkLeft"), 1e-5f);
        Assert.AreEqual(0.5f, frame.Blendshape("jawOpen"), 1e-5f, "新しい版の「&」も読む");
        Assert.AreEqual(new Vector3(-21.5f, -6.0f, -6.6f), frame.Rotation);
        Assert.AreEqual(new Vector3(-0.03f, -0.10f, -0.66f), frame.Position);
        Assert.IsFalse(frame.Blendshapes.ContainsKey("rightEye"), "目の向きは表情の値で使う");

        Assert.IsNull(PhoneMessages.ParseIFacialMocap("hello"), "読めるものが無い");
        Assert.AreEqual(PhoneProtocol.VTubeStudio, PhoneMessages.Parse(Encoding.UTF8.GetBytes(" {\"BlendShapes\":[]} "))!.Protocol, "JSON なら VTube Studio の形式");
        Assert.AreEqual(PhoneProtocol.IFacialMocap, PhoneMessages.Parse(Encoding.UTF8.GetBytes(Text))!.Protocol);
        Assert.IsNull(PhoneMessages.Parse([0xFF, 0xFE, 0x00]), "文字でないもの");
        Assert.AreEqual("eyeLookOutRight", PhoneMessages.NormalizeName("EyeLookOut_R"));
    }

    [TestMethod]
    public void VTubeStudioPosesFollowWhatTheUsersIPhoneSends()
    {
        // 利用者の iPhone（VTube Studio）: 自分の右を向くと x が正、上を向くと y が負、自分の右へ傾けると z が負
        var right = new PhoneFrame(PhoneProtocol.VTubeStudio, true, new Vector3(20, 0, 0), Vector3.Zero, new Dictionary<string, float>());
        Assert.AreEqual(-20f, PhonePoseMapping.ToCameraConvention(right, false, false, false).Yaw,
            "自分の右を向く → カメラの絵では左を向く（左右は負）");
        var up = right with { Rotation = new Vector3(0, -15, 0) };
        Assert.AreEqual(15f, PhonePoseMapping.ToCameraConvention(up, false, false, false).Pitch, "上を向く → 上下は正");
        var tilt = right with { Rotation = new Vector3(0, 0, -10) };
        Assert.AreEqual(-10f, PhonePoseMapping.ToCameraConvention(tilt, false, false, false).Roll,
            "自分の右へ傾ける → 絵の上では反時計回り（傾きは負）");
        Assert.AreEqual(20f, PhonePoseMapping.ToCameraConvention(right, true, false, false).Yaw, "パネルで逆にできる");

        // 位置はセンチメートルとみなす（顔の幅 15 cm）。奥行きは使わない
        var moved = right with { Rotation = Vector3.Zero, Position = new Vector3(3, 1.5f, 40) };
        PhonePose pose = PhonePoseMapping.ToCameraConvention(moved, false, false, false);
        Assert.AreEqual(new Vector2(-3, -1.5f), pose.Center, "自分の右へ動くと、絵の左へ");
        Assert.AreEqual(15f, pose.Size);
        Assert.AreEqual((Vector2.Zero, 1f), (PhonePoseMapping.ToCameraConvention(right, false, false, false).Center, PhonePoseMapping.ToCameraConvention(right, false, false, false).Size),
            "位置が無ければ体はずらさない");
    }

    [TestMethod]
    public void IFacialMocapPosesFollowTheARKitAxes()
    {
        var frame = new PhoneFrame(PhoneProtocol.IFacialMocap, true, new Vector3(10, 20, 5), new Vector3(0.03f, 0.015f, -0.5f), new Dictionary<string, float>());
        PhonePose pose = PhonePoseMapping.ToCameraConvention(frame, false, false, false);
        Assert.AreEqual(20f, pose.Yaw, "左右 = y");
        Assert.AreEqual(-10f, pose.Pitch, "上下 = −x（ARKit は x 軸まわりの正で下を向く）");
        Assert.AreEqual(-5f, pose.Roll, "傾き = −z");
        Assert.AreEqual(0.06f, pose.Center.X, 1e-5f, "顔が見ている人の右へ動くと、絵の右へ");
        Assert.AreEqual(-0.03f, pose.Center.Y, 1e-5f, "上へ動くと、絵の上（y は下が正）");
        Assert.AreEqual(0.3f, pose.Size, 1e-5f, "顔の幅 15 cm ÷ 距離");
        PhonePose centimeters = PhonePoseMapping.ToCameraConvention(frame with { Position = new Vector3(3, 1.5f, -50) }, false, false, false);
        Assert.AreEqual(0.3f, centimeters.Size, 1e-5f, "奥行きの大きさから、センチメートルと見分ける");
        Assert.AreEqual(0.06f, centimeters.Center.X, 1e-5f);

        PhonePose inverted = PhonePoseMapping.ToCameraConvention(frame, true, true, true);
        Assert.AreEqual((-20f, 10f, 5f), (inverted.Yaw, inverted.Pitch, inverted.Roll), "パネルで逆にできる");
        Assert.AreEqual(-0.06f, inverted.Center.X, 1e-5f, "左右を逆にすると、体のずれも逆");

        var noPosition = frame with { Position = Vector3.Zero };
        Assert.AreEqual((Vector2.Zero, 1f), (PhonePoseMapping.ToCameraConvention(noPosition, false, false, false).Center, PhonePoseMapping.ToCameraConvention(noPosition, false, false, false).Size),
            "位置が無ければ体はずらさない");
    }

    [TestMethod]
    public void ReceiverReadsPushedPackets()
    {
        int port = FreePort();
        using var receiver = new PhoneReceiver([port], IPAddress.Loopback);
        using var received = new ManualResetEventSlim();
        PhoneFrame? got = null;
        receiver.FrameReceived += (frame, _) =>
        {
            got = frame;
            received.Set();
        };
        receiver.Start();
        using var phone = new UdpClient(AddressFamily.InterNetwork);
        byte[] garbage = [0xFF, 0x00];
        phone.Send(garbage, garbage.Length, new IPEndPoint(IPAddress.Loopback, port));
        byte[] packet = Encoding.UTF8.GetBytes("jawOpen-40|=head#1,2,3,0,0,-0.5|");
        phone.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(3)), "届く");
        Assert.AreEqual(0.4f, got!.Blendshape("jawOpen"), 1e-5f);
        Assert.AreEqual(2L, receiver.Packets);
        Assert.AreEqual(1L, receiver.Frames, "読めないものは飛ばす");

        Assert.ThrowsExactly<SocketException>(() => new PhoneReceiver([port], IPAddress.Loopback).Start(), "同じポートは 2 つで待てない");
    }

    [TestMethod]
    public void ReceiverAsksTheVTubeStudioPhoneAndReadsTheReplies()
    {
        int listen = FreePort();
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var phoneEndPoint = (IPEndPoint)phone.Client.LocalEndPoint!;
        using var receiver = new PhoneReceiver([listen], IPAddress.Loopback, phoneEndPoint);
        using var received = new ManualResetEventSlim();
        receiver.FrameReceived += (_, _) => received.Set();
        receiver.Start();

        // スマホの代わり: 頼みを受けたら、頼みに書かれたポートへ 1 枚返す
        phone.Client.ReceiveTimeout = 3000;
        var from = new IPEndPoint(IPAddress.Any, 0);
        using JsonDocument request = JsonDocument.Parse(phone.Receive(ref from));
        Assert.AreEqual("iOSTrackingDataRequest", request.RootElement.GetProperty("messageType").GetString());
        int replyPort = request.RootElement.GetProperty("ports")[0].GetInt32();
        Assert.AreEqual(listen, replyPort, "頼みは、受け取るポートを書いて送る");
        Assert.AreEqual(listen, from.Port, "頼みは、受け取るポートから送る");
        byte[] reply = Encoding.UTF8.GetBytes("""{"FaceFound":true,"Rotation":{"x":0,"y":5,"z":0},"BlendShapes":[{"k":"EyeBlinkRight","v":1}]}""");
        phone.Send(reply, reply.Length, new IPEndPoint(IPAddress.Loopback, replyPort));
        Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(3)), "返事が届く");
    }
}
