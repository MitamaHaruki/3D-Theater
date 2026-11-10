using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class MaterialOverrideTests
{
    private static readonly MaterialOverride Metal = new()
    {
        Shading = MaterialShading.Pbr,
        Tint = new Vector3(1f, 0.8f, 0.4f),
        Metallic = 1f,
        Roughness = 0.2f,
    };

    [TestMethod]
    public void ValuesAreKeptInRange()
    {
        Assert.IsTrue(MaterialOverride.None.IsDefault);
        Assert.IsFalse(Metal.IsDefault);
        MaterialOverride wild = new MaterialOverride
        {
            Shading = (MaterialShading)9,
            Tint = new Vector3(2f, -1f, float.NaN),
            Metallic = 5f,
            Roughness = 0f,
            EmissiveColor = new Vector3(0.5f, 2f, 0f),
            EmissiveStrength = float.PositiveInfinity,
        }.Normalized();
        Assert.AreEqual(MaterialShading.Toon, wild.Shading, "知らない色の計算はトゥーン");
        Assert.AreEqual(Vector3.One, wild.Tint, "数でない値を含む色は初期値");
        Assert.AreEqual(1f, wild.Metallic);
        Assert.AreEqual(0.04f, wild.Roughness, "粗さは 0.04 まで");
        Assert.AreEqual(new Vector3(0.5f, 1f, 0f), wild.EmissiveColor);
        Assert.AreEqual(0f, wild.EmissiveStrength, "無限大は初期値");
    }

    [TestMethod]
    public void SetsCompareByValueAndDropDefaults()
    {
        MaterialOverrideSet set = MaterialOverrideSet.Empty.With(1, Metal);
        Assert.AreEqual(1, set.Count);
        Assert.AreEqual(Metal, set[1]);
        Assert.AreEqual(MaterialOverride.None, set[0], "上書きの無い材質は「なし」");
        Assert.AreEqual(set, MaterialOverrideSet.Empty.With(1, Metal), "同じ中身なら等しい（別の入れ物でも）");
        Assert.AreNotEqual(set, set.With(1, Metal with { Roughness = 0.3f }));
        Assert.AreEqual(MaterialOverrideSet.Empty, set.With(1, MaterialOverride.None), "「なし」に戻すと外す");
        Assert.AreSame(MaterialOverrideSet.Empty, set.With(1, new MaterialOverride()), "空になれば空の組そのもの");
        Assert.AreEqual(0.04f, set.With(2, Metal with { Roughness = -1f })[2].Roughness, "入れるときに範囲に収める");
    }

    [TestMethod]
    public void OverridesAreUndoable()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        var undo = new UndoService();
        var edits = new EditService(undo, animation, playback);
        int changes = 0;
        scene.NodeChanged += (_, e) => changes += e.PropertyName == nameof(ModelNode.MaterialOverrides) ? 1 : 0;

        Assert.IsTrue(edits.Change("材質「髪」を変える", [model], () => model.MaterialOverrides = model.MaterialOverrides.With(1, Metal)));
        Assert.AreEqual(Metal, model.MaterialOverrides[1]);
        Assert.AreEqual(1, changes, "変わったことを知らせる（描き直しのため）");
        Assert.IsFalse(edits.Change("同じ値", [model], () => model.MaterialOverrides = MaterialOverrideSet.Empty.With(1, Metal)),
            "同じ中身なら操作を積まない");

        undo.Undo();
        Assert.AreEqual(MaterialOverrideSet.Empty, model.MaterialOverrides, "元に戻すと上書きなし");
        undo.Redo();
        Assert.AreEqual(Metal, model.MaterialOverrides[1]);
    }

    [TestMethod]
    public void OverridesAreSavedAndFoundByName()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        model.MaterialOverrides = MaterialOverrideSet.Empty
            .With(1, Metal)
            .With(2, new MaterialOverride { EmissiveColor = new Vector3(0f, 1f, 0f), EmissiveStrength = 2.5f });
        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;
        List<MaterialEntry> entries = project.Models[0].Materials!;
        Assert.HasCount(2, entries, "上書きのある材質だけを書く");
        Assert.AreEqual("髪", entries[0].Name);
        Assert.AreEqual("pbr", entries[0].Shading);
        Assert.AreEqual("#FFCC66", entries[0].Tint);

        var (_, _, animation2, model2) = AnimationFixtures.Create();
        var report = new LoadReport();
        ProjectApply.ApplyModel(model2, project.Models[0], animation2, report);
        Assert.IsFalse(report.Issues.Any());
        Assert.AreEqual(model.MaterialOverrides, model2.MaterialOverrides, "色は #RRGGBB で保存しても同じ値に戻る");

        // 材質の順番が変わったモデルでは名前で探し、無い材質は知らせる
        entries[0].Index = 0;
        entries.Add(new MaterialEntry { Index = 7, Name = "眼鏡", Shading = "pbr" });
        var (_, _, animation3, model3) = AnimationFixtures.Create();
        var report3 = new LoadReport();
        ProjectApply.ApplyModel(model3, project.Models[0], animation3, report3);
        Assert.AreEqual(Metal, model3.MaterialOverrides[1], "番号が違っても名前で見つける");
        Assert.AreEqual(MaterialOverride.None, model3.MaterialOverrides[0]);
        Assert.IsTrue(report3.Issues.Any(i => i.Message.Contains("材質")),
            "見つからない材質を知らせる");
    }

    [TestMethod]
    public void ModelsWithoutOverridesWriteNothing()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        ProjectFile project = ProjectCapture.Capture(scene, playback, animation, null, []);
        Assert.IsNull(project.Models[0].Materials);
        Assert.IsFalse(ProjectSerializer.Serialize(project).Contains("\"materials\"", StringComparison.Ordinal));
    }
}
