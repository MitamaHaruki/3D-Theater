using System.IO;
using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class ProjectTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, ModelNode Model) Posed()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        var ease = new BoneInterpolation(BezierCurve.FromMmd(64, 0, 64, 127), BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear);
        model.Bones[1].Keys.SetRange(
        [
            new BoneKey(0, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear),
            new BoneKey(30, new Vector3(0, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f), ease, KeyInterpolationKind.Step),
        ]);
        model.Morphs[0].Keys.SetRange([new MorphKey(10, 1, KeyEditingCurve())]);
        playback.EndFrame = 600;
        playback.FrameRate = 60;
        playback.Seek(12);
        model.Bones[0].Position = new Vector3(0, 0, 3); // 未登録の変更
        model.Morphs[1].IsLocked = true;
        model.Bones[1].Parent!.IsVisible = false;          // まとまり「ボーン」を隠す
        model.Position = new Vector3(5, 0, 0);
        model.Name = "名前を変えた人形";
        CameraNode camera = scene.FindFirst<CameraNode>()!;
        camera.FieldOfView = 40;
        camera.Name = "正面カメラ";
        animation.AutoKey = true;
        return (scene, playback, animation, model);
    }

    private static BezierCurve KeyEditingCurve() => KeyEditing.SmoothCurve;

    private static ProjectFile RoundTrip(ProjectFile project)
    {
        var report = new LoadReport();
        ProjectFile? read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project), report);
        Assert.IsNotNull(read, string.Join("\n", report.Issues.Select(i => i.Message)));
        return read;
    }

    [TestMethod]
    public void SavedProjectRestoresSceneModelAndKeys()
    {
        var (scene, playback, animation, model) = Posed();
        ProjectFile saved = RoundTrip(ProjectCapture.Capture(scene, playback, animation, null, []));

        var (scene2, playback2, animation2, model2) = AnimationFixtures.Create();
        var report = new LoadReport();
        ProjectApply.ApplyScene(scene2, playback2, animation2, saved, report);
        ProjectApply.ApplyModel(model2, saved.Models.Single(), animation2, report);

        Assert.IsFalse(report.Issues.Any(), string.Join("\n", report.Issues.Select(i => i.Message)));
        Assert.AreEqual(60, playback2.FrameRate);
        Assert.AreEqual(600, playback2.EndFrame);
        Assert.IsTrue(animation2.AutoKey);
        Assert.AreEqual("正面カメラ", scene2.FindFirst<CameraNode>()!.Name);
        Assert.AreEqual(40f, scene2.FindFirst<CameraNode>()!.FieldOfView);
        Assert.AreEqual("名前を変えた人形", model2.Name);
        Assert.AreEqual(new Vector3(5, 0, 0), model2.Position);
        CollectionAssert.AreEqual(model.Bones[1].Keys.Keys.ToArray(), model2.Bones[1].Keys.Keys.ToArray(), "曲線と段階も含めて同じキー");
        CollectionAssert.AreEqual(model.Morphs[0].Keys.Keys.ToArray(), model2.Morphs[0].Keys.Keys.ToArray());
        Assert.AreEqual(new Vector3(0, 0, 3), model2.Bones[0].Position);
        Assert.IsTrue(model2.Bones[0].HasUnregisteredChange, "未登録の変更も保存して戻す（黙って捨てない）");
        Assert.AreEqual(1, animation2.UnregisteredCount);
        Assert.IsTrue(model2.Morphs[1].IsLocked);
        Assert.IsFalse(model2.Bones[1].Parent!.IsVisible);
        Assert.AreEqual(12, saved.Playback.CurrentFrame);
    }

    [TestMethod]
    public void UntouchedBonesAreNotWritten()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        model.Morphs[0].Weight = 0.5f;

        ProjectFile project = ProjectCapture.Capture(scene, playback, animation, null, []);

        Assert.IsNull(project.Models[0].Bones, "初期のままのボーンは書かない");
        Assert.HasCount(1, project.Models[0].Morphs!);
    }

    [TestMethod]
    public void BonesAreFoundByNameWhenTheModelChanged()
    {
        var (scene, playback, animation, model) = AnimationFixtures.Create();
        model.Bones[1].Keys.Set(new BoneKey(5, Vector3.Zero, Quaternion.Identity, BoneInterpolation.Linear));
        ProjectFile project = ProjectCapture.Capture(scene, playback, animation, null, []);
        project.Models[0].Bones![0].Index = 0; // ボーンの順番が変わったモデル
        project.Models[0].Bones!.Add(new BoneEntry { Name = "しっぽ", Index = 9 });

        var (_, _, animation2, model2) = AnimationFixtures.Create();
        var report = new LoadReport();
        ProjectApply.ApplyModel(model2, project.Models[0], animation2, report);

        Assert.AreEqual(1, model2.Bones[1].Keys.Count, "番号が違っても名前で見つける");
        Assert.AreEqual(0, model2.Bones[0].Keys.Count);
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("しっぽ")));
    }

    [TestMethod]
    public void ModelPathsAreRelativeToTheProjectAndResolvedBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "3DTheaterTests", Guid.NewGuid().ToString("N"));
        string modelDir = Path.Combine(root, "モデル", "人形");
        Directory.CreateDirectory(modelDir);
        string modelFile = Path.Combine(modelDir, "人形.pmx");
        File.WriteAllText(modelFile, "dummy");
        try
        {
            Assert.AreEqual(Path.Combine("モデル", "人形", "人形.pmx"), ProjectCapture.RelativeOrAbsolute(modelFile, root));
            Assert.AreEqual(modelFile, ProjectCapture.RelativeOrAbsolute(modelFile, null), "無題のプロジェクトは絶対パス");

            var entry = new ModelEntry { File = Path.Combine("モデル", "人形", "人形.pmx"), AbsoluteFile = @"Z:\どこにもない\人形.pmx" };
            Assert.AreEqual(modelFile, ProjectApply.ResolveModelPath(entry, root));

            // プロジェクトだけ別の場所へ動かしても、保存したときの絶対パスで見つける
            Assert.AreEqual(modelFile, ProjectApply.ResolveModelPath(new ModelEntry { File = @"..\x\人形.pmx", AbsoluteFile = modelFile }, root));
            Assert.IsNull(ProjectApply.ResolveModelPath(new ModelEntry { File = "無い.pmx" }, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void MissingModelsAreWrittenBackUnchanged()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var missing = new ModelEntry { File = "消えた.pmx", Name = "消えたモデル", Bones = [new BoneEntry { Name = "頭", Keys = [[0, 0, 0, 0, 0, 0, 0, 1, 0]] }] };

        ProjectFile project = RoundTrip(ProjectCapture.Capture(scene, playback, animation, null, [missing]));

        ModelEntry kept = project.Models.Single(m => m.Name == "消えたモデル");
        Assert.AreEqual("消えた.pmx", kept.File);
        Assert.HasCount(1, kept.Bones![0].Keys!);
    }

    [TestMethod]
    public void RenderingSettingsAreSavedAndOldFilesUseTheDefaults()
    {
        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.SelfShadow = false;
        settings.SelfShadow = false; // 同じ値なら知らせない
        Assert.AreEqual(1, changes);

        var report = new LoadReport();
        var project = new ProjectFile { Rendering = RenderingEntry.From(settings) };
        ProjectFile read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project), report)!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.IsFalse(restored.SelfShadow);
        Assert.IsTrue(restored.GroundShadow);

        // 描き方の無い前の版のファイルは、初期の設定（どちらも ON）
        ProjectFile old = ProjectSerializer.Deserialize("""{"format":"3DTheater.Project","version":1}""", report)!;
        var fromOld = new RenderSettings { SelfShadow = false, GroundShadow = false };
        old.Rendering.ApplyTo(fromOld);
        Assert.IsTrue(fromOld.SelfShadow && fromOld.GroundShadow);

        restored.Reset();
        Assert.IsTrue(restored.SelfShadow, "新しいプロジェクトは初期の設定に戻す");
    }

    [TestMethod]
    public void VisualEffectsAreSavedOnlyWhenUsed()
    {
        var report = new LoadReport();
        var settings = new RenderSettings();
        string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) });
        Assert.IsFalse(plain.Contains("effects", StringComparison.OrdinalIgnoreCase), "使っていなければ書かない（前の版と同じファイル）");

        settings.Effects = VisualEffects.None with { BloomEnabled = true, BloomIntensity = 1.5f, Exposure = 0.5f };
        string json = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) });
        ProjectFile read = ProjectSerializer.Deserialize(json, report)!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Effects, restored.Effects, "OFF の値（明るさ）も含めて戻る");
        Assert.IsTrue(restored.Effects.BloomEnabled && !restored.Effects.ColorEnabled);

        // 範囲の外の値は収める。一部だけ書かれたものは、残りを初期値にする
        ProjectFile odd = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"rendering":{"effects":{"vignetteEnabled":true,"vignette":5,"contrast":-1}}}""", report)!;
        var fromOdd = new RenderSettings();
        odd.Rendering.ApplyTo(fromOdd);
        Assert.AreEqual(1f, fromOdd.Effects.Vignette);
        Assert.AreEqual(0f, fromOdd.Effects.Contrast);
        Assert.AreEqual(0.8f, fromOdd.Effects.BloomThreshold, "書かれていない値は初期値");
        Assert.IsTrue(fromOdd.SelfShadow, "書かれていない影の設定も初期値");
    }

    [TestMethod]
    public void IllustrationStyleIsSavedOnlyWhenUsed()
    {
        var report = new LoadReport();
        var settings = new RenderSettings();
        string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) });
        Assert.IsFalse(plain.Contains("illustration", StringComparison.OrdinalIgnoreCase), "使っていなければ書かない");

        settings.Illustration = IllustrationStyle.None with { LinesEnabled = true, LineColor = "#ff0000", PaintLevels = 3 };
        string json = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) });
        ProjectFile read = ProjectSerializer.Deserialize(json, report)!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Illustration, restored.Illustration);
        Assert.AreEqual("#FF0000", restored.Illustration.LineColor, "色は大文字の #RRGGBB にそろえる");

        ProjectFile odd = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"rendering":{"illustration":{"paperEnabled":true,"paintLevels":99,"shadowColor":"青"}}}""",
            report)!;
        var fromOdd = new RenderSettings();
        odd.Rendering.ApplyTo(fromOdd);
        Assert.IsTrue(fromOdd.Illustration.PaperEnabled);
        Assert.AreEqual(IllustrationStyle.MaxPaintLevels, fromOdd.Illustration.PaintLevels);
        Assert.AreEqual(IllustrationStyle.None.ShadowColor, fromOdd.Illustration.ShadowColor, "読めない色は初期値");
        Assert.AreEqual(VisualEffects.None, fromOdd.Effects, "書かれていない映像効果はなし");
    }

    [TestMethod]
    public void BrokenNewerOrForeignFilesAreRejected()
    {
        var report = new LoadReport();
        Assert.IsNull(ProjectSerializer.Deserialize("{ これは JSON ではない", report));
        Assert.IsNull(ProjectSerializer.Deserialize("""{"format":"Other","version":1}""", report));
        Assert.IsNull(ProjectSerializer.Deserialize("""{"format":"3DTheater.Project","version":99}""", report));
        Assert.HasCount(3, report.Issues);
        StringAssert.Contains(report.Issues[2].Message, "新しい版");
    }

    [TestMethod]
    public void BadKeysInTheFileAreSkipped()
    {
        var (_, _, animation, model) = AnimationFixtures.Create();
        var entry = new ModelEntry
        {
            Bones = [new BoneEntry { Name = "センター", Index = 0, Keys = [[0, 0, 0, 0, 0, 0, 0, 1, 0], [1, 2, 3], [-5, 0, 0, 0, 0, 0, 0, 1, 0], [float.NaN, 0, 0, 0, 0, 0, 0, 1, 0]] }],
        };

        ProjectApply.ApplyModel(model, entry, animation, new LoadReport());

        Assert.AreEqual(1, model.Bones[0].Keys.Count);
    }

    [TestMethod]
    public void SceneCanBeResetToTheNewProjectState()
    {
        var (scene, _, _, model) = Posed();

        IReadOnlyList<ModelNode> removed = scene.ResetToDefault();

        Assert.AreSame(model, removed.Single());
        Assert.IsNull(model.Parent);
        Assert.IsFalse(scene.ModelsGroup.Children.OfType<ModelNode>().Any());
        Assert.AreEqual("メインカメラ", scene.FindFirst<CameraNode>()!.Name);
        CameraNode camera = scene.FindFirst<CameraNode>()!;
        Assert.AreEqual(30f, camera.FieldOfView, "MMD の初期のカメラと同じ");
        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0, 10, 0), camera.Pose.Target), 1e-4f, "注視点は (0, 10, 0)");
        Assert.AreEqual(45f, camera.TargetDistance);
        Assert.IsNotNull(scene.FindFirst<TestObjectNode>(), "確認用の立方体は残す");
        Assert.ThrowsExactly<InvalidOperationException>(() => scene.Remove(scene.FindFirst<CameraNode>()!));
    }
}
