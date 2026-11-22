using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Editor.Tests;

/// <summary>体のトラッキングを、キャラクターの上半身と腕に当てはめる計算（設計判断記録 100 章）。</summary>
[TestClass]
public sealed class BodyRetargetTests
{
    private const float ToRadians = MathF.PI / 180f;

    // 本人の体（カメラの決まり: x 右・y 下・z 奥。本人はカメラの方を向く）。腕は、肩からひじ・ひじから手首の向き（同じ決まり。長さ 1 でよい）。
    // yaw は、体を縦の軸で回した角度（度）
    private static BodySignal Body(Vector3? leftUpper = null, Vector3? leftFore = null, Vector3? rightUpper = null, Vector3? rightFore = null,
        float yaw = 0, float leftWrist = 1, float rightWrist = 1)
    {
        var points = new Vector3[33];
        var seen = new float[33];
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * ToRadians);
        Vector3 T(Vector3 v) => Vector3.Transform(v, turn);
        var left = new Vector3(0.17f, -0.4f, 0);
        var right = new Vector3(-0.17f, -0.4f, 0);
        Vector3 down = new(0, 1, 0);
        points[11] = T(left);
        points[12] = T(right);
        points[13] = T(left + ((leftUpper ?? down) * 0.28f));
        points[14] = T(right + ((rightUpper ?? down) * 0.28f));
        points[15] = T(left + ((leftUpper ?? down) * 0.28f) + ((leftFore ?? down) * 0.25f));
        points[16] = T(right + ((rightUpper ?? down) * 0.28f) + ((rightFore ?? down) * 0.25f));
        seen[11] = seen[12] = seen[13] = seen[14] = 1f;
        seen[15] = leftWrist;
        seen[16] = rightWrist;
        return new BodySignal(points, seen);
    }

    private static BodyOutput Run(BodyRetarget retarget, Func<int, BodySignal?> body, int frames, ref double time, bool mirror = false,
        Func<int, IReadOnlyList<HandSignal>?>? hands = null)
    {
        BodyOutput output = BodyOutput.None;
        for (int i = 0; i < frames; i++, time += 1 / 30.0)
        {
            output = retarget.Compute(body(i), hands?.Invoke(i), time, mirror, 0.5f);
        }
        return output;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance, string message) =>
        Assert.IsLessThan(tolerance, Vector3.Distance(expected, actual), $"{message}（{actual}）");

    private static float Degrees(Quaternion q) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) / ToRadians;

    [TestMethod]
    public void LevelShouldersAreTheModelAxes()
    {
        Assert.AreEqual(1f, MathF.Abs(BodyRetarget.Frame(Vector3.UnitX)!.Value.W), 1e-5f);
        // 左肩が奥へ（左右の線が +X から +Z の側へ 30°）: X はその線へ、Y は上のまま
        Vector3 line = new(MathF.Cos(30 * ToRadians), 0, MathF.Sin(30 * ToRadians));
        Quaternion frame = BodyRetarget.Frame(line)!.Value;
        AssertClose(line, Vector3.Transform(Vector3.UnitX, frame), 1e-4f, "横の向き");
        AssertClose(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, frame), 1e-4f, "上の向き");
        Assert.IsNull(BodyRetarget.Frame(Vector3.UnitY), "肩の線が縦なら決まらない");
    }

    [TestMethod]
    public void TorsoTurnsFromTheCalibratedFrontAndArmsStayOnTheTorso()
    {
        var retarget = new BodyRetarget();
        double time = 0;
        Run(retarget, _ => Body(), 30, ref time);
        Assert.IsTrue(retarget.IsCalibrated);
        // 体を 20° 回し、右腕を体の前へ（Body は腕ごと回すので、腕は回す前の前 = -z で渡す）
        var front = new Vector3(0, 0, -1);
        BodyOutput o = Run(retarget, _ => Body(rightUpper: front, rightFore: front, yaw: 20), 90, ref time);
        Assert.AreEqual(1f, o.TorsoWeight, 1e-4f);
        Assert.AreEqual(20f, Degrees(o.Torso), 0.5f, "上半身の回り");
        AssertClose(new Vector3(0, 0, -1), o.Right.Upper, 0.01f, "腕は、上半身から見て前");
        AssertClose(new Vector3(0, -1, 0), o.Left.Upper, 0.01f, "下ろした腕は、上半身から見て下");
        Assert.AreEqual(1f, o.Right.Weight, 1e-4f);
    }

    [TestMethod]
    public void ArmsMoveTheSameSideAndMirrorSwapsThem()
    {
        // 本人の左腕を横へ上げる（画像の右 = カメラの +x）。右腕は手首が見えない
        BodySignal raised = Body(leftUpper: Vector3.UnitX, leftFore: Vector3.UnitX, rightWrist: 0f);
        var retarget = new BodyRetarget();
        double time = 0;
        BodyOutput o = Run(retarget, _ => raised, 30, ref time);
        Assert.AreEqual(1f, o.Left.Weight, 1e-4f, "本人の左腕 → キャラクターの左腕");
        Assert.AreEqual(0f, o.Right.Weight, "手首の見えない腕は動かさない");
        AssertClose(Vector3.UnitX, o.Left.Upper, 0.01f, "キャラクターの左（+X）へ");

        var mirrored = new BodyRetarget();
        time = 0;
        o = Run(mirrored, _ => raised, 30, ref time, mirror: true);
        Assert.AreEqual(1f, o.Right.Weight, 1e-4f, "鏡のようにすると、反対の腕");
        Assert.AreEqual(0f, o.Left.Weight);
        AssertClose(-Vector3.UnitX, o.Right.Upper, 0.01f, "キャラクターの右（-X）へ（画面では同じ側）");
    }

    [TestMethod]
    public void SwitchingTheMirrorRelearnsTheFront()
    {
        var retarget = new BodyRetarget();
        double time = 0;
        // 少し回って座っている人（正面は 15° 回った向き）
        BodyOutput o = Run(retarget, _ => Body(yaw: 15), 60, ref time);
        Assert.AreEqual(0f, Degrees(o.Torso), 0.5f, "正面合わせの向きのまま");
        o = Run(retarget, _ => Body(yaw: 15), 60, ref time, mirror: true);
        Assert.IsTrue(retarget.IsCalibrated);
        Assert.AreEqual(0f, Degrees(o.Torso), 0.5f, "鏡にしても、覚え直すので回らない");
    }

    [TestMethod]
    public void ArmsEaseBackWhenTheWristLeavesAndHandsKeepThemSeen()
    {
        var retarget = new BodyRetarget();
        double time = 0;
        Run(retarget, _ => Body(leftWrist: 0f, rightWrist: 0f), 30, ref time);
        // 手首がぎりぎり（見えている度合い 0.45）でも、見え始めの境に届かなければ動かさない
        BodyOutput o = Run(retarget, _ => Body(leftWrist: 0.45f, rightWrist: 0.45f), 10, ref time);
        Assert.AreEqual(0f, o.Left.Weight, "見え始めの境に届かない");
        // 手のトラッキングの手があっても、それだけでは見えているとみなさない（手の左右の取り違えで、見えていない反対の腕を動かさない）
        IReadOnlyList<HandSignal> leftHand = [new HandSignal(false, new float[20])];
        o = Run(retarget, _ => Body(leftWrist: 0.45f, rightWrist: 0.45f), 10, ref time, hands: _ => leftHand);
        Assert.AreEqual(0f, o.Left.Weight, "手だけでは動かさない");
        // 3D Theater VCamera が、手と結び付いた手首を見えているとして送る（0.9）
        o = Run(retarget, _ => Body(leftWrist: 0.9f, rightWrist: 0.45f), 30, ref time, hands: _ => leftHand);
        Assert.AreEqual(1f, o.Left.Weight, 1e-4f, "見えている手首の腕は動かす");
        Assert.AreEqual(0f, o.Right.Weight);

        // 手首が見えなくなった: 少し待ってから、ゆっくり戻す
        o = Run(retarget, _ => Body(leftWrist: 0f), 3, ref time);
        Assert.AreEqual(1f, o.Left.Weight, 1e-4f, "すぐには戻さない");
        o = Run(retarget, _ => Body(leftWrist: 0f), 15, ref time);
        Assert.IsGreaterThan(0.05f, o.Left.Weight);
        Assert.IsLessThan(0.95f, o.Left.Weight, "少しずつ戻す");
        o = Run(retarget, _ => null, 30, ref time);
        Assert.AreEqual(0f, o.Left.Weight, "見失って少し経てば、場面の姿勢");
        Assert.AreEqual(0f, o.TorsoWeight);
    }

    [TestMethod]
    public void HiddenElbowsAreSmoothedMore()
    {
        // ひじが 1 枚ごとに ±4 cm 揺れる腕。ひじが絵の外（見えている度合い 0.1）だと、見えているとき（1）より、腕の向きの揺れが小さい
        float Shake(float elbowSeen)
        {
            var retarget = new BodyRetarget();
            double time = 0;
            var angles = new List<float>();
            Vector3 previous = Vector3.Zero;
            for (int i = 0; i < 120; i++, time += 1 / 30.0)
            {
                BodySignal body = Body(leftUpper: Vector3.UnitX, leftFore: Vector3.UnitX);
                body.Points[13] += new Vector3(0, i % 2 == 0 ? 0.04f : -0.04f, 0);
                body.Visibility[13] = elbowSeen;
                BodyOutput o = retarget.Compute(body, null, time, false, 0.5f);
                if (i >= 60)
                {
                    angles.Add(MathF.Acos(Math.Clamp(Vector3.Dot(o.Left.Upper, previous), -1f, 1f)) / ToRadians);
                }
                previous = o.Left.Upper;
            }
            return angles.Average();
        }
        float shown = Shake(1f);
        float hidden = Shake(0.1f);
        Assert.IsGreaterThan(0f, shown);
        Assert.IsLessThan(shown * 0.5f, hidden, $"見えている: {shown:F2}°・見えていない: {hidden:F2}°");
    }

    [TestMethod]
    public void SingleFlippedHandIsIgnoredAndLastingTurnsAreFollowedSlowly()
    {
        var retarget = new BodyRetarget();
        double time = 0;
        // 右手: 指は上（カメラの -y）、掌はカメラの方（-z）
        HandSignal Hand(Vector3 normal) => new(true, new float[20]) { Forward = new Vector3(0, -1, 0), Normal = normal };
        BodySignal body = Body(rightUpper: Vector3.UnitY, rightFore: -Vector3.UnitY);
        BodyOutput o = Run(retarget, _ => body, 30, ref time, hands: _ => [Hand(new Vector3(0, 0, -1))]);
        AssertClose(new Vector3(0, 0, -1), o.Right.HandNormal, 0.02f, "掌は前（モデルの -Z）");
        AssertClose(new Vector3(0, 1, 0), o.Right.HandForward, 0.02f, "指は上");

        // 1 枚だけ裏返った掌は使わない
        o = Run(retarget, _ => body, 1, ref time, hands: _ => [Hand(new Vector3(0, 0, 1))]);
        AssertClose(new Vector3(0, 0, -1), o.Right.HandNormal, 0.02f, "1 枚だけの跳ね");
        o = Run(retarget, _ => body, 1, ref time, hands: _ => [Hand(new Vector3(0, 0, -1))]);
        AssertClose(new Vector3(0, 0, -1), o.Right.HandNormal, 0.02f, "元の向きのまま");

        // 本当に回した（続いた）: 少しずつ回して、追いつく
        o = Run(retarget, _ => body, 4, ref time, hands: _ => [Hand(new Vector3(1, 0, 0))]);
        float partial = MathF.Acos(Math.Clamp(Vector3.Dot(o.Right.HandNormal, new Vector3(0, 0, -1)), -1f, 1f)) / ToRadians;
        Assert.IsGreaterThan(5f, partial, "回し始める");
        Assert.IsLessThan(80f, partial, "一気には回さない");
        o = Run(retarget, _ => body, 30, ref time, hands: _ => [Hand(new Vector3(1, 0, 0))]);
        AssertClose(new Vector3(1, 0, 0), o.Right.HandNormal, 0.05f, "追いつく");
    }

    [TestMethod]
    public void ArmBonesFollowTheArmAndTheElbowBendsForward()
    {
        PmxDocument model = ArmModel();
        var rig = ArmRig.Create(model);
        Assert.AreEqual(2 * 5, rig.BoneCount, "左右の 腕・腕捩・ひじ・手捩・手首");
        Vector3 P(string name) => model.Bones[Index(model, name)].Position;
        Vector3 upperRest = Vector3.Normalize(P("左ひじ") - P("左腕"));
        Vector3 foreRest = Vector3.Normalize(P("左手首") - P("左ひじ"));

        // 腕を下ろし、前腕を前へ（ひじを 90° 曲げる）
        var arm = new ArmOutput(1f, new Vector3(0, -1, 0), new Vector3(0, 0, -1), 0f, Vector3.Zero, Vector3.Zero);
        var aims = new List<(int Bone, int Reference, Quaternion Rotation, float Weight)>();
        rig.Build(new BodyOutput(Quaternion.Identity, 0f, arm, ArmOutput.None with { Weight = 0f }), aims);
        Assert.HasCount(3, aims, "腕・腕捩・ひじ（手の向きが無ければ手首は動かさない）");
        int reference = Index(model, "上半身2");
        var upper = aims.Single(a => a.Bone == Index(model, "左腕"));
        var elbow = aims.Single(a => a.Bone == Index(model, "左ひじ"));
        Assert.AreEqual(reference, upper.Reference, "上半身から見た向き");
        AssertClose(new Vector3(0, -1, 0), Vector3.Transform(upperRest, upper.Rotation), 1e-3f, "腕は下");
        AssertClose(new Vector3(0, 0, -1), Vector3.Transform(foreRest, elbow.Rotation), 1e-3f, "前腕は前");
        var twist = aims.Single(a => a.Bone == Index(model, "左腕捩"));
        Assert.AreEqual(-1, twist.Reference);
        Assert.AreEqual(1f, MathF.Abs(twist.Rotation.W), 1e-5f, "腕捩は捩らない");
        // ひじだけで前へ曲げている（ひじの回転は、腕を向けた後の、横の軸まわりの 90° だけ）
        Quaternion bend = Quaternion.Normalize(Quaternion.Inverse(upper.Rotation) * elbow.Rotation);
        if (bend.W < 0)
        {
            bend = -bend;
        }
        Assert.AreEqual(90f, Degrees(bend), 0.5f);
        AssertClose(Vector3.Normalize(Vector3.Cross(upperRest, new Vector3(0, 0, -1) - (upperRest * Vector3.Dot(upperRest, new Vector3(0, 0, -1))))),
            Vector3.Normalize(new Vector3(bend.X, bend.Y, bend.Z)), 0.02f, "曲げる軸は、もとの腕と前の向きに直角");
    }

    [TestMethod]
    public void SmoothedBendDecidesTheUpperArmTwist()
    {
        PmxDocument model = ArmModel();
        var rig = ArmRig.Create(model);
        Vector3 P(string name) => model.Bones[Index(model, name)].Position;
        Vector3 upperRest = Vector3.Normalize(P("左ひじ") - P("左腕"));
        Vector3 bendRest = Vector3.Normalize(new Vector3(0, 0, -1) - (upperRest * Vector3.Dot(upperRest, new Vector3(0, 0, -1))));
        Quaternion Upper(ArmOutput arm)
        {
            var aims = new List<(int Bone, int Reference, Quaternion Rotation, float Weight)>();
            rig.Build(new BodyOutput(Quaternion.Identity, 0f, arm, ArmOutput.None), aims);
            return aims.Single(a => a.Bone == Index(model, "左腕")).Rotation;
        }
        // 腕を横（+X）へ。ならした曲がる向きは上（+Y）
        var arm = new ArmOutput(1f, Vector3.UnitX, Vector3.UnitX, 0f, Vector3.Zero, Vector3.Zero) { Bend = Vector3.UnitY, BendWeight = 1f };
        AssertClose(Vector3.UnitY, Vector3.Transform(bendRest, Upper(arm)), 1e-3f, "曲がる向きを使う割合 1: ひじは上へ曲がる向き");
        // 使う割合 0: いちばん小さく回した向き（もとの形の前のまま）
        AssertClose(new Vector3(0, 0, -1), Vector3.Transform(bendRest, Upper(arm with { BendWeight = 0f })), 1e-3f, "使う割合 0: 前のまま");
    }

    [TestMethod]
    public void WristTakesTheHandDirectionWithinRange()
    {
        PmxDocument model = ArmModel();
        var rig = ArmRig.Create(model);
        Vector3 P(string name) => model.Bones[Index(model, name)].Position;
        Vector3 handRest = Vector3.Normalize(P("左中指１") - P("左手首"));
        Vector3 palmRest = Vector3.Normalize(Vector3.Cross(P("左人指１") - P("左手首"), P("左小指１") - P("左手首")));
        palmRest = Vector3.Normalize(palmRest - (handRest * Vector3.Dot(palmRest, handRest)));

        // 前腕を上へ、手も上へ・掌を前へ（手を上げて、掌をカメラに見せる）
        var arm = new ArmOutput(1f, new Vector3(1, 0, 0), new Vector3(0, 1, 0), 1f, new Vector3(0, 1, 0), new Vector3(0, 0, -1));
        var aims = new List<(int Bone, int Reference, Quaternion Rotation, float Weight)>();
        rig.Build(new BodyOutput(Quaternion.Identity, 0f, arm, ArmOutput.None), aims);
        var wrist = aims.Single(a => a.Bone == Index(model, "左手首"));
        AssertClose(new Vector3(0, 1, 0), Vector3.Transform(handRest, wrist.Rotation), 0.02f, "手は上");
        AssertClose(new Vector3(0, 0, -1), Vector3.Transform(palmRest, wrist.Rotation), 0.02f, "掌は前");
        var twist = aims.Single(a => a.Bone == Index(model, "左手捩"));
        Assert.AreEqual(-1, twist.Reference, "手捩は捩りだけ（親から見た回転）");

        // 手首を前腕から 150° も曲げた向き: 範囲に収める
        var bent = arm with { HandForward = new Vector3(0, -1, 0.05f) };
        aims.Clear();
        rig.Build(new BodyOutput(Quaternion.Identity, 0f, bent, ArmOutput.None), aims);
        var limited = aims.Single(a => a.Bone == Index(model, "左手首"));
        var elbow = aims.Single(a => a.Bone == Index(model, "左ひじ"));
        Quaternion relative = Quaternion.Normalize(Quaternion.Inverse(elbow.Rotation) * limited.Rotation);
        float swing = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Transform(handRest, relative), handRest), -1f, 1f)) / ToRadians;
        Assert.IsLessThan(ArmRig.MaxWristDegrees + 15f, swing, $"手首の曲がり {swing:F0}°");
    }

    [TestMethod]
    public void JitterMeterCountsOnlyTheBackAndForth()
    {
        var meter = new JitterMeter();
        // ゆっくり回し続ける（1 枚に 2°）: 揺れは 0
        for (int i = 0; i < 30; i++)
        {
            meter.Add(0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * 2 * ToRadians), true);
        }
        // ±3° を行ったり来たり: 揺れは 6° ほど（真ん中からのずれ）
        for (int i = 0; i < 30; i++)
        {
            meter.Add(1, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (i % 2 == 0 ? 3 : -3) * ToRadians), true);
        }
        // 動かしていない枚は数えない
        meter.Add(2, Quaternion.Identity, false);
        IReadOnlyDictionary<int, (double Rms, double Max, double ActiveShare)> r = meter.Take();
        Assert.AreEqual(0, r[0].Rms, 0.05);
        Assert.AreEqual(6, r[1].Rms, 0.1);
        Assert.AreEqual(6, r[1].Max, 0.1);
        Assert.AreEqual(1, r[1].ActiveShare, 1e-9);
        Assert.AreEqual(0, r[2].ActiveShare, 1e-9);
        Assert.AreEqual(0, meter.Take()[1].Rms, "読むと数え直す");
    }

    [TestMethod]
    public void TorsoReplacesTheShareOfTheHeadAndTheHeadKeepsItsDirection()
    {
        var settings = new FaceRetargetSettings { BodySway = false };
        var plain = new FaceRetarget(settings);
        var withTorso = new FaceRetarget(settings);
        var neutral = new FaceSignal(0, 0, 0, new Vector2(360, 500), 300, new Dictionary<string, float>());
        plain.Calibrate(neutral);
        withTorso.Calibrate(neutral);
        FaceSignal turned = neutral with { Yaw = 30, Pitch = 10 };
        Quaternion torso = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 12 * ToRadians);
        FaceOutput a = FaceOutput.Neutral;
        FaceOutput b = FaceOutput.Neutral;
        for (int i = 0; i <= 90; i++)
        {
            a = plain.Compute(turned, i / 30.0);
            b = withTorso.Compute(turned, i / 30.0, torso: (torso, 1f));
        }
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(torso, b.UpperBody)), 1e-4f, "上半身は肩の線の回り");
        Quaternion totalA = a.UpperBody * a.Neck * a.Head;
        Quaternion totalB = b.UpperBody * b.Neck * b.Head;
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(totalA, totalB)), 1e-4f, "頭の向きは同じ（首と頭は残りを回す）");
    }

    private static int Index(PmxDocument model, string name) => Array.FindIndex(model.Bones, b => b.Name == name);

    // A の形の腕（45° 下がる。左手は +X。人差し指が前 = -Z、小指が後ろ = +Z。掌は下）
    private static PmxDocument ArmModel()
    {
        var bones = new List<PmxBone>();
        int Add(string name, Vector3 position, int parent, Vector3 axis = default)
        {
            bones.Add(new PmxBone
            {
                Name = name, Position = position, ParentIndex = parent,
                Flags = PmxBoneFlags.Rotatable | (axis == default ? 0 : PmxBoneFlags.FixedAxis), FixedAxis = axis,
            });
            return bones.Count - 1;
        }
        int lower = Add("上半身", new Vector3(0, 11, 0), -1);
        int chest = Add("上半身2", new Vector3(0, 13, 0), lower);
        foreach (float sign in new[] { 1f, -1f })
        {
            string side = sign > 0 ? "左" : "右";
            Vector3 along = Vector3.Normalize(new Vector3(sign, -1, 0));
            var shoulder = new Vector3(sign * 1, 15, 0);
            Vector3 At(float d) => shoulder + (along * d);
            int upper = Add(side + "腕", At(0), chest);
            int twist = Add(side + "腕捩", At(1), upper, along);
            int elbow = Add(side + "ひじ", At(2), twist);
            int handTwist = Add(side + "手捩", At(3), elbow, along);
            int wrist = Add(side + "手首", At(4), handTwist);
            Vector3 hand = At(4.8f);
            Add(side + "中指１", hand, wrist);
            Add(side + "人指１", hand + new Vector3(0, 0, -0.2f) + (along * -0.05f), wrist);
            Add(side + "小指１", hand + new Vector3(0, 0, 0.3f) + (along * -0.1f), wrist);
        }
        return new PmxDocument { Bones = [.. bones] };
    }
}
