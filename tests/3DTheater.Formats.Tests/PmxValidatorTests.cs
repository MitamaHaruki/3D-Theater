using System.IO;
using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

/// <summary>読み込みレポート（範囲外の参照・見つからない素材・同じ名前）の確認。</summary>
[TestClass]
public sealed class PmxValidatorTests
{
    [TestMethod]
    public void BrokenReferencesAreReportedButModelStillLoads()
    {
        PmxDocument full = TestModels.Full(TestModels.SmallSizes, PmxTextEncoding.Utf16);
        PmxVertex badVertex = full.Vertices[0];
        badVertex.Bone1 = 50;
        var doc = new PmxDocument
        {
            IndexSizes = full.IndexSizes,
            AdditionalUvCount = full.AdditionalUvCount,
            AdditionalUvs = full.AdditionalUvs,
            Vertices = [badVertex, full.Vertices[1], full.Vertices[2], full.Vertices[3]],
            Indices = [0, 1, 2, 1, 3, 99],
            Textures = full.Textures,
            Materials =
            [
                new PmxMaterial { Name = "体", IndexCount = 3, TextureIndex = 20, SphereTextureIndex = -1, SharedToon = true, ToonIndex = 0 },
                new PmxMaterial { Name = "髪", IndexCount = 6, TextureIndex = -1, SphereTextureIndex = -1, SharedToon = true, ToonIndex = 0 },
            ],
            Bones =
            [
                new PmxBone { Name = "親", ParentIndex = 1 },
                new PmxBone { Name = "子", ParentIndex = 0 },
                new PmxBone { Name = "自分が親", ParentIndex = 2 },
                new PmxBone { Name = "IK", ParentIndex = -1, Flags = PmxBoneFlags.IK, IkTarget = 1, IkLinks = [new PmxIkLink { Bone = 30 }] },
            ],
            Morphs =
            [
                new PmxMorph { Name = "頂点", Type = PmxMorphType.Vertex, VertexOffsets = [new PmxVertexOffset(500, Vector3.One)] },
                new PmxMorph { Name = "入れ子", Type = PmxMorphType.Group, GroupOffsets = [new PmxGroupOffset(1, 1)] },
            ],
            RigidBodies = [new PmxRigidBody { Name = "剛体", BoneIndex = 77 }],
            Joints = [new PmxJoint { Name = "つなぎ", RigidBodyA = 0, RigidBodyB = 5 }],
        };

        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(doc));

        Assert.IsTrue(result.Succeeded, "参照が壊れていても読み込みは続ける");
        Assert.IsFalse(result.Report.HasErrors);
        string all = string.Join("\n", result.Report.Issues.Select(i => i.Message));
        StringAssert.Contains(all, "頂点が存在しないボーン");
        StringAssert.Contains(all, "面が存在しない頂点");
        StringAssert.Contains(all, "合計（9）");
        StringAssert.Contains(all, "存在しないテクスチャ");
        StringAssert.Contains(all, "ボーンの親が存在しないか自分自身");
        StringAssert.Contains(all, "親子関係が輪");
        StringAssert.Contains(all, "IK の目標やリンク");
        StringAssert.Contains(all, "モーフが存在しない");
        StringAssert.Contains(all, "グループモーフの中に");
        StringAssert.Contains(all, "剛体の関連ボーン");
        StringAssert.Contains(all, "ジョイントが存在しない剛体");
        Assert.IsTrue(result.Report.Issues.Where(i => i.Message.Contains("存在しない")).All(i => i.Category == LoadIssueCategory.InvalidReference));
    }

    [TestMethod]
    public void ManyProblemsOfOneKindAreSummarizedInOneLine()
    {
        var vertices = Enumerable.Range(0, 100).Select(i => TestModels.Bdef1(new Vector3(i, 0, 0), 9)).ToArray();
        var doc = new PmxDocument
        {
            IndexSizes = TestModels.SmallSizes,
            Vertices = vertices,
            Bones = TestModels.Triangle().Bones,
        };
        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(doc));

        LoadIssue issue = result.Report.Issues.Single(i => i.Message.Contains("頂点が存在しないボーン"));
        StringAssert.Contains(issue.Message, "100 件");
        StringAssert.Contains(issue.Message, "例: 頂点 0、頂点 1、頂点 2 ほか");
    }

    [TestMethod]
    public void MissingTexturesAreListedAndSharedToonsAreNotedOnly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "3DTheaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tex"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "tex", "body.png"), [0]);
            PmxDocument triangle = TestModels.Triangle();
            var doc = new PmxDocument
            {
                IndexSizes = TestModels.SmallSizes,
                Vertices = triangle.Vertices,
                Indices = triangle.Indices,
                Bones = triangle.Bones,
                Textures = ["tex\\body.png", "tex/face.png", "toon03.bmp", "使われていない.png"],
                Materials =
                [
                    new PmxMaterial { Name = "体", IndexCount = 3, TextureIndex = 0, SphereTextureIndex = 1, SharedToon = false, ToonIndex = 2 },
                ],
            };
            string path = Path.Combine(dir, "model.pmx");
            File.WriteAllBytes(path, PmxTestWriter.Write(doc));

            PmxLoadResult result = PmxReader.Load(path);

            Assert.IsTrue(result.Succeeded);
            LoadIssue[] missing = [.. result.Report.Issues.Where(i => i.Category == LoadIssueCategory.MissingFile)];
            Assert.HasCount(1, missing, "使っていないテクスチャと、MMD 付属の共有トゥーンは見つからなくても注意にしない");
            StringAssert.Contains(missing[0].Message, "tex/face.png");
            Assert.IsTrue(result.Report.Issues.Any(i => i.Severity == LoadSeverity.Info && i.Message.Contains("toon03.bmp")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void DuplicateNamesAreReportedBecauseMotionsMatchByName()
    {
        PmxDocument triangle = TestModels.Triangle();
        var doc = new PmxDocument
        {
            IndexSizes = TestModels.SmallSizes,
            Vertices = triangle.Vertices,
            Indices = triangle.Indices,
            Materials = triangle.Materials,
            Bones = [new PmxBone { Name = "腕" }, new PmxBone { Name = "腕" }],
        };
        PmxLoadResult result = PmxReader.Read(PmxTestWriter.Write(doc));

        LoadIssue issue = result.Report.Issues.Single(i => i.Message.Contains("同じ名前のボーン"));
        StringAssert.Contains(issue.Message, "「腕」");
    }

    [TestMethod]
    public void TexturePathsAreResolvedAgainstModelFolder()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "model");
        Assert.AreEqual(Path.Combine(baseDir, "tex", "a.png"), PmxValidator.ResolvePath(baseDir, "tex\\a.png"));
        Assert.AreEqual(Path.Combine(baseDir, "tex", "a.png"), PmxValidator.ResolvePath(baseDir, "tex/a.png"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(baseDir, "..", "shared", "b.png")), PmxValidator.ResolvePath(baseDir, "..\\shared\\b.png"));
        Assert.IsNull(PmxValidator.ResolvePath(baseDir, "  "));
    }
}
