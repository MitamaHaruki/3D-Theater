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
        camera.FieldOfView = 30;
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
        Assert.AreEqual(30f, scene2.FindFirst<CameraNode>()!.FieldOfView);
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
        Assert.AreEqual(45f, scene.FindFirst<CameraNode>()!.FieldOfView);
        Assert.IsNotNull(scene.FindFirst<TestObjectNode>(), "確認用の立方体は残す");
        Assert.ThrowsExactly<InvalidOperationException>(() => scene.Remove(scene.FindFirst<CameraNode>()!));
    }
}
