using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Animation.Tests;

[TestClass]
public sealed class RotationsTests
{
    [TestMethod]
    public void ScaleMultipliesTheAngle()
    {
        Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        AssertEx.SameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4), Rotations.Scale(q, 0.5f));
        AssertEx.SameRotation(Quaternion.Inverse(q), Rotations.Scale(q, -1));
        AssertEx.SameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), Rotations.Scale(q, 2));
        AssertEx.SameRotation(Quaternion.Identity, Rotations.Scale(q, 0));
        // -q（同じ回転を表す逆符号）でも短い向きで数える
        AssertEx.SameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4), Rotations.Scale(Quaternion.Negate(q), 0.5f));
    }

    [TestMethod]
    public void EulerXyzRoundTrips()
    {
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var q = Quaternion.Normalize(new Quaternion(
                (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
            Vector3 euler = Rotations.ToEulerXyz(q, Vector3.Zero);
            AssertEx.SameRotation(q, Rotations.FromEulerXyz(euler), 1e-4f, $"#{i}");
        }
    }

    [TestMethod]
    public void EulerXyzPicksTheSolutionNearThePreviousAngles()
    {
        var angles = new Vector3(0.3f, 0.2f, -0.4f);
        Quaternion q = Rotations.FromEulerXyz(angles);

        Vector3 near = Rotations.ToEulerXyz(q, angles);
        AssertEx.AreClose(angles, near);

        // もう一方の答え（X + π、π - Y、Z + π）の近くからなら、そちらを選ぶ（角度は 1 周ずれても同じとみなす）
        var other = new Vector3(angles.X + MathF.PI, MathF.PI - angles.Y, angles.Z + MathF.PI);
        Vector3 picked = Rotations.ToEulerXyz(q, other);
        Assert.AreEqual(0, Wrap(picked.X - other.X), 1e-3f);
        Assert.AreEqual(0, Wrap(picked.Y - other.Y), 1e-3f);
        Assert.AreEqual(0, Wrap(picked.Z - other.Z), 1e-3f);
        AssertEx.SameRotation(q, Rotations.FromEulerXyz(picked));
    }

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);
}

[TestClass]
public sealed class SkinnerTests
{
    [TestMethod]
    public void Bdef2BlendsTwoBones()
    {
        var b = new TestRigBuilder();
        int root = b.Bone("根元", Vector3.Zero);
        int moved = b.Bone("動く", Vector3.Zero, flags: PmxBoneFlags.Translatable);
        b.Vertex(new Vector3(1, 0, 0), PmxWeightType.Bdef2, root, moved, 0.25f);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Translations[moved] = new Vector3(0, 4, 0);
        var state = new PoseState(rig);
        PoseEvaluator.Evaluate(rig, input, state);
        var output = new SkinnedVertex[1];

        Skinner.Apply(rig, state, output, out Vector3 min, out Vector3 max);

        AssertEx.AreClose(new Vector3(1, 3, 0), output[0].Position);
        Assert.AreEqual(min, max);
    }

    [TestMethod]
    public void SdefKeepsDistanceFromTheJointWhenBending()
    {
        var b = new TestRigBuilder();
        int upper = b.Bone("上腕", Vector3.Zero);
        int lower = b.Bone("前腕", new Vector3(1, 0, 0), upper);
        var c = new Vector3(1, 0, 0);
        b.Vertex(new Vector3(1, 0.2f, 0), PmxWeightType.Sdef, upper, lower, 0.5f, c, c, c);
        b.Vertex(new Vector3(1, 0.2f, 0), PmxWeightType.Bdef2, upper, lower, 0.5f);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        var state = new PoseState(rig);
        var output = new SkinnedVertex[2];

        PoseEvaluator.Evaluate(rig, input, state);
        Skinner.Apply(rig, state, output, out _, out _);
        AssertEx.AreClose(new Vector3(1, 0.2f, 0), output[0].Position, message: "初期の姿勢で動いた");

        input.Rotations[lower] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        PoseEvaluator.Evaluate(rig, input, state);
        Skinner.Apply(rig, state, output, out _, out _);

        // SDEF は曲げても関節からの距離が変わらない（普通の重み付けは縮む）
        Assert.AreEqual(0.2f, Vector3.Distance(c, output[0].Position), 1e-4f);
        Assert.IsLessThan(0.18f, Vector3.Distance(c, output[1].Position));
        float s = MathF.Sqrt(0.5f) * 0.2f;
        AssertEx.AreClose(new Vector3(1 - s, s, 0), output[0].Position);
    }

