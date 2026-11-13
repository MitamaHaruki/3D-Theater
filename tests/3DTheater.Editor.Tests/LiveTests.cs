using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

/// <summary>ライブモードの舞台（物理をいつも動かす・待機モーションのループ・配信用の画面の設定）。</summary>
[TestClass]
public sealed class LiveTests
{
    [TestMethod]
    public void LivePhysicsStepsWithRealTimeRegardlessOfFrames()
    {
        var clock = new PhysicsClock();
        Assert.AreEqual(PhysicsStep.Reset, clock.NextLive(TimeSpan.FromMilliseconds(16)), "初めは置き直す");

        PhysicsStep step = clock.NextLive(TimeSpan.FromMilliseconds(16));
        Assert.AreEqual(PhysicsStepKind.Step, step.Kind);
        Assert.AreEqual(0.016f, step.Seconds, 1e-5f);
        Assert.AreEqual(PhysicsClock.MaxSettleStep, clock.NextLive(TimeSpan.FromSeconds(1)).Seconds, "遅れても 1 回に進める時間は上限まで");
        Assert.AreEqual(PhysicsStep.None, clock.NextLive(TimeSpan.Zero));

        // 止まっていても（姿勢を変えていなくても）、いつまでも進める
        for (int i = 0; i < 300; i++)
        {
            Assert.AreEqual(PhysicsStepKind.Step, clock.NextLive(TimeSpan.FromMilliseconds(16)).Kind);
        }
        clock.RequestReset();
        Assert.AreEqual(PhysicsStep.Reset, clock.NextLive(TimeSpan.FromMilliseconds(16)), "物理をリセットしたら置き直す");
    }

    [TestMethod]
    public void LoopFrameRepeatsWithTheMotionLength()
    {
        Assert.AreEqual(30f, LiveLoop.Frame(1.0, 30, 60), 1e-4f);
        Assert.AreEqual(15f, LiveLoop.Frame(2.5, 30, 60), 1e-4f, "75 フレーム目は、長さ 60 で回して 15");
        Assert.AreEqual(0f, LiveLoop.Frame(2.0, 30, 60), 1e-4f, "最後のキー（60）は 0 フレーム目と同じ時刻");
        Assert.AreEqual(45f, LiveLoop.Frame(0.75, 60, 90), 1e-4f, "作品のフレームの速さで数える");
        Assert.AreEqual(0f, LiveLoop.Frame(5, 30, 0), "長さが無ければ 0");
        Assert.AreEqual(0f, LiveLoop.Frame(-1, 30, 60));
        Assert.AreEqual(0f, LiveLoop.Frame(double.NaN, 30, 60));
    }

    [TestMethod]
    public void EachModelLoopsItsOwnIdleMotion()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        animation.TracksUserEdits = false; // ライブモードと同じ
        BoneNode center = model.Bones[0];
        center.Keys.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        center.Keys.Set(new BoneKey(60, new Vector3(0, 6, 0), Quaternion.Identity, BoneInterpolation.Linear));
        ModelNode other = AddModel(scene, "二体目");
        MorphNode blink = other.Morphs[0];
        blink.Keys.Set(new MorphKey(0, 0, BezierCurve.Linear));
        blink.Keys.Set(new MorphKey(20, 1, BezierCurve.Linear));
        ModelNode still = AddModel(scene, "動かない");
        still.Bones[0].Position = new Vector3(1, 2, 3); // キーの無いモデルは触らない

        Assert.IsTrue(animation.ApplyLive(1.5));
        Assert.AreEqual(4.5f, center.Position.Y, 1e-3f, "45 フレーム目（長さ 60）");
        Assert.AreEqual(0.25f, blink.Weight, 1e-3f, "45 フレーム目は、長さ 20 で回して 5 フレーム目");
        Assert.AreEqual(new Vector3(1, 2, 3), still.Bones[0].Position);
        Assert.AreEqual(0, playback.CurrentFrame, "タイムラインの再生位置は使わない");

