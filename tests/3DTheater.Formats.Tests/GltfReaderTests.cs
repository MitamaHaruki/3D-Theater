using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Theater3D.Formats.Gltf;
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
        Assert.IsFalse(result.Report.Issues.Any(i => i.Severity != LoadSeverity.Info), "注意やエラーは無い");
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

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.IsLessThan(1e-4f, Vector3.Distance(expected, actual), $"期待 {expected}・実際 {actual}");
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
