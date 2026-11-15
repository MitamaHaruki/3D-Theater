using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Theater3D.Formats.Gltf;
using Theater3D.Formats.Images;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

/// <summary>VRM・glTF を、PMX と同じ中身にする（設計判断記録 82 章）。</summary>
[TestClass]
public sealed class GltfReaderTests
{
    private const float Scale = 12.5f;

    [TestMethod]
    public void Vrm1IsConvertedToMmdSizeAndDirection()
    {
        PmxLoadResult result = GltfReader.Read(SkinnedModel(Vrm1Extensions()), name: "テスト");
        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        PmxDocument d = result.Document!;

        // ボーン: 全ての親を足し、変形に使うノードとその親（Body は形だけなのでボーンにしない）
        CollectionAssert.AreEqual(new[] { "全ての親", "Root", "Hips", "Head" }, d.Bones.Select(b => b.Name).ToArray());
        CollectionAssert.AreEqual(new[] { -1, 0, 1, 2 }, d.Bones.Select(b => b.ParentIndex).ToArray());
        AssertNear(new Vector3(0, 1 * Scale, 0), d.Bones[2].Position);
        AssertNear(new Vector3(0, 1.5f * Scale, 0), d.Bones[3].Position);
        Assert.IsTrue(d.Bones[1].Has(PmxBoneFlags.Translatable), "いちばん上のボーンは動かせる");
        Assert.AreEqual(2, d.Bones[1].TailBone, "先端は初めの子");

        // 位置: 1 m を 12.5 に、VRM 1.0 は Z を裏返す（正面を -Z に）
        AssertNear(new Vector3(0, 1 * Scale, 0), d.Vertices[0].Position);
        AssertNear(new Vector3(1 * Scale, 1 * Scale, 0), d.Vertices[1].Position);
        AssertNear(new Vector3(0, 1.5f * Scale, -0.5f * Scale), d.Vertices[2].Position);
        AssertNear(new Vector3(0, 0, -1), d.Vertices[0].Normal);
        Assert.AreEqual(new Vector2(0, 1), d.Vertices[2].Uv);
        CollectionAssert.AreEqual(new[] { 0, 2, 1 }, d.Indices, "裏返したので、面の頂点の順を逆にする");

        // 重み: 1 つは BDEF1、2 つは BDEF2（足して 1）
        Assert.AreEqual(PmxWeightType.Bdef1, d.Vertices[0].WeightType);
        Assert.AreEqual(2, d.Vertices[0].Bone1);
        Assert.AreEqual(PmxWeightType.Bdef2, d.Vertices[1].WeightType);
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, new[] { d.Vertices[1].Bone1, d.Vertices[1].Bone2 });
        Assert.AreEqual(0.5f, d.Vertices[1].Weights.X, 1e-6f);
        Assert.AreEqual(3, d.Vertices[2].Bone1);

        // 材質: 色は画面の色の値に直し、環境色は 0.4 倍。中に入っている画像はそのまま持つ
        PmxMaterial m = d.Materials.Single();
        Assert.AreEqual("Skin", m.Name);
        Assert.AreEqual(0.7354f, m.Diffuse.X, 1e-3f, "光の強さの 0.5 は、画面の色の値で 0.735");
        Assert.AreEqual(1f, m.Diffuse.W);
        AssertNear(new Vector3(m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z) * 0.4f, m.Ambient);
        Assert.IsTrue(m.Flags.HasFlag(PmxMaterialFlags.DoubleSided));
        Assert.AreEqual(3, m.IndexCount);
        Assert.AreEqual(0, m.TextureIndex);
        Assert.AreEqual("skin.png", d.Textures[0]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, d.EmbeddedTextures[0].ToArray());

        // 表情の元: 変わる所だけを書いた並び（sparse）を読み、向きと大きさを合わせる
        PmxMorph blink = d.Morphs.Single();
        Assert.AreEqual("blink", blink.Name);
        Assert.AreEqual(PmxMorphType.Vertex, blink.Type);
        Assert.AreEqual(2, blink.VertexOffsets.Single().Vertex);
        AssertNear(new Vector3(0, 0, -0.1f * Scale), blink.VertexOffsets.Single().Translation);