        Assert.IsTrue(animation.ApplyLive(2.5));
        Assert.AreEqual(1.5f, center.Position.Y, 1e-3f, "75 フレーム目は 15 フレーム目");
        Assert.AreEqual(0.75f, blink.Weight, 1e-3f, "75 フレーム目は 15 フレーム目");
        Assert.AreEqual(0, animation.UnregisteredCount, "当てはめは手の変更ではない");
    }

    [TestMethod]
    public void PosesAndModelsWithoutKeysAreLeftAlone()
    {
        var (_, _, animation, model) = AnimationFixtures.Create();
        Assert.IsFalse(animation.ApplyLive(1), "キーが無ければ何もしない");
        model.Bones[1].Keys.Set(new BoneKey(0, Vector3.UnitX, Quaternion.Identity, BoneInterpolation.Linear));
        model.Bones[1].Position = new Vector3(0, 9, 0);
        Assert.IsFalse(animation.ApplyLive(1), "0 フレーム目だけのキー（ポーズ）は繰り返さない");
        Assert.AreEqual(new Vector3(0, 9, 0), model.Bones[1].Position);
    }

    [TestMethod]
    public void LiveDoesNotMarkUserEditsAsUnregistered()
    {
        var (_, _, animation, model) = AnimationFixtures.Create();
        animation.TracksUserEdits = false;
        BoneNode center = model.Bones[0];
        center.Keys.Set(new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        center.Keys.Set(new BoneKey(30, Vector3.UnitY, Quaternion.Identity, BoneInterpolation.Linear));

        center.Position = new Vector3(5, 0, 0); // 手で動かす
        model.Morphs[1].Weight = 0.5f;
        Assert.AreEqual(0, animation.UnregisteredCount, "ライブは未登録の印を付けない");
        Assert.IsFalse(center.HasUnregisteredChange);

        animation.ApplyLive(0.5);
        Assert.AreEqual(new Vector3(0, 0.5f, 0), center.Position, "キーのあるボーンは、次の当てはめでモーションの値に戻る");
        Assert.AreEqual(0.5f, model.Morphs[1].Weight, "キーの無いモーフは手の値のまま");

        animation.TracksUserEdits = true;
        model.Morphs[0].Weight = 0.3f;
        Assert.AreEqual(1, animation.UnregisteredCount, "エディットでは今までどおり印を付ける");
    }

    [TestMethod]
    public void OutputSettingsClampAndNotify()
    {
        var output = new LiveOutputSettings();
        int changes = 0;
        output.Changed += (_, _) => changes++;
        Assert.AreEqual((1920, 1080), (output.Width, output.Height));
        Assert.AreEqual(new Vector3(0, 1, 0), output.Background, "初めはグリーンバック");

        output.SetSize(1280, 720);
        output.SetSize(1280, 720);
        Assert.AreEqual(1, changes, "同じ大きさなら知らせない");
        output.SetSize(1, 100000);
        Assert.AreEqual((16, 8192), (output.Width, output.Height));
        output.Background = new Vector3(2, -1, 0.5f);
        Assert.AreEqual(new Vector3(1, 0, 0.5f), output.Background);
        output.Reset();
        Assert.AreEqual((1920, 1080), (output.Width, output.Height));
        Assert.AreEqual(new Vector3(0, 1, 0), output.Background);
        Assert.AreEqual(4, changes);

        CollectionAssert.Contains(LiveOutputSizes.Presets.Select(p => (p.Width, p.Height)).ToList(), (1080, 1920), "縦長の配信");
        Assert.AreEqual(new Vector3(0, 1, 0), LiveOutputBackgrounds.Presets[0].Color);
    }

    [TestMethod]
    public void LiveSettingsAreSavedOnlyInLiveFiles()
    {
        var output = new LiveOutputSettings();
        output.SetSize(1080, 1920);
        output.Background = new Vector3(0, 0, 1);
        var project = new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(output) };
        string json = ProjectSerializer.Serialize(project);
        StringAssert.Contains(json, "\"outputWidth\":1080");
        StringAssert.Contains(json, "\"outputBackground\":\"#0000FF\"");

        var read = new LiveOutputSettings();
        ProjectSerializer.Deserialize(json, new LoadReport())!.Live!.ApplyTo(read);
        Assert.AreEqual((1080, 1920), (read.Width, read.Height));
        Assert.AreEqual(new Vector3(0, 0, 1), read.Background);

        Assert.DoesNotContain("\"live\"", ProjectSerializer.Serialize(new ProjectFile()), "エディットのファイルには書かない");
        var broken = new LiveOutputSettings();
        new LiveEntry { OutputBackground = "みどり" }.ApplyTo(broken);
        Assert.AreEqual(new Vector3(0, 1, 0), broken.Background, "読めない色は緑のまま");
    }

    [TestMethod]
    public void TrackingSettingsAreSavedWithTheTargetModel()
    {
        var model = Guid.NewGuid();
        var tracking = new FaceRetargetSettings { Mirror = true, Gaze = false, BodySway = false, Smoothing = 0.8f, TargetModelId = model };
        var project = new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(new LiveOutputSettings(), tracking) };
        string json = ProjectSerializer.Serialize(project);
        StringAssert.Contains(json, "\"tracking\":{\"target\":\"" + model.ToString("N") + "\"");
        StringAssert.Contains(json, "\"mirror\":true");

        // 読み込みでは、ファイルの中のモデルの目印から、読み込み直したモデルの Id を探す
        var reopened = Guid.NewGuid();
        var read = new FaceRetargetSettings();
        int changes = 0;
        read.Changed += (_, _) => changes++;
        ProjectSerializer.Deserialize(json, new LoadReport())!.Live!.Tracking!.ApplyTo(read, id => id == model.ToString("N") ? reopened : null);
        Assert.AreEqual(reopened, read.TargetModelId);
        Assert.IsTrue(read.Mirror);
        Assert.IsFalse(read.Gaze);
        Assert.IsTrue(read.UpperBody);
        Assert.IsFalse(read.BodySway);
        Assert.AreEqual(0.8f, read.Smoothing, 1e-6f);
        Assert.IsGreaterThan(0, changes, "変わったことを知らせる（保存していない印・パネルの表示）");

        // 当てはめるモデルが見つからなければ「自動で選ぶ」に戻す
        new TrackingEntry { Target = "ないモデル" }.ApplyTo(read, _ => null);
        Assert.IsNull(read.TargetModelId);
        Assert.IsFalse(read.Mirror, "書いていない値は既定（映像と同じ向き）");

        // スマホの設定も一緒に書く
        var phone = new PhoneTrackingSettings { Address = " 192.168.1.20 ", InvertPitch = true };
        string withPhone = ProjectSerializer.Serialize(new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(new LiveOutputSettings(), tracking, null, phone) });
        var readPhone = new PhoneTrackingSettings();
        ProjectSerializer.Deserialize(withPhone, new LoadReport())!.Live!.Tracking!.ApplyTo(readPhone);
        Assert.AreEqual("192.168.1.20", readPhone.Address, "前後の空白は除く");
        Assert.IsTrue(readPhone.InvertPitch);
        Assert.IsFalse(readPhone.InvertYaw);

        // 自動で選ぶときは、モデルを書かない
        StringAssert.DoesNotMatch(ProjectSerializer.Serialize(new ProjectFile { Mode = AppModes.LiveName, Live = LiveEntry.From(new LiveOutputSettings(), new FaceRetargetSettings()) }),
            new System.Text.RegularExpressions.Regex("\"target\""));
    }

    private static ModelNode AddModel(SceneDocument scene, string name)
    {
        var model = new ModelNode(name, name + ".pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, 1, 1, 0, 0), string.Empty, Vector3.Zero, Vector3.One);
        BoneNode[] bones = [new BoneNode("センター", 0, AnimationFixtures.Movable)];
        MorphNode[] morphs = [new MorphNode("まばたき", 0, MorphCategory.Eye, "頂点")];
        var boneGroup = new SceneNode(SceneNodeKind.Group, "ボーン", canRename: false, hasTransform: false);
        var morphGroup = new SceneNode(SceneNodeKind.Group, "表情", canRename: false, hasTransform: false);
        boneGroup.AddChild(bones[0]);
        morphGroup.AddChild(morphs[0]);
        model.AttachParts([boneGroup, morphGroup], bones, morphs);
        scene.Add(scene.ModelsGroup, model);
        return model;
    }
}
