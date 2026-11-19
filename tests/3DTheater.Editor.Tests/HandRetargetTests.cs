using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Editor.Tests;

/// <summary>手のトラッキングを、キャラクターの指のボーンに当てはめる計算（設計判断記録 99 章）。</summary>
[TestClass]
public sealed class HandRetargetTests
{
    // 開いた手でトラッキングが読む値（HandRetarget の差し引く分と同じ）
    private static readonly float[] OpenHand = [16, 10, 15, 32, 10, 10, 25, -2, 8, 16, 6, -5, 9, 12, 7, -10, 4, 12, 12, -22];

    private static float[] Fist()
    {
        float[] a = [.. OpenHand];
        for (int f = 1; f < 5; f++)
        {
            a[f * 4] = 65;
            a[(f * 4) + 1] = 90;
            a[(f * 4) + 2] = 70;
            a[(f * 4) + 3] = 0;
        }
        return a;
    }

    [TestMethod]
    public void OpenHandIsStraightAndFistBendsStrongly()
    {
        float[] open = HandRetarget.Map(OpenHand);
        foreach (float v in open)
        {
            Assert.AreEqual(0f, v, 1e-4f, "開いた手は、モデルのもとの形");
        }
        float[] fist = HandRetarget.Map(Fist());
        Assert.AreEqual((65 - 10) * 1.4f, fist[4], 1e-3f, "人差し指の根元は、開いた手の分を引いて強める");
        Assert.AreEqual((90 - 16) * 1.2f, fist[9], 1e-3f, "中指の中の関節");
        Assert.IsGreaterThan(0.8f, HandOutput.Grip(fist));
        Assert.AreEqual(0f, fist[19], 1e-3f, "根元を大きく曲げた指の開きは読まない（もとの開きへ戻す）");

        float[] extreme = [.. Enumerable.Repeat(500f, 20)];
        float[] limited = HandRetarget.Map(extreme);
        Assert.AreEqual(110f, limited[5], "範囲に収める");
    }

    [TestMethod]
    public void HandsMoveTheSameSideAndMirrorSwapsThem()
    {
        var retarget = new HandRetarget();
        HandOutput output = HandOutput.Rest;
        for (int i = 0; i < 30; i++)
        {
            output = retarget.Compute([new HandSignal(true, Fist())], i / 30.0, mirror: false, smoothing: 0.5f);
        }
        Assert.IsGreaterThan(0.7f, HandOutput.Grip(output.Right), "本人の右手 → キャラクターの右手");
        Assert.AreEqual(0f, HandOutput.Grip(output.Left), 1e-4f);

        var mirrored = new HandRetarget();
        for (int i = 0; i < 30; i++)
        {
            output = mirrored.Compute([new HandSignal(true, Fist())], i / 30.0, mirror: true, smoothing: 0.5f);
        }
        Assert.IsGreaterThan(0.7f, HandOutput.Grip(output.Left), "鏡のようにすると、反対の手");
        Assert.AreEqual(0f, HandOutput.Grip(output.Right), 1e-4f);
    }

    [TestMethod]
    public void LostHandsEaseBackToTheModelShape()
    {
        var retarget = new HandRetarget();
        double t = 0;
        for (int i = 0; i < 30; i++, t += 1 / 30.0)
        {
            retarget.Compute([new HandSignal(false, Fist())], t, mirror: false, smoothing: 0.5f);
        }
        HandOutput half = retarget.Compute([], t + (HandRetarget.FadeOutSeconds * 0.5), false, 0.5f);
        float grip = HandOutput.Grip(half.Left);
        Assert.IsGreaterThan(0.1f, grip, "すぐには伸ばさない");
        Assert.IsLessThan(0.7f, grip, "少しずつ伸ばす");
        HandOutput rest = retarget.Compute(null, t + HandRetarget.FadeOutSeconds + 0.1, false, 0.5f);
        Assert.AreEqual(0f, HandOutput.Grip(rest.Left), 1e-4f, "見失って少し経てば、もとの形");
    }

