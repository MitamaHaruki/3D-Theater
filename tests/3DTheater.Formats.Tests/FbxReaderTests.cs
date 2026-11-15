using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Theater3D.Formats.Fbx;
using Theater3D.Formats.Gltf;
using Theater3D.Formats.Pmx;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.Tests;

/// <summary>FBX を、PMX と同じ中身にする（設計判断記録 83 章）。FBX のモーションを、キャラクターに当てはめる（85 章）。</summary>
[TestClass]
public sealed class FbxReaderTests
{
    // 1 cm（FBX の初めの単位）は MMD の 0.125
    private const float Cm = 0.125f;

    [TestMethod]
    public void BinaryAndTextFilesGiveTheSameModel()
    {
        var scene = new TestScene();
        long model = scene.Add("Model", "Quad", "Mesh");
        scene.Connect(model, 0);
        long material = scene.Add("Material", "Skin", string.Empty, Props(P("DiffuseColor", "Color", 0.5, 0.25, 1.0)));
        scene.Connect(material, model);
        scene.Connect(Quad(scene), model);

        PmxDocument[] documents =
        [
            Load(FbxTestWriter.Binary(scene.Nodes(7400))),
            Load(FbxTestWriter.Binary(scene.Nodes(7500), version: 7500, compress: true)),
            Load(FbxTestWriter.Ascii(scene.Nodes(7400))),
        ];
        foreach (PmxDocument d in documents)
        {
            // 4 つの頂点・2 つの三角形。cm を MMD の長さに、Z を裏返し、UV の上下を逆に
            Assert.HasCount(4, d.Vertices);
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 0, 3, 2 }, d.Indices.ToArray(), "扇の形に分け、裏返したので順を逆に");
            AssertNear(new Vector3(100 * Cm, 100 * Cm, 0), d.Vertices[2].Position);
            AssertNear(new Vector3(0, 0, -1), d.Vertices[0].Normal);
            Assert.AreEqual(new Vector2(0, 1), d.Vertices[0].Uv);
            Assert.AreEqual(new Vector2(1, 0), d.Vertices[2].Uv);
            // 材質: 拡散色は光の強さの値なので、画面の色の値に直す
            PmxMaterial m = d.Materials.Single();
            Assert.AreEqual("Skin", m.Name);
            Assert.AreEqual(GltfConverter.LinearToSrgb(0.5f), m.Diffuse.X, 1e-4f);
            Assert.AreEqual(GltfConverter.LinearToSrgb(0.25f), m.Diffuse.Y, 1e-4f);
            Assert.AreEqual(1f, m.Diffuse.W);
            Assert.IsTrue(m.Flags.HasFlag(PmxMaterialFlags.DoubleSided), "Culling が無ければ両面");
            Assert.AreEqual("全ての親", d.Bones[0].Name);
        }
        CollectionAssert.AreEqual(documents[0].Vertices.Select(v => v.Position).ToArray(), documents[2].Vertices.Select(v => v.Position).ToArray(), "文字の FBX も同じ");
    }

    [TestMethod]
    public void ZUpFileIsTurnedToYUp()
    {
        // 3ds Max と同じ軸（上が +Z・正面が -Y）で、長さの 1 が 1 m
        var scene = new TestScene();
        scene.Global = [P("UpAxis", "int", 2), P("UpAxisSign", "int", 1), P("FrontAxis", "int", 1), P("FrontAxisSign", "int", -1),
            P("CoordAxis", "int", 0), P("CoordAxisSign", "int", 1), P("UnitScaleFactor", "double", 100.0)];
        long model = scene.Add("Model", "Tri", "Mesh");
        scene.Connect(model, 0);
        scene.Connect(Triangle(scene, [0, 0, 1, 0, -1, 0, 1, 0, 0]), model);
        PmxLoadResult result = FbxReader.Read(FbxTestWriter.Binary(scene.Nodes(7400)));
        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;
        AssertNear(new Vector3(0, 12.5f, 0), d.Vertices[0].Position, "上（+Z）は MMD の上");
        AssertNear(new Vector3(0, 0, -12.5f), d.Vertices[1].Position, "正面（-Y）は MMD の正面（-Z）");
        AssertNear(new Vector3(12.5f, 0, 0), d.Vertices[2].Position);
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("上が +Z", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NodeTransformsFollowFbxRules()
    {
        var scene = new TestScene();
        // 回転の順: XYZ（X を先に）と ZYX（Z を先に）
        AddPart(scene, "XYZ", [0, 100, 0], P("Lcl Rotation", "Lcl Rotation", 90.0, 0.0, 90.0));
        AddPart(scene, "ZYX", [0, 100, 0], P("Lcl Rotation", "Lcl Rotation", 90.0, 0.0, 90.0), P("RotationOrder", "enum", 5));
        // 回転の中心
        AddPart(scene, "Pivot", [200, 0, 0], P("Lcl Rotation", "Lcl Rotation", 0.0, 0.0, 90.0), P("RotationPivot", "Vector3D", 100.0, 0.0, 0.0));
        // 前の回転は、ふつうの回転の後に掛かる（Z で回してから X）
        AddPart(scene, "Pre", [100, 0, 0], P("Lcl Rotation", "Lcl Rotation", 0.0, 0.0, 90.0), P("PreRotation", "Vector3D", 90.0, 0.0, 0.0));
        // 形だけに掛かる移動は、子のノードには掛からない
        long parent = AddPart(scene, "Geometric", [0, 0, 0], P("GeometricTranslation", "Vector3D", 0.0, 0.0, 50.0));
        AddPart(scene, "Child", [0, 0, 0], parent);

        PmxDocument d = Load(FbxTestWriter.Binary(scene.Nodes(7400)));
        AssertNear(new Vector3(0, 0, -100 * Cm), VerticesOf(d, "XYZ")[0], "X で (0,0,100)、Z ではそのまま");
        AssertNear(new Vector3(-100 * Cm, 0, 0), VerticesOf(d, "ZYX")[0], "Z で (-100,0,0)、X ではそのまま");
        AssertNear(new Vector3(100 * Cm, 100 * Cm, 0), VerticesOf(d, "Pivot")[0]);
        AssertNear(new Vector3(0, 0, -100 * Cm), VerticesOf(d, "Pre")[0]);
        AssertNear(new Vector3(0, 0, -50 * Cm), VerticesOf(d, "Geometric")[0]);
        AssertNear(Vector3.Zero, VerticesOf(d, "Child")[0]);
    }

    [TestMethod]
    public void SkinnedMeshFollowsClusters()
    {
        var scene = new TestScene();
        long root = scene.Add("Model", "Root", "LimbNode");
        scene.Connect(root, 0);
        // ボーンは今の姿勢で Z に 90 度回っている（作ったときの姿勢は回っていない）
        long bone = scene.Add("Model", "Bone", "LimbNode",
            Props(P("Lcl Translation", "Lcl Translation", 0.0, 100.0, 0.0), P("Lcl Rotation", "Lcl Rotation", 0.0, 0.0, 90.0)));
        scene.Connect(bone, root);
        long body = scene.Add("Model", "Body", "Mesh");
        scene.Connect(body, 0);
        long geometry = Triangle(scene, [0, 150, 0, 10, 0, 0, 0, 100, 0]);
        scene.Connect(geometry, body);
        long skin = scene.Add("Deformer", "Skin", "Skin");
        scene.Connect(skin, geometry);
        // Transform は「形 → 骨」、TransformLink は作ったときの骨の位置
        long boneCluster = scene.Add("Deformer", "Bone", "Cluster",
            new FbxTestNode("Indexes", new[] { 0, 2 }), new FbxTestNode("Weights", new[] { 1.0, 0.5 }),
            new FbxTestNode("Transform", Translation(0, -100, 0)), new FbxTestNode("TransformLink", Translation(0, 100, 0)));
        long rootCluster = scene.Add("Deformer", "Root", "Cluster",
            new FbxTestNode("Indexes", new[] { 1, 2 }), new FbxTestNode("Weights", new[] { 1.0, 0.5 }),
            new FbxTestNode("Transform", Translation(0, 0, 0)), new FbxTestNode("TransformLink", Translation(0, 0, 0)));
        scene.Connect(boneCluster, skin);
        scene.Connect(rootCluster, skin);
        scene.Connect(bone, boneCluster);
        scene.Connect(root, rootCluster);

        PmxDocument d = Load(FbxTestWriter.Ascii(scene.Nodes(7400)));
        CollectionAssert.AreEqual(new[] { "全ての親", "Root", "Bone" }, d.Bones.Select(b => b.Name).ToArray());
        Assert.AreEqual(1, d.Bones[2].ParentIndex);
        AssertNear(new Vector3(0, 100 * Cm, 0), d.Bones[2].Position);
        // (0,150,0) は骨から (0,50,0)。骨が Z に 90 度回るので (-50,0,0)、骨の位置を足して (-50,100,0)
        AssertNear(new Vector3(-50 * Cm, 100 * Cm, 0), d.Vertices[0].Position);
        Assert.AreEqual(PmxWeightType.Bdef1, d.Vertices[0].WeightType);
        Assert.AreEqual(2, d.Vertices[0].Bone1);
        AssertNear(new Vector3(10 * Cm, 0, 0), d.Vertices[1].Position);
        Assert.AreEqual(1, d.Vertices[1].Bone1);
        AssertNear(new Vector3(0, 100 * Cm, 0), d.Vertices[2].Position, "半分ずつ");
        Assert.AreEqual(PmxWeightType.Bdef2, d.Vertices[2].WeightType);
        Assert.AreEqual(0.5f, d.Vertices[2].Weights.X, 1e-4f);
    }

    [TestMethod]
    public void BlendShapesBecomeMorphs()
    {
        var scene = new TestScene();
        long model = scene.Add("Model", "Face", "Mesh");
        scene.Connect(model, 0);
        long geometry = Quad(scene);
        scene.Connect(geometry, model);
        long blend = scene.Add("Deformer", "Face", "BlendShape");
        scene.Connect(blend, geometry);
        // 途中の形（50%）と終わりの形（100%）があれば、終わりの形を使う
        long smile = scene.Add("Deformer", "smile", "BlendShapeChannel", new FbxTestNode("DeformPercent", 0.0), new FbxTestNode("FullWeights", new[] { 50.0, 100.0 }));
        long half = scene.Add("Geometry", "smile_half", "Shape", new FbxTestNode("Indexes", new[] { 2 }), new FbxTestNode("Vertices", new[] { 0.0, 4.0, 0.0 }));
        long full = scene.Add("Geometry", "smile", "Shape", new FbxTestNode("Indexes", new[] { 2 }), new FbxTestNode("Vertices", new[] { 0.0, 8.0, 0.0 }));
        long blink = scene.Add("Deformer", "vrc.blink", "BlendShapeChannel", new FbxTestNode("DeformPercent", 0.0), new FbxTestNode("FullWeights", new[] { 100.0 }));
        long blinkShape = scene.Add("Geometry", "vrc.blink", "Shape",
            new FbxTestNode("Indexes", new[] { 0, 1 }), new FbxTestNode("Vertices", new[] { 0.0, 0.0, 8.0, 0.0, 0.0, 8.0 }));
        scene.Connect(smile, blend);
        scene.Connect(half, smile);
        scene.Connect(full, smile);
        scene.Connect(blink, blend);
        scene.Connect(blinkShape, blink);

        PmxDocument d = Load(FbxTestWriter.Binary(scene.Nodes(7400)));
        // 元の表情の元はチャンネルの名前のまま（3 段目: vrc.blink からは MMD の「まばたき」も作る）
        CollectionAssert.AreEqual(new[] { "まばたき", "smile", "vrc.blink" }, d.Morphs.Select(m => m.Name).ToArray());
        PmxVertexOffset offset = d.Morphs[1].VertexOffsets.Single();
        Assert.AreEqual(2, offset.Vertex);
        AssertNear(new Vector3(0, 8 * Cm, 0), offset.Translation);
        Assert.HasCount(2, d.Morphs[2].VertexOffsets);
        AssertNear(new Vector3(0, 0, -8 * Cm), d.Morphs[2].VertexOffsets[0].Translation, "Z を裏返す");
    }

    [TestMethod]
    public void TexturesAreEmbeddedOrFoundNearby()
    {
        string root = Path.Combine(Path.GetTempPath(), "3dt-fbx-" + Guid.NewGuid().ToString("N"));
        string fbxFolder = Path.Combine(root, "FBX");
        Directory.CreateDirectory(fbxFolder);
        Directory.CreateDirectory(Path.Combine(root, "Texture"));
        File.WriteAllBytes(Path.Combine(root, "Texture", "body.png"), [0x89, (byte)'P', (byte)'N', (byte)'G']);
        try
        {
            var scene = new TestScene();
            long model = scene.Add("Model", "Body", "Mesh");
            scene.Connect(model, 0);
            // 4 つの三角形に、それぞれの材質
            long geometry = scene.Add("Geometry", "Body", "Mesh",
                new FbxTestNode("Vertices", new double[] { 0, 0, 0, 10, 0, 0, 0, 10, 0 }),
                new FbxTestNode("PolygonVertexIndex", new[] { 0, 1, ~2, 0, 1, ~2, 0, 1, ~2, 0, 1, ~2 }),
                new FbxTestNode("LayerElementMaterial", 0).With(
                    new FbxTestNode("MappingInformationType", "ByPolygon"), new FbxTestNode("ReferenceInformationType", "IndexToDirect"),
                    new FbxTestNode("Materials", new[] { 0, 1, 2, 3 })));
            scene.Connect(geometry, model);
            long embedded = AddTexturedMaterial(scene, model, "Skin", "model.fbm\\skin", "C:\\nowhere\\skin", content: [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2]);
            long nearby = AddTexturedMaterial(scene, model, "Wear", "..\\..\\author\\Texture\\body.png", "C:\\author\\Texture\\body.png", content: null);
            long missing = AddTexturedMaterial(scene, model, "Gone", "gone.png", string.Empty, content: null);
            long glass = scene.Add("Material", "Glass", string.Empty, Props(P("DiffuseColor", "Color", 1.0, 1.0, 1.0), P("Opacity", "Number", 0.5)));
            scene.Connect(glass, model);
            Assert.IsGreaterThan(0, embedded + nearby + missing);

            PmxLoadResult result = FbxReader.Read(FbxTestWriter.Binary(scene.Nodes(7400)), fbxFolder, "model");
            Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
            PmxDocument d = result.Document!;
            Assert.HasCount(4, d.Materials);
            // ファイルの中の画像: 名前に拡張子が無ければ中身の頭で決める。テクスチャがあれば色はテクスチャのまま
            PmxMaterial skin = d.Materials[0];
            Assert.AreEqual("skin.png", d.Textures[skin.TextureIndex]);
            CollectionAssert.AreEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2 }, d.EmbeddedTextures[skin.TextureIndex]);
            Assert.AreEqual(Vector4.One, skin.Diffuse);
            // 書かれた場所に無い画像は、FBX のフォルダーの 1 つ上の下で同じ名前のものを使う
            Assert.AreEqual("../Texture/body.png", d.Textures[d.Materials[1].TextureIndex]);
            Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("近くのフォルダーで同じ名前", StringComparison.Ordinal)));
            // 見つからない画像は、書かれた名前のまま（見つからないとレポートに出る）
            Assert.AreEqual("gone.png", d.Textures[d.Materials[2].TextureIndex]);
            Assert.IsTrue(result.Report.Issues.Any(i => i.Category == LoadIssueCategory.MissingFile));
            Assert.AreEqual(0.5f, d.Materials[3].Diffuse.W, 1e-4f, "不透明度");
            Assert.AreEqual(-1, d.Materials[3].TextureIndex);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void OldOrBrokenFilesAreRejected()
    {
        // FBX 6.x（物を名前で結ぶ古い形）
        byte[] old = Encoding.UTF8.GetBytes("; FBX 6.1.0 project file\nFBXHeaderExtension:  {\n\tFBXVersion: 6100\n}\nObjects:  {\n}\n");
        PmxLoadResult result = FbxReader.Read(old);
        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("7.0 以降", StringComparison.Ordinal)));

        Assert.IsFalse(FbxReader.Read(Encoding.UTF8.GetBytes("これは FBX ではありません")).Succeeded);

        // 途中で切れたバイナリ
        var scene = new TestScene();
        long model = scene.Add("Model", "Quad", "Mesh");
        scene.Connect(model, 0);
        scene.Connect(Quad(scene), model);
        byte[] whole = FbxTestWriter.Binary(scene.Nodes(7400));
        for (int length = 40; length < whole.Length; length += 97)
        {
            PmxLoadResult cut = FbxReader.Read(whole[..length]);
            Assert.IsNotNull(cut.Report, $"{length} バイトでも落ちない");
        }
    }

    [TestMethod]
    public void HumanoidIsGuessedFromMixamoNames()
    {
        var (names, parents) = Tree(
        [
            "mixamorig:Hips",
            " mixamorig:Spine",
            "  mixamorig:Spine1",
            "   mixamorig:Spine2",
            "    mixamorig:Neck",
            "     mixamorig:Head",
            "      mixamorig:HeadTop_End",
            "      mixamorig:LeftEye",
            "      mixamorig:RightEye",
            .. Sided(
            [
                "    mixamorig:$Shoulder",
                "     mixamorig:$Arm",
                "      mixamorig:$ForeArm",
                "       mixamorig:$Hand",
                "        mixamorig:$HandThumb1",
                "         mixamorig:$HandThumb2",
                "          mixamorig:$HandThumb3",
                "           mixamorig:$HandThumb4",
                "        mixamorig:$HandIndex1",
                "         mixamorig:$HandIndex2",
                "          mixamorig:$HandIndex3",
                "           mixamorig:$HandIndex4",
            ], "Left", "Right"),
            .. Sided([" mixamorig:$UpLeg", "  mixamorig:$Leg", "   mixamorig:$Foot", "    mixamorig:$ToeBase", "     mixamorig:$Toe_End"], "Left", "Right"),
        ]);
        AssertHumanoid(names, parents, new()
        {
            ["hips"] = "mixamorig:Hips", ["spine"] = "mixamorig:Spine", ["chest"] = "mixamorig:Spine1", ["upperChest"] = "mixamorig:Spine2",
            ["neck"] = "mixamorig:Neck", ["head"] = "mixamorig:Head", ["leftEye"] = "mixamorig:LeftEye",
            ["leftShoulder"] = "mixamorig:LeftShoulder", ["leftUpperArm"] = "mixamorig:LeftArm", ["leftLowerArm"] = "mixamorig:LeftForeArm",
            ["rightHand"] = "mixamorig:RightHand",
            ["leftThumbMetacarpal"] = "mixamorig:LeftHandThumb1", ["leftThumbDistal"] = "mixamorig:LeftHandThumb3",
            ["leftIndexProximal"] = "mixamorig:LeftHandIndex1", ["leftIndexDistal"] = "mixamorig:LeftHandIndex3",
            ["leftUpperLeg"] = "mixamorig:LeftUpLeg", ["leftLowerLeg"] = "mixamorig:LeftLeg", ["rightFoot"] = "mixamorig:RightFoot",
            ["leftToes"] = "mixamorig:LeftToeBase",
        });
    }

    [TestMethod]
    public void HumanoidIsGuessedFromOtherNamingStyles()
    {
        // VRChat（CATS）: 「Left leg」は太もも、「Left knee」はひざ。ねじれのボーンは使わない
        var (names, parents) = Tree(
        [
            "Armature",
            " Hips",
            "  Spine",
            "   Chest",
            "    Neck",
            "     Head",
            "      Eye_L",
            "      Eye_R",
            .. Sided(["    $ shoulder", "     $ arm", "      $ arm twist", "      $ elbow", "       $ wrist", "        Thumb0_#", "         Thumb1_#", "          Thumb2_#",
                "        IndexFinger1_#", "         IndexFinger2_#", "          IndexFinger3_#"], "Left", "Right", "L", "R"),
            .. Sided(["  $ leg", "   $ knee", "    $ ankle", "     $ toe"], "Left", "Right"),
        ]);
        AssertHumanoid(names, parents, new()
        {
            ["hips"] = "Hips", ["chest"] = "Chest", ["head"] = "Head", ["leftEye"] = "Eye_L", ["leftUpperArm"] = "Left arm", ["leftLowerArm"] = "Left elbow",
            ["leftHand"] = "Left wrist", ["leftThumbMetacarpal"] = "Thumb0_L", ["leftThumbDistal"] = "Thumb2_L", ["leftIndexIntermediate"] = "IndexFinger2_L",
            ["leftUpperLeg"] = "Left leg", ["leftLowerLeg"] = "Left knee", ["leftFoot"] = "Left ankle", ["rightToes"] = "Right toe",
        });

        // Unity の人型の名前（.L・.R）
        (names, parents) = Tree(
        [
            "Hips", " Spine", "  Chest", "   UpperChest", "    Neck", "     Head",
            .. Sided(["    Shoulder.#", "     UpperArm.#", "      LowerArm.#", "       Hand.#", "        Thumb Proximal.#", "         Thumb Intermediate.#",
                "          Thumb Distal.#", "        Little Proximal.#"], "", "", "L", "R"),
            .. Sided([" UpperLeg.#", "  LowerLeg.#", "   Foot.#", "    Toes.#"], "", "", "L", "R"),
        ]);
        AssertHumanoid(names, parents, new()
        {
            ["upperChest"] = "UpperChest", ["leftUpperArm"] = "UpperArm.L", ["rightLowerArm"] = "LowerArm.R", ["leftThumbMetacarpal"] = "Thumb Proximal.L",
            ["leftThumbProximal"] = "Thumb Intermediate.L", ["leftLittleProximal"] = "Little Proximal.L", ["leftToes"] = "Toes.L",
        });

        // VRoid（J_Bip_）: 補助のボーン（J_Aim_・J_Roll_・J_Sec_）は使わない
        (names, parents) = Tree(
        [
            "Root", " J_Bip_C_Hips", "  J_Aim_L_UpperLeg", "   J_Roll_L_UpperLeg", "  J_Bip_C_Spine", "   J_Bip_C_Chest", "    J_Bip_C_UpperChest",
            "     J_Bip_C_Neck", "      J_Bip_C_Head", "       J_Adj_L_FaceEye", "       J_Adj_R_FaceEye", "       J_Sec_Hair1_01",
            .. Sided(["     J_Bip_#_Shoulder", "      J_Bip_#_UpperArm", "       J_Bip_#_LowerArm", "        J_Bip_#_Hand", "         J_Bip_#_Middle1"], "", "", "L", "R"),
            .. Sided(["  J_Bip_#_UpperLeg", "   J_Bip_#_LowerLeg", "    J_Bip_#_Foot", "     J_Bip_#_ToeBase"], "", "", "L", "R"),
        ]);
        AssertHumanoid(names, parents, new()
        {
            ["hips"] = "J_Bip_C_Hips", ["leftEye"] = "J_Adj_L_FaceEye", ["leftUpperLeg"] = "J_Bip_L_UpperLeg", ["rightMiddleProximal"] = "J_Bip_R_Middle1",
        });

        // Blender（upper_arm.L・thigh.L・shin.L）。手首は、ひじのねじれのボーンの下にあってもよい
        (names, parents) = Tree(
        [
            "Armature", " hips", "  spine", "   chest", "    neck", "     head 1",
            .. Sided(["    shoulder.#", "     upper_arm.#", "      forearm.#", "       forearm.twist.#", "        hand.#", "         f_index.01.#",
                "          f_index.02.#", "           f_index.03.#", "            f_index.03.#_end"], "", "", "L", "R"),
            .. Sided(["  thigh.#", "   shin.#", "    foot.#", "     toe.#", "    foot_rope_#"], "", "", "L", "R"),
        ]);
        AssertHumanoid(names, parents, new()
        {
            ["head"] = "head 1", ["leftLowerArm"] = "forearm.L", ["leftHand"] = "hand.L", ["leftIndexDistal"] = "f_index.03.L", ["rightLowerLeg"] = "shin.R",
        });

        // 3ds Max の Biped（Bip01 〜。太ももは背骨の下）
        (names, parents) = Tree(
        [
            "Bip01", " Bip01 Pelvis", "  Bip01 Spine", "   Bip01 Spine1", "    Bip01 Neck", "     Bip01 Head",
            .. Sided(["     Bip01 # Clavicle", "      Bip01 # UpperArm", "       Bip01 # Forearm", "        Bip01 # Hand"], "", "", "L", "R"),
            .. Sided(["   Bip01 # Thigh", "    Bip01 # Calf", "     Bip01 # Foot", "      Bip01 # Toe0"], "", "", "L", "R"),
        ]);
        AssertHumanoid(names, parents, new()
        {
            ["hips"] = "Bip01 Pelvis", ["chest"] = "Bip01 Spine1", ["leftShoulder"] = "Bip01 L Clavicle", ["leftUpperLeg"] = "Bip01 L Thigh",
            ["leftLowerLeg"] = "Bip01 L Calf", ["rightToes"] = "Bip01 R Toe0",
        });

        // 人型でないもの（部屋・小物）
        (names, parents) = Tree(["Root", " Door", "  Handle", " Lamp"]);
        Assert.IsEmpty(FbxHumanoid.Guess(names, parents, out string[] missing));
        CollectionAssert.IsSubsetOf(FbxHumanoid.Required, missing);
    }

    [TestMethod]
    public void HumanoidFbxBecomesMmdSkeletonFacingFront()
    {
        // 後ろを向いた（左腕が -X にある）T の字の人型（cm）
        var scene = new TestScene();
        long Bone(string name, long parent, double x, double y, double z)
        {
            long id = scene.Add("Model", name, "LimbNode", Props(P("Lcl Translation", "Lcl Translation", x, y, z)));
            scene.Connect(id, parent);
            return id;
        }
        long hips = Bone("Hips", 0, 0, 100, 0);
        long spine = Bone("Spine", hips, 0, 10, 0);
        long chest = Bone("Spine1", spine, 0, 15, 0);
        long neck = Bone("Neck", chest, 0, 20, 0);
        long head = Bone("Head", neck, 0, 10, 0);
        Bone("LeftEye", head, -3, 8, -5);
        Bone("RightEye", head, 3, 8, -5);
        foreach ((string side, double sign) in new[] { ("Left", -1.0), ("Right", 1.0) })
        {
            long shoulder = Bone(side + "Shoulder", chest, sign * 3, 18, 0);
            long arm = Bone(side + "Arm", shoulder, sign * 10, 0, 0);
            long fore = Bone(side + "ForeArm", arm, sign * 25, 0, 0);
            Bone(side + "Hand", fore, sign * 22, 0, 0);
            long upLeg = Bone(side + "UpLeg", hips, sign * 10, -5, 0);
            long leg = Bone(side + "Leg", upLeg, 0, -45, 0);
            long foot = Bone(side + "Foot", leg, 0, -42, 0);
            Bone(side + "ToeBase", foot, 0, -6, -12);
        }
        long body = scene.Add("Model", "Body", "Mesh");
        scene.Connect(body, 0);
        scene.Connect(Triangle(scene, [0, 0, 0, 10, 0, 0, 0, 10, 0]), body);

        PmxLoadResult result = FbxReader.Read(FbxTestWriter.Binary(scene.Nodes(7400)));
        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        PmxDocument d = result.Document!;
        PmxBone Find(string name) => d.Bones.FirstOrDefault(b => b.Name == name) ?? throw new AssertFailedException($"ボーン「{name}」がありません");
        foreach (string name in new[] { "センター", "グルーブ", "下半身", "上半身", "上半身2", "首", "頭", "両目", "左目", "左肩", "左腕", "左ひじ", "左手首",
            "左足", "左ひざ", "左足首", "左足先EX", "左つま先", "左足ＩＫ", "左つま先ＩＫ", "右腕", "右足ＩＫ" })
        {
            Find(name);
        }
        Assert.AreEqual(Find("グルーブ"), d.Bones[Find("上半身").ParentIndex], "上半身はグルーブの子");
        // 半回転して前を向き、左は MMD と同じく +X
        Assert.IsGreaterThan(0f, Find("左腕").Position.X);
        Assert.IsLessThan(0f, Find("右腕").Position.X);
        Assert.IsLessThan(0f, Find("左目").Position.Z, "目は顔の前（MMD の正面は -Z）");
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("半回転", StringComparison.Ordinal)));
        // 腕は A の字（水平から 37 度下がる）
        Vector3 upperArm = Find("左ひじ").Position - Find("左腕").Position;
        Assert.AreEqual(GltfConverter.ArmAngleDegrees, MathF.Atan2(-upperArm.Y, upperArm.X) * 180f / MathF.PI, 0.1f);
        Assert.AreEqual(25 * Cm, upperArm.Length(), 1e-3f, "腕の長さはそのまま");
    }

    [TestMethod]
    public void ExpressionNamesAreRecognized()
    {
        var expected = new (string Name, string? Key)[]
        {
            // VRChat
            ("vrc.v_aa", "aa"), ("vrc.v_ih", "ih"), ("vrc.v_ou", "ou"), ("vrc.v_e", "ee"), ("vrc.v_oh", "oh"), ("vrc.blink_left", "blinkLeft"), ("Blink", "blink"),
            ("Blink_R", "blinkRight"),
            // VRoid（前に「Face_Blendshape.」が付くことがある）
            ("Face_Blendshape.Fcl_MTH_A", "aa"), ("Fcl_MTH_O", "oh"), ("Fcl_EYE_Close", "blink"), ("Fcl_EYE_Close_L", "blinkLeft"), ("Fcl_ALL_Joy", "happy"),
            ("Fcl_ALL_Fun", "relaxed"), ("Fcl_ALL_Sorrow", "sad"), ("Fcl_ALL_Surprised", "surprised"),
            // Maya（ブレンドシェイプの節の名前が前に付く）・そのほか
            ("bs_face.mouth_a", "aa"), ("bs_face.eye_blink", "blink"), ("bs_face.eye_winkL", "blinkLeft"), ("bs_face.eye_smile", "happy"),
            ("lip_u", "ou"), ("eye_close", "blink"), ("face_relax", "relaxed"), ("face_angry", "angry"), ("look_left", "lookLeft"), ("e", "ee"),
            // iPhone の顔の動き
            ("eyeBlinkRight", "blinkRight"), ("jawOpen", "aa"), ("mouthSmileLeft", "smileLeft"),
            // 当てはめないもの
            ("bs_face.mouth_a2", null), ("bs_face.mouth_smile", null), ("Fcl_MTH_Close", null), ("Fcl_EYE_Joy", null), ("wink", null), ("close", null),
            ("mouth_wide", null), ("Fcl_HA_Hide", null), ("eyeLookUpLeft", null), ("まばたき", null),
        };
        foreach (var (name, key) in expected)
        {
            Assert.AreEqual(key, FbxExpressions.Classify(name)?.Key, name);
        }
        Assert.IsLessThan(FbxExpressions.Classify("mouth_a")!.Value.Priority, FbxExpressions.Classify("vrc.v_aa")!.Value.Priority, "VRChat の名前を先に");
        Assert.IsLessThan(FbxExpressions.Classify("eye_close")!.Value.Priority, FbxExpressions.Classify("Blink")!.Value.Priority);
    }

    [TestMethod]
    public void ExpressionsChooseNamesAndCombineSides()
    {
        // 決まった付け方の名前を先に使い、同じ名前の表情の元はすべての形で使う
        List<VrmExpression> e = FbxExpressions.Guess([(0, ["mouth_a", "vrc.v_aa", "Blink_L", "Blink_R"]), (3, ["vrc.v_aa"])]);
        VrmExpression aa = e.Single(x => x.Key == "aa");
        CollectionAssert.AreEquivalent(new[] { (0, 1, 1f), (3, 0, 1f) }, aa.MorphBinds.ToArray());
        // 両目を閉じる表情の元が無ければ、左右を合わせる
        VrmExpression blink = e.Single(x => x.Key == "blink");
        CollectionAssert.AreEquivalent(new[] { (0, 2, 1f), (0, 3, 1f) }, blink.MorphBinds.ToArray());
        Assert.AreEqual((0, 2, 1f), e.Single(x => x.Key == "blinkLeft").MorphBinds.Single());

        // iPhone の顔の動きだけのモデル
        e = FbxExpressions.Guess([(0, ["eyeBlinkLeft", "eyeBlinkRight", "jawOpen", "mouthSmileLeft", "mouthSmileRight", "browInnerUp"])]);
        CollectionAssert.AreEquivalent(new[] { "blink", "blinkLeft", "blinkRight", "aa", "relaxed" }, e.Select(x => x.Key).ToArray());
        Assert.HasCount(2, e.Single(x => x.Key == "relaxed").MorphBinds, "にっこりは左右の口角");

        // 表情の元がもう MMD の名前なら、その表情は作らない（そのまま使える）
        e = FbxExpressions.Guess([(0, ["あ", "vrc.v_aa", "vrc.v_ih"])]);
        CollectionAssert.AreEqual(new[] { "ih" }, e.Select(x => x.Key).ToArray());
    }

    [TestMethod]
    public void BlendShapeNamesBecomeMmdExpressions()
    {
        // 顔と歯の 2 つの形に、同じ名前の口の表情の元
        var scene = new TestScene();
        foreach (string part in new[] { "Face", "Teeth" })
        {
            long model = scene.Add("Model", part, "Mesh");
            scene.Connect(model, 0);
            long geometry = Quad(scene);
            scene.Connect(geometry, model);
            long blend = scene.Add("Deformer", part, "BlendShape");
            scene.Connect(blend, geometry);
            foreach ((string name, int cp) in new[] { ("vrc.v_aa", 0), ("Blink", 2) })
            {
                if (part == "Teeth" && name == "Blink")
                {
                    continue;
                }
                long channel = scene.Add("Deformer", name, "BlendShapeChannel", new FbxTestNode("DeformPercent", 0.0), new FbxTestNode("FullWeights", new[] { 100.0 }));
                long shape = scene.Add("Geometry", name, "Shape", new FbxTestNode("Indexes", new[] { cp }), new FbxTestNode("Vertices", new[] { 0.0, -8.0, 0.0 }));
                scene.Connect(channel, blend);
                scene.Connect(shape, channel);
            }
        }
        PmxLoadResult result = FbxReader.Read(FbxTestWriter.Binary(scene.Nodes(7400)));
        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;
        CollectionAssert.AreEqual(new[] { "まばたき", "あ", "vrc.v_aa", "Blink" }, d.Morphs.Select(m => m.Name).ToArray(), "MMD の名前の表情 → 元の表情の元（名前のまま）");
        PmxMorph a = d.Morphs.Single(m => m.Name == "あ");
        Assert.AreEqual(PmxMorphPanel.Mouth, a.Panel);
        Assert.HasCount(2, a.VertexOffsets, "顔と歯の両方");
        AssertNear(new Vector3(0, -8 * Cm, 0), a.VertexOffsets[0].Translation);
        Assert.AreEqual(PmxMorphPanel.Eye, d.Morphs[0].Panel);
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("表情の元の名前から", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MotionIsRetargetedToTheCharacter()
    {
        // 元: T の字の人型（cm・正面が +Z）。1 秒で、左腕を Z の周りに 60 度上げ、頭を Y の周りに 30 度（モデルの左へ）、腰を 10 cm 下げる
        var (motion, bones) = MotionSkeleton();
        AddCurves(motion, bones["LeftArm"], "Lcl Rotation", "R", [0, 0, 0], [0, 0, 60]);
        AddCurves(motion, bones["Head"], "Lcl Rotation", "R", [0, 0, 0], [0, 30, 0]);
        AddCurves(motion, bones["Hips"], "Lcl Translation", "T", [0, 100, 0], [0, 90, 0]);
        // キャラクター: 同じ骨組みを読み込んだもの（腕は A の字）
        var (model, _) = MotionSkeleton();
        long body = model.Add("Model", "Body", "Mesh");
        model.Connect(body, 0);
        model.Connect(Triangle(model, [0, 0, 0, 10, 0, 0, 0, 10, 0]), body);
        PmxDocument target = Load(FbxTestWriter.Binary(model.Nodes(7400)));

        VmdLoadResult result = FbxMotionReader.Read(FbxTestWriter.Binary(motion.Nodes(7400)), target);
        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        VmdDocument vmd = result.Document!;
        Assert.AreEqual(30, vmd.LastFrame, "1 秒を 30 fps で");
        Assert.AreEqual(31, vmd.BoneKeys.Count(k => k.Bone.Text == "左腕"), "毎フレームにキー");
        // 足の IK を切る
        CollectionAssert.IsSubsetOf(new[] { "左足ＩＫ", "右足ＩＫ", "左つま先ＩＫ", "右つま先ＩＫ" },
            vmd.VisibilityKeys.Single().IkStates.Where(s => !s.Enabled).Select(s => s.Bone.Text).ToArray());

        // 0 フレーム目: 元と同じく T の字（キャラクターは A の字なので、腕を水平に戻す）
        AssertNear(Vector3.UnitX, Direction(target, vmd, 0, "左腕", "左ひじ"), "腕は水平");
        AssertNear(-Vector3.UnitX, Direction(target, vmd, 0, "右腕", "右ひじ"));
        // 30 フレーム目: 元と同じ向き（MMD の座標。正面は -Z）
        AssertNear(new Vector3(0.5f, 0.866f, 0), Direction(target, vmd, 30, "左腕", "左ひじ"), "左腕は 60 度上");
        AssertNear(new Vector3(0.5f, 0.866f, 0), Direction(target, vmd, 30, "左ひじ", "左手首"), "前腕も一緒に");
        AssertNear(new Vector3(0.5f, 0, -0.866f), Vector3.Transform(-Vector3.UnitZ, Global(target, vmd, 30, "頭")), "頭はモデルの左へ");
        AssertNear(new Vector3(0, -1.25f, 0), vmd.BoneKeys.Single(k => k.Bone.Text == "センター" && k.Frame == 30).Translation, "腰の 10 cm はセンターの 1.25");
        AssertNear(-Vector3.UnitY, Direction(target, vmd, 30, "右足", "右ひざ"), "動かない足はそのまま");
    }

    [TestMethod]
    public void MotionNeedsAHumanoidAndAnimation()
    {
        var (model, _) = MotionSkeleton();
        long body = model.Add("Model", "Body", "Mesh");
        model.Connect(body, 0);
        model.Connect(Triangle(model, [0, 0, 0, 10, 0, 0, 0, 10, 0]), body);
        PmxDocument target = Load(FbxTestWriter.Binary(model.Nodes(7400)));

        var (still, _) = MotionSkeleton();
        VmdLoadResult noAnimation = FbxMotionReader.Read(FbxTestWriter.Binary(still.Nodes(7400)), target);
        Assert.IsFalse(noAnimation.Succeeded);
        Assert.IsTrue(noAnimation.Report.Issues.Any(i => i.Message.Contains("アニメーション", StringComparison.Ordinal)));

        var props = new TestScene();
        long door = props.Add("Model", "Door", "Null");
        props.Connect(door, 0);
        AddCurves(props, door, "Lcl Rotation", "R", [0, 0, 0], [0, 90, 0]);
        VmdLoadResult notHuman = FbxMotionReader.Read(FbxTestWriter.Binary(props.Nodes(7400)), target);
        Assert.IsFalse(notHuman.Succeeded);
        Assert.IsTrue(notHuman.Report.Issues.Any(i => i.Message.Contains("人型", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RealFbxMotionIsRetargeted()
    {
        // アリシアの骨に Blender で付けた動き（artifacts\motion-samples。リポジトリには入れない）を、Twist（VRM）に当てはめる
        string? folder = FindSamples("motion-samples", "*.fbx");
        string? vrmFolder = FindSamples("vrm-samples", "*.vrm");
        if (folder is null || vrmFolder is null || !File.Exists(Path.Combine(folder, "Alicia_Motion.fbx")))
        {
            Assert.Inconclusive("artifacts\\motion-samples に確かめ用のモーションがありません");
        }
        PmxDocument target = GltfReader.Load(Path.Combine(vrmFolder!, "VRM1_Constraint_Twist_Sample.vrm")).Document!;
        VmdLoadResult result = FbxMotionReader.Load(Path.Combine(folder!, "Alicia_Motion.fbx"), target);
        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        VmdDocument vmd = result.Document!;
        // Blender で付けた向き（上が +Z・正面が -Y）を MMD の座標（上が +Y・正面が -Z）にしたもの
        AssertNear(new Vector3(0.5f, 0.866f, 0), Direction(target, vmd, 30, "左腕", "左ひじ"), "左腕を 60 度上", 0.06f);
        AssertNear(new Vector3(0, 0, -1), Direction(target, vmd, 30, "右ひじ", "右手首"), "右の前腕を前へ", 0.06f);
        AssertNear(new Vector3(0, -0.707f, -0.707f), Direction(target, vmd, 30, "左足", "左ひざ"), "左の太ももを前へ 45 度", 0.06f);
        AssertNear(new Vector3(0, -0.966f, 0.259f), Direction(target, vmd, 30, "左ひざ", "左足首"), "ひざを 60 度", 0.06f);
        AssertNear(new Vector3(0.47f, -0.342f, -0.814f), Vector3.Transform(-Vector3.UnitZ, Global(target, vmd, 30, "頭")), "頭を左へ（背骨と一緒に前へ）", 0.06f);
        AssertNear(new Vector3(0, 0.94f, -0.342f), Vector3.Transform(Vector3.UnitY, Global(target, vmd, 30, "上半身")), "背骨を前へ 20 度", 0.06f);
        Assert.IsLessThan(-1f, vmd.BoneKeys.Single(k => k.Bone.Text == "センター" && k.Frame == 30).Translation.Y, "腰を下げる");
        AssertNear(Vector3.UnitX, Direction(target, vmd, 0, "左腕", "左ひじ"), "0 フレーム目は T の字", 0.06f);
    }

    // モーションの確かめ用の人型（Mixamo と同じ名前・T の字・cm・正面が +Z）
    private static (TestScene Scene, Dictionary<string, long> Bones) MotionSkeleton()
    {
        var scene = new TestScene();
        var bones = new Dictionary<string, long>();
        long Bone(string name, string? parent, double x, double y, double z)
        {
            long id = scene.Add("Model", name, "LimbNode", Props(P("Lcl Translation", "Lcl Translation", x, y, z)));
            scene.Connect(id, parent is null ? 0 : bones[parent]);
            bones[name] = id;
            return id;
        }
        Bone("Hips", null, 0, 100, 0);
        Bone("Spine", "Hips", 0, 10, 0);
        Bone("Spine1", "Spine", 0, 15, 0);
        Bone("Neck", "Spine1", 0, 20, 0);
        Bone("Head", "Neck", 0, 10, 0);
        foreach ((string side, double sign) in new[] { ("Left", 1.0), ("Right", -1.0) })
        {
            Bone(side + "Shoulder", "Spine1", sign * 3, 18, 0);
            Bone(side + "Arm", side + "Shoulder", sign * 10, 0, 0);
            Bone(side + "ForeArm", side + "Arm", sign * 25, 0, 0);
            Bone(side + "Hand", side + "ForeArm", sign * 22, 0, 0);
            Bone(side + "UpLeg", "Hips", sign * 10, -5, 0);
            Bone(side + "Leg", side + "UpLeg", 0, -45, 0);
            Bone(side + "Foot", side + "Leg", 0, -42, 0);
            Bone(side + "ToeBase", side + "Foot", 0, -6, 12);
        }
        return (scene, bones);
    }

    // 0 秒の値から 1 秒の値へ、まっすぐ動く曲線（X・Y・Z）を、テイクの 1 つ目のレイヤーに足す
    private static void AddCurves(TestScene scene, long model, string property, string kind, double[] from, double[] to)
    {
        const long Second = 46186158000;
        long stack = scene.Stack ??= scene.Add("AnimationStack", "Take 001", string.Empty,
            Props(P("LocalStart", "KTime", 0L), P("LocalStop", "KTime", Second)));
        long layer = scene.Layer ??= AddLayer(scene, stack);
        long node = scene.Add("AnimationCurveNode", kind, string.Empty, Props(P("d|X", "Number", from[0]), P("d|Y", "Number", from[1]), P("d|Z", "Number", from[2])));
        scene.Connect(node, layer);
        scene.ConnectProperty(node, model, property);
        string[] axes = ["d|X", "d|Y", "d|Z"];
        for (int k = 0; k < 3; k++)
        {
            long curve = scene.Add("AnimationCurve", string.Empty, string.Empty, new FbxTestNode("Default", from[k]),
                new FbxTestNode("KeyTime", new[] { 0L, Second }), new FbxTestNode("KeyValueFloat", new[] { (float)from[k], (float)to[k] }));
            scene.ConnectProperty(curve, node, axes[k]);
        }
    }

    private static long AddLayer(TestScene scene, long stack)
    {
        long layer = scene.Add("AnimationLayer", "BaseLayer", string.Empty);
        scene.Connect(layer, stack);
        return layer;
    }

    // キーを当てはめた、キャラクターのボーンのワールドでの回転（親から順に掛ける。キーの無いボーンは回さない）
    private static Quaternion Global(PmxDocument target, VmdDocument vmd, int frame, string bone)
    {
        var local = vmd.BoneKeys.Where(k => k.Frame == frame).ToDictionary(k => k.Bone.Text, k => k.Rotation);
        Quaternion Of(int index) => index < 0 ? Quaternion.Identity
            : Of(target.Bones[index].ParentIndex) * (local.TryGetValue(target.Bones[index].Name, out Quaternion q) ? q : Quaternion.Identity);
        return Of(Array.FindIndex(target.Bones, b => b.Name == bone));
    }

    private static Vector3 Direction(PmxDocument target, VmdDocument vmd, int frame, string from, string to)
    {
        PmxBone a = target.Bones.Single(b => b.Name == from);
        PmxBone b = target.Bones.Single(b => b.Name == to);
        return Vector3.Normalize(Vector3.Transform(b.Position - a.Position, Global(target, vmd, frame, from)));
    }

    [TestMethod]
    public void RealFbxSamplesLoad()
    {
        // VRM の公式サンプルを Blender で FBX にしたもの（artifacts\fbx-samples に置いたとき。リポジトリには入れない）
        string? folder = FindSamples("fbx-samples", "*.fbx");
        if (folder is null)
        {
            Assert.Inconclusive("artifacts\\fbx-samples に FBX のサンプルがありません");
        }
        string? vrmFolder = FindSamples("vrm-samples", "*.vrm");
        var sameExpressions = new Dictionary<string, string[]>
        {
            ["AliciaSolid"] = ["まばたき", "あ", "い", "う", "え", "お"],
            ["Seed-san"] = ["ウィンク", "ウィンク右", "あ", "い", "う", "え", "お", "視線上", "視線下", "視線左", "視線右"],
            ["Twist"] = ["まばたき", "ウィンク", "ウィンク右", "あ", "い", "う", "え", "お", "笑い", "にっこり", "怒り", "困る", "びっくり"],
        };
        var vrmOf = new Dictionary<string, string>
        {
            ["AliciaSolid"] = "AliciaSolid_vrm-0.51.vrm",
            ["Seed-san"] = "Seed-san.vrm",
            ["Twist"] = "VRM1_Constraint_Twist_Sample.vrm",
        };
        foreach (string path in Directory.GetFiles(folder!, "*.fbx"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            PmxLoadResult result = FbxReader.Load(path);
            Assert.IsTrue(result.Succeeded, $"{name}: " + string.Join("\n", result.Report.Issues.Select(i => i.Message)));
            Assert.AreEqual(0, result.Report.Issues.Count(i => i.Severity != LoadSeverity.Info), $"{name}: " + string.Join("\n", result.Report.Issues.Select(i => i.Message)));
            PmxDocument d = result.Document!;
            float top = d.Vertices.Max(v => v.Position.Y);
            float bottom = d.Vertices.Min(v => v.Position.Y);
            Assert.IsGreaterThan(12f, top - bottom, $"{name}: 背丈");
            Assert.IsLessThan(25f, top - bottom, $"{name}: 背丈");
            Assert.IsLessThan(0.5f, Math.Abs(bottom), $"{name}: 足が床の近く");
            Assert.IsTrue(d.Materials.All(m => m.TextureIndex >= 0) || name == "Seed-san", $"{name}: テクスチャ");
            Assert.IsGreaterThan(40, d.Morphs.Length, $"{name}: 表情の元");
            Assert.IsGreaterThan(100, d.Bones.Length, $"{name}: ボーン");
            // 人型のボーン（2 段目）。左手は MMD と同じく +X（正面が -Z）
            PmxBone? leftHand = d.Bones.FirstOrDefault(b => b.Name == "左手首");
            Assert.IsNotNull(leftHand, $"{name}: 左手首");
            Assert.IsGreaterThan(3f, leftHand.Position.X, $"{name}: 左手");
            Assert.IsTrue(d.Bones.Any(b => b.Name == "左足ＩＫ"), $"{name}: 足の IK");
            // 元の VRM と三角形の数が同じ。ボーンの名前から見つけた人型のボーンは、VRM の人型の決まりと同じ所に来る（2 段目）
            if (vrmFolder is not null && vrmOf.TryGetValue(name, out string? vrm) && File.Exists(Path.Combine(vrmFolder, vrm)))
            {
                PmxDocument original = GltfReader.Load(Path.Combine(vrmFolder, vrm)).Document!;
                Assert.HasCount(original.Indices.Length, d.Indices, $"{name}: 三角形の数");
                Assert.AreEqual(original.Vertices.Max(v => v.Position.Y), d.Vertices.Max(v => v.Position.Y), 0.05f, $"{name}: 背丈が元の VRM と同じ");
                int compared = 0;
                foreach (PmxBone bone in original.Bones.Where(b => b.Name.Length > 0 && !b.Name.Any(char.IsAsciiLetter)))
                {
                    PmxBone? same = d.Bones.FirstOrDefault(b => b.Name == bone.Name);
                    Assert.IsNotNull(same, $"{name}: VRM にある「{bone.Name}」が FBX に無い");
                    Assert.IsLessThan(0.02f, Vector3.Distance(bone.Position, same.Position), $"{name}: 「{bone.Name}」の位置 VRM {bone.Position}・FBX {same.Position}");
                    compared++;
                }
                Assert.IsGreaterThan(40, compared, $"{name}: 比べた MMD の名前のボーン");

                // 3 段目: 表情の元の名前から作った表情が、VRM の表情と同じ形の変化になる（VRM が同じ表情の元を 1 つだけ使うもの）
                foreach (string expression in sameExpressions[name])
                {
                    AssertSameMorph(original, d, expression, name);
                }
            }
        }
    }

    // ---- 組み立ての道具 ----

    private static PmxDocument Load(byte[] data)
    {
        PmxLoadResult result = FbxReader.Read(data);
        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        return result.Document!;
    }

    private static FbxTestNode P(string name, string type, params object[] values) => new("P", [name, type, string.Empty, "A", .. values]);

    private static FbxTestNode Props(params FbxTestNode[] properties) => new FbxTestNode("Properties70").With(properties);

    private static double[] Translation(double x, double y, double z) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1];

    // 四角形 1 つ（cm）。法線は頂点ごと、UV は番号で引く
    private static long Quad(TestScene scene) => scene.Add("Geometry", "Quad", "Mesh",
        new FbxTestNode("Vertices", new double[] { 0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0 }),
        new FbxTestNode("PolygonVertexIndex", new[] { 0, 1, 2, ~3 }),
        new FbxTestNode("LayerElementNormal", 0).With(
            new FbxTestNode("MappingInformationType", "ByPolygonVertex"), new FbxTestNode("ReferenceInformationType", "Direct"),
            new FbxTestNode("Normals", new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 })),
        new FbxTestNode("LayerElementUV", 0).With(
            new FbxTestNode("MappingInformationType", "ByPolygonVertex"), new FbxTestNode("ReferenceInformationType", "IndexToDirect"),
            new FbxTestNode("UV", new double[] { 0, 0, 1, 0, 1, 1, 0, 1 }), new FbxTestNode("UVIndex", new[] { 0, 1, 2, 3 })),
        new FbxTestNode("LayerElementMaterial", 0).With(
            new FbxTestNode("MappingInformationType", "AllSame"), new FbxTestNode("ReferenceInformationType", "IndexToDirect"),
            new FbxTestNode("Materials", new[] { 0 })));

    private static long Triangle(TestScene scene, double[] points) => scene.Add("Geometry", "Tri", "Mesh",
        new FbxTestNode("Vertices", points), new FbxTestNode("PolygonVertexIndex", new[] { 0, 1, ~2 }));

    // 三角形 1 つの形を持つノード（材質の名前 = ノードの名前。形の点の 1 つ目が first）
    private static long AddPart(TestScene scene, string name, double[] first, params FbxTestNode[] properties) =>
        AddPart(scene, name, first, 0, properties);

    private static long AddPart(TestScene scene, string name, double[] first, long parent, params FbxTestNode[] properties)
    {
        long model = scene.Add("Model", name, "Mesh", Props(properties));
        scene.Connect(model, parent);
        scene.Connect(Triangle(scene, [first[0], first[1], first[2], first[0] + 1, first[1], first[2], first[0], first[1] + 1, first[2]]), model);
        long material = scene.Add("Material", name, string.Empty);
        scene.Connect(material, model);
        return model;
    }

    private static long AddTexturedMaterial(TestScene scene, long model, string name, string relative, string absolute, byte[]? content)
    {
        long material = scene.Add("Material", name, string.Empty, Props(P("DiffuseColor", "Color", 0.8, 0.8, 0.8)));
        scene.Connect(material, model);
        long texture = scene.Add("Texture", name, string.Empty, new FbxTestNode("RelativeFilename", relative), new FbxTestNode("FileName", absolute));
        scene.ConnectProperty(texture, material, "DiffuseColor");
        var videoChildren = new List<FbxTestNode> { new("RelativeFilename", relative), new("Filename", absolute) };
        if (content is not null)
        {
            videoChildren.Add(new FbxTestNode("Content", content));
        }
        long video = scene.Add("Video", name, "Clip", [.. videoChildren]);
        scene.Connect(video, texture);
        return material;
    }

    // 材質の面が使う頂点の位置（面の順）
    private static Vector3[] VerticesOf(PmxDocument d, string material)
    {
        int start = 0;
        foreach (PmxMaterial m in d.Materials)
        {
            if (m.Name == material)
            {
                return [.. d.Indices.Skip(start).Take(m.IndexCount).Select(i => d.Vertices[i].Position)];
            }
            start += m.IndexCount;
        }
        throw new AssertFailedException($"材質「{material}」がありません");
    }

    // 字下げで親子を書いた木（1 字で 1 段）→ 名前と親の番号
    private static (string[] Names, int[] Parents) Tree(string[] lines)
    {
        var names = new string[lines.Length];
        var parents = new int[lines.Length];
        var last = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            int depth = lines[i].Length - lines[i].TrimStart(' ').Length;
            names[i] = lines[i].TrimStart(' ');
            parents[i] = depth > 0 && depth - 1 < last.Count ? last[depth - 1] : -1;
            if (last.Count > depth)
            {
                last.RemoveRange(depth, last.Count - depth);
            }
            last.Add(i);
        }
        return (names, parents);
    }

    // 左右の枝: 「$」は左右の語（Left・Right など）、「#」は左右の 1 文字（L・R など）にする
    private static string[] Sided(string[] lines, string left, string right, string leftLetter = "L", string rightLetter = "R") =>
    [
        .. lines.Select(l => l.Replace("$", left, StringComparison.Ordinal).Replace("#", leftLetter, StringComparison.Ordinal)),
        .. lines.Select(l => l.Replace("$", right, StringComparison.Ordinal).Replace("#", rightLetter, StringComparison.Ordinal)),
    ];

    private static void AssertHumanoid(string[] names, int[] parents, Dictionary<string, string> expected)
    {
        Dictionary<string, int> human = FbxHumanoid.Guess(names, parents, out string[] missing);
        Assert.IsNotEmpty(human, "人型が見つからない: " + string.Join(",", missing));
        foreach (var (bone, name) in expected)
        {
            Assert.IsTrue(human.TryGetValue(bone, out int node), $"{bone} が見つからない（期待 {name}）");
            Assert.AreEqual(name, names[node], bone);
        }
        Assert.HasCount(human.Count, human.Values.Distinct(), "1 つのボーンを 2 つの名前に使わない");
    }

    // 2 つのモデルの同じ名前の表情が、同じ位置の頂点を同じだけ動かす（頂点の番号は違ってよい）
    private static void AssertSameMorph(PmxDocument expected, PmxDocument actual, string morph, string model)
    {
        PmxMorph? e = expected.Morphs.FirstOrDefault(m => m.Name == morph);
        PmxMorph? a = actual.Morphs.FirstOrDefault(m => m.Name == morph);
        Assert.IsNotNull(e, $"{model}: VRM に「{morph}」が無い");
        Assert.IsNotNull(a, $"{model}: FBX に「{morph}」が無い");
        Assert.AreEqual(PmxMorphType.Vertex, a.Type, $"{model}: {morph}");
        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 500), (int)MathF.Round(p.Y * 500), (int)MathF.Round(p.Z * 500));
        var moved = new Dictionary<(int, int, int), List<Vector3>>();
        foreach (PmxVertexOffset o in a.VertexOffsets)
        {
            var key = Key(actual.Vertices[o.Vertex].Position);
            if (!moved.TryGetValue(key, out List<Vector3>? list))
            {
                list = [];
                moved[key] = list;
            }
            list.Add(o.Translation);
        }
        int same = e.VertexOffsets.Count(o => moved.TryGetValue(Key(expected.Vertices[o.Vertex].Position), out List<Vector3>? list)
            && list.Any(t => Vector3.Distance(t, o.Translation) < 2e-3f));
        Assert.IsGreaterThanOrEqualTo(e.VertexOffsets.Count * 0.98, (double)same, $"{model}: 「{morph}」の同じ頂点の動き {same}/{e.VertexOffsets.Count}");
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, string? message = null, float tolerance = 1e-3f) =>
        Assert.IsLessThan(tolerance, Vector3.Distance(expected, actual), $"期待 {expected}・実際 {actual} {message}");

    private static string? FindSamples(string folder, string pattern)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", folder);
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, pattern).Length > 0)
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>テスト用の FBX の場面（物とつながり）。</summary>
    private sealed class TestScene
    {
        private long _next = 1000;
        private readonly List<FbxTestNode> _objects = [];
        private readonly List<FbxTestNode> _connections = [];

        public List<FbxTestNode> Global { get; set; } =
            [P("UpAxis", "int", 1), P("UpAxisSign", "int", 1), P("FrontAxis", "int", 2), P("FrontAxisSign", "int", 1),
             P("CoordAxis", "int", 0), P("CoordAxisSign", "int", 1), P("UnitScaleFactor", "double", 1.0)];

        public long Add(string kind, string name, string type, params FbxTestNode[] children)
        {
            long id = _next++;
            _objects.Add(new FbxTestNode(kind, id, new FbxName(kind, name), type).With(children));
            return id;
        }

        public void Connect(long child, long parent) => _connections.Add(new FbxTestNode("C", "OO", child, parent));

        public void ConnectProperty(long child, long parent, string property) => _connections.Add(new FbxTestNode("C", "OP", child, parent, property));

        /// <summary>モーションのテイクとレイヤー（<see cref="AddCurves"/> で作る）。</summary>
        public long? Stack { get; set; }

        public long? Layer { get; set; }

        public List<FbxTestNode> Nodes(int version) =>
        [
            new FbxTestNode("FBXHeaderExtension").With(new FbxTestNode("FBXVersion", version)),
            new FbxTestNode("Creator", "3D Theater のテスト"),
            new FbxTestNode("GlobalSettings").With(new FbxTestNode("Version", 1000), Props([.. Global])),
            new FbxTestNode("Objects").With([.. _objects]),
            new FbxTestNode("Connections").With([.. _connections]),
        ];
    }
}

