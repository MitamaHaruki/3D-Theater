using System.IO;
using System.Numerics;
using System.Text;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

[TestClass]
public sealed class PmxReaderTests
{
    /// <summary>
    /// 仕様の並びどおりに手でバイト列を組み、テスト用の書き出し器を通さずに読めることを確かめる
    /// （書き出し器と読み込み側が同じ思い込みで間違えていないことの確認）。
    /// </summary>
    [TestMethod]
    public void ReadsHandBuiltMinimalFile()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Text(string s)
        {
            byte[] b = Encoding.Unicode.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }
        void Floats(params float[] values)
        {
            foreach (float v in values)
            {
                w.Write(v);
            }
        }

        w.Write("PMX "u8);
        w.Write(2.0f);
        w.Write((byte)8);
        w.Write(new byte[] { 0, 0, 1, 1, 1, 1, 1, 1 }); // UTF-16、追加 UV なし、番号はすべて 1 バイト
        Text("テスト");
        Text("test");
        Text("c");
        Text("");
        w.Write(3); // 頂点
        for (int i = 0; i < 3; i++)
        {
            Floats(i, i * 2, i * 3); // 位置
            Floats(0, 0, -1); // 法線
            Floats(0.5f, 0.25f); // UV
            w.Write((byte)0); // BDEF1
            w.Write((byte)0); // ボーン 0
            Floats(1); // エッジ倍率
        }
        w.Write(3); // 面の頂点番号
        w.Write(new byte[] { 0, 1, 2 });
        w.Write(0); // テクスチャ
        w.Write(1); // 材質
        Text("材質");
        Text("");
        Floats(1, 1, 1, 1, 0, 0, 0, 5, 0.5f, 0.5f, 0.5f);
        w.Write((byte)0x11); // 両面描画 + エッジ
        Floats(0, 0, 0, 1, 1);
        w.Write((byte)0xFF); // テクスチャなし（-1）
        w.Write((byte)0xFF); // スフィアなし（-1）
        w.Write((byte)0); // スフィアの合成なし
        w.Write((byte)1); // 共有トゥーン
        w.Write((byte)3); // toon04.bmp
        Text("");
        w.Write(3);
        w.Write(1); // ボーン
        Text("センター");
        Text("center");
        Floats(0, 8, 0);
        w.Write((byte)0xFF); // 親なし
        w.Write(0); // 変形階層
        w.Write((ushort)0x001E); // 回転・移動・表示・操作。先端は座標で指定
        Floats(0, 1, 0);
        w.Write(0); // モーフ
        w.Write(0); // 表示枠
        w.Write(0); // 剛体
        w.Write(0); // ジョイント