    [TestMethod]
    public void FingerBonesBendTowardThePalmOnBothHands()
    {
        PmxDocument model = HandModel();
        var rig = HandRig.Create(model);
        Assert.AreEqual(2 * 15, rig.BoneCount, "左右の 5 本の指 × 3 つの関節");

        // 左右とも、人差し指の中の関節を 90° 曲げる
        var angles = new float[20];
        angles[5] = 90;
        var bones = new List<(int Bone, Quaternion Rotation)>();
        rig.Build(new HandOutput(angles, angles), bones);
        Assert.HasCount(2, bones, "曲げたボーンだけ");
        int Index(string name) => Array.FindIndex(model.Bones, b => b.Name == name);
        Quaternion left = bones.Single(b => b.Bone == Index("左人指２")).Rotation;
        Quaternion right = bones.Single(b => b.Bone == Index("右人指２")).Rotation;
        // MMD のモデルはもとの形で掌が下を向く。指は掌の側（下）へ曲がる
        AssertClose(-Vector3.UnitY, Vector3.Transform(Vector3.UnitX, left), "左手の指（+X を向く）が下へ");
        AssertClose(-Vector3.UnitY, Vector3.Transform(-Vector3.UnitX, right), "右手の指（-X を向く）が下へ");

        // 開き: 親指の側（前 = -Z）へ開く
        var spread = new float[20];
        spread[7] = 20;
        bones.Clear();
        rig.Build(new HandOutput(spread, spread), bones);
        Quaternion opened = bones.Single(b => b.Bone == Index("左人指１")).Rotation;
        Assert.IsLessThan(-0.3f, Vector3.Transform(Vector3.UnitX, opened).Z, "人差し指は親指の側へ");
    }

    [TestMethod]
    public void ThumbWithoutTheMetacarpalBoneGetsBothBaseJoints()
    {
        PmxDocument model = HandModel(withThumbBase: false);
        var rig = HandRig.Create(model);
        Assert.AreEqual(2 * 14, rig.BoneCount, "親指０ の無いモデル");
        var angles = new float[20];
        angles[0] = 10;
        angles[1] = 20;
        var bones = new List<(int Bone, Quaternion Rotation)>();
        rig.Build(new HandOutput(angles, HandOutput.Rest.Right), bones);
        Quaternion thumb = bones.Single().Rotation;
        float degrees = 2 * MathF.Acos(Math.Clamp(MathF.Abs(thumb.W), 0, 1)) * 180 / MathF.PI;
        Assert.AreEqual(30f, degrees, 0.1f, "親指１ に根元と中の両方");
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, string message)
    {
        Assert.IsLessThan(0.02f, Vector3.Distance(expected, actual), $"{message}（{actual}）");
    }

    // T の形の MMD のモデルの手（-Z を向き、左手は +X。掌は下。人差し指が前 = -Z、小指が後ろ = +Z）
    private static PmxDocument HandModel(bool withThumbBase = true)
    {
        var bones = new List<PmxBone>();
        int Add(string name, Vector3 position, int parent)
        {
            bones.Add(new PmxBone { Name = name, Position = position, ParentIndex = parent, Flags = PmxBoneFlags.Rotatable });
            return bones.Count - 1;
        }
        foreach (float sign in new[] { 1f, -1f })
        {
            string side = sign > 0 ? "左" : "右";
            int wrist = Add(side + "手首", new Vector3(sign * 5, 15, 0), -1);
            (string Name, float Z)[] fingers = [("人指", -0.3f), ("中指", -0.1f), ("薬指", 0.1f), ("小指", 0.3f)];
            // 親指: 手首から前へ出て、指の向きへ
            int parent = wrist;
            string[] thumb = withThumbBase ? ["親指０", "親指１", "親指２"] : ["親指１", "親指２"];
            for (int k = 0; k < thumb.Length; k++)
            {
                parent = Add(side + thumb[k], new Vector3(sign * (5.2f + (0.3f * k)), 14.9f, -0.4f - (0.2f * k)), parent);
            }
            Add(side + "親指先", new Vector3(sign * (5.2f + (0.3f * thumb.Length)), 14.9f, -0.4f - (0.2f * thumb.Length)), parent);
            foreach (var (name, z) in fingers)
            {
                parent = wrist;
                for (int k = 1; k <= 3; k++)
                {
                    parent = Add(side + name + (char)('０' + k), new Vector3(sign * (6 + (0.3f * (k - 1))), 15, z), parent);
                }
                Add(side + name + "先", new Vector3(sign * 6.9f, 15, z), parent);
            }
        }
        // 最後の関節の向きは、子の「先」のボーンで決まる（先端を子にする）
        for (int i = 0; i < bones.Count; i++)
        {
            int child = bones.FindIndex(b => b.ParentIndex == i);
            if (child >= 0)
            {
                PmxBone b = bones[i];
                bones[i] = new PmxBone { Name = b.Name, Position = b.Position, ParentIndex = b.ParentIndex, Flags = b.Flags | PmxBoneFlags.TailIsBone, TailBone = child };
            }
        }
        return new PmxDocument { Bones = [.. bones] };
    }
}