    [TestMethod]
    public void NormalsFollowRotationAndStayUnitLength()
    {
        var b = new TestRigBuilder();
        int bone = b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.UnitX, normal: new Vector3(0, 3, 0));
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Rotations[bone] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var state = new PoseState(rig);
        PoseEvaluator.Evaluate(rig, input, state);
        var output = new SkinnedVertex[1];

        Skinner.Apply(rig, state, output, out _, out _);

        AssertEx.AreClose(new Vector3(-1, 0, 0), output[0].Normal);
        AssertEx.AreClose(new Vector3(0, 1, 0), output[0].Position);
    }

    [TestMethod]
    public void VertexAndUvMorphsAreAppliedBeforeSkinning()
    {
        var b = new TestRigBuilder();
        int bone = b.Bone("根元", Vector3.Zero, flags: PmxBoneFlags.Translatable);
        b.Vertex(Vector3.Zero);
        b.Morph(new PmxMorph { Name = "伸び", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, new Vector3(2, 0, 0))] });
        b.Morph(new PmxMorph { Name = "目線", Type = PmxMorphType.Uv, UvOffsets = [new PmxUvOffset(0, new Vector4(0.5f, 0, 0, 0))] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[0] = 0.5f;
        input.MorphWeights[1] = 1f;
        input.Translations[bone] = new Vector3(0, 0, 5);
        var state = new PoseState(rig);
        PoseEvaluator.Evaluate(rig, input, state);
        var output = new SkinnedVertex[1];

        Skinner.Apply(rig, state, output, out _, out _);

        AssertEx.AreClose(new Vector3(1, 0, 5), output[0].Position);
        Assert.AreEqual(new Vector2(0.5f, 0), output[0].Uv);
    }

    [TestMethod]
    public void ManyVerticesAreSplitAcrossThreadsWithTheSameResult()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        int moved = b.Bone("動く", Vector3.Zero, flags: PmxBoneFlags.Translatable);
        const int count = 50_000;
        for (int i = 0; i < count; i++)
        {
            b.Vertex(new Vector3(i * 0.001f, 0, 0), PmxWeightType.Bdef2, 0, moved, (i % 5) / 4f);
        }
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Translations[moved] = new Vector3(0, 8, 0);
        var state = new PoseState(rig);
        PoseEvaluator.Evaluate(rig, input, state);

        var array = new SkinnedVertex[count];
        Skinner.Apply(rig, state, array, out Vector3 min, out Vector3 max);
        Span<SkinnedVertex> span = new SkinnedVertex[count + 3];
        Skinner.Apply(rig, state, span, out Vector3 spanMin, out Vector3 spanMax);

        for (int i = 0; i < count; i++)
        {
            float expectedY = 8 * (1 - ((i % 5) / 4f));
            Assert.AreEqual(expectedY, array[i].Position.Y, 1e-4f, $"#{i}");
            Assert.AreEqual(array[i], span[i]);
        }
        AssertEx.AreClose(new Vector3(0, 0, 0), min);
        AssertEx.AreClose(new Vector3((count - 1) * 0.001f, 8, 0), max, 1e-3f);
        Assert.AreEqual(min, spanMin);
        Assert.AreEqual(max, spanMax);
    }

    [TestMethod]
    public void TooSmallOutputIsRejected()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        b.Vertex(Vector3.One);
        ModelRig rig = b.Build();
        var state = new PoseState(rig);
        PoseEvaluator.Evaluate(rig, new PoseInput(rig), state);

        Assert.ThrowsExactly<ArgumentException>(() => Skinner.Apply(rig, state, new SkinnedVertex[1], out _, out _));
    }
}
