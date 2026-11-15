using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>ライブモードの「キャラクター」と「背景」の分け方（設計判断記録 78 章の 2 段目）。</summary>
[TestClass]
public sealed class LiveCastTests
{
    private static ModelNode AddModel(SceneDocument scene, string file, params string[] boneNames)
    {
        var model = new ModelNode(Path.GetFileNameWithoutExtension(file), file,
            new ModelSummary("PMX 2.0", 0, 0, 0, 0, boneNames.Length, 0, 0, 0), string.Empty, Vector3.Zero, Vector3.One);
        BoneNode[] bones = [.. boneNames.Select((b, i) => new BoneNode(b, i, AnimationFixtures.Movable))];
        var group = new SceneNode(SceneNodeKind.Group, "ボーン", canRename: false, hasTransform: false);
        foreach (BoneNode b in bones)
        {
            group.AddChild(b);
        }
        model.AttachParts([group], bones, []);
        scene.Add(scene.ModelsGroup, model);
        return model;
    }

    [TestMethod]
    public void TheCharacterIsTheTrackingTarget()
    {
        SceneDocument scene = SceneDocument.CreateDefault(); // モデルの無いシーン
        ModelNode room = AddModel(scene, "部屋.pmx", "全ての親");
        ModelNode ren = AddModel(scene, "レン.pmx", "センター", "首", "頭");
        ModelNode mic = AddModel(scene, "マイク.x");
        var settings = new FaceRetargetSettings();

        Assert.IsNull(LiveCast.Character(scene, settings), "決めていなければ、キャラクターはいない（自動では選ばない）");
        CollectionAssert.AreEqual(new[] { room, ren, mic }, LiveCast.Background(scene, settings).ToArray(), "全部が背景");

        settings.TargetModelId = ren.Id;
        Assert.AreSame(ren, LiveCast.Character(scene, settings));
        CollectionAssert.AreEqual(new[] { room, mic }, LiveCast.Background(scene, settings).ToArray(), "キャラクターは背景に入らない");

        scene.Remove(ren);
        Assert.IsNull(LiveCast.Character(scene, settings), "外したらいない");
        scene.Add(scene.ModelsGroup, ren);
        Assert.AreSame(ren, LiveCast.Character(scene, settings), "元に戻すと、またキャラクター");
    }

    [TestMethod]
    public void OldFilesChooseTheFirstPmxWithAHead()
    {
        SceneDocument scene = SceneDocument.CreateDefault(); // モデルの無いシーン
        ModelNode stage = AddModel(scene, "ステージ.pmx", "全ての親");
        AddModel(scene, "ネギ.x", "頭");
        ModelNode ren = AddModel(scene, "レン.PMX", "センター", "頭");
        AddModel(scene, "リン.pmx", "センター", "頭");

        Assert.AreSame(ren, LiveCast.ChooseCharacter(LiveCast.Models(scene)), "頭のボーンを持つ最初の PMX（アクセサリは選ばない。拡張子の大文字も）");
        Assert.AreSame(stage, LiveCast.ChooseCharacter([stage]), "頭が無ければ最初の PMX");
        Assert.IsNull(LiveCast.ChooseCharacter([]), "モデルが無ければいない");
    }

    [TestMethod]
    public void SavedFilesSayTheCharacterWasChosen()
    {
        // 新しい版は、キャラクターを決めた印を書く（キャラクターがいなくても）。前の版のファイルには無い（開くときに選ぶ）
        var settings = new FaceRetargetSettings();
        var project = new ProjectFile { Live = LiveEntry.From(new LiveOutputSettings(), settings) };
        var report = new Theater3D.Formats.LoadReport();
        ProjectFile read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project), report)!;
        Assert.IsTrue(read.Live!.Tracking!.ExplicitCharacter, "保存して読み戻しても残る");
        Assert.IsNull(read.Live.Tracking.Target, "キャラクターがいない");

        ProjectFile old = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"mode":"live","live":{"tracking":{"mirror":true}}}""", report)!;
        Assert.IsFalse(old.Live!.Tracking!.ExplicitCharacter, "前の版のファイル");
    }

    [TestMethod]
    public void OnlyPmxFilesCanBeCharacters()
    {
        Assert.IsTrue(LiveCast.IsCharacterFile(@"C:\モデル\レン.pmx"));
        Assert.IsTrue(LiveCast.IsCharacterFile("a.PMX"));
        Assert.IsTrue(LiveCast.IsCharacterFile("a.vrm"));
        Assert.IsTrue(LiveCast.IsCharacterFile("a.fbx"));
        Assert.IsFalse(LiveCast.IsCharacterFile("ネギ.x"));
        Assert.IsFalse(LiveCast.IsCharacterFile("ネギ.vac"));
        Assert.IsFalse(LiveCast.IsCharacterFile("部屋.glb"));
        Assert.IsFalse(LiveCast.IsCharacterFile(null));
    }

    [TestMethod]
    public void FbxNeedsAHeadToBeACharacter()
    {
        // FBX は、ボーンの名前から人型のボーンが見つかった（頭がある）ものだけキャラクターにできる（83 章の 2 段目）
        SceneDocument scene = SceneDocument.CreateDefault();
        ModelNode room = AddModel(scene, "部屋.fbx", "Root", "Door");
        ModelNode avatar = AddModel(scene, "アバター.FBX", "センター", "頭");
        Assert.IsFalse(LiveCast.CanBeCharacter(room));
        Assert.IsTrue(LiveCast.CanBeCharacter(avatar));
        Assert.IsTrue(LiveCast.CanBeCharacter(AddModel(scene, "ステージ.pmx", "全ての親")), "PMX は頭が無くてもよい（今まで通り）");
        Assert.AreSame(avatar, LiveCast.ChooseCharacter([room, avatar]));
    }
}
