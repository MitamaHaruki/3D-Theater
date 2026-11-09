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
    private static PmxDocument Hair(PmxPhysicsMode hairMode = PmxPhysicsMode.Physics, PmxJointType jointType = PmxJointType.Spring6Dof,
        PmxMorph[]? morphs = null) => new()
    {
        Morphs = morphs ?? [],
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

    private static PmxMorph Impulse(params PmxImpulseOffset[] offsets) => new() { Name = "押す", Type = PmxMorphType.Impulse, ImpulseOffsets = offsets };

    /// <summary>垂れた髪を、モーフの値を最初の 1 フレームだけ <paramref name="value"/> にして押し、10 フレーム進める間の、髪の向きの X のいちばん大きい値。</summary>
    private static float SwingAfterImpulse(PmxImpulseOffset offset, float value)
    {
        var (rig, physics, input, state) = Create(Hair(morphs: [Impulse(offset)]));
        using (physics)
        {
            PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
            physics.Reset(state, warmupSeconds: 3);
            float swing = 0;
            for (int frame = 0; frame < 10; frame++)
            {
                input.MorphWeights[0] = frame == 0 ? value : 0;
                PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
                physics.Step(state, 1f / 30f);
                swing = MathF.Max(swing, HairDirection(state).X);
            }
            return swing;
        }
    }

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

    // 頭を上下に揺らす入力（フレームごと）
    private static void HeadBob(PoseInput input, int frame) => input.Translations[0] = new Vector3(0, 2 * MathF.Sin(frame * 0.2f), 0);

    [TestMethod]
    public void BakedFramesMatchPlayingFromTheStart()
    {
        // 20 フレーム目にインパルスモーフで押す（押す勢いも、焼き込みと画面で同じに与える）
        PmxDocument document = Hair(morphs: [Impulse(new PmxImpulseOffset(1, false, new Vector3(0, 0, 8), Vector3.Zero))]);
        ModelRig rig = ModelRig.FromPmx(document);
        var input = new PoseInput(rig);
        using var bake = new PhysicsBake(ModelPhysics.Create(document, rig)!, rig);
        bake.Invalidate(30, new Vector3(0, -ModelPhysics.GravityStrength, 0));
        for (int frame = 0; frame < 60; frame++)
        {
            HeadBob(input, frame);
            input.MorphWeights[0] = frame == 20 ? 1 : 0;
            bake.BakeNext(input);
        }

        // 画面と同じやり方（最初に置き直し、あとは 1 フレームずつ）で、別の世界で計算する
        using ModelPhysics live = ModelPhysics.Create(document, rig)!;
        var liveState = new PoseState(rig);
        for (int frame = 0; frame < 60; frame++)
        {
            HeadBob(input, frame);
            input.MorphWeights[0] = frame == 20 ? 1 : 0;
            PoseEvaluator.EvaluateBeforePhysics(rig, input, liveState);
            if (frame == 0)
            {
                live.Reset(liveState);
            }
            else
            {
                live.Step(liveState, 1f / 30f);
            }
        }

        var bakedState = new PoseState(rig);
        PoseEvaluator.EvaluateBeforePhysics(rig, input, bakedState);
        Assert.IsTrue(bake.TryApply(59, bakedState));
        Assert.AreEqual(60, bake.Count);
        Assert.AreEqual(0f, Vector3.Distance(liveState.Global[1].Translation, bakedState.Global[1].Translation), 1e-4f);
        Assert.AreEqual(1f, MathF.Abs(Quaternion.Dot(liveState.GlobalRotation[1], bakedState.GlobalRotation[1])), 1e-5f, "続けて計算した結果と同じ");
    }

    [TestMethod]
    public void ScrubbingBackAndForthGivesTheSamePose()
    {
        PmxDocument document = Hair();
        ModelRig rig = ModelRig.FromPmx(document);
        var input = new PoseInput(rig);
        using var bake = new PhysicsBake(ModelPhysics.Create(document, rig)!, rig);
        bake.Invalidate(30, new Vector3(0, -ModelPhysics.GravityStrength, 0));
        for (int frame = 0; frame < 50; frame++)
        {
            HeadBob(input, frame);
            bake.BakeNext(input);
        }
        var state = new PoseState(rig);
        Quaternion HairAt(int frame)
        {
            HeadBob(input, frame);
            PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
            Assert.IsTrue(bake.TryApply(frame, state));
            return state.GlobalRotation[1];
        }

        Quaternion first = HairAt(40);
        HairAt(10);
        HairAt(49);
        Assert.AreEqual(first, HairAt(40), "どこから来ても、同じフレームなら同じ姿勢");
        Assert.AreNotEqual(HairAt(10), HairAt(40), "揺れているので、フレームが違えば姿勢も違う");
    }

    [TestMethod]
    public void InvalidatingForgetsTheFrames()
    {
        PmxDocument document = Hair();
        ModelRig rig = ModelRig.FromPmx(document);
        var input = new PoseInput(rig);
        using var bake = new PhysicsBake(ModelPhysics.Create(document, rig)!, rig);
        bake.Invalidate(30, new Vector3(0, -ModelPhysics.GravityStrength, 0));
        bake.BakeNext(input);
        bake.BakeNext(input);
        int version = bake.Version;

        bake.Invalidate(60, new Vector3(0, -ModelPhysics.GravityStrength, 0));

        Assert.AreEqual(0, bake.Count);
        Assert.AreEqual(60, bake.FrameRate);
        Assert.AreEqual(version + 1, bake.Version);
        Assert.IsFalse(bake.TryApply(0, new PoseState(rig)));
        bake.BakeNext(input);
        Assert.AreEqual(1, bake.Count, "0 フレーム目から計算し直せる");
    }

    [TestMethod]
    public void ImpulseMorphPushesTheHairOnlyWhileItsValueIsNotZero()
    {
        var push = new PmxImpulseOffset(1, false, new Vector3(8, 0, 0), Vector3.Zero);
        float none = SwingAfterImpulse(push, 0);
        float half = SwingAfterImpulse(push, 0.5f);
        float full = SwingAfterImpulse(push, 1);

        // 押さなくても、垂れたあとの揺れが少し残る
        Assert.IsLessThan(0.05f, none, "値が 0 なら押さない");
        Assert.IsGreaterThan(none + 0.2f, full, "押した向き（+X）へ振れる");
        Assert.IsGreaterThan(none + 0.05f, half);
        Assert.IsGreaterThan(half + 0.05f, full, "値が大きいほど強く押す");
        Assert.AreEqual(none, SwingAfterImpulse(new PmxImpulseOffset(0, false, new Vector3(8, 0, 0), Vector3.Zero), 1), 1e-5f,
            "ボーン追従の剛体は押さない（押さないときと同じ）");
    }

    [TestMethod]
    public void LocalImpulseIsTurnedWithTheBody()
    {
        // 垂れた髪の剛体は Z まわりに -90° 回っていて、剛体の +Y が世界の +X を向いている
        float none = SwingAfterImpulse(new PmxImpulseOffset(1, true, new Vector3(0, 8, 0), Vector3.Zero), 0);
        float local = SwingAfterImpulse(new PmxImpulseOffset(1, true, new Vector3(0, 8, 0), Vector3.Zero), 1);
        float global = SwingAfterImpulse(new PmxImpulseOffset(1, false, new Vector3(0, 8, 0), Vector3.Zero), 1);

        Assert.IsGreaterThan(none + 0.2f, local, "剛体の向きで回すと、横へ押す");
        Assert.AreEqual(none, global, 0.05f, "回さなければ上へ押すだけで、横へは振れない");
    }

    [TestMethod]
    public void StopImpulseClearsTheMotion()
    {
        // 横に伸ばした髪を落とし始めてから、速さも回す力も 0 のインパルスモーフで止める
        float TurnInOneFrame(bool stop)
        {
            var (rig, physics, input, state) = Create(Hair(morphs: [Impulse(new PmxImpulseOffset(1, false, Vector3.Zero, Vector3.Zero))]));
            using (physics)
            {
                PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
                physics.Reset(state, warmupSeconds: 0);
                for (int frame = 0; frame < 3; frame++)
                {
                    PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
                    physics.Step(state, 1f / 30f);
                }
                Vector3 before = HairDirection(state);
                input.MorphWeights[0] = stop ? 1 : 0;
                PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
                physics.Step(state, 1f / 30f);
                return MathF.Acos(Math.Clamp(Vector3.Dot(before, HairDirection(state)), -1f, 1f));
            }
        }

        float moving = TurnInOneFrame(stop: false);
        float stopped = TurnInOneFrame(stop: true);
        Assert.IsGreaterThan(stopped * 2, moving, "止めると、その後の 1 フレームで回る角度が小さい（速さが 0 から始まる）");
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
