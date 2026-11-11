using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class EffectKeyTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, RenderSettings Settings) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var settings = new RenderSettings();
        animation.Rendering = settings;
        return (scene, playback, animation, settings);
    }

    private static EffectPose Pose(float exposure, float bloom, Vector3 fogColor, float focus = 45f) =>
        new(exposure, 1f, 1f, 0f, bloom, 0.8f, 0.7f, 0.35f, focus, 4f, 6f, 20f, 200f, fogColor);

    private static EffectKey Key(int frame, EffectPose pose) => new(frame, pose, BezierCurve.Linear);

    [TestMethod]
    public void EffectKeysInterpolateEveryValue()
    {
        var track = new KeyTrack<EffectKey>();
        track.Set(Key(0, Pose(0f, 0.2f, Vector3.One, 10f)));
        track.Set(Key(20, Pose(2f, 1.0f, Vector3.Zero, 50f)));

        Assert.IsTrue(KeyframeEvaluator.TryEvaluate(track, 10, out EffectPose half));
        Assert.AreEqual(1f, half.Exposure);
        Assert.AreEqual(0.6f, half.BloomIntensity, 1e-6f);
        Assert.AreEqual(30f, half.DofFocusDistance);
        Assert.AreEqual(new Vector3(0.5f), half.FogColor);

        track.Set(track.Keys[1] with { Kind = KeyInterpolationKind.Step });
        KeyframeEvaluator.TryEvaluate(track, 19, out EffectPose step);
        Assert.AreEqual(0f, step.Exposure, "段階: 次のキーまで前の値");
    }

    [TestMethod]
    public void EffectKeysDriveTheRenderSettings()
    {
        var (scene, playback, animation, settings) = Create();
        settings.Effects = settings.Effects with { ColorEnabled = true, BloomEnabled = true, AoStrength = 0.3f };
        settings.Fog = settings.Fog with { Enabled = true, Height = 5f };
        var applying = new List<bool>();
        settings.Changed += (_, _) => applying.Add(animation.IsApplying);

        scene.Effects.Keys.SetRange([Key(0, Pose(0f, 0.2f, Vector3.One)), Key(30, Pose(2f, 1.0f, Vector3.Zero, 15f))]);
        playback.Seek(15);

        Assert.AreEqual(1f, settings.Effects.Exposure, 1e-5f, "再生位置のキーの値を、映像効果の設定へ入れる");
        Assert.AreEqual(0.6f, settings.Effects.BloomIntensity, 1e-5f);
        Assert.AreEqual(30f, settings.Effects.DofFocusDistance, 1e-4f);
        Assert.AreEqual("#808080", settings.Fog.Color, "霧の色も動く（#RRGGBB に丸める）");
        Assert.IsTrue(settings.Effects.ColorEnabled && settings.Effects.BloomEnabled && settings.Fog.Enabled, "使うか（ON / OFF）は変えない");
        Assert.AreEqual(0.3f, settings.Effects.AoStrength, "キーで動かさない値は変えない");
        Assert.AreEqual(5f, settings.Fog.Height);
        Assert.IsFalse(scene.Effects.HasUnregisteredChange, "当てはめは、手で変えたことにしない");
        Assert.IsTrue(applying.Count > 0 && applying.All(a => a), "当てはめている間の設定の変化は、当てはめの途中と分かる");
    }

    [TestMethod]
    public void HandEditsOfKeyedEffectsAreKeptUntilRegistered()
    {
        var (scene, playback, animation, settings) = Create();
        EffectsNode effects = scene.Effects;
        var undo = new UndoService();
        var edits = new EditService(undo, animation, playback);

        settings.Effects = settings.Effects with { Exposure = 1f };
        Assert.IsFalse(effects.HasUnregisteredChange, "キーの無い映像効果を変えても未登録にしない");

        effects.Keys.SetRange([Key(0, Pose(0f, 0.2f, Vector3.One)), Key(30, Pose(2f, 1.0f, Vector3.One))]);
        playback.Seek(10);
        settings.SelfShadow = false;
        settings.Effects = settings.Effects with { AoStrength = 0.9f };
        Assert.IsFalse(effects.HasUnregisteredChange, "キーで動かさない設定を変えても未登録にしない");

        settings.Effects = settings.Effects with { Exposure = 3f };
        Assert.IsTrue(effects.HasUnregisteredChange);
        CollectionAssert.Contains(animation.Unregistered.ToList(), effects);
        playback.Seek(20);
        Assert.AreEqual(3f, settings.Effects.Exposure, "キーのある映像効果の手の変更は、フレームを動かしても保つ");
        CollectionAssert.AreEqual(new SceneNode[] { effects }, animation.RegistrationTargets([effects]).ToArray());

        edits.Change("キーの登録", [effects], () => animation.Register([effects]), includeKeys: true);
        Assert.IsTrue(effects.Keys.TryGet(20, out EffectKey key));
        Assert.AreEqual(3f, key.Pose.Exposure);
        Assert.IsFalse(effects.HasUnregisteredChange);
        undo.Undo();
        Assert.AreEqual(2, effects.Keys.Count, "元に戻すと、置いたキーが消える");
        Assert.IsTrue(effects.HasUnregisteredChange);

        animation.DiscardUnregistered();
        Assert.AreEqual(4f / 3f, settings.Effects.Exposure, 1e-4f, "捨てると、キーの値に戻る");

        animation.AutoKey = true;
        playback.Seek(40);
        settings.Fog = settings.Fog with { Start = 55f };
        Assert.IsTrue(effects.Keys.TryGet(40, out EffectKey auto), "自動キーは、変えたときにキーを置く");
        Assert.AreEqual(55f, auto.Pose.FogStart);
    }

    [TestMethod]
    public void EffectKeysCanBeEditedSavedAndReset()
    {
        var (scene, playback, animation, settings) = Create();
        EffectsNode effects = scene.Effects;
        effects.Keys.SetRange([Key(0, Pose(0f, 0.2f, Vector3.One)), Key(10, Pose(1f, 0.4f, Vector3.One)),
            Key(20, Pose(2f, 0.6f, new Vector3(1f, 0.5f, 0f))) with { Curve = KeyEditing.SmoothCurve, Kind = KeyInterpolationKind.Step }]);

        Assert.IsTrue(KeyEditing.HasKeys(effects));
        Assert.HasCount(3, KeyEditing.InRange([scene.Root], 0, 30), "シーンの行で、映像効果のキーも選べる");
        KeyEditing.Move([new KeyRef(effects, 10)], 5, duplicate: false);
        CollectionAssert.AreEqual(new[] { 0, 15, 20 }, effects.Keys.Keys.Select(k => k.Frame).ToArray());
        KeyEditing.Paste(KeyEditing.Copy([new KeyRef(effects, 15)]), 40);
        Assert.AreEqual(1f, effects.Keys.Keys[^1].Pose.Exposure);
        Assert.AreEqual(1, KeyEditing.Delete([new KeyRef(effects, 40)]));
        KeyEditing.SetInterpolation([new KeyRef(effects, 15)], InterpolationPreset.Smooth);
        Assert.IsTrue(CurveEditing.TryGetCurve(new KeyRef(effects, 15), 0, out BezierCurve curve, out _));
        Assert.AreEqual(KeyEditing.SmoothCurve, curve);
        CollectionAssert.Contains(CurveEditing.Sample(effects, 0, 20).Select(s => s.Name).ToArray(), "明るさ");

        // 保存と読み込み
        effects.HasUnregisteredChange = true;
        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;
        var (scene2, playback2, animation2, _) = Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, project, new LoadReport());
        CollectionAssert.AreEqual(effects.Keys.Keys.ToArray(), scene2.Effects.Keys.Keys.ToArray(), "曲線・段階も戻る");
        Assert.IsTrue(scene2.Effects.HasUnregisteredChange);

        // 新しいプロジェクトに戻すと、映像効果・カメラ・照明のキーも消える
        scene.FindFirst<CameraNode>()!.Keys.Set(new CameraKey(0, Vector3.Zero, Vector3.Zero, 45, 30, CameraInterpolation.Linear));
        scene.MainLight.Keys.Set(new LightKey(0, scene.MainLight.Pose, BezierCurve.Linear));
        scene.ResetToDefault();
        Assert.AreEqual(0, effects.Keys.Count);
        Assert.IsFalse(effects.HasUnregisteredChange);
        Assert.AreEqual(0, scene.FindFirst<CameraNode>()!.Keys.Count);
        Assert.AreEqual(0, scene.MainLight.Keys.Count);
        _ = settings;
    }
}
