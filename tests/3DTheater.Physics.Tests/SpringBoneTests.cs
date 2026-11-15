using System.Numerics;
using Theater3D.Animation;
using Theater3D.Formats.Pmx;

namespace Theater3D.Physics.Tests;

/// <summary>VRM の揺れ物（スプリングボーン）の計算（設計判断記録 82 章の 4 段目）。</summary>
[TestClass]
public sealed class SpringBoneTests
{
    private const PmxBoneFlags Normal = PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible;

    /// <summary>
    /// 頭 (0,10,0) に、後ろ（+Z）へ水平に伸びた髪が 2 節付いたモデル（剛体は無く、揺れ物だけ）。
    /// ボーン: 0 頭、1 髪1 (0,10,0)、2 髪2 (0,10,1)。揺れ物: 髪1 の先端は髪2、髪2 の先端は (0,10,2)
    /// </summary>
    private static PmxDocument Hair(float stiffness, float gravity, float drag = 0.4f, SpringBoneCollider[]? colliders = null) => new()
    {
        Bones =
        [
            new PmxBone { Name = "頭", Position = new Vector3(0, 10, 0), Flags = Normal },
            new PmxBone { Name = "髪1", Position = new Vector3(0, 10, 0), ParentIndex = 0, Flags = Normal },
            new PmxBone { Name = "髪2", Position = new Vector3(0, 10, 1), ParentIndex = 1, Flags = Normal },
        ],
        SpringBones = new SpringBoneSet(
        [
            new SpringBoneJoint(1, new Vector3(0, 10, 1), stiffness, gravity, -Vector3.UnitY, drag, 0.1f, colliders is null ? [] : [.. Enumerable.Range(0, colliders.Length)], "髪"),
            new SpringBoneJoint(2, new Vector3(0, 10, 2), stiffness, gravity, -Vector3.UnitY, drag, 0.1f, colliders is null ? [] : [.. Enumerable.Range(0, colliders.Length)], "髪"),
        ], colliders ?? [], LengthScale: 12.5f),
    };

    private static (ModelRig Rig, ModelPhysics Physics, PoseInput Input, PoseState State) Create(PmxDocument document)
    {
        ModelRig rig = ModelRig.FromPmx(document);
        ModelPhysics physics = ModelPhysics.Create(document, rig)!;
        return (rig, physics, new PoseInput(rig), new PoseState(rig));
    }

    private static Vector3 Tip(PoseState state) => Vector3.Transform(new Vector3(0, 0, 1), state.Global[2]); // 髪2 の先端（髪2 から 1 先）

    private static void Run(ModelRig rig, ModelPhysics physics, PoseInput input, PoseState state, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
            physics.Step(state, 1f / 60f);
        }
    }

    [TestMethod]
    public void SpringsAloneMakePhysics()
    {
        var (_, physics, _, _) = Create(Hair(1f, 0f));
        Assert.AreEqual(0, physics.BodyCount, "剛体は無い");
        Assert.AreEqual(2, physics.SpringJointCount);
        Assert.AreEqual(2, physics.TransformCount, "焼き込みで覚える置き方は、揺れるボーンの数だけ");
        Assert.IsNull(ModelPhysics.Create(new PmxDocument { Bones = [new PmxBone { Name = "頭", Flags = Normal }] }, ModelRig.FromPmx(new PmxDocument
        {
            Bones = [new PmxBone { Name = "頭", Flags = Normal }],
        })), "剛体も揺れ物も無ければ作らない");
    }

    [TestMethod]
    public void GravityMakesTheHairHangDown()
    {
        var (rig, physics, input, state) = Create(Hair(stiffness: 0f, gravity: 1f, drag: 0.5f));
        PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
        physics.Reset(state, warmupSeconds: 0f);
        AssertNear(new Vector3(0, 10, 2), Tip(state), "初めは真後ろ");
        Run(rig, physics, input, state, 300);
        // 重力だけなら、根元の真下へ垂れる（長さは 2 のまま）
        AssertNear(new Vector3(0, 8, 0), Tip(state), "真下に垂れる");
        Assert.AreEqual(1f, Vector3.Distance(state.Global[1].Translation, state.Global[2].Translation), 1e-4f, "節の長さは変わらない");
    }

    [TestMethod]
    public void StiffnessFollowsTheHeadWithALag()
    {
        var (rig, physics, input, state) = Create(Hair(stiffness: 1f, gravity: 0f));
        PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
        physics.Reset(state, warmupSeconds: 0f);
        Run(rig, physics, input, state, 10);
        AssertNear(new Vector3(0, 10, 2), Tip(state), "重力が無ければ、元の向きのまま");

        // 頭を急に Y まわりに 90 度回すと、髪は遅れて付いてくる
        input.Rotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        Run(rig, physics, input, state, 1);
        Vector3 target = Vector3.Transform(new Vector3(0, 0, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)) + new Vector3(0, 10, 0);
        Assert.IsGreaterThan(0.5f, Vector3.Distance(target, Tip(state)), "回した直後は、まだ遅れている");
        Run(rig, physics, input, state, 600);
        AssertNear(target, Tip(state), "しばらくすると、回した頭の後ろへ戻る");
    }

    [TestMethod]
    public void CollidersPushTheTailOut()
    {
        // 頭に付いた球を、髪がまっすぐ垂れるとめり込む所（根元の少し下・少し前）に置く。重力で垂れても、球の後ろへよける
        var ball = new SpringBoneCollider(0, new Vector3(0, 9.2f, -0.5f), 0.5f);
        var (rig, physics, input, state) = Create(Hair(stiffness: 0f, gravity: 1f, drag: 0.5f, colliders: [ball]));
        PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
        physics.Reset(state, warmupSeconds: 0f);
        Run(rig, physics, input, state, 300);
        // 押し出した後に節の長さへそろえ直すので（VRM の決まりと同じ順）、わずかにめり込むことはある
        Vector3 first = state.Global[2].Translation; // 髪1 の先端 = 髪2 の根元
        Assert.IsGreaterThanOrEqualTo(0.57f, Vector3.Distance(first, ball.Center), "球の半径 0.5 ＋ 当たりの半径 0.1 の近くより外");
        Assert.IsGreaterThan(0.03f, first.Z, "まっすぐ（Z が 0）ではなく、球の後ろ（+Z）へよける");
        Assert.IsGreaterThanOrEqualTo(0.57f, Vector3.Distance(Tip(state), ball.Center));
    }

    [TestMethod]
    public void BakedTransformsPutTheHairBack()
    {
        var (rig, physics, input, state) = Create(Hair(stiffness: 0f, gravity: 1f, drag: 0.5f));
        PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
        physics.Reset(state, warmupSeconds: 0f);
        Run(rig, physics, input, state, 30);
        var baked = new PhysicsTransform[physics.TransformCount];
        physics.CopyTransforms(baked);
        Vector3 swung = Tip(state);

        // 別の姿勢から、覚えた置き方を当てはめると、同じ揺れた形に戻る
        var other = new PoseState(rig);
        PoseEvaluator.EvaluateBeforePhysics(rig, input, other);
        AssertNear(new Vector3(0, 10, 2), Tip(other), "当てはめる前は揺れていない");
        physics.ApplyTransforms(other, baked);
        AssertNear(swung, Tip(other), "覚えた揺れに戻る");
        Assert.ThrowsExactly<ArgumentException>(() => physics.ApplyTransforms(other, new PhysicsTransform[1]));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, string message)
    {
        Assert.IsLessThan(0.05f, Vector3.Distance(expected, actual), $"{message}（期待 {expected}・実際 {actual}）");
    }
}
