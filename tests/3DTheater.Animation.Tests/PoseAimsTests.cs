using System.Numerics;

namespace Theater3D.Animation.Tests;

/// <summary>ボーンを決まった向きへ向ける頼み（トラッキングの腕。設計判断記録 100 章）。</summary>
[TestClass]
public sealed class PoseAimsTests
{
    // 上半身 (0,10,0) → 腕 (1,10,0) → 腕捩 (2,10,0)（+X の軸に決まった捩り）→ ひじ (3,10,0) → 手首 (5,10,0)
    private static ModelRig Arm()
    {
        var b = new TestRigBuilder();
        int torso = b.Bone("上半身", new Vector3(0, 10, 0));
        int upper = b.Bone("腕", new Vector3(1, 10, 0), torso);
        int twist = b.Bone("腕捩", new Vector3(2, 10, 0), upper,
            Formats.Pmx.PmxBoneFlags.Rotatable | Formats.Pmx.PmxBoneFlags.Visible | Formats.Pmx.PmxBoneFlags.FixedAxis, fixedAxis: Vector3.UnitX);
        int elbow = b.Bone("ひじ", new Vector3(3, 10, 0), twist);
        b.Bone("手首", new Vector3(5, 10, 0), elbow);
        return b.Build();
    }

    [TestMethod]
    public void AimFollowsTheReferenceBoneAndChildrenUseTheAimedParent()
    {
        ModelRig rig = Arm();
        var input = new PoseInput(rig);
        var state = new PoseState(rig);
        // 上半身を縦の軸で 30° 回し、腕には場面の回転（待機モーションの分）が入っている
        Quaternion torso = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30 * MathF.PI / 180);
        input.Rotations[0] = torso;
        input.Rotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        // 腕: 上半身から見て下へ（+X → -Y）。ひじ: 上半身から見て前へ（+X → -Z）
        Quaternion down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        Quaternion forward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        PoseAims.Apply(rig, input, state,
        [
            new BoneAim(1, 0, down, 1f),
            new BoneAim(2, -1, Quaternion.Identity, 1f),
            new BoneAim(3, 0, forward, 1f),
        ]);
        PoseEvaluator.Evaluate(rig, input, state);
        AssertEx.SameRotation(torso * down, state.GlobalRotation[1], 1e-4f, "腕は、上半身の回りの中で下へ");
        Vector3 upperDirection = Vector3.Normalize(state.Global[3].Translation - state.Global[1].Translation);
        AssertEx.AreClose(Vector3.Transform(-Vector3.UnitY, torso), upperDirection, 1e-4f, "腕の先（ひじ）は下");
        Vector3 foreDirection = Vector3.Normalize(state.Global[4].Translation - state.Global[3].Translation);
        AssertEx.AreClose(Vector3.Transform(-Vector3.UnitZ, torso), foreDirection, 1e-4f, "前腕は、上半身から見て前");
    }

    [TestMethod]
    public void WeightBlendsWithTheSceneRotationAndNothingChangesWithoutAims()
    {
        ModelRig rig = Arm();
        var input = new PoseInput(rig);
        var state = new PoseState(rig);
        Quaternion scene = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f);
        input.Rotations[1] = scene;
        PoseAims.Apply(rig, input, state, []);
        Assert.AreEqual(scene, input.Rotations[1], "頼みが無ければ、そのまま");

        Quaternion target = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.8f);
        PoseAims.Apply(rig, input, state, [new BoneAim(1, 0, target, 0.5f)]);
        AssertEx.SameRotation(Quaternion.Slerp(scene, target, 0.5f), input.Rotations[1], 1e-4f, "重み 0.5 は、場面の回転と頼んだ向きの間");
        PoseAims.Apply(rig, input, state, [new BoneAim(1, 0, new Quaternion(float.NaN, 0, 0, 1), 1f), new BoneAim(99, 0, target, 1f)]);
        AssertEx.SameRotation(Quaternion.Slerp(scene, target, 0.5f), input.Rotations[1], 1e-4f, "数でない向き・無いボーンは使わない");
    }
}