        StringAssert.Contains(d.Comment, "VRM 1.0");
        StringAssert.Contains(d.Comment, "名前: Test");
        StringAssert.Contains(d.Comment, "アバターとして使える人: everyone");
        Assert.AreEqual("Test", d.NameUniversal);
        Assert.AreEqual("テスト", d.Name);
        Assert.IsFalse(result.Report.Issues.Any(i => i.Severity == LoadSeverity.Error), "エラーは無い");
        // この VRM には人型のボーンの決まりが無いので、その注意だけ出る（ボーンの名前は元のまま）
        StringAssert.Contains(result.Report.Issues.Single(i => i.Severity == LoadSeverity.Warning).Message, "人型のボーンの決まり");
    }

    [TestMethod]
    public void HumanoidBonesGetMmdNamesIkAndAPose()
    {
        PmxLoadResult result = GltfReader.Read(HumanoidModel(), name: "人型");
        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        PmxDocument d = result.Document!;
        int Bone(string name)
        {
            int index = Array.FindIndex(d.Bones, b => b.Name == name);
            Assert.IsGreaterThanOrEqualTo(0, index, $"ボーン「{name}」が無い");
            return index;
        }
        string ParentName(string name) => d.Bones[d.Bones[Bone(name)].ParentIndex].Name;

        // MMD の体の中心: 全ての親 → センター → グルーブ → 下半身・上半身（上半身は下半身の子にしない）
        Assert.AreEqual("全ての親", ParentName("センター"));
        Assert.AreEqual("センター", ParentName("グルーブ"));
        Assert.AreEqual("グルーブ", ParentName("下半身"));
        Assert.AreEqual("グルーブ", ParentName("上半身"));
        Assert.AreEqual("上半身", ParentName("上半身2"));
        Assert.AreEqual("首", ParentName("頭"));
        Assert.AreEqual("下半身", ParentName("左足"));
        Assert.AreEqual("左足", ParentName("左ひざ"));
        Assert.AreEqual("左肩", ParentName("左腕"));
        Assert.AreEqual(12.5f * CenterRatio, d.Bones[Bone("センター")].Position.Y, 1e-3f, "センターは腰の高さの 0.69 倍");
        Assert.AreEqual("RightToes", d.Bones[Bone("右足先EX")].NameUniversal, "元のノードの名前は、英語の名前に残す");
        for (int i = 0; i < d.Bones.Length; i++)
        {
            Assert.IsLessThan(i, d.Bones[i].ParentIndex, $"親が先（{d.Bones[i].Name}）");
        }

        // 両目: 頭の子で、左目・右目より先。目は両目の回転を受け取る
        Assert.AreEqual("頭", ParentName("両目"));
        Assert.IsLessThan(Bone("左目"), Bone("両目"));
        PmxBone leftEye = d.Bones[Bone("左目")];
        Assert.AreEqual(Bone("両目"), leftEye.InheritParent);
        Assert.AreEqual(1f, leftEye.InheritWeight);
        Assert.IsTrue(leftEye.Has(PmxBoneFlags.InheritRotation));

        // 足の IK（MMD の標準と同じ）
        PmxBone legIk = d.Bones[Bone("左足ＩＫ")];
        Assert.AreEqual("全ての親", d.Bones[legIk.ParentIndex].Name);
        Assert.IsTrue(legIk.Has(PmxBoneFlags.IK) && legIk.Has(PmxBoneFlags.Translatable));
        Assert.AreEqual(Bone("左足首"), legIk.IkTarget);
        Assert.AreEqual(40, legIk.IkLoopCount);
        Assert.AreEqual(2f, legIk.IkLimitAngle);
        CollectionAssert.AreEqual(new[] { Bone("左ひざ"), Bone("左足") }, legIk.IkLinks.Select(l => l.Bone).ToArray());
        Assert.IsTrue(legIk.IkLinks[0].HasLimits, "ひざは後ろにだけ曲がる");
        Assert.AreEqual(-MathF.PI, legIk.IkLinks[0].MinAngle.X, 1e-5f);
        Assert.AreEqual(-0.5f * MathF.PI / 180f, legIk.IkLinks[0].MaxAngle.X, 1e-6f);
        AssertNear(d.Bones[Bone("左足首")].Position, legIk.Position);
        PmxBone toeIk = d.Bones[Bone("左つま先ＩＫ")];
        Assert.AreEqual(Bone("左足ＩＫ"), toeIk.ParentIndex);
        Assert.AreEqual(Bone("左つま先"), toeIk.IkTarget);
        Assert.AreEqual(3, toeIk.IkLoopCount);
        Assert.AreEqual("左足首", ParentName("左つま先"));
        Assert.IsLessThan(d.Bones[Bone("左足首")].Position.Z, d.Bones[Bone("左つま先")].Position.Z, "つま先が無ければ、足首の前（-Z）に先端を置く");
        AssertNear(d.Bones[Bone("右足先EX")].Position, d.Bones[Bone("右つま先")].Position);
        Assert.IsGreaterThan(Bone("右つま先"), Bone("左足ＩＫ"), "IK は、ほかのボーンの後");

        // 腕を A の字に: 左腕（+X）から左ひじへの向きが、水平から 37 度下がる。左ひじに付いた頂点も一緒に動く
        Vector3 shoulder = d.Bones[Bone("左腕")].Position;
        Vector3 elbow = d.Bones[Bone("左ひじ")].Position;
        AssertNear(new Vector3(0.15f, 1.45f, 0) * Scale, shoulder);
        Assert.AreEqual(37f, MathF.Atan2(shoulder.Y - elbow.Y, elbow.X - shoulder.X) * 180f / MathF.PI, 0.01f);
        Assert.AreEqual(0.25f * Scale, Vector3.Distance(shoulder, elbow), 1e-3f, "長さは変えない");
        AssertNear(elbow, d.Vertices[0].Position);
        Vector3 rightShoulder = d.Bones[Bone("右腕")].Position;
        Vector3 rightElbow = d.Bones[Bone("右ひじ")].Position;
        Assert.IsLessThan(0f, rightElbow.X, "右腕は -X");
        Assert.AreEqual(37f, MathF.Atan2(rightShoulder.Y - rightElbow.Y, rightShoulder.X - rightElbow.X) * 180f / MathF.PI, 0.01f);
        AssertNear(new Vector3(0, 1 * Scale, 0), d.Vertices[1].Position, "腰の頂点は動かない");

        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("A の字", StringComparison.Ordinal)));
        Assert.IsFalse(result.Report.Issues.Any(i => i.Severity != LoadSeverity.Info));
    }

    [TestMethod]
    public void VrmBoneNamesFollowMmd()
    {
        Assert.AreEqual("下半身", VrmHumanoid.MmdName("hips", VrmVersion.Vrm1));
        Assert.AreEqual("上半身2", VrmHumanoid.MmdName("chest", VrmVersion.Vrm1));
        Assert.AreEqual("右ひじ", VrmHumanoid.MmdName("rightLowerArm", VrmVersion.Vrm1));
        Assert.AreEqual("左人指３", VrmHumanoid.MmdName("leftIndexDistal", VrmVersion.Vrm1));
        // 親指: 1.0 は Metacarpal・Proximal・Distal、0.x は Proximal・Intermediate・Distal。どちらも根元から ０・１・２
        Assert.AreEqual("左親指０", VrmHumanoid.MmdName("leftThumbMetacarpal", VrmVersion.Vrm1));
        Assert.AreEqual("左親指１", VrmHumanoid.MmdName("leftThumbProximal", VrmVersion.Vrm1));
        Assert.AreEqual("左親指０", VrmHumanoid.MmdName("leftThumbProximal", VrmVersion.Vrm0));
        Assert.AreEqual("左親指１", VrmHumanoid.MmdName("leftThumbIntermediate", VrmVersion.Vrm0));
        Assert.AreEqual("左親指２", VrmHumanoid.MmdName("leftThumbDistal", VrmVersion.Vrm0));
        Assert.AreEqual("右足先EX", VrmHumanoid.MmdName("rightToes", VrmVersion.Vrm0));
        Assert.IsNull(VrmHumanoid.MmdName("jaw", VrmVersion.Vrm1));
    }

    private const float CenterRatio = 0.69f;

    // 人型のボーン（VRM 1.0）。腕は T の字。頂点は左ひじ・腰・頭に 1 つずつ
    private static byte[] HumanoidModel()
    {
        var nodes = new List<(string Name, string? Human, float[] T, int[] Children)>
        {
            ("Body", null, [0, 0, 0], []),                       // 0
            ("Hips", "hips", [0, 1, 0], [2, 7, 10]),            // 1
            ("Spine", "spine", [0, 0.1f, 0], [3]),              // 2
            ("Chest", "chest", [0, 0.2f, 0], [4, 13, 16]),      // 3
            ("Neck", "neck", [0, 0.2f, 0], [5]),                // 4
            ("Head", "head", [0, 0.1f, 0], [6, 19]),            // 5
            ("LeftEye", "leftEye", [0.03f, 0.05f, 0.08f], []),  // 6
            ("LeftUpperLeg", "leftUpperLeg", [0.1f, -0.05f, 0], [8]),   // 7
            ("LeftLowerLeg", "leftLowerLeg", [0, -0.45f, 0], [9]),      // 8
            ("LeftFoot", "leftFoot", [0, -0.45f, 0], []),               // 9
            ("RightUpperLeg", "rightUpperLeg", [-0.1f, -0.05f, 0], [11]), // 10
            ("RightLowerLeg", "rightLowerLeg", [0, -0.45f, 0], [12]),     // 11
            ("RightFoot", "rightFoot", [0, -0.45f, 0], [20]),             // 12
            ("LeftShoulder", "leftShoulder", [0.05f, 0.15f, 0], [14]),    // 13
            ("LeftUpperArm", "leftUpperArm", [0.1f, 0, 0], [15]),         // 14
            ("LeftLowerArm", "leftLowerArm", [0.25f, 0, 0], []),          // 15
            ("RightShoulder", "rightShoulder", [-0.05f, 0.15f, 0], [17]), // 16
            ("RightUpperArm", "rightUpperArm", [-0.1f, 0, 0], [18]),      // 17
            ("RightLowerArm", "rightLowerArm", [-0.25f, 0, 0], []),       // 18
            ("RightEye", "rightEye", [-0.03f, 0.05f, 0.08f], []),         // 19
            ("RightToes", "rightToes", [0, -0.04f, 0.1f], []),            // 20
        };
        // ワールドの位置（回転は無いので、親の位置を足すだけ）
        var world = new Vector3[nodes.Count];
        var parent = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (int i = 0; i < nodes.Count; i++)
        {
            foreach (int c in nodes[i].Children)
            {
                parent[c] = i;
            }
        }
        for (int pass = 0; pass < nodes.Count; pass++)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var t = new Vector3(nodes[i].T[0], nodes[i].T[1], nodes[i].T[2]);
                world[i] = parent[i] >= 0 ? world[parent[i]] + t : t;
            }
        }
        int[] joints = [.. Enumerable.Range(1, nodes.Count - 1)];
        var inverseBind = new List<float>();
        foreach (int j in joints)
        {
            inverseBind.AddRange([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -world[j].X, -world[j].Y, -world[j].Z, 1]);
        }
        var b = new GlbBuilder();
        Vector3 elbow = world[15];
        int position = b.Floats([elbow.X, elbow.Y, elbow.Z, 0, 1, 0, 0, 1.6f, 0], "VEC3");
        // 関節の番号は joints の中の番号（ノード 15 は 14 番、ノード 1 は 0 番、ノード 5 は 4 番）
        int jointAccessor = b.UShorts([14, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0], "VEC4");
        int weights = b.Floats([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4");
        int ibm = b.Floats([.. inverseBind], "MAT4");
        var humanBones = nodes.Select((n, i) => (n.Human, i)).Where(x => x.Human is not null).ToDictionary(x => x.Human!, x => (object)new { node = x.i });
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0, 1 } } },
            ["nodes"] = nodes.Select((n, i) => i == 0
                ? (object)new { name = n.Name, mesh = 0, skin = 0 }
                : new { name = n.Name, translation = n.T, children = n.Children }).ToArray(),
            ["skins"] = new[] { new { joints, inverseBindMatrices = ibm } },
            ["meshes"] = new[]
            {
                new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = position, ["JOINTS_0"] = jointAccessor, ["WEIGHTS_0"] = weights } } } },
            },
            ["extensionsUsed"] = new[] { "VRMC_vrm" },
            ["extensions"] = new Dictionary<string, object>
            {
                ["VRMC_vrm"] = new { specVersion = "1.0", meta = new { name = "Human" }, humanoid = new { humanBones } },
            },
        };
        return b.Build(json);
    }

    [TestMethod]
    public void Vrm1ExpressionsBecomeMmdMorphs()
    {
        var expressions = new
        {
            preset = new Dictionary<string, object>
            {
                ["aa"] = new { morphTargetBinds = new[] { new { node = 0, index = 1, weight = 0.5f } } },
                ["blink"] = new { morphTargetBinds = new[] { new { node = 0, index = 0, weight = 1f } } },
                ["happy"] = new
                {
                    morphTargetBinds = new[] { new { node = 0, index = 0, weight = 1f } },
                    textureTransformBinds = new[] { new { material = 0, offset = new[] { 0.25f, 0 }, scale = new[] { 1f, 1 } } },
                    isBinary = true,
                },
                ["angry"] = new { materialColorBinds = new[] { new { material = 0, type = "color", targetValue = new[] { 1f, 0, 0, 1 } } } },
                ["lookLeft"] = new { morphTargetBinds = new[] { new { node = 0, index = 1, weight = 1f } } },
                ["neutral"] = new { },
            },
            custom = new Dictionary<string, object> { ["Wink2"] = new { morphTargetBinds = new[] { new { node = 0, index = 0, weight = 1f } } } },
        };
        var extensions = new Dictionary<string, object>
        {
            ["VRMC_vrm"] = new { specVersion = "1.0", meta = new { name = "Face" }, expressions, lookAt = new { type = "expression" } },
        };
        PmxLoadResult result = GltfReader.Read(FaceModel(extensions));
        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;

        // 並び: 決まった表情（MMD の表情の欄の順）→ 自分で作った表情 → 部品 → 元の形の変化。中身の無い neutral は作らない
        CollectionAssert.AreEqual(
            new[] { "まばたき", "あ", "笑い", "怒り", "視線左", "Wink2", "笑い（形）", "笑い（UV）", "Fcl_Blink", "Fcl_A" },
            d.Morphs.Select(m => m.Name).ToArray());
        PmxMorph Morph(string name) => d.Morphs.Single(m => m.Name == name);
        Assert.AreEqual(PmxMorphPanel.Eye, Morph("まばたき").Panel);
        Assert.AreEqual(PmxMorphPanel.Mouth, Morph("あ").Panel);
        Assert.AreEqual(PmxMorphPanel.Eyebrow, Morph("怒り").Panel);

        // 形: 指した表情の元を強さで混ぜる
        PmxMorph a = Morph("あ");
        PmxMorph rawA = Morph("Fcl_A");
        Assert.AreEqual(PmxMorphType.Vertex, a.Type);
        Assert.HasCount(rawA.VertexOffsets.Count, a.VertexOffsets);
        AssertNear(rawA.VertexOffsets[0].Translation * 0.5f, a.VertexOffsets.Single(o => o.Vertex == rawA.VertexOffsets[0].Vertex).Translation);

        // 2 種類（形と UV）を持つ表情は、部品を混ぜるグループ
        PmxMorph happy = Morph("笑い");
        Assert.AreEqual(PmxMorphType.Group, happy.Type);
        CollectionAssert.AreEqual(new[] { 6, 7 }, happy.GroupOffsets.Select(g => g.Morph).ToArray());
        PmxMorph happyUv = Morph("笑い（UV）");
        Assert.AreEqual(PmxMorphType.Uv, happyUv.Type);
        Assert.HasCount(3, happyUv.UvOffsets, "材質 0 を使う頂点すべて");
        Assert.AreEqual(new Vector4(0.25f, 0, 0, 0), happyUv.UvOffsets[0].Value);

        // 材質の色: 目標の色との差を足す（拡散色は白なので、赤にするには緑と青を 1 引く）
        PmxMorph angry = Morph("怒り");
        Assert.AreEqual(PmxMorphType.Material, angry.Type);
        PmxMaterialOffset color = angry.MaterialOffsets.Single();
        Assert.AreEqual(PmxMaterialOperation.Add, color.Operation);
        Assert.IsLessThan(1e-5f, Vector4.Distance(new Vector4(0, -1, -1, 0), color.Diffuse), $"実際 {color.Diffuse}");

        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("isBinary", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vrm0BlendShapesBecomeMmdMorphs()
    {
        var extensions = new Dictionary<string, object>
        {
            ["VRM"] = new
            {
                meta = new { title = "Face0" },
                blendShapeMaster = new
                {
                    blendShapeGroups = new object[]
                    {
                        new { presetName = "a", name = "A", binds = new[] { new { mesh = 0, index = 1, weight = 100f } } },
                        new { presetName = "blink_l", name = "Blink_L", binds = new[] { new { mesh = 0, index = 0, weight = 50f } } },
                        new
                        {
                            presetName = "unknown",
                            name = "Scroll",
                            materialValues = new[] { new { materialName = "Face", propertyName = "_MainTex_ST", targetValue = new[] { 1f, 1, 0.5f, 0.1f } } },
                        },
                    },
                },
            },
        };
        PmxDocument d = GltfReader.Read(FaceModel(extensions)).Document!;
        PmxMorph Morph(string name) => d.Morphs.Single(m => m.Name == name);
        CollectionAssert.AreEqual(new[] { "ウィンク", "あ", "Scroll" }, d.Morphs.Take(3).Select(m => m.Name).ToArray());
        // 0.x の強さは 0〜100
        AssertNear(Morph("Fcl_A").VertexOffsets[0].Translation, Morph("あ").VertexOffsets[0].Translation);
        AssertNear(Morph("Fcl_Blink").VertexOffsets[0].Translation * 0.5f, Morph("ウィンク").VertexOffsets[0].Translation);
        // Unity の決まり（V が上向き）のずらす量を glTF へ: v のずらす量は 1 - 大きさ - ずらす量 = -0.1
        PmxUvOffset scroll = Morph("Scroll").UvOffsets[0];
        Assert.AreEqual(0.5f, scroll.Value.X, 1e-6f);
        Assert.AreEqual(-0.1f, scroll.Value.Y, 1e-6f);
    }

    // 顔の形（三角形 1 枚・材質 Face）と、表情の元 2 つ（Fcl_Blink は頂点 2 を、Fcl_A は頂点 0 を動かす）
    private static byte[] FaceModel(Dictionary<string, object> extensions)
    {
        var b = new GlbBuilder();
        int position = b.Floats([0, 1, 0, 1, 1, 0, 0, 1.5f, 0], "VEC3");
        int uv = b.Floats([0, 0, 1, 0, 0, 1], "VEC2");
        int blink = b.Floats([0, 0, 0, 0, 0, 0, 0, -0.01f, 0], "VEC3");
        int open = b.Floats([0, -0.02f, 0, 0, 0, 0, 0, 0, 0], "VEC3");
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = new object[] { new { name = "Face", mesh = 0 } },
            ["meshes"] = new[]
            {
                new
                {
                    extras = new { targetNames = new[] { "Fcl_Blink", "Fcl_A" } },
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new Dictionary<string, int> { ["POSITION"] = position, ["TEXCOORD_0"] = uv },
                            material = 0,
                            targets = new[] { new Dictionary<string, int> { ["POSITION"] = blink }, new Dictionary<string, int> { ["POSITION"] = open } },
                        },
                    },
                },
            },
            ["materials"] = new[] { new { name = "Face" } },
            ["extensionsUsed"] = extensions.Keys.ToArray(),
            ["extensions"] = extensions,
        };
        return b.Build(json);
    }

    [TestMethod]
    public void Vrm1SpringBonesAreRead()
    {
        var extensions = new Dictionary<string, object>
        {
            ["VRMC_vrm"] = new { specVersion = "1.0", meta = new { name = "Hair" } },
            ["VRMC_springBone"] = new
            {
                specVersion = "1.0",
                colliders = new[] { new { node = 1, shape = new { sphere = new { offset = new[] { 0f, 0, 0.05f }, radius = 0.1f } } } },
                colliderGroups = new[] { new { colliders = new[] { 0 } } },
                springs = new[]
                {
                    new
                    {
                        name = "Hair",
                        colliderGroups = new[] { 0 },
                        joints = new object[]
                        {
                            new { node = 2, stiffness = 1.5f, gravityPower = 0.5f, gravityDir = new[] { 0f, -1, 0 }, dragForce = 0.4f, hitRadius = 0.02f },
                            new { node = 3, stiffness = 1f, gravityPower = 0f, gravityDir = new[] { 0f, -1, 0 }, dragForce = 0.3f, hitRadius = 0.01f },
                            new { node = 4 },
                        },
                    },
                },
            },
        };
        PmxLoadResult result = GltfReader.Read(SpringModel(extensions));
        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;
        SpringBoneSet springs = d.SpringBones!;
        Assert.AreEqual(Scale, springs.LengthScale);
        // 1.0 は、最後のジョイント（先端だけ）の手前まで揺れる
        Assert.HasCount(2, springs.Joints);
        SpringBoneJoint first = springs.Joints[0];
        Assert.AreEqual("Hair1", d.Bones[first.Bone].Name);
        // 先端は次のジョイント（Hair2 は頭から後ろ 0.1・下 0.1。1.0 は Z を裏返す）
        AssertNear(new Vector3(0, 1.4f, -0.1f) * Scale, first.Tail, "先端は次のジョイント");
        Assert.AreEqual(1.5f, first.Stiffness);
        Assert.AreEqual(0.5f, first.GravityPower);
        AssertNear(-Vector3.UnitY, first.GravityDirection);
        Assert.AreEqual(0.4f, first.DragForce, 1e-6f);
        Assert.AreEqual(0.02f * Scale, first.HitRadius, 1e-5f);
        Assert.AreEqual("Hair", first.Chain);
        CollectionAssert.AreEqual(new[] { 0 }, first.Colliders.ToArray());
        AssertNear(new Vector3(0, 1.3f, -0.1f) * Scale, springs.Joints[1].Tail);
        // 当たる物: 頭のボーンに付いた球（中心は頭から前へ 0.05。Z を裏返す）
        SpringBoneCollider ball = springs.Colliders.Single();
        Assert.AreEqual("Head", d.Bones[ball.Bone].Name);
        AssertNear(new Vector3(0, 1.5f, -0.05f) * Scale, ball.Center);
        Assert.AreEqual(0.1f * Scale, ball.Radius, 1e-5f);
        Assert.IsNull(ball.Tail);
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("スプリングボーン 1 組", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vrm0SpringBonesAreRead()
    {
        var extensions = new Dictionary<string, object>
        {
            ["VRM"] = new
            {
                meta = new { title = "Hair0" },
                secondaryAnimation = new
                {
                    boneGroups = new[]
                    {
                        new
                        {
                            comment = "hair",
                            stiffiness = 2f, // 0.x の綴り
                            gravityPower = 0f,
                            gravityDir = new { x = 0f, y = -1f, z = 0f },
                            dragForce = 0.7f,
                            hitRadius = 0.02f,
                            bones = new[] { 2 },
                            colliderGroups = new[] { 0 },
                        },
                    },
                    colliderGroups = new[] { new { node = 1, colliders = new[] { new { offset = new { x = 0f, y = 0f, z = 0.05f }, radius = 0.1f } } } },
                },
            },
        };
        PmxDocument d = GltfReader.Read(SpringModel(extensions)).Document!;
        SpringBoneSet springs = d.SpringBones!;
        // 0.x は、書かれたボーンから先のすべてのボーンが揺れる。子の無い末端（HairEnd）は、骨の向きに 7 cm 先が先端
        CollectionAssert.AreEqual(new[] { "Hair1", "Hair2", "HairEnd" }, springs.Joints.Select(j => d.Bones[j.Bone].Name).ToArray());
        Assert.AreEqual(2f, springs.Joints[0].Stiffness);
        Assert.AreEqual(0.7f, springs.Joints[0].DragForce, 1e-6f);
        // VRM 0.x は X を裏返す（Z はそのまま）
        AssertNear(new Vector3(0, 1.4f, 0.1f) * Scale, springs.Joints[0].Tail);
        AssertNear(new Vector3(0, 1.3f - 0.07f, 0.1f) * Scale, springs.Joints[2].Tail, "末端は 7 cm 先");
        AssertNear(new Vector3(0, 1.5f, 0.05f) * Scale, springs.Colliders.Single().Center);
    }

    // 頭（ボーン）に、後ろへ出て下へ垂れる髪の節（Hair1 → Hair2 → HairEnd）。形は頭と髪に付く
    private static byte[] SpringModel(Dictionary<string, object> extensions)
    {
        var b = new GlbBuilder();
        int position = b.Floats([0, 1.5f, 0, 0.1f, 1.5f, 0, 0, 1.4f, 0.1f], "VEC3");
        int joints = b.UShorts([0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0], "VEC4");
        int weights = b.Floats([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4");
        int ibm = b.Floats(
        [
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1.5f, 0, 1,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1.5f, -0.1f, 1,
        ], "MAT4");
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0, 1 } } },
            ["nodes"] = new object[]
            {
                new { name = "Body", mesh = 0, skin = 0 },
                new { name = "Head", translation = new[] { 0f, 1.5f, 0 }, children = new[] { 2 } },
                new { name = "Hair1", translation = new[] { 0f, 0, 0.1f }, children = new[] { 3 } },
                new { name = "Hair2", translation = new[] { 0f, -0.1f, 0 }, children = new[] { 4 } },
                new { name = "HairEnd", translation = new[] { 0f, -0.1f, 0 } },
            },
            ["skins"] = new[] { new { joints = new[] { 1, 2 }, inverseBindMatrices = ibm } },
            ["meshes"] = new[]
            {
                new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = position, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights } } } },
            },
            ["extensionsUsed"] = extensions.Keys.ToArray(),
            ["extensions"] = extensions,
        };
        return b.Build(json);
    }

    [TestMethod]
    public void ShadeToonFollowsMToon()
    {
        var ratio = new Vector3(0.5f, 0.6f, 0.7f);
        Vector3[] column = MToon.ToonColumn(ratio, shift: 0f, toony: 0.9f);
        Assert.HasCount(MToon.ToonHeight, column);
        AssertNear(Vector3.One, column[0], "上（光の当たる側）は白");
        AssertNear(ratio, column[^1], "下（影の側）は影の色 ÷ 材質の色");
        // くっきりさ 0.9: 光の当たり方 -0.1〜0.1 の間で白から影へ変わる（真ん中で半分）
        AssertNear(Vector3.Lerp(ratio, Vector3.One, 0.5f), Vector3.Lerp(column[MToon.ToonHeight / 2 - 1], column[MToon.ToonHeight / 2], 0.5f), "真ん中で半分");
        // 境目を -0.5 ずらすと、影の側が広がる（光の当たり方 0.5 で、もう半分）
        Vector3[] shifted = MToon.ToonColumn(ratio, shift: -0.5f, toony: 0.9f);
        AssertNear(Vector3.Lerp(ratio, Vector3.One, 0.5f), Vector3.Lerp(shifted[MToon.ToonHeight / 4 - 1], shifted[MToon.ToonHeight / 4], 0.5f), "境目がずれる");

        // TGA にしても、同じ色に読める（上が先）
        DecodedImage image = TgaDecoder.Decode(MToon.ToonTga(column));
        Assert.AreEqual(MToon.ToonWidth, image.Width);
        Assert.AreEqual(MToon.ToonHeight, image.Height);
        Assert.AreEqual(255, image.Bgra[2], "いちばん上の R");
        int last = (MToon.ToonHeight - 1) * image.Stride;
        Assert.AreEqual((byte)MathF.Round(0.5f * 255), image.Bgra[last + 2], "いちばん下の R");
        Assert.AreEqual((byte)MathF.Round(0.7f * 255), image.Bgra[last], "いちばん下の B");
    }

    [TestMethod]
    public void Vrm1MToonBecomesToonEdgeAndSphere()
    {
        var mtoon = new
        {
            specVersion = "1.0",
            shadeColorFactor = new[] { 0.5f, 0.5f, 1f },
            shadingShiftFactor = -0.1f,
            shadingToonyFactor = 0.9f,
            outlineWidthMode = "worldCoordinates",
            outlineWidthFactor = 0.0008f,
            outlineColorFactor = new[] { 1f, 0, 0 },
            outlineWidthMultiplyTexture = new { index = 0 },
            matcapFactor = new[] { 1f, 1, 1 },
            matcapTexture = new { index = 0 },
        };
        var extensions = new Dictionary<string, object> { ["VRMC_vrm"] = new { specVersion = "1.0", meta = new { name = "Toon" } } };
        PmxLoadResult result = GltfReader.Read(ToonModel(new { name = "Skin", extensions = new Dictionary<string, object> { ["VRMC_materials_mtoon"] = mtoon } }, extensions));
        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;
        PmxMaterial m = d.Materials.Single();
        // 影の色: トゥーン（ファイルの中の TGA）。いちばん下は影の色（画面の色の値）÷ 材質の色（白）
        Assert.IsFalse(m.SharedToon);
        Assert.AreEqual("mtoon_shade_1.tga", d.Textures[m.ToonIndex]);
        DecodedImage toon = TgaDecoder.Decode(d.EmbeddedTextures[m.ToonIndex]);
        int bottom = (toon.Height - 1) * toon.Stride;
        Assert.AreEqual((byte)MathF.Round(GltfConverter.LinearToSrgb(0.5f) * 255), toon.Bgra[bottom + 2], "影の R");
        Assert.AreEqual(255, toon.Bgra[bottom], "影の B");
        // 輪郭線: 0.8 mm → 太さ 1（1 mm で 1.25 画素）。色は画面の色の値。太さの画像は G を使う
        Assert.IsTrue(m.Flags.HasFlag(PmxMaterialFlags.Edge));
        Assert.AreEqual(1f, m.EdgeSize, 1e-4f);
        AssertNear(new Vector3(1, 0, 0), new Vector3(m.EdgeColor.X, m.EdgeColor.Y, m.EdgeColor.Z), "輪郭線の色");
        Assert.AreEqual(1f, m.EdgeColor.W);
        Assert.AreEqual(new EdgeWidthTexture(m.TextureIndex, 1), d.EdgeWidthTextures[0], "太さの画像（ここでは色の画像と同じもの）");
        // matcap → スフィア（足す）
        Assert.AreEqual(PmxSphereMode.Add, m.SphereMode);
        Assert.AreEqual(m.TextureIndex, m.SphereTextureIndex);
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("MToon", StringComparison.Ordinal) && i.Message.Contains("トゥーン", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Vrm0MToonBecomesToonAndEdge()
    {
        var extensions = new Dictionary<string, object>
        {
            ["VRM"] = new
            {
                meta = new { title = "Toon0" },
                materialProperties = new[]
                {
                    new
                    {
                        name = "Skin",
                        shader = "VRM/MToon",
                        floatProperties = new Dictionary<string, float> { ["_ShadeShift"] = 0f, ["_ShadeToony"] = 0.9f, ["_OutlineWidthMode"] = 1f, ["_OutlineWidth"] = 0.08f },
                        vectorProperties = new Dictionary<string, float[]> { ["_ShadeColor"] = [0.5f, 0.5f, 0.5f, 1], ["_OutlineColor"] = [0.2f, 0.2f, 0.2f, 1] },
                        textureProperties = new Dictionary<string, int> { ["_MainTex"] = 0, ["_SphereAdd"] = 0 },
                    },
                },
            },
        };
        PmxDocument d = GltfReader.Read(ToonModel(new { name = "Skin" }, extensions)).Document!;
        PmxMaterial m = d.Materials.Single();
        DecodedImage toon = TgaDecoder.Decode(d.EmbeddedTextures[m.ToonIndex]);
        // 0.x の色は、そのまま画面の色の値
        Assert.AreEqual((byte)MathF.Round(0.5f * 255), toon.Bgra[(toon.Height - 1) * toon.Stride + 2]);
        Assert.AreEqual(1f, m.EdgeSize, 1e-4f, "0.08 cm → 太さ 1");
        Assert.AreEqual(new Vector4(0.2f, 0.2f, 0.2f, 1), m.EdgeColor);
        Assert.AreEqual(PmxSphereMode.Add, m.SphereMode);
        Assert.IsEmpty(d.EdgeWidthTextures);
    }

    // 三角形 1 枚に、色の画像（材質の名前は Skin）。MToon の設定は material と extensions で渡す
    private static byte[] ToonModel(object material, Dictionary<string, object> extensions)
    {
        var b = new GlbBuilder();
        int position = b.Floats([0, 1, 0, 1, 1, 0, 0, 1.5f, 0], "VEC3");
        int uv = b.Floats([0, 0, 1, 0, 0, 1], "VEC2");
        int image = b.View([1, 2, 3, 4]);
        string materialJson = JsonSerializer.Serialize(material);
        var materialObject = JsonSerializer.Deserialize<Dictionary<string, object>>(materialJson)!;
        materialObject["pbrMetallicRoughness"] = new { baseColorTexture = new { index = 0 } };
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = new object[] { new { name = "Body", mesh = 0 } },
            ["meshes"] = new[] { new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = position, ["TEXCOORD_0"] = uv }, material = 0 } } } },
            ["materials"] = new[] { materialObject },
            ["textures"] = new[] { new { source = 0 } },
            ["images"] = new[] { new { bufferView = image, mimeType = "image/png", name = "skin" } },
            ["extensionsUsed"] = extensions.Keys.ToArray(),
            ["extensions"] = extensions,
        };
        return b.Build(json);
    }

    [TestMethod]
    public void Vrm0IsMirroredOnX()
    {
        var extensions = new Dictionary<string, object> { ["VRM"] = new { meta = new { title = "T0", author = "B" } } };
        PmxDocument d = GltfReader.Read(SkinnedModel(extensions)).Document!;
        // VRM 0.x は正面が glTF の -Z なので、X を裏返すと正面が -Z のまま左手系になる
        AssertNear(new Vector3(-1 * Scale, 1 * Scale, 0), d.Vertices[1].Position);
        AssertNear(new Vector3(0, 1.5f * Scale, 0.5f * Scale), d.Vertices[2].Position);
        AssertNear(new Vector3(0, 0, 1), d.Vertices[0].Normal);
        StringAssert.Contains(d.Comment, "VRM 0.x");
        StringAssert.Contains(d.Comment, "作者: B");
    }

    [TestMethod]
    public void RigidMeshKeepsItsNodeTransform()
    {
        var b = new GlbBuilder();
        int position = b.Floats([1, 0, 0, 2, 0, 0, 1, 1, 0], "VEC3");
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = new object[] { new { name = "Prop", mesh = 0, translation = new[] { 1f, 0, 0 }, scale = new[] { 2f, 2, 2 } } },
            ["meshes"] = new[] { new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = position } } } } },
        };
        PmxLoadResult result = GltfReader.Read(b.Build(json));
        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;
        CollectionAssert.AreEqual(new[] { "全ての親", "Prop" }, d.Bones.Select(x => x.Name).ToArray());
        // (1, 0, 0) を 2 倍して (1, 0, 0) ずらすと (3, 0, 0)。ふつうの glTF は Z を裏返す
        AssertNear(new Vector3(3 * Scale, 0, 0), d.Vertices[0].Position);
        Assert.AreEqual(1, d.Vertices[0].Bone1, "骨で変形しない形は、置いてあるノードのボーンに付ける");
        Assert.AreEqual(PmxWeightType.Bdef1, d.Vertices[0].WeightType);
        Assert.HasCount(1, d.Materials, "材質が無くても、白い材質で描く");
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("法線", StringComparison.Ordinal)), "面の向きの無い形は、面から求めたと書く");
        // 面の向きは glTF で +Z（反時計回りが表）。Z を裏返すので -Z
        AssertNear(new Vector3(0, 0, -1), d.Vertices[0].Normal);
    }

    [TestMethod]
    public void UnsupportedFilesAreReported()
    {
        var b = new GlbBuilder();
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["extensionsRequired"] = new[] { "KHR_draco_mesh_compression" },
        };
        PmxLoadResult draco = GltfReader.Read(b.Build(json));
        Assert.IsFalse(draco.Succeeded);
        StringAssert.Contains(draco.Report.Issues.Single(i => i.Severity == LoadSeverity.Error).Message, "KHR_draco_mesh_compression");

        PmxLoadResult text = GltfReader.Read(Encoding.ASCII.GetBytes("not a model"));
        Assert.IsFalse(text.Succeeded);
        StringAssert.Contains(text.Report.Issues.Single().Message, "glTF ではありません");

        PmxLoadResult old = GltfReader.Read(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"1.0\"}}"));
        Assert.IsFalse(old.Succeeded);
        StringAssert.Contains(old.Report.Issues.Single().Message, "1.0");

        Assert.IsTrue(GltfReader.IsGltfFile(@"C:\a\model.VRM"));
        Assert.IsTrue(GltfReader.IsGltfFile("b.glb"));
        Assert.IsTrue(GltfReader.IsGltfFile("c.gltf"));
        Assert.IsFalse(GltfReader.IsGltfFile("d.pmx"));
        Assert.IsTrue(GltfReader.IsVrmFile("e.vrm"));
        Assert.IsFalse(GltfReader.IsVrmFile("f.glb"));
    }

    [TestMethod]
    public void RealVrmSamplesLoad()
    {
        // VRM コンソーシアムのサンプル（artifacts\vrm-samples に置いたとき。リポジトリには入れない）
        string? folder = FindSamples();
        if (folder is null)
        {
            Assert.Inconclusive("artifacts\\vrm-samples に VRM のサンプルがありません");
        }
        foreach (string path in Directory.GetFiles(folder!, "*.vrm"))
        {
            PmxLoadResult result = GltfReader.Load(path);
            string name = Path.GetFileName(path);
            Assert.IsTrue(result.Succeeded, $"{name}: {string.Join(" / ", result.Report.Issues.Select(i => i.Message))}");
            PmxDocument d = result.Document!;
            Assert.IsFalse(result.Report.Issues.Any(i => i.Severity == LoadSeverity.Error), name);
            Assert.IsGreaterThan(1000, d.Vertices.Length, name);
            Assert.IsGreaterThan(50, d.Bones.Length, name);
            Assert.IsNotEmpty(d.Materials, name);
            Assert.IsNotEmpty(d.EmbeddedTextures, name);
            Assert.AreEqual(d.Indices.Length, d.Materials.Sum(m => m.IndexCount), name);
            float top = d.Vertices.Max(v => v.Position.Y);
            float bottom = d.Vertices.Min(v => v.Position.Y);
            Assert.IsGreaterThan(12f, top - bottom, $"{name}: 人の背丈（MMD の 12〜25 ほど）");
            Assert.IsLessThan(25f, top - bottom, $"{name}: 人の背丈（MMD の 12〜25 ほど）");
            Assert.IsGreaterThan(-1f, bottom, $"{name}: 足は床の近く");
            Assert.IsTrue(d.Vertices.All(v => float.IsFinite(v.Position.X) && float.IsFinite(v.Normal.X)), name);
            Assert.IsTrue(d.Morphs.All(m => m.VertexOffsets.All(o => o.Vertex < d.Vertices.Length)), name);

            // 2 段目: MMD の名前・IK・A の字
            string[] names = [.. d.Bones.Select(b => b.Name)];
            foreach (string required in new[] { "センター", "グルーブ", "上半身", "下半身", "首", "頭", "左腕", "右ひじ", "左足ＩＫ", "右つま先ＩＫ" })
            {
                CollectionAssert.Contains(names, required, $"{name}: {required}");
            }
            PmxBone arm = d.Bones.Single(b => b.Name == "左腕");
            PmxBone elbow = d.Bones.Single(b => b.Name == "左ひじ");
            Assert.IsGreaterThan(0f, arm.Position.X, $"{name}: 左腕は +X");
            Assert.AreEqual(37f, MathF.Atan2(arm.Position.Y - elbow.Position.Y, elbow.Position.X - arm.Position.X) * 180f / MathF.PI, 0.5f, $"{name}: 腕は A の字");
            for (int i = 0; i < d.Bones.Length; i++)
            {
                Assert.IsLessThan(i, d.Bones[i].ParentIndex, $"{name}: 親が先（{d.Bones[i].Name}）");
            }
            Assert.IsFalse(result.Report.Issues.Any(i => i.Severity == LoadSeverity.Warning), $"{name}: 注意は無い");

            // 3 段目: 表情は MMD の名前で、表情の欄の初めに並ぶ。目線を表情で動かすモデル（Seed-san）は両目を作らず、視線の表情を持つ
            foreach (string required in new[] { "まばたき", "ウィンク", "ウィンク右", "あ", "い", "う", "え", "お", "笑い", "怒り", "困る", "にっこり" })
            {
                CollectionAssert.Contains(d.Morphs.Select(m => m.Name).ToArray(), required, $"{name}: {required}");
            }
            Assert.AreEqual("まばたき", d.Morphs[0].Name, name);
            bool lookByExpression = d.Morphs.Any(m => m.Name == "視線左");
            Assert.AreEqual(name.StartsWith("Seed", StringComparison.Ordinal), lookByExpression, $"{name}: 目線を表情で動かすのは Seed-san だけ");
            Assert.AreEqual(!lookByExpression, names.Contains("両目"), $"{name}: 目線を表情で動かすモデルは両目を作らない");

            // 4 段目: 揺れ物（1.0 は、揺れ物ごとに最後のジョイントの手前まで）
            SpringBoneSet springs = d.SpringBones!;
            int expectedJoints = name switch
            {
                "Seed-san.vrm" => 28 - 9,
                "VRM1_Constraint_Twist_Sample.vrm" => 79 - 22,
                _ => -1,
            };
            if (expectedJoints >= 0)
            {
                Assert.HasCount(expectedJoints, springs.Joints, name);
            }
            else
            {
                Assert.IsGreaterThan(20, springs.Joints.Count, name);
            }
            Assert.IsNotEmpty(springs.Colliders, name);
            Assert.IsTrue(springs.Joints.All(j => (uint)j.Bone < (uint)d.Bones.Length && j.Colliders.All(c => (uint)c < (uint)springs.Colliders.Count)), name);
            // 先端が根元と重なる節（Seed-san の前髪 F・G の 2 本。元のモデルの作り）は、向きが決まらないので計算で飛ばす
            Assert.IsLessThanOrEqualTo(2, springs.Joints.Count(j => Vector3.Distance(j.Tail, d.Bones[j.Bone].Position) < 1e-4f), $"{name}: 先端が根元と重なる節");

            // 5 段目: MToon の影の色はトゥーンに、輪郭線は MMD の輪郭線に（太さは 0.3〜3 ほど）
            PmxMaterial[] toon = [.. d.Materials.Where(m => m.ToonIndex >= 0)];
            Assert.IsGreaterThan(5, toon.Length, $"{name}: トゥーンの付いた材質");
            Assert.IsTrue(toon.All(m => d.EmbeddedTextures.ContainsKey(m.ToonIndex) && d.Textures[m.ToonIndex].StartsWith("mtoon_shade_", StringComparison.Ordinal)), name);
            PmxMaterial[] edged = [.. d.Materials.Where(m => m.Flags.HasFlag(PmxMaterialFlags.Edge))];
            Assert.IsNotEmpty(edged, $"{name}: 輪郭線のある材質");
            Assert.IsTrue(edged.All(m => m.EdgeSize is > 0.3f and < 3f), $"{name}: 輪郭線の太さ {string.Join(",", edged.Select(m => m.EdgeSize))}");
        }
    }

    private static string? FindSamples()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "vrm-samples");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.vrm").Length > 0)
            {
                return candidate;
            }
        }
        return null;
    }

    private static Dictionary<string, object> Vrm1Extensions() => new()
    {
        ["VRMC_vrm"] = new { specVersion = "1.0", meta = new { name = "Test", authors = new[] { "A" }, avatarPermission = "everyone" } },
    };

    // 三角形 1 枚・骨 2 本（Hips と Head）。表情の元を 1 つ（変わる所だけを書いた並び）
    private static byte[] SkinnedModel(Dictionary<string, object> extensions)
    {
        var b = new GlbBuilder();
        int position = b.Floats([0, 1, 0, 1, 1, 0, 0, 1.5f, 0.5f], "VEC3");
        int normal = b.Floats([0, 0, 1, 0, 0, 1, 0, 0, 1], "VEC3");
        int uv = b.Floats([0, 0, 1, 0, 0, 1], "VEC2");
        int joints = b.UShorts([0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0], "VEC4");
        int weights = b.Floats([1, 0, 0, 0, 0.5f, 0.5f, 0, 0, 1, 0, 0, 0], "VEC4");
        int indices = b.UInts([0, 1, 2], "SCALAR");
        int inverseBind = b.Floats(
        [
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1.5f, 0, 1,
        ], "MAT4");
        int blink = b.SparseFloats(3, "VEC3", [2], [0, 0, 0.1f]);
        int image = b.View([1, 2, 3, 4]);
        var json = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0, 1 } } },
            ["nodes"] = new object[]
            {
                new { name = "Body", mesh = 0, skin = 0 },
                new { name = "Root", children = new[] { 2 } },
                new { name = "Hips", translation = new[] { 0f, 1, 0 }, children = new[] { 3 } },
                new { name = "Head", translation = new[] { 0f, 0.5f, 0 } },
            },
            ["skins"] = new[] { new { joints = new[] { 2, 3 }, inverseBindMatrices = inverseBind } },
            ["meshes"] = new[]
            {
                new
                {
                    name = "body",
                    extras = new { targetNames = new[] { "blink" } },
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new Dictionary<string, int>
                            {
                                ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv, ["JOINTS_0"] = joints, ["WEIGHTS_0"] = weights,
                            },
                            indices,
                            material = 0,
                            targets = new[] { new Dictionary<string, int> { ["POSITION"] = blink } },
                        },
                    },
                },
            },
            ["materials"] = new[]
            {
                new { name = "Skin", pbrMetallicRoughness = new { baseColorFactor = new[] { 0.5f, 1, 1, 1 }, baseColorTexture = new { index = 0 } }, doubleSided = true },
            },
            ["textures"] = new[] { new { source = 0 } },
            ["images"] = new[] { new { bufferView = image, mimeType = "image/png", name = "skin" } },
            ["extensionsUsed"] = extensions.Keys.ToArray(),
            ["extensions"] = extensions,
        };
        return b.Build(json);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, string? message = null) => AssertNearCore(expected, actual, message);

    private static void AssertNearCore(Vector3 expected, Vector3 actual, string? message)
    {
        Assert.IsLessThan(1e-3f, Vector3.Distance(expected, actual), $"期待 {expected}・実際 {actual} {message}");
    }

    /// <summary>テスト用の GLB を組み立てる（データの塊は 1 つ。並びは 4 バイトずつそろえる）。</summary>
    private sealed class GlbBuilder
    {
        private readonly List<byte> _binary = [];
        private readonly List<object> _views = [];
        private readonly List<object> _accessors = [];

        public int View(byte[] bytes)
        {
            while (_binary.Count % 4 != 0)
            {
                _binary.Add(0);
            }
            _views.Add(new { buffer = 0, byteOffset = _binary.Count, byteLength = bytes.Length });
            _binary.AddRange(bytes);
            return _views.Count - 1;
        }

        public int Floats(float[] values, string type) => Accessor(View(ToBytes(values)), 5126, values.Length / Components(type), type);

        public int UShorts(ushort[] values, string type)
        {
            var bytes = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
            }
            return Accessor(View(bytes), 5123, values.Length / Components(type), type);
        }

        public int UInts(uint[] values, string type)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
            }
            return Accessor(View(bytes), 5125, values.Length / Components(type), type);
        }

        // データの無い並び（すべて 0）に、変わる所だけを書く
        public int SparseFloats(int count, string type, uint[] indices, float[] values)
        {
            var indexBytes = new byte[indices.Length * 4];
            for (int i = 0; i < indices.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(i * 4), indices[i]);
            }
            int indexView = View(indexBytes);
            int valueView = View(ToBytes(values));
            _accessors.Add(new
            {
                componentType = 5126,
                count,
                type,
                sparse = new
                {
                    count = indices.Length,
                    indices = new { bufferView = indexView, componentType = 5125 },
                    values = new { bufferView = valueView },
                },
            });
            return _accessors.Count - 1;
        }

        public byte[] Build(Dictionary<string, object> json)
        {
            if (_accessors.Count > 0)
            {
                json["accessors"] = _accessors;
                json["bufferViews"] = _views;
                json["buffers"] = new[] { new { byteLength = _binary.Count } };
            }
            byte[] text = JsonSerializer.SerializeToUtf8Bytes(json);
            int jsonLength = (text.Length + 3) & ~3;
            int binaryLength = (_binary.Count + 3) & ~3;
            bool hasBinary = _binary.Count > 0;
            int total = 12 + 8 + jsonLength + (hasBinary ? 8 + binaryLength : 0);
            var glb = new byte[total];
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0), 0x46546C67);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonLength);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
            text.CopyTo(glb, 20);
            for (int i = 20 + text.Length; i < 20 + jsonLength; i++)
            {
                glb[i] = (byte)' ';
            }
            if (hasBinary)
            {
                int offset = 20 + jsonLength;
                BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset), (uint)binaryLength);
                BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset + 4), 0x004E4942);
                _binary.CopyTo(glb, offset + 8);
            }
            return glb;
        }

        private int Accessor(int view, int componentType, int count, string type)
        {
            _accessors.Add(new { bufferView = view, componentType, count, type });
            return _accessors.Count - 1;
        }

        private static int Components(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 16 };

        private static byte[] ToBytes(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
            }
            return bytes;
        }
    }
}
