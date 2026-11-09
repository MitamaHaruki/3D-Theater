using System.Numerics;
using Theater3D.Animation;
using Theater3D.Formats;
using Theater3D.Formats.Pmx;

namespace Theater3D.Physics.Tests;

/// <summary>PMX の剛体・ジョイントからモデルの物理を作り、ボーンが物理に従って動くことを確かめる。</summary>
[TestClass]
public sealed class ModelPhysicsTests
{
    private const PmxBoneFlags Normal = PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible;

    /// <summary>
    /// 頭に、横（+X）へ伸びた髪が 1 本付いたモデル。
    /// ボーン: 0 頭 (0,10,0)、1 髪 (0,10,0)、2 髪飾り (4,10,0)（髪の子、物理なし）、3 髪追従（物理後、髪の回転を付与で受ける）
    /// 剛体: 0 頭（ボーン追従）、1 髪（中心 (2,10,0)、物理）。ジョイントは (0,10,0) で、位置は固定・回転は自由
    /// </summary>
    private static PmxDocument Hair(PmxPhysicsMode hairMode = PmxPhysicsMode.Physics, PmxJointType jointType = PmxJointType.Spring6Dof) => new()
    {
        Bones =
        [
            new PmxBone { Name = "頭", Position = new Vector3(0, 10, 0), Flags = Normal },
            new PmxBone { Name = "髪", Position = new Vector3(0, 10, 0), ParentIndex = 0, Flags = Normal },
            new PmxBone { Name = "髪飾り", Position = new Vector3(4, 10, 0), ParentIndex = 1, Flags = Normal },
            new PmxBone
            {
                Name = "髪追従", Position = new Vector3(0, 10, 0), ParentIndex = 0,
                Flags = Normal | PmxBoneFlags.InheritRotation | PmxBoneFlags.AfterPhysics, InheritParent = 1, InheritWeight = 1,
            },
        ],
        RigidBodies =
        [
            new PmxRigidBody
            {
                Name = "頭", BoneIndex = 0, Shape = PmxRigidShape.Sphere, Size = new Vector3(1, 0, 0), Position = new Vector3(0, 10, 0),
                Mass = 1, Mode = PmxPhysicsMode.FollowBone, Group = 0, CollisionMask = 0,
            },
            new PmxRigidBody
            {
                Name = "髪", BoneIndex = 1, Shape = PmxRigidShape.Sphere, Size = new Vector3(0.3f, 0, 0), Position = new Vector3(2, 10, 0),
                Mass = 1, LinearDamping = 0.9f, AngularDamping = 0.9f, Mode = hairMode, Group = 1, CollisionMask = 0,
            },
        ],
        Joints =
        [
            new PmxJoint
            {
                Name = "髪", Type = jointType, RigidBodyA = 0, RigidBodyB = 1, Position = new Vector3(0, 10, 0),
                RotationMin = new Vector3(1), RotationMax = new Vector3(-1),
            },
        ],
    };

    private static (ModelRig Rig, ModelPhysics Physics, PoseInput Input, PoseState State) Create(PmxDocument document)
    {
        ModelRig rig = ModelRig.FromPmx(document);
        ModelPhysics physics = ModelPhysics.Create(document, rig)!;
        return (rig, physics, new PoseInput(rig), new PoseState(rig));
    }

    private static Vector3 HairDirection(PoseState state) => Vector3.Transform(Vector3.UnitX, state.GlobalRotation[1]);

    [TestMethod]
    public void HairHangsDownAndCarriesItsChildren()
    {
        var (rig, physics, input, state) = Create(Hair());
        using (physics)
        {
            PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
            physics.Reset(state, warmupSeconds: 3);
            PoseEvaluator.EvaluateAfterPhysics(rig, state);

            Assert.IsGreaterThan(0.95f, Vector3.Dot(HairDirection(state), -Vector3.UnitY), "横に伸びた髪が、重力で下へ垂れる");
            Assert.IsTrue(state.PhysicsDriven[1]);
            Assert.IsFalse(state.PhysicsDriven[0], "ボーン追従の剛体のボーンは、物理で動かさない");
            Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 6, 0), state.Global[2].Translation), 0.4f, "物理で動いた髪の子（物理なし）も一緒に動く");
            float follow = MathF.Abs(Quaternion.Dot(state.GlobalRotation[1], state.GlobalRotation[3]));
            Assert.AreEqual(1f, follow, 1e-3f, "物理後のボーンは、物理の結果を付与で受ける");
        }
    }

    [TestMethod]
    public void KinematicBodiesFollowTheBoneAndPullTheHair()
    {
        var (rig, physics, input, state) = Create(Hair());
        using (physics)
        {
            PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
            physics.Reset(state, warmupSeconds: 2);

            input.Translations[0] = new Vector3(0, 5, 0); // 頭を上へ動かす
            for (int frame = 0; frame < 60; frame++)
            {
                PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
                physics.Step(state, 1f / 30f);
            }
            PoseEvaluator.EvaluateAfterPhysics(rig, state);

            Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 15, 0), state.Global[1].Translation), 0.3f, "髪の付け根は頭に付いていく");
            Assert.IsGreaterThan(0.9f, Vector3.Dot(HairDirection(state), -Vector3.UnitY));
        }
    }

    [TestMethod]
    public void PhysicsWithBoneKeepsTheBonePosition()
    {
        var (rig, physics, input, state) = Create(Hair(PmxPhysicsMode.PhysicsWithBone));
        using (physics)
        {
            input.Translations[1] = new Vector3(0, 1, 0);
            PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
            physics.Reset(state, warmupSeconds: 3);
            PoseEvaluator.EvaluateAfterPhysics(rig, state);

            Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 11, 0), state.Global[1].Translation), 1e-3f, "位置はボーン（動かした値）のまま");
            Assert.IsGreaterThan(0.95f, Vector3.Dot(HairDirection(state), -Vector3.UnitY), "向きは物理に従う");
        }
    }

    [TestMethod]
    public void WithoutPhysicsTheSplitEvaluationMatchesTheWholeOne()
    {
        ModelRig rig = ModelRig.FromPmx(Hair());
        var input = new PoseInput(rig);
        input.Rotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        var whole = new PoseState(rig);
        var split = new PoseState(rig);

        PoseEvaluator.Evaluate(rig, input, whole);
        PoseEvaluator.EvaluateBeforePhysics(rig, input, split);
        PoseEvaluator.EvaluateAfterPhysics(rig, split);

        CollectionAssert.AreEqual(whole.Global, split.Global);
        Assert.AreEqual(3, rig.AfterPhysicsStart, "物理後のボーンは変形の順番の最後");
    }

    [TestMethod]
    public void ModelsWithoutRigidBodiesHaveNoPhysicsAndUnsupportedJointsAreReported()
    {
        PmxDocument plain = new() { Bones = [new PmxBone { Name = "センター", Flags = Normal }] };
        Assert.IsNull(ModelPhysics.Create(plain, ModelRig.FromPmx(plain)));

        PmxDocument hinge = Hair(jointType: PmxJointType.Hinge);
        var report = new LoadReport();
        using ModelPhysics physics = ModelPhysics.Create(hinge, ModelRig.FromPmx(hinge), report)!;
        Assert.AreEqual(0, physics.JointCount);
        Assert.AreEqual(1, physics.DynamicBodyCount);
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("ジョイント")));
    }
}
