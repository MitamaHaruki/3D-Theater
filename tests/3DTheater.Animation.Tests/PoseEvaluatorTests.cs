using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Animation.Tests;

[TestClass]
public sealed class PoseEvaluatorTests
{
    private static PoseState Evaluate(ModelRig rig, PoseInput input)
    {
        var state = new PoseState(rig);
        PoseEvaluator.Evaluate(rig, input, state);
        return state;
    }

    [TestMethod]
    public void RestPoseKeepsBonesAndVerticesInPlace()
    {
        ModelRig rig = TestRigs.Leg();
        PoseState state = Evaluate(rig, new PoseInput(rig));

        for (int i = 0; i < rig.Bones.Length; i++)
        {
            AssertEx.AreClose(rig.Bones[i].RestPosition, state.BonePosition(i), message: rig.Bones[i].Name);
            Assert.IsTrue(state.Skinning[i].IsIdentity || Matrix4x4Close(state.Skinning[i], Matrix4x4.Identity), rig.Bones[i].Name);
        }
    }

    [TestMethod]
    public void ParentRotationMovesChildren()
    {
        var b = new TestRigBuilder();
        int root = b.Bone("根元", Vector3.Zero);
        int tip = b.Bone("先", new Vector3(1, 0, 0), root);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Rotations[root] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(0, 1, 0), state.BonePosition(tip));
        AssertEx.SameRotation(input.Rotations[root], state.GlobalRotation[tip]);
    }

    [TestMethod]
    public void TranslationIsAddedToRestOffset()
    {
        var b = new TestRigBuilder();
        int root = b.Bone("センター", new Vector3(0, 8, 0), flags: PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable);
        int child = b.Bone("上半身", new Vector3(0, 10, 0), root);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Translations[root] = new Vector3(0, 0, 3);

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(0, 8, 3), state.BonePosition(root));
        AssertEx.AreClose(new Vector3(0, 10, 3), state.BonePosition(child));
    }

    [TestMethod]
    public void InheritRotationUsesWeightIncludingNegative()
    {
        var b = new TestRigBuilder();
        int source = b.Bone("元", Vector3.Zero);
        int half = b.Bone("半分", Vector3.Zero, flags: PmxBoneFlags.Rotatable | PmxBoneFlags.InheritRotation, inheritParent: source, inheritWeight: 0.5f);
        int cancel = b.Bone("打ち消し", Vector3.Zero, flags: PmxBoneFlags.Rotatable | PmxBoneFlags.InheritRotation, inheritParent: source, inheritWeight: -1f);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Rotations[source] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        PoseState state = Evaluate(rig, input);

        AssertEx.SameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4), state.GlobalRotation[half]);
        AssertEx.SameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2), state.GlobalRotation[cancel]);
    }

    [TestMethod]
    public void InheritTranslationUsesWeight()
    {
        var b = new TestRigBuilder();
        int source = b.Bone("元", Vector3.Zero, flags: PmxBoneFlags.Translatable);
        int follower = b.Bone("追う", new Vector3(2, 0, 0), flags: PmxBoneFlags.InheritTranslation, inheritParent: source, inheritWeight: 0.5f);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Translations[source] = new Vector3(0, 4, 0);

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(2, 2, 0), state.BonePosition(follower));
    }

    [TestMethod]
    public void InheritedRotationAlsoMovesChildren()
    {
        var b = new TestRigBuilder();
        int source = b.Bone("元", Vector3.Zero);
        int follower = b.Bone("追う", Vector3.Zero, flags: PmxBoneFlags.Rotatable | PmxBoneFlags.InheritRotation, inheritParent: source, inheritWeight: 1);
        int child = b.Bone("子", new Vector3(1, 0, 0), follower);
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.Rotations[source] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(0, 1, 0), state.BonePosition(child));
    }

    [TestMethod]
    public void FixedAxisKeepsOnlyTheTwistAroundTheAxis()
    {
        var b = new TestRigBuilder();
        int bone = b.Bone("腕捩", Vector3.Zero, flags: PmxBoneFlags.Rotatable | PmxBoneFlags.FixedAxis, fixedAxis: new Vector3(2, 0, 0));
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        Quaternion twist = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
        input.Rotations[bone] = twist * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);

        PoseState state = Evaluate(rig, input);

        // Y まわりの分は消え、X まわりの成分だけが残る
        Quaternion result = state.GlobalRotation[bone];
        Assert.AreEqual(0, result.Y, 1e-5f);
        Assert.AreEqual(0, result.Z, 1e-5f);
        Assert.IsGreaterThan(0.1f, MathF.Abs(result.X));
    }

    [TestMethod]
    public void LegIkReachesTargetAndBendsKneeForward()
    {
        ModelRig rig = TestRigs.Leg();
        var input = new PoseInput(rig);
        int ik = rig.FindBone("足ＩＫ"), ankle = rig.FindBone("足首"), knee = rig.FindBone("ひざ");
        input.Translations[ik] = new Vector3(0, 3, 0);

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(1, 4, 0), state.BonePosition(ankle), 0.02f, "足首が IK に届いていない");
        Assert.IsLessThan(-0.5f, state.BonePosition(knee).Z, "ひざは前（-Z）へ出るはず");
        // 制限の範囲（X 軸まわりに -180°〜-0.5°）に入っている
        Vector3 euler = Rotations.ToEulerXyz(state.IkRotation[knee] * state.AnimationRotation[knee], Vector3.Zero);
        Assert.IsLessThanOrEqualTo(TestRigs.KneeMax + 1e-4f, euler.X);
        Assert.AreEqual(0, euler.Y, 1e-3f);
        Assert.AreEqual(0, euler.Z, 1e-3f);
    }

    [TestMethod]
    public void DisabledIkLeavesTheLinksAtTheirOwnRotations()
    {
        // MMD の「表示・IK」で IK を切ると、IK を解かず、ひざなどは自分のキーの回転のまま（物理の前と後に分けても同じ）
        ModelRig rig = TestRigs.Leg();
        var input = new PoseInput(rig);
        int ik = rig.FindBone("足ＩＫ"), ankle = rig.FindBone("足首"), knee = rig.FindBone("ひざ");
        input.Translations[ik] = new Vector3(0, 3, 0);
        input.Rotations[knee] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.4f);
        Assert.IsTrue(input.IkEnabled[ik], "初めは IK を解く");

        input.IkEnabled[ik] = false;
        PoseState off = Evaluate(rig, input);
        var split = new PoseState(rig);
        PoseEvaluator.EvaluateBeforePhysics(rig, input, split);
        PoseEvaluator.EvaluateAfterPhysics(rig, split);

        Assert.AreEqual(Quaternion.Identity, off.IkRotation[knee], "IK を切ると、リンクを回さない");
        Assert.IsGreaterThan(0.5f, Vector3.Distance(new Vector3(1, 4, 0), off.BonePosition(ankle)), "足首は IK の位置へ行かない");
        AssertEx.AreClose(off.BonePosition(ankle), split.BonePosition(ankle), 1e-5f, "物理の前と後に分けても同じ");
        input.IkEnabled[ik] = true;
        AssertEx.AreClose(new Vector3(1, 4, 0), Evaluate(rig, input).BonePosition(ankle), 0.02f, "IK を戻すと届く");
    }

    [TestMethod]
    public void LegIkWithTargetOutOfReachStaysFinite()
    {
        ModelRig rig = TestRigs.Leg();
        var input = new PoseInput(rig);
        int ik = rig.FindBone("足ＩＫ"), ankle = rig.FindBone("足首");
        input.Translations[ik] = new Vector3(0, -20, -3);

        PoseState state = Evaluate(rig, input);

        for (int i = 0; i < rig.Bones.Length; i++)
        {
            AssertEx.Finite(state.BonePosition(i), rig.Bones[i].Name);
        }
        // 届かないときは IK の向きへ伸びきる（腰の足の付け根から 9 の長さ）
        Vector3 hip = state.BonePosition(rig.FindBone("足"));
        Assert.AreEqual(9f, Vector3.Distance(hip, state.BonePosition(ankle)), 0.05f);
    }

    [TestMethod]
    public void IkWithoutLimitsStillReachesTarget()
    {
        ModelRig rig = TestRigs.Leg(kneeLimit: false);
        var input = new PoseInput(rig);
        int ik = rig.FindBone("足ＩＫ"), ankle = rig.FindBone("足首");
        input.Translations[ik] = new Vector3(2, 3, -1);

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(3, 4, -1), state.BonePosition(ankle), 0.02f);
    }

    [TestMethod]
    public void InheritFromIkLinkReceivesIkRotation()
    {
        ModelRig rig = TestRigs.Leg();
        var input = new PoseInput(rig);
        input.Translations[rig.FindBone("足ＩＫ")] = new Vector3(0, 3, 0);

        PoseState state = Evaluate(rig, input);

        // 足D は足の回転（IK で決まった分を含む）を受け継ぐ
        AssertEx.SameRotation(state.GlobalRotation[rig.FindBone("足")], state.GlobalRotation[rig.FindBone("足D")]);
    }

    [TestMethod]
    public void BoneMorphRotatesAndMovesBones()
    {
        var b = new TestRigBuilder();
        int root = b.Bone("根元", Vector3.Zero);
        int tip = b.Bone("先", new Vector3(1, 0, 0), root);
        b.Morph(new PmxMorph
        {
            Name = "曲げ",
            Type = PmxMorphType.Bone,
            BoneOffsets = [new PmxBoneOffset(root, new Vector3(0, 0, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2))],
        });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[0] = 0.5f;

        PoseState state = Evaluate(rig, input);

        AssertEx.AreClose(new Vector3(0, 0, 1), state.BonePosition(root));
        AssertEx.AreClose(new Vector3(MathF.Cos(MathF.PI / 4), MathF.Sin(MathF.PI / 4), 1), state.BonePosition(tip));
    }

    [TestMethod]
    public void GroupMorphDrivesChildMorphs()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        int smile = b.Morph(new PmxMorph { Name = "笑い", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, new Vector3(0, 1, 0))] });
        b.Morph(new PmxMorph { Name = "まとめ", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(smile, 0.5f)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[smile] = 0.2f;
        input.MorphWeights[1] = 1f;

        PoseState state = Evaluate(rig, input);

        Assert.AreEqual(0.7f, state.EffectiveMorphWeights[smile], 1e-6f);
        AssertEx.AreClose(new Vector3(0, 0.7f, 0), state.VertexOffsets[0]);
    }

    [TestMethod]
    public void NestedAndSelfReferencingGroupsAreIgnored()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        int vertexMorph = b.Morph(new PmxMorph { Name = "頂点", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitX)] });
        int inner = b.Morph(new PmxMorph { Name = "内", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(vertexMorph, 1)] });
        b.Morph(new PmxMorph { Name = "外", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(inner, 1), new PmxGroupOffset(2, 1)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[2] = 1f;

        PoseState state = Evaluate(rig, input);

        Assert.AreEqual(0f, state.EffectiveMorphWeights[vertexMorph]);
        AssertEx.AreClose(Vector3.Zero, state.VertexOffsets[0]);
    }

    [TestMethod]
    public void FlipIndexSplitsTheValueIntoCountPlusOneParts()
    {
        // 選び先 3 つ: 0〜0.25 未満は「どれも選ばない」、その後 0.25 ずつ 1 つめ・2 つめ・3 つめ
        Assert.AreEqual(-1, PoseEvaluator.FlipIndex(3, 0f));
        Assert.AreEqual(-1, PoseEvaluator.FlipIndex(3, 0.24f));
        Assert.AreEqual(0, PoseEvaluator.FlipIndex(3, 0.25f));
        Assert.AreEqual(1, PoseEvaluator.FlipIndex(3, 0.5f));
        Assert.AreEqual(2, PoseEvaluator.FlipIndex(3, 0.75f));
        Assert.AreEqual(2, PoseEvaluator.FlipIndex(3, 1f), "1 は最後");
        Assert.AreEqual(-1, PoseEvaluator.FlipIndex(3, -0.5f), "範囲の外は端に寄せる");
        Assert.AreEqual(2, PoseEvaluator.FlipIndex(3, 5f));
        Assert.AreEqual(-1, PoseEvaluator.FlipIndex(3, float.NaN));
        Assert.AreEqual(-1, PoseEvaluator.FlipIndex(0, 1f), "選び先が無い");
    }

    [TestMethod]
    public void FlipMorphOverwritesOnlyTheSelectedMorph()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        int a = b.Morph(new PmxMorph { Name = "A", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitX)] });
        int c = b.Morph(new PmxMorph { Name = "B", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitY)] });
        int flip = b.Morph(new PmxMorph { Name = "切り替え", Type = PmxMorphType.Flip, GroupOffsets = [new PmxGroupOffset(a, 1), new PmxGroupOffset(c, 0.5f)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[a] = 0.3f;
        input.MorphWeights[c] = 0.2f;

        input.MorphWeights[flip] = 0.5f; // 3 つに分けた真ん中 → 1 つめ（A）
        PoseState state = Evaluate(rig, input);
        Assert.AreEqual(1f, state.EffectiveMorphWeights[a], 1e-6f, "掛けたり足したりせず、登録の値で書き換える");
        Assert.AreEqual(0.2f, state.EffectiveMorphWeights[c], 1e-6f, "選ばなかったものはそのまま");
        AssertEx.AreClose(new Vector3(1, 0.2f, 0), state.VertexOffsets[0]);

        input.MorphWeights[flip] = 1f; // → 2 つめ（B）
        state = Evaluate(rig, input);
        Assert.AreEqual(0.3f, state.EffectiveMorphWeights[a], 1e-6f);
        Assert.AreEqual(0.5f, state.EffectiveMorphWeights[c], 1e-6f);

        input.MorphWeights[flip] = 0.1f; // どれも選ばない
        state = Evaluate(rig, input);
        Assert.AreEqual(0.3f, state.EffectiveMorphWeights[a], 1e-6f);
        Assert.AreEqual(0.2f, state.EffectiveMorphWeights[c], 1e-6f);
    }

    [TestMethod]
    public void FlipMorphCanSwitchGroupMorphs()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        int a = b.Morph(new PmxMorph { Name = "A", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitX)] });
        int c = b.Morph(new PmxMorph { Name = "B", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitY)] });
        int groupA = b.Morph(new PmxMorph { Name = "表情A", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(a, 1)] });
        int groupB = b.Morph(new PmxMorph { Name = "表情B", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(c, 1)] });
        int flip = b.Morph(new PmxMorph { Name = "切り替え", Type = PmxMorphType.Flip, GroupOffsets = [new PmxGroupOffset(groupA, 1), new PmxGroupOffset(groupB, 1)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);

        input.MorphWeights[flip] = 0.4f;
        PoseState state = Evaluate(rig, input);
        AssertEx.AreClose(Vector3.UnitX, state.VertexOffsets[0], message: "表情A");

        input.MorphWeights[flip] = 0.9f;
        state = Evaluate(rig, input);
        AssertEx.AreClose(Vector3.UnitY, state.VertexOffsets[0], message: "表情B");
    }

    [TestMethod]
    public void GroupMorphDrivesFlipMorphsInsideIt()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        int a = b.Morph(new PmxMorph { Name = "A", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitX)] });
        int c = b.Morph(new PmxMorph { Name = "B", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitY)] });
        int flip = b.Morph(new PmxMorph { Name = "切り替え", Type = PmxMorphType.Flip, GroupOffsets = [new PmxGroupOffset(a, 1), new PmxGroupOffset(c, 1)] });
        int group = b.Morph(new PmxMorph { Name = "まとめ", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(flip, 0.5f), new PmxGroupOffset(c, 0.25f)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[group] = 1f;

        PoseState state = Evaluate(rig, input);

        // 切り替えには 1 × 0.5 = 0.5 が渡って A を選ぶ。B には普通に 0.25 を足す
        Assert.AreEqual(1f, state.EffectiveMorphWeights[a], 1e-6f);
        Assert.AreEqual(0.25f, state.EffectiveMorphWeights[c], 1e-6f);
        Assert.AreEqual(0f, state.EffectiveMorphWeights[flip], "フリップモーフ自身の値には足さない");
    }

    [TestMethod]
    public void FlipMorphsRunInIndexOrder()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        int a = b.Morph(new PmxMorph { Name = "A", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitX)] });
        int c = b.Morph(new PmxMorph { Name = "B", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(0, Vector3.UnitY)] });
        int first = b.Morph(new PmxMorph { Name = "先", Type = PmxMorphType.Flip, GroupOffsets = [new PmxGroupOffset(3, 1)] }); // 後のフリップを 1 にする
        int second = b.Morph(new PmxMorph { Name = "後", Type = PmxMorphType.Flip, GroupOffsets = [new PmxGroupOffset(a, 1), new PmxGroupOffset(c, 1)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[first] = 1f;

        PoseState state = Evaluate(rig, input);

        Assert.AreEqual(1f, state.EffectiveMorphWeights[second]);
        Assert.AreEqual(0f, state.EffectiveMorphWeights[a]);
        Assert.AreEqual(1f, state.EffectiveMorphWeights[c], "書き換えた値で、後のフリップが最後の選び先を選ぶ");
    }

    [TestMethod]
    public void UvMorphMovesTextureCoordinates()
    {
        var b = new TestRigBuilder();
        b.Bone("根元", Vector3.Zero);
        b.Vertex(Vector3.Zero);
        b.Vertex(Vector3.UnitX);
        b.Morph(new PmxMorph { Name = "目線", Type = PmxMorphType.Uv, UvOffsets = [new PmxUvOffset(1, new Vector4(0.25f, -0.5f, 9, 9))] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[0] = 0.5f;

        PoseState state = Evaluate(rig, input);

        Assert.AreEqual(Vector2.Zero, state.UvOffsets[0]);
        Assert.AreEqual(new Vector2(0.125f, -0.25f), state.UvOffsets[1]);
    }

    [TestMethod]
    public void MaterialMorphMultipliesThenAdds()
    {
        var b = new TestRigBuilder();
        b.Material(Vector4.One);
        b.Material(new Vector4(0.2f, 0.2f, 0.2f, 1));
        PmxMaterialOffset Offset(int material, PmxMaterialOperation op, Vector4 diffuse, Vector4 tint) =>
            new(material, op, diffuse, op == PmxMaterialOperation.Add ? Vector3.Zero : Vector3.One, op == PmxMaterialOperation.Add ? 0 : 1,
                op == PmxMaterialOperation.Add ? Vector3.Zero : Vector3.One, Vector4.One, 1, tint, Vector4.One, Vector4.One);
        // 加算を先に並べても、乗算を先に計算する
        b.Morph(new PmxMorph { Name = "足す", Type = PmxMorphType.Material, MaterialOffsets = [Offset(0, PmxMaterialOperation.Add, new Vector4(0.1f, 0, 0, 0), new Vector4(0.2f, 0, 0, 0))] });
        b.Morph(new PmxMorph { Name = "掛ける", Type = PmxMorphType.Material, MaterialOffsets = [Offset(-1, PmxMaterialOperation.Multiply, new Vector4(0.5f, 0.5f, 0.5f, 1), new Vector4(0, 0, 0, 1))] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[0] = 1;
        input.MorphWeights[1] = 1;

        PoseState state = Evaluate(rig, input);

        Assert.AreEqual(0.6f, state.Materials[0].Diffuse.X, 1e-6f);
        Assert.AreEqual(0.5f, state.Materials[0].Diffuse.Y, 1e-6f);
        Assert.AreEqual(0.1f, state.Materials[1].Diffuse.X, 1e-6f); // 「すべての材質」(-1) の乗算は 2 つめにも効く
        Assert.AreEqual(new Vector4(0, 0, 0, 1), state.Materials[0].TextureMultiply);
        Assert.AreEqual(new Vector4(0.2f, 0, 0, 0), state.Materials[0].TextureAdd);

        input.MorphWeights[1] = 0.5f;
        PoseEvaluator.Evaluate(rig, input, state);
        Assert.AreEqual(new Vector4(0.5f, 0.5f, 0.5f, 1), state.Materials[0].TextureMultiply);
    }

    [TestMethod]
    public void MaterialMorphChangesEdgeSphereAndToon()
    {
        var b = new TestRigBuilder();
        b.Material(Vector4.One, edgeSize: 1, edgeColor: new Vector4(0, 0, 0, 1));
        b.Morph(new PmxMorph
        {
            Name = "太く", Type = PmxMorphType.Material,
            MaterialOffsets = [new PmxMaterialOffset(0, PmxMaterialOperation.Multiply, Vector4.One, Vector3.One, 1, Vector3.One,
                new Vector4(1, 1, 1, 0.5f), 3, Vector4.One, new Vector4(0.5f), new Vector4(0, 0, 0, 1))],
        });
        b.Morph(new PmxMorph
        {
            Name = "色", Type = PmxMorphType.Material,
            MaterialOffsets = [new PmxMaterialOffset(0, PmxMaterialOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero,
                new Vector4(0.2f, 0, 0, 0), 0.5f, Vector4.Zero, new Vector4(0.1f, 0, 0, 0), new Vector4(0, 0.3f, 0, 0))],
        });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);

        PoseState state = Evaluate(rig, input);
        Assert.AreEqual(1f, state.Materials[0].EdgeSize, "モーフが 0 なら材質のまま");
        Assert.AreEqual(new Vector4(0, 0, 0, 1), state.Materials[0].EdgeColor);
        Assert.AreEqual(Vector4.One, state.Materials[0].SphereMultiply);
        Assert.AreEqual(Vector4.Zero, state.Materials[0].ToonAdd);

        input.MorphWeights[0] = 0.5f;
        input.MorphWeights[1] = 1f;
        PoseEvaluator.Evaluate(rig, input, state);
        MaterialValues v = state.Materials[0];
        Assert.AreEqual(2.5f, v.EdgeSize, 1e-6f, "1 × (1 + (3 − 1) × 0.5) + 0.5");
        AssertEx.AreClose(new Vector3(0.2f, 0, 0), new Vector3(v.EdgeColor.X, v.EdgeColor.Y, v.EdgeColor.Z));
        Assert.AreEqual(0.75f, v.EdgeColor.W, 1e-6f, "不透明度も掛ける");
        Assert.AreEqual(new Vector4(0.75f), v.SphereMultiply);
        Assert.AreEqual(new Vector4(0.1f, 0, 0, 0), v.SphereAdd);
        Assert.AreEqual(new Vector4(0.5f, 0.5f, 0.5f, 1), v.ToonMultiply);
        Assert.AreEqual(new Vector4(0, 0.3f, 0, 0), v.ToonAdd);
    }

    [TestMethod]
    public void BrokenReferencesDoNotStopEvaluation()
    {
        var b = new TestRigBuilder();
        b.Bone("輪A", Vector3.Zero, parent: 1);
        b.Bone("輪B", Vector3.One, parent: 0);
        b.Bone("範囲外の親", Vector3.UnitX, parent: 99, flags: PmxBoneFlags.Rotatable | PmxBoneFlags.InheritRotation, inheritParent: 42, inheritWeight: 1);
        b.Bone("壊れた IK", Vector3.UnitY, flags: PmxBoneFlags.IK, ikTarget: 77, ikLoop: 10, ikLinks: [new PmxIkLink { Bone = 55 }]);
        b.Vertex(new PmxVertex { Position = Vector3.One, Normal = Vector3.Zero, WeightType = PmxWeightType.Bdef4, Bone1 = 99, Bone2 = 1, Bone3 = -1, Bone4 = -1, Weights = new Vector4(0.5f, 0.5f, 0, 0) });
        b.Vertex(new PmxVertex { Position = Vector3.One, WeightType = PmxWeightType.Sdef, Bone1 = 0, Bone2 = 99, Weights = new Vector4(0.5f, 0.5f, 0, 0) });
        b.Morph(new PmxMorph { Name = "壊れた", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(999, Vector3.One)] });
        b.Morph(new PmxMorph { Name = "壊れたボーン", Type = PmxMorphType.Bone, BoneOffsets = [new PmxBoneOffset(999, Vector3.One, Quaternion.Identity)] });
        ModelRig rig = b.Build();
        var input = new PoseInput(rig);
        input.MorphWeights[0] = input.MorphWeights[1] = 1;
        input.Rotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1);

        PoseState state = Evaluate(rig, input);
        var vertices = new SkinnedVertex[rig.RestPositions.Length];
        Skinner.Apply(rig, state, vertices, out _, out _);

        Assert.IsTrue(rig.Bones.All(bone => bone.Parent == -1 || rig.Bones[bone.Parent].Parent != bone.Index), "親子の輪が残っている");
        Assert.IsFalse(rig.Bones[3].IsIk);
        Assert.AreEqual(PmxWeightType.Bdef2, rig.Skin[1].Type);
        foreach (SkinnedVertex v in vertices)
        {
            AssertEx.Finite(v.Position);
            AssertEx.Finite(v.Normal);
        }
    }

    [TestMethod]
    public void DeformOrderPutsAfterPhysicsLastThenLayer()
    {
        var b = new TestRigBuilder();
        b.Bone("物理後", Vector3.Zero, flags: PmxBoneFlags.Rotatable | PmxBoneFlags.AfterPhysics);
        b.Bone("階層2", Vector3.Zero, layer: 2);
        b.Bone("階層0", Vector3.Zero);
        b.Bone("階層1", Vector3.Zero, layer: 1);
        ModelRig rig = b.Build();

        CollectionAssert.AreEqual(new[] { 2, 3, 1, 0 }, rig.DeformOrder);
    }

    private static bool Matrix4x4Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                if (MathF.Abs(a[r, c] - b[r, c]) > 1e-4f)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