        PmxLoadResult result = PmxReader.Read(ms.ToArray());

        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        Assert.HasCount(0, result.Report.Issues, "問題のないファイルではレポートは空");
        PmxDocument d = result.Document!;
        Assert.AreEqual("テスト", d.Name);
        Assert.AreEqual("test", d.NameUniversal);
        Assert.AreEqual("c", d.Comment);
        Assert.HasCount(3, d.Vertices);
        Assert.AreEqual(new Vector3(2, 4, 6), d.Vertices[2].Position);
        Assert.AreEqual(new Vector2(0.5f, 0.25f), d.Vertices[1].Uv);
        Assert.AreEqual(PmxWeightType.Bdef1, d.Vertices[0].WeightType);
        Assert.AreEqual(0, d.Vertices[0].Bone1);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, d.Indices);

        PmxMaterial m = d.Materials.Single();
        Assert.AreEqual("材質", m.Name);
        Assert.AreEqual(PmxMaterialFlags.DoubleSided | PmxMaterialFlags.Edge, m.Flags);
        Assert.AreEqual(-1, m.TextureIndex, "1 バイトの番号の 0xFF は -1（なし）");
        Assert.IsTrue(m.SharedToon);
        Assert.AreEqual(3, m.ToonIndex);
        Assert.AreEqual(5f, m.Specularity);
        Assert.AreEqual(3, m.IndexCount);

        PmxBone b = d.Bones.Single();
        Assert.AreEqual("センター", b.Name);
        Assert.AreEqual(-1, b.ParentIndex);
        Assert.AreEqual(new Vector3(0, 1, 0), b.TailOffset);
        Assert.IsTrue(b.Has(PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable));
        Assert.IsFalse(b.Has(PmxBoneFlags.TailIsBone));
    }

    [TestMethod]
    [DataRow(PmxTextEncoding.Utf16, DisplayName = "UTF-16・番号はすべて 1 バイト")]
    [DataRow(PmxTextEncoding.Utf8, DisplayName = "UTF-8・番号は 2 / 4 バイトの混在")]
    public void ReadsEveryPmx20Element(PmxTextEncoding encoding)
    {
        PmxIndexSizes sizes = encoding == PmxTextEncoding.Utf16 ? TestModels.SmallSizes : TestModels.MixedSizes;
        PmxDocument expected = TestModels.Full(sizes, encoding);
        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(expected));

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(0, result.Report.Issues, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        PmxDocument d = result.Document!;

        Assert.AreEqual(encoding, d.Encoding);
        Assert.AreEqual(sizes, d.IndexSizes);
        Assert.AreEqual("コメント\r\n2 行目", d.Comment, "改行を含むコメントも元のまま");
        CollectionAssert.AreEqual(expected.AdditionalUvs, d.AdditionalUvs);
        CollectionAssert.AreEqual(expected.Indices, d.Indices);
        CollectionAssert.AreEqual(expected.Textures, d.Textures);

        for (int i = 0; i < expected.Vertices.Length; i++)
        {
            Assert.AreEqual(expected.Vertices[i], d.Vertices[i], $"頂点 {i}");
        }
        Assert.AreEqual(PmxWeightType.Sdef, d.Vertices[3].WeightType);
        Assert.AreEqual(new Vector3(0.4f, 0.5f, 0.6f), d.Vertices[3].SdefR1);

        PmxMaterial body = d.Materials[0];
        Assert.AreEqual(PmxSphereMode.Add, body.SphereMode);
        Assert.IsFalse(body.SharedToon);
        Assert.AreEqual(2, body.ToonIndex);
        Assert.AreEqual("メモ", body.Memo);

        PmxBone arm = d.Bones[1];
        Assert.AreEqual(0, arm.InheritParent);
        Assert.AreEqual(0.5f, arm.InheritWeight);
        Assert.AreEqual(Vector3.UnitX, arm.FixedAxis);
        Assert.AreEqual(Vector3.UnitZ, arm.LocalAxisZ);
        Assert.AreEqual(1, d.Bones[0].TailBone);

        PmxBone ik = d.Bones[2];
        Assert.AreEqual(1, ik.IkTarget);
        Assert.AreEqual(40, ik.IkLoopCount);
        Assert.HasCount(2, ik.IkLinks);
        Assert.IsTrue(ik.IkLinks[0].HasLimits);
        Assert.AreEqual(new Vector3(-3.1f, 0, 0), ik.IkLinks[0].MinAngle);
        Assert.IsFalse(ik.IkLinks[1].HasLimits);

        CollectionAssert.AreEqual(expected.Morphs.Select(m => (m.Name, m.Type, m.Panel, m.OffsetCount)).ToArray(),
            d.Morphs.Select(m => (m.Name, m.Type, m.Panel, m.OffsetCount)).ToArray());
        Assert.AreEqual(new Quaternion(0, 0, 0.7071f, 0.7071f), d.Morphs[1].BoneOffsets[0].Rotation);
        Assert.AreEqual(-1, d.Morphs[4].MaterialOffsets[0].Material, "材質モーフの -1 は「すべての材質」");
        Assert.AreEqual(PmxMaterialOperation.Add, d.Morphs[4].MaterialOffsets[0].Operation);
        Assert.AreEqual(new PmxGroupOffset(1, 1f), d.Morphs[5].GroupOffsets[1]);

        Assert.HasCount(2, d.DisplayFrames);
        Assert.IsTrue(d.DisplayFrames[1].IsSpecial);
        Assert.AreEqual(new PmxDisplayElement(PmxDisplayTarget.Morph, 5), d.DisplayFrames[1].Elements[1]);

        PmxRigidBody head = d.RigidBodies[0];
        Assert.AreEqual(PmxRigidShape.Capsule, head.Shape);
        Assert.AreEqual((ushort)0xFFF7, head.CollisionMask);
        Assert.AreEqual(PmxPhysicsMode.Physics, head.Mode);
        Assert.AreEqual(0.1f, head.Restitution);

        PmxJoint neck = d.Joints.Single();
        Assert.AreEqual(1, neck.RigidBodyA);
        Assert.AreEqual(new Vector3(5, 5, 5), neck.RotationSpring);
    }

    [TestMethod]
    public void VertexIndicesAreUnsignedButOtherIndicesAreSigned()
    {
        // 1 バイトの頂点番号は 0〜255、ボーン番号は -128〜127（0xFF は -1）
        var vertices = Enumerable.Range(0, 256).Select(i => TestModels.Bdef1(new Vector3(i, 0, 0), 0)).ToArray();
        PmxDocument doc = TestModels.Triangle();
        doc = new PmxDocument
        {
            IndexSizes = TestModels.SmallSizes,
            Vertices = vertices,
            Indices = [0, 128, 255],
            Materials = [new PmxMaterial { Name = "m", IndexCount = 3, TextureIndex = -1, SphereTextureIndex = -1, SharedToon = true, ToonIndex = 0 }],
            Bones = doc.Bones,
        };
        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(doc));

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { 0, 128, 255 }, result.Document!.Indices);
        Assert.AreEqual(-1, result.Document.Bones[0].ParentIndex);
    }

    [TestMethod]
    public void TwoByteVertexIndexReachesBeyondSignedRange()
    {
        var vertices = Enumerable.Range(0, 40000).Select(i => TestModels.Bdef1(new Vector3(i, 0, 0), 0)).ToArray();
        var doc = new PmxDocument
        {
            IndexSizes = new PmxIndexSizes(2, 1, 1, 1, 1, 1),
            Vertices = vertices,
            Indices = [39999, 32768, 0],
            Materials = [new PmxMaterial { Name = "m", IndexCount = 3, TextureIndex = -1, SphereTextureIndex = -1, SharedToon = true, ToonIndex = 0 }],
            Bones = TestModels.Triangle().Bones,
        };
        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(doc));

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { 39999, 32768, 0 }, result.Document!.Indices);
        Assert.HasCount(0, result.Report.Issues);
    }

    [TestMethod]
    public void OlderFilesWithoutTrailingSectionsStillLoad()
    {
        byte[] data = PmxTestWriter.Write(TestModels.Triangle(), stopAfterMorphs: true);
        PmxLoadResult result = PmxReader.Read(data);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Report.Issues.All(i => i.Severity == LoadSeverity.Info));
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("表示枠")));
        Assert.IsEmpty(result.Document!.RigidBodies);
    }

    [TestMethod]
    public void Pmx21FeaturesAreReadAndReportedAsUnsupported()
    {
        PmxDocument baseDoc = TestModels.Full(TestModels.SmallSizes, PmxTextEncoding.Utf16);
        PmxVertex qdef = baseDoc.Vertices[2];
        qdef.WeightType = PmxWeightType.Qdef;
        var doc = new PmxDocument
        {
            Version = 2.1f,
            IndexSizes = baseDoc.IndexSizes,
            AdditionalUvCount = baseDoc.AdditionalUvCount,
            Vertices = [baseDoc.Vertices[0], baseDoc.Vertices[1], qdef, baseDoc.Vertices[3]],
            AdditionalUvs = baseDoc.AdditionalUvs,
            Indices = baseDoc.Indices,
            Textures = baseDoc.Textures,
            Materials = [new PmxMaterial { Name = "線", Flags = PmxMaterialFlags.LineDraw, IndexCount = 6, TextureIndex = -1, SphereTextureIndex = -1, SharedToon = true, ToonIndex = 0 }],
            Bones = baseDoc.Bones,
            Morphs =
            [
                .. baseDoc.Morphs,
                new PmxMorph { Name = "反転", Type = PmxMorphType.Flip, GroupOffsets = [new PmxGroupOffset(0, 1)] },
                new PmxMorph { Name = "衝撃", Type = PmxMorphType.Impulse, ImpulseOffsets = [new PmxImpulseOffset(0, true, Vector3.UnitY, Vector3.Zero)] },
            ],
            RigidBodies = baseDoc.RigidBodies,
            Joints = [new PmxJoint { Name = "蝶番", Type = PmxJointType.Hinge, RigidBodyA = 0, RigidBodyB = 1 }],
        };
        byte[] softBodies = BitConverter.GetBytes(2); // ソフトボディ 2 個（中身は読まない）
        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(doc, trailer: softBodies));

        Assert.IsTrue(result.Succeeded);
        PmxDocument d = result.Document!;
        Assert.AreEqual(PmxWeightType.Qdef, d.Vertices[2].WeightType);
        Assert.AreEqual(PmxMorphType.Flip, d.Morphs[^2].Type);
        Assert.AreEqual(Vector3.UnitY, d.Morphs[^1].ImpulseOffsets[0].Velocity);
        Assert.AreEqual(PmxJointType.Hinge, d.Joints[0].Type);
        Assert.AreEqual(2, d.SoftBodyCount);

        string[] unsupported = [.. result.Report.Issues.Where(i => i.Category == LoadIssueCategory.Unsupported).Select(i => i.Message)];
        Assert.IsTrue(unsupported.Any(m => m.Contains("QDEF")), "QDEF");
        Assert.IsFalse(unsupported.Any(m => m.Contains("フリップ")), "フリップ・インパルスモーフは対応済み");
        Assert.IsTrue(result.Report.Issues.Any(i => i.Category == LoadIssueCategory.Note && i.Message.Contains("フリップ")), "MMD では効かないことを知らせる");
        Assert.IsTrue(unsupported.Any(m => m.Contains("ジョイントの種類")), "2.1 のジョイント");
        Assert.IsTrue(unsupported.Any(m => m.Contains("ソフトボディ")), "ソフトボディ");
        Assert.IsTrue(unsupported.Any(m => m.Contains("点・線")), "材質の描き方");
        Assert.IsFalse(result.Report.HasErrors);
    }

    [TestMethod]
    public void WrongFilesFailWithReadableReasons()
    {
        AssertFails("ABCD"u8.ToArray(), "PMX ファイルではありません");
        AssertFails([.. "Pmd"u8, .. new byte[100]], "PMD 形式");
        AssertFails([], "途中でファイルが終わりました");

        byte[] valid = PmxTestWriter.Write(TestModels.Triangle());
        AssertFails(valid[..60], "途中でファイルが終わりました");

        byte[] badSize = (byte[])valid.Clone();
        badSize[11] = 3; // 頂点の番号の大きさ
        AssertFails(badSize, "番号の大きさ");

        byte[] badEncoding = (byte[])valid.Clone();
        badEncoding[9] = 7;
        AssertFails(badEncoding, "文字コード");

        static void AssertFails(byte[] data, string expectedText)
        {
            PmxLoadResult result = PmxReader.Read(data);
            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Report.HasErrors);
            string message = result.Report.Issues.Single(i => i.Severity == LoadSeverity.Error).Message;
            StringAssert.Contains(message, expectedText);
        }
    }

    [TestMethod]
    public void HugeCountsInBrokenFilesAreRejectedWithoutAllocating()
    {
        byte[] valid = PmxTestWriter.Write(TestModels.Triangle());
        // 頂点数の 4 バイトを壊す（モデル名などの後ろ）。ヘッダー 17 バイト + 文字列 4 つ
        int offset = 17;
        for (int i = 0; i < 4; i++)
        {
            offset += 4 + BitConverter.ToInt32(valid, offset);
        }
        BitConverter.GetBytes(int.MaxValue).CopyTo(valid, offset);

        PmxLoadResult result = PmxReader.Read(valid);
        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Report.Issues[0].Message, "頂点の数");
    }

    [TestMethod]
    public void LoadFromFileAndFromMissingFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "3DTheaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "三角.pmx");
            File.WriteAllBytes(path, PmxTestWriter.Write(TestModels.Triangle()));
            PmxLoadResult ok = PmxReader.Load(path);
            Assert.IsTrue(ok.Succeeded);
            Assert.AreEqual(path, ok.SourcePath);

            PmxLoadResult missing = PmxReader.Load(Path.Combine(dir, "無い.pmx"));
            Assert.IsFalse(missing.Succeeded);
            StringAssert.Contains(missing.Report.Issues[0].Message, "ファイルを開けませんでした");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
