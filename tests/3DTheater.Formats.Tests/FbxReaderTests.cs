using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Theater3D.Formats.Fbx;
using Theater3D.Formats.Gltf;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

/// <summary>FBX を、PMX と同じ中身にする（設計判断記録 83 章）。</summary>
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
        CollectionAssert.AreEqual(new[] { "smile", "vrc.blink" }, d.Morphs.Select(m => m.Name).ToArray(), "チャンネルの名前のまま");
        PmxVertexOffset offset = d.Morphs[0].VertexOffsets.Single();
        Assert.AreEqual(2, offset.Vertex);
        AssertNear(new Vector3(0, 8 * Cm, 0), offset.Translation);
        Assert.HasCount(2, d.Morphs[1].VertexOffsets);
        AssertNear(new Vector3(0, 0, -8 * Cm), d.Morphs[1].VertexOffsets[0].Translation, "Z を裏返す");
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
    public void RealFbxSamplesLoad()
    {
        // VRM の公式サンプルを Blender で FBX にしたもの（artifacts\fbx-samples に置いたとき。リポジトリには入れない）
        string? folder = FindSamples("fbx-samples", "*.fbx");
        if (folder is null)
        {
            Assert.Inconclusive("artifacts\\fbx-samples に FBX のサンプルがありません");
        }
        string? vrmFolder = FindSamples("vrm-samples", "*.vrm");
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
            // 左手は MMD と同じく +X（正面が -Z）
            PmxBone? leftHand = d.Bones.FirstOrDefault(b => b.Name is "LeftHand" or "J_Bip_L_Hand" or "leftHand" or "hand_L");
            if (leftHand is not null)
            {
                Assert.IsGreaterThan(3f, leftHand.Position.X, $"{name}: 左手");
            }
            // 元の VRM と三角形の数が同じ
            if (vrmFolder is not null && vrmOf.TryGetValue(name, out string? vrm) && File.Exists(Path.Combine(vrmFolder, vrm)))
            {
                PmxDocument original = GltfReader.Load(Path.Combine(vrmFolder, vrm)).Document!;
                Assert.HasCount(original.Indices.Length, d.Indices, $"{name}: 三角形の数");
                Assert.AreEqual(original.Vertices.Max(v => v.Position.Y), top, 0.05f, $"{name}: 背丈が元の VRM と同じ");
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

    private static void AssertNear(Vector3 expected, Vector3 actual, string? message = null) =>
        Assert.IsLessThan(1e-3f, Vector3.Distance(expected, actual), $"期待 {expected}・実際 {actual} {message}");

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
