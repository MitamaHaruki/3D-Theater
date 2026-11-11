using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class SettingsUndoTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, RenderSettings Settings, UndoService Undo, EditService Edits) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var settings = new RenderSettings();
        animation.Rendering = settings;
        var undo = new UndoService();
        return (scene, playback, animation, settings, undo, new EditService(undo, animation, playback));
    }

    [TestMethod]
    public void ChangesWhileASettingsWindowIsOpenAreUndoneTogether()
    {
        var (_, _, _, settings, undo, edits) = Create();
        RenderSettingsSnapshot original = settings.Capture();

        // 画面を開いている間に、つまみを何度も動かす
        SettingsTransaction window = edits.BeginSettings("映像効果の変更", settings);
        settings.Effects = settings.Effects with { ColorEnabled = true, Exposure = 0.5f };
        settings.Effects = settings.Effects with { Exposure = 1.2f };
        settings.Fog = settings.Fog with { Enabled = true, Start = 40f };
        Assert.IsTrue(window.Commit());
        Assert.IsFalse(window.Commit(), "2 回目からは何もしない");

        Assert.AreEqual(1, undo.UndoCount, "画面を開いている間の変更は 1 つの操作");
        Assert.AreEqual("映像効果の変更", undo.UndoDescription);
        RenderSettingsSnapshot changed = settings.Capture();
        Assert.IsTrue(undo.Undo());
        Assert.AreEqual(original, settings.Capture(), "元に戻すと、画面を開く前の設定");
        Assert.IsTrue(undo.Redo());
        Assert.AreEqual(changed, settings.Capture(), "やり直すと、閉じたときの設定");

        Assert.IsFalse(edits.BeginSettings("霧の変更", settings).Commit(), "何も変えずに閉じたら積まない");
        Assert.AreEqual(1, undo.UndoCount);
    }

    [TestMethod]
    public void MenuTogglesAreUndoableOneByOne()
    {
        var (_, _, _, settings, undo, edits) = Create();

        Assert.IsTrue(edits.ChangeSettings("セルフシャドウの切り替え", settings, () => settings.SelfShadow = false));
        Assert.IsTrue(edits.ChangeSettings("地面の影の切り替え", settings, () => settings.GroundShadow = false));
        Assert.IsFalse(edits.ChangeSettings("地面の影の切り替え", settings, () => settings.GroundShadow = false), "変わらなければ積まない");

        Assert.AreEqual(2, undo.UndoCount);
        undo.Undo();
        Assert.IsTrue(settings.GroundShadow);
        Assert.IsFalse(settings.SelfShadow);
        undo.Undo();
        Assert.IsTrue(settings.SelfShadow);
    }

    [TestMethod]
    public void UndoingSettingsAlsoRestoresEffectKeysAndTheUnregisteredMark()
    {
        var (scene, playback, animation, settings, undo, edits) = Create();
        EffectsNode effects = scene.Effects;
        var pose = new EffectPose(0f, 1f, 1f, 0f, 0.6f, 0.8f, 0.7f, 0.35f, 45f, 4f, 6f, 20f, 200f, Vector3.One);
        effects.Keys.SetRange([new EffectKey(0, pose, BezierCurve.Linear), new EffectKey(30, pose with { Exposure = 2f }, BezierCurve.Linear)]);
        playback.Seek(15);
        Assert.AreEqual(1f, settings.Effects.Exposure, 1e-5f);

        // キーのあるときに画面で値を変えると未登録の変更になる。元に戻すと、値も印も戻る
        SettingsTransaction window = edits.BeginSettings("映像効果の変更", settings);
        settings.Effects = settings.Effects with { Exposure = -2f };
        window.Commit();
        Assert.IsTrue(effects.HasUnregisteredChange);
        undo.Undo();
        Assert.IsFalse(effects.HasUnregisteredChange, "未登録の印も戻る");
        Assert.AreEqual(1f, settings.Effects.Exposure, 1e-5f, "値も開く前（キーの値）に戻る");
        undo.Redo();
        Assert.IsTrue(effects.HasUnregisteredChange);
        Assert.AreEqual(-2f, settings.Effects.Exposure, 1e-5f);
        animation.DiscardUnregistered();

        // 自動キーなら画面の変更でキーが置かれる。元に戻すと、置かれたキーも消える
        animation.AutoKey = true;
        playback.Seek(20);
        SettingsTransaction auto = edits.BeginSettings("映像効果の変更", settings);
        settings.Effects = settings.Effects with { Saturation = 0f };
        auto.Commit();
        Assert.IsTrue(effects.Keys.HasKeyAt(20));
        undo.Undo();
        Assert.IsFalse(effects.Keys.HasKeyAt(20), "自動キーで置かれたキーも消える");
        Assert.AreEqual(1f, settings.Effects.Saturation, 1e-5f);

        // 別のフレームで元に戻すと、そのフレームのキーの値に合わせ直す
        animation.AutoKey = false;
        playback.Seek(0);
        edits.ChangeSettings("セルフシャドウの切り替え", settings, () => settings.SelfShadow = false);
        playback.Seek(30);
        undo.Undo();
        Assert.IsTrue(settings.SelfShadow);
        Assert.AreEqual(2f, settings.Effects.Exposure, 1e-5f, "元に戻した後も、今のフレームのキーの値");
    }
}
