using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class DisplayKeyTests
{
    private static readonly BoneTraits Ik = new(true, true, true, false, false, false, false, true, string.Empty);

    /// <summary>ボーン「センター」（0 番）・「左足ＩＫ」（1 番）・「右足ＩＫ」（2 番）を持つモデルをシーンに置く。</summary>
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, ModelNode Model, ModelDisplayNode Display) Create()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var model = new ModelNode("人形", "人形.pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, 3, 0, 0, 0), string.Empty, Vector3.Zero, Vector3.One);
        BoneNode[] bones = [new BoneNode("センター", 0, AnimationFixtures.Movable), new BoneNode("左足ＩＫ", 1, Ik), new BoneNode("右足ＩＫ", 2, Ik)];
        var boneGroup = new SceneNode(SceneNodeKind.Group, "ボーン", canRename: false, hasTransform: false);
        foreach (BoneNode b in bones)
        {
            boneGroup.AddChild(b);
        }
        model.AttachParts([boneGroup], bones, []);
        scene.Add(scene.ModelsGroup, model);
        var playback = new PlaybackService();
        return (scene, playback, new AnimationService(scene, playback), model, model.Display!);
    }

    private static IkSwitchSet Off(params int[] bones) => new(bones);

    [TestMethod]
    public void ModelsGetADisplayNodeWithTheirIkBones()
    {
        var (_, _, _, model, display) = Create();

        Assert.AreSame(display, model.Children[0], "表示・IK はモデルのいちばん上");
        Assert.AreEqual("表示・IK", display.Name);
        Assert.AreEqual(SceneNodeKind.Display, display.Kind);
        CollectionAssert.AreEqual(new[] { "左足ＩＫ", "右足ＩＫ" }, display.IkBones.Select(b => b.Name).ToArray());
        Assert.IsTrue(display.Shown);
        Assert.IsTrue(display.Ik.AllEnabled);
        Assert.IsFalse(display.CanToggleVisibility, "目の印は出さない（MMD の表示とは別）");
        Assert.IsTrue(model.IsShownByDisplay);

        Assert.AreEqual(Off(2, 1), Off(1, 2), "切った IK の集まりは値で比べる");
        Assert.AreEqual(Off(1), IkSwitchSet.AllOn.With(1, false));
        Assert.AreSame(IkSwitchSet.AllOn, IkSwitchSet.AllOn.With(1, true), "変わらなければ同じもの");
        Assert.IsTrue(Off(1).With(1, true).AllEnabled);
        Assert.IsFalse(Off(1).IsEnabled(1));
        Assert.IsTrue(Off(1).IsEnabled(2));
    }

    [TestMethod]
    public void DisplayKeysSwitchAtTheirFrames()
    {
        var (_, playback, animation, model, display) = Create();
        display.Keys.SetRange([new DisplayKey(0, true, IkSwitchSet.AllOn), new DisplayKey(10, false, Off(1)), new DisplayKey(20, true, Off(2))]);

        playback.Seek(9);
        Assert.IsTrue(display.Shown);
        Assert.IsTrue(display.Ik.AllEnabled, "次のキーの直前まで前のキーの値（いつも段階）");
        playback.Seek(10);
        Assert.IsFalse(display.Shown);
        Assert.IsFalse(model.IsShownByDisplay, "表示のキーでモデルを隠す");
        Assert.IsFalse(display.IsIkEnabled(1));
        Assert.IsTrue(display.IsIkEnabled(2));
        playback.Seek(30);
        Assert.IsTrue(display.Shown);
        Assert.AreEqual(Off(2), display.Ik, "最後のキーの後は最後のキーの値");
        Assert.IsFalse(display.HasUnregisteredChange, "当てはめは、手で変えたことにしない");

        animation.ApplyFrameAt(9.5f, 10);
        Assert.IsTrue(display.Shown, "モーションブラーの途中の時刻も、段階で切り替える");
        animation.ApplyFrame();
        Assert.AreEqual(20, AnimationService.LastKeyFrame(model), "表示・IK のキーも、モデルのいちばん後ろのキーに入る");
    }

    [TestMethod]
    public void KeyedDisplayEditsAreKeptUntilRegistered()
    {
        var (_, playback, animation, _, display) = Create();
        var undo = new UndoService();
        var edits = new EditService(undo, animation, playback);

        display.Ik = Off(1);
        Assert.IsFalse(display.HasUnregisteredChange, "キーの無い表示・IK を変えても未登録にしない");
        display.Ik = IkSwitchSet.AllOn;

        display.Keys.Set(new DisplayKey(0, true, IkSwitchSet.AllOn));
        playback.Seek(15);
        display.Ik = display.Ik.With(2, false);
        playback.Seek(16);
        Assert.IsTrue(display.HasUnregisteredChange);
        Assert.AreEqual(Off(2), display.Ik, "キーのある表示・IK の手の変更は、フレームを動かしても保つ");
        CollectionAssert.Contains(animation.Unregistered.ToList(), display);
        CollectionAssert.AreEqual(new SceneNode[] { display }, animation.RegistrationTargets([display]).ToArray());

        edits.Change("キーの登録", [display], () => animation.Register([display]), includeKeys: true);
        Assert.IsTrue(display.Keys.TryGet(16, out DisplayKey key));
        Assert.AreEqual(Off(2), key.Ik);
        Assert.IsTrue(key.Shown);
        Assert.IsFalse(display.HasUnregisteredChange);
        undo.Undo();
        Assert.AreEqual(1, display.Keys.Count, "元に戻すと、置いたキーが消える");
        Assert.IsTrue(display.HasUnregisteredChange);

        animation.DiscardUnregistered();
        Assert.IsTrue(display.Ik.AllEnabled, "捨てると、キーの値に戻る");

        animation.AutoKey = true;
        playback.Seek(40);
        display.Shown = false;
        Assert.IsTrue(display.Keys.TryGet(40, out DisplayKey auto), "自動キーは、変えたときにキーを置く");
        Assert.IsFalse(auto.Shown);
    }

    [TestMethod]
    public void DisplayKeysCanBeEditedLikeOtherKeys()
    {
        var (_, _, _, model, display) = Create();
        display.Keys.SetRange([new DisplayKey(0, true, IkSwitchSet.AllOn), new DisplayKey(10, true, Off(1)), new DisplayKey(11, false, Off(1, 2))]);

        Assert.IsTrue(KeyEditing.HasKeys(display));
        Assert.AreEqual(3, KeyEditing.CountKeys(display));
        Assert.HasCount(3, KeyEditing.InRange([model], 0, 20), "モデルの行で、表示・IK のキーも選べる");
        Assert.HasCount(1, KeyEditing.AtFrame(model, 10));

        KeyEditing.Move([new KeyRef(display, 10)], 5, duplicate: false);
        CollectionAssert.AreEqual(new[] { 0, 11, 15 }, display.Keys.Keys.Select(k => k.Frame).ToArray());
        KeyEditing.Paste(KeyEditing.Copy([new KeyRef(display, 15)]), 30);
        Assert.AreEqual(Off(1), display.Keys.Keys[^1].Ik);
        Assert.AreEqual(1, KeyEditing.Delete([new KeyRef(display, 30)]));
        KeyEditing.SetInterpolation([new KeyRef(display, 15)], InterpolationPreset.Smooth);
        Assert.AreEqual(3, display.Keys.Count, "補間の切り替えは、表示・IK のキーを変えない");
        Assert.IsEmpty(CurveEditing.ChannelNames(display), "曲線は無い");

        KeyEditing.ScaleTime([display], 2);
        CollectionAssert.AreEqual(new[] { 0, 22, 30 }, display.Keys.Keys.Select(k => k.Frame).ToArray());
    }

    [TestMethod]
    public void PhysicsBakingSamplesIkSwitchesPerFrame()
    {
        var (_, _, _, model, display) = Create();
        display.Keys.SetRange([new DisplayKey(0, true, IkSwitchSet.AllOn), new DisplayKey(10, true, Off(1))]);
        var translations = new Vector3[3];
        var rotations = new Quaternion[3];
        var ik = new bool[3];

        PoseSampler.Sample(model, 5, translations, rotations, [], ik);
        CollectionAssert.AreEqual(new[] { true, true, true }, ik);
        PoseSampler.Sample(model, 12, translations, rotations, [], ik);
        CollectionAssert.AreEqual(new[] { true, false, true }, ik, "焼き込みでも、そのフレームの IK の切り替えを使う");

        display.HasUnregisteredChange = true;
        display.Ik = Off(2);
        PoseSampler.Sample(model, 12, translations, rotations, [], ik);
        CollectionAssert.AreEqual(new[] { true, true, false }, ik, "未登録の変更があれば今の値");

        var changed = new SceneNodeChangedEventArgs(display, nameof(ModelDisplayNode.Keys));
        Assert.AreSame(model, PoseSampler.ModelAffectedBy(changed), "表示・IK のキーが変われば、物理の焼き込みをやり直す");
    }

    [TestMethod]
    public void VmdDisplayKeysBecomeDisplayKeys()
    {
        var (_, _, _, model, display) = Create();
        VmdName left = AnimationFixtures.Name("左足ＩＫ", VmdText.IkNameBytes);
        VmdName right = AnimationFixtures.Name("右足ＩＫ", VmdText.IkNameBytes);
        VmdName other = AnimationFixtures.Name("つま先ＩＫ", VmdText.IkNameBytes);
        var vmd = new VmdDocument
        {
            VisibilityKeys =
            [
                new VmdVisibilityKey(0, true, [new VmdIkState(left, true), new VmdIkState(right, true)]),
                new VmdVisibilityKey(30, true, [new VmdIkState(left, false), new VmdIkState(right, true), new VmdIkState(other, false)]),
                new VmdVisibilityKey(45, false, [new VmdIkState(left, false), new VmdIkState(right, false)]),
            ],
        };
        var report = new LoadReport();

        MotionImportResult result = MotionImport.ApplyVmd(vmd, model, frameRate: 60, report);

        Assert.AreEqual(3, result.DisplayKeys);
        Assert.AreEqual(90, result.LastFrame, "60 fps の作品では 2 倍の位置。表示・IK のキーも最後のキーに入る");
        CollectionAssert.AreEqual(new[] { 0, 60, 90 }, display.Keys.Keys.Select(k => k.Frame).ToArray());
        Assert.AreEqual(Off(1), display.Keys.Keys[1].Ik);
        Assert.AreEqual(Off(1, 2), display.Keys.Keys[2].Ik);
        Assert.IsFalse(display.Keys.Keys[2].Shown);
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("つま先ＩＫ")), "モデルに無い IK は知らせる");
        Assert.IsFalse(report.Issues.Any(i => i.Message.Contains("まだ対応していません")), "IK を切るキーは、もう使える");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("表示・IK のキー")));
    }

    [TestMethod]
    public void ExportedDisplayKeysImportBackTheSame()
    {
        var (_, _, _, model, display) = Create();
        display.Keys.SetRange([new DisplayKey(0, true, IkSwitchSet.AllOn), new DisplayKey(30, false, Off(2))]);
        var report = new LoadReport();

        VmdDocument written = MotionExport.CreateVmd(model, 30, report);
        VmdDocument read = VmdReader.Read(VmdWriter.Write(written)).Document!;

        Assert.HasCount(2, read.VisibilityKeys);
        Assert.HasCount(2, read.VisibilityKeys[0].IkStates, "IK ボーンすべての切り替えを書く（MMD と同じ）");
        Assert.IsFalse(report.Issues.Any(i => i.Message.Contains("空のモーション")), "表示・IK のキーだけでも空ではない");
        var (_, _, _, other, otherDisplay) = Create();
        MotionImport.ApplyVmd(read, other, 30, new LoadReport());
        CollectionAssert.AreEqual(display.Keys.Keys.ToArray(), otherDisplay.Keys.Keys.ToArray());
    }

    [TestMethod]
    public void DisplayStateAndKeysAreSavedInTheProject()
    {
        var (scene, playback, animation, _, display) = Create();
        display.Keys.SetRange([new DisplayKey(0, true, IkSwitchSet.AllOn), new DisplayKey(20, false, Off(1, 2))]);
        playback.Seek(5);
        display.Ik = Off(2);
        display.HasUnregisteredChange = true;
        display.IsLocked = true;
        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;
        DisplayEntry entry = project.Models.Single().Display!;
        CollectionAssert.AreEqual(new[] { "右足ＩＫ" }, entry.IkOff, "切った IK は名前で書く");

        var (_, _, animation2, model2, display2) = Create();
        var report = new LoadReport();
        ProjectApply.ApplyModel(model2, project.Models.Single(), animation2, report);

        CollectionAssert.AreEqual(display.Keys.Keys.ToArray(), display2.Keys.Keys.ToArray());
        Assert.AreEqual(Off(2), display2.Ik);
        Assert.IsTrue(display2.HasUnregisteredChange);
        Assert.IsTrue(display2.IsLocked);
        Assert.IsFalse(report.Issues.Any());

        // 初期のままなら書かない。モデルに無い IK の名前は知らせる
        var (sceneDefault, playbackDefault, animationDefault, _, _) = Create();
        Assert.IsNull(ProjectCapture.Capture(sceneDefault, playbackDefault, animationDefault, null, []).Models.Single().Display);
        ModelEntry changed = project.Models.Single();
        changed.Display!.IkOff = ["しっぽＩＫ"];
        var report3 = new LoadReport();
        ProjectApply.ApplyModel(model2, changed, animation2, report3);
        Assert.IsTrue(display2.Ik.AllEnabled);
        Assert.IsTrue(report3.Issues.Any(i => i.Message.Contains("IK の切り替え")));
    }
}