/// <summary>物の名前（バイナリは「名前\0\x01種類」、文字の FBX は「種類::名前」と書く）。</summary>
internal sealed record FbxName(string Kind, string Name);

internal sealed class FbxTestNode(string name, params object[] properties)
{
    public string Name { get; } = name;

    public object[] Properties { get; } = properties;

    public List<FbxTestNode> Children { get; } = [];

    public FbxTestNode With(params FbxTestNode[] children)
    {
        Children.AddRange(children);
        return this;
    }
}

/// <summary>テスト用に FBX を書く（バイナリと文字）。</summary>
internal static class FbxTestWriter
{
    public static byte[] Binary(IReadOnlyList<FbxTestNode> nodes, int version = 7400, bool compress = false)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("Kaydara FBX Binary  \0"u8);
        w.Write((byte)0x1A);
        w.Write((byte)0);
        w.Write((uint)version);
        bool wide = version >= 7500;
        foreach (FbxTestNode node in nodes)
        {
            WriteNode(w, node, wide, compress);
        }
        w.Write(new byte[wide ? 25 : 13]);
        w.Write(new byte[16]);
        w.Flush();
        return stream.ToArray();
    }

    private static void WriteNode(BinaryWriter w, FbxTestNode node, bool wide, bool compress)
    {
        long start = w.BaseStream.Position;
        w.Write(new byte[wide ? 24 : 12]);
        byte[] name = Encoding.ASCII.GetBytes(node.Name);
        w.Write((byte)name.Length);
        w.Write(name);
        long propertiesStart = w.BaseStream.Position;
        foreach (object p in node.Properties)
        {
            WriteProperty(w, p, compress);
        }
        long propertiesLength = w.BaseStream.Position - propertiesStart;
        foreach (FbxTestNode child in node.Children)
        {
            WriteNode(w, child, wide, compress);
        }
        if (node.Children.Count > 0)
        {
            w.Write(new byte[wide ? 25 : 13]);
        }
        long end = w.BaseStream.Position;
        w.BaseStream.Position = start;
        if (wide)
        {
            w.Write((ulong)end);
            w.Write((ulong)node.Properties.Length);
            w.Write((ulong)propertiesLength);
        }
        else
        {
            w.Write((uint)end);
            w.Write((uint)node.Properties.Length);
            w.Write((uint)propertiesLength);
        }
        w.BaseStream.Position = end;
    }

    private static void WriteProperty(BinaryWriter w, object p, bool compress)
    {
        switch (p)
        {
            case FbxName n:
                Bytes(w, 'S', Encoding.UTF8.GetBytes($"{n.Name}\0\u0001{n.Kind}"));
                break;
            case string s:
                Bytes(w, 'S', Encoding.UTF8.GetBytes(s));
                break;
            case byte[] raw:
                Bytes(w, 'R', raw);
                break;
            case long l:
                w.Write((byte)'L');
                w.Write(l);
                break;
            case int i:
                w.Write((byte)'I');
                w.Write(i);
                break;
            case double d:
                w.Write((byte)'D');
                w.Write(d);
                break;
            case double[] doubles:
                Array(w, 'd', MemoryMarshal.AsBytes(doubles.AsSpan()), doubles.Length, compress);
                break;
            case float[] floats:
                Array(w, 'f', MemoryMarshal.AsBytes(floats.AsSpan()), floats.Length, compress);
                break;
            case long[] longs:
                Array(w, 'l', MemoryMarshal.AsBytes(longs.AsSpan()), longs.Length, compress);
                break;
            case int[] ints:
                Array(w, 'i', MemoryMarshal.AsBytes(ints.AsSpan()), ints.Length, compress);
                break;
            default:
                throw new ArgumentException($"書けない値: {p.GetType().Name}");
        }
    }

    private static void Bytes(BinaryWriter w, char type, byte[] bytes)
    {
        w.Write((byte)type);
        w.Write((uint)bytes.Length);
        w.Write(bytes);
    }

    private static void Array(BinaryWriter w, char type, ReadOnlySpan<byte> bytes, int count, bool compress)
    {
        w.Write((byte)type);
        w.Write((uint)count);
        if (compress)
        {
            using var packed = new MemoryStream();
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(bytes);
            }
            w.Write(1u);
            w.Write((uint)packed.Length);
            w.Write(packed.ToArray());
        }
        else
        {
            w.Write(0u);
            w.Write((uint)bytes.Length);
            w.Write(bytes);
        }
    }

    public static byte[] Ascii(IReadOnlyList<FbxTestNode> nodes)
    {
        var text = new StringBuilder("; FBX 7.4.0 project file\n; 3D Theater のテスト\n\n");
        foreach (FbxTestNode node in nodes)
        {
            WriteAscii(text, node, 0);
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static void WriteAscii(StringBuilder text, FbxTestNode node, int depth)
    {
        string indent = new('\t', depth);
        text.Append(indent).Append(node.Name).Append(": ");
        if (node.Properties is [System.Array array] && array is not byte[])
        {
            object[] values = [.. array.Cast<object>()];
            text.Append('*').Append(values.Length).Append(" {\n")
                .Append(indent).Append("\ta: ").Append(string.Join(",", values.Select(Value))).Append('\n')
                .Append(indent).Append("}\n");
            return;
        }
        text.Append(string.Join(", ", node.Properties.Select(Value)));
        if (node.Children.Count > 0)
        {
            text.Append(" {\n");
            foreach (FbxTestNode child in node.Children)
            {
                WriteAscii(text, child, depth + 1);
            }
            text.Append(indent).Append("}\n");
        }
        else
        {
            text.Append('\n');
        }
    }

    private static string Value(object p) => p switch
    {
        FbxName n => $"\"{n.Kind}::{n.Name}\"",
        string s => $"\"{s.Replace("\"", "&quot;", StringComparison.Ordinal)}\"",
        byte[] raw => $"\"{Convert.ToBase64String(raw)}\"",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => p.ToString() ?? string.Empty,
    };
}
