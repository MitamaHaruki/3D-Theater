using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class SelfShadowKeyTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, RenderSettings Settings) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var settings = new RenderSettings();
        animation.Rendering = settings;
        return (scene, playback, animation, settings);
    }

    [TestMethod]
    public void SelfShadowKeysSwitchTheSettingAtTheirFrames()
    {
        var (scene, playback, _, settings) = Create();
        scene.SelfShadow.Keys.SetRange([new SelfShadowKey(0, 1, 0.01f), new SelfShadowKey(30, 0, 0.02f), new SelfShadowKey(60, 2, 0.03f)]);

        playback.Seek(29);
        Assert.IsTrue(settings.SelfShadow, "次のキーの直前までは前のキー（いつも段階）");
        playback.Seek(30);
        Assert.IsFalse(settings.SelfShadow, "モード 0 は影なし");
        playback.Seek(70);
        Assert.IsTrue(settings.SelfShadow, "モード 2 も影あり（モード 1 と同じに描く）");
        Assert.IsFalse(scene.SelfShadow.HasUnregisteredChange, "当てはめは、手で変えたことにしない");
    }

    [TestMethod]
    public void HandTogglesOfKeyedSelfShadowAreKeptUntilRegistered()
    {
        var (scene, playback, animation, settings) = Create();
        SelfShadowNode shadow = scene.SelfShadow;
        var undo = new UndoService();
        var edits = new EditService(undo, animation, playback);

        settings.SelfShadow = false;
        Assert.IsFalse(shadow.HasUnregisteredChange, "キーの無いセルフ影を切り替えても未登録にしない");
        settings.SelfShadow = true;

        shadow.Keys.SetRange([new SelfShadowKey(0, 2, 0.02f), new SelfShadowKey(30, 0, 0.03f)]);
        playback.Seek(40);
        Assert.IsFalse(settings.SelfShadow);
        settings.Effects = settings.Effects with { Exposure = 1f };
        Assert.IsFalse(shadow.HasUnregisteredChange, "ほかの設定を変えても未登録にしない");

        edits.ChangeSettings("セルフシャドウの切り替え", settings, () => settings.SelfShadow = true);
        Assert.IsTrue(shadow.HasUnregisteredChange);
        CollectionAssert.Contains(animation.Unregistered.ToList(), shadow);
        playback.Seek(45);
        Assert.IsTrue(settings.SelfShadow, "キーのあるセルフ影の手の変更は、フレームを動かしても保つ");

        undo.Undo();
        Assert.IsFalse(shadow.HasUnregisteredChange, "設定の元に戻すで、未登録の印も戻る");
        Assert.IsFalse(settings.SelfShadow, "今のフレームのキーの値");
        undo.Redo();
        Assert.IsTrue(shadow.HasUnregisteredChange);

        Assert.AreEqual(1, animation.Register([shadow]));
        Assert.IsTrue(shadow.Keys.TryGet(45, out SelfShadowKey key));
        Assert.AreEqual((byte)1, key.Mode, "影なしのキーの後で影ありにしたら、モード 1");
        Assert.AreEqual(0.03f, key.Distance, "影範囲は前のキーから引き継ぐ");
        Assert.IsFalse(shadow.HasUnregisteredChange);

        settings.SelfShadow = false;
        animation.DiscardUnregistered();
        Assert.IsTrue(settings.SelfShadow, "捨てると、キーの値に戻る");

        animation.AutoKey = true;
        playback.Seek(10);
        settings.SelfShadow = false;
        Assert.IsTrue(shadow.Keys.TryGet(10, out SelfShadowKey auto), "自動キーは、切り替えたときにキーを置く");
        Assert.AreEqual((byte)0, auto.Mode);
    }

    [TestMethod]
    public void SelfShadowKeysCanBeEditedAndSaved()
    {
        var (scene, playback, animation, _) = Create();
        SelfShadowNode shadow = scene.SelfShadow;
        shadow.Keys.SetRange([new SelfShadowKey(0, 1, 0.01f), new SelfShadowKey(10, 0, 0.02f), new SelfShadowKey(20, 2, 0.03f)]);

        Assert.IsTrue(KeyEditing.HasKeys(shadow));
        Assert.HasCount(3, KeyEditing.InRange([scene.Root], 0, 30), "シーンの行で、セルフ影のキーも選べる");
        KeyEditing.Move([new KeyRef(shadow, 10)], 5, duplicate: false);
        CollectionAssert.AreEqual(new[] { 0, 15, 20 }, shadow.Keys.Keys.Select(k => k.Frame).ToArray());
        KeyEditing.Paste(KeyEditing.Copy([new KeyRef(shadow, 15)]), 40);
        Assert.AreEqual((byte)0, shadow.Keys.Keys[^1].Mode);
        Assert.AreEqual(1, KeyEditing.Delete([new KeyRef(shadow, 40)]));
        KeyEditing.SetInterpolation([new KeyRef(shadow, 15)], InterpolationPreset.Smooth);
        Assert.AreEqual(3, shadow.Keys.Count, "補間の切り替えは、セルフ影のキーを変えない");
        KeyEditing.ScaleTime([shadow], 2);
        CollectionAssert.AreEqual(new[] { 0, 30, 40 }, shadow.Keys.Keys.Select(k => k.Frame).ToArray());

        shadow.HasUnregisteredChange = true;
        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;
        var (scene2, playback2, animation2, _) = Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, project, new LoadReport());
        CollectionAssert.AreEqual(shadow.Keys.Keys.ToArray(), scene2.SelfShadow.Keys.Keys.ToArray(), "モードと影範囲も戻る");
        Assert.IsTrue(scene2.SelfShadow.HasUnregisteredChange);

        scene.ResetToDefault();
        Assert.AreEqual(0, shadow.Keys.Count, "新しいプロジェクトでは消える");
    }

    [TestMethod]
    public void VmdSelfShadowKeysRoundTrip()
    {
        var (scene, _, _, _) = Create();
        var vmd = new VmdDocument
        {
            ShadowKeys =
            [
                new VmdShadowKey(0, 1, 0.01125f),
                new VmdShadowKey(30, 0, 0.02f),
                new VmdShadowKey(45, 2, 0.03f),
                new VmdShadowKey(50, 7, 0.04f), // 分からないモード
            ],
        };
        Assert.IsTrue(vmd.IsCameraMotion, "セルフ影のキーだけのモーションも、カメラ・照明用のモーション");
        var report = new LoadReport();

        SelfShadowImportResult result = SelfShadowMotion.Apply(vmd, scene.SelfShadow, frameRate: 60, report);

        Assert.AreEqual(4, result.Keys);
        Assert.AreEqual(100, result.LastFrame, "60 fps の作品では 2 倍の位置");
        Assert.AreEqual((byte)2, scene.SelfShadow.Keys.Keys[2].Mode, "モード 2 は残す（書き戻すため）");
        Assert.AreEqual((byte)1, scene.SelfShadow.Keys.Keys[3].Mode, "分からないモードはモード 1");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("モード 2")));
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("分からないセルフ影のモード")));

        CameraNode camera = scene.FindFirst<CameraNode>()!;
        var exportReport = new LoadReport();
        VmdDocument written = CameraMotion.CreateVmd(camera, 60, exportReport, scene.MainLight, scene.SelfShadow);
        VmdDocument read = VmdReader.Read(VmdWriter.Write(written)).Document!;
        Assert.HasCount(4, read.ShadowKeys);
        CollectionAssert.AreEqual(new[] { 0, 30, 45, 50 }, read.ShadowKeys.Select(k => k.Frame).ToArray(), "30 fps に直して書く");
        Assert.AreEqual(0.03f, read.ShadowKeys[2].Distance, "影範囲は読んだ値のまま書き戻す");
        Assert.IsFalse(exportReport.Issues.Any(i => i.Message.Contains("空のモーション")), "セルフ影のキーだけでも空ではない");
        _ = Vector3.Zero;
    }
}
