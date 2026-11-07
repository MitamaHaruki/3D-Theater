using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class CameraMotionTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, CameraNode Camera) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        return (scene, playback, animation, scene.FindFirst<CameraNode>()!);
    }

    private static CameraKey Key(int frame, Vector3 target, Vector3 rotation, float distance = 45, float fov = 30) =>
        new(frame, target, rotation, distance, fov, CameraInterpolation.Linear);

    [TestMethod]
    public void CameraOrbitsAroundTheTargetBetweenKeys()
    {
        var track = new KeyTrack<CameraKey>();
        track.Set(Key(0, new Vector3(0, 10, 0), Vector3.Zero));
        track.Set(Key(30, new Vector3(0, 10, 0), new Vector3(0, 360, 0)));

        Assert.IsTrue(KeyframeEvaluator.TryEvaluate(track, 15, out CameraPose half));

        Assert.AreEqual(180f, half.Rotation.Y, 1e-3f, "角度は成分ごとに直線で動く（1 周も表せる）");
        Assert.AreEqual(45f, Vector3.Distance(half.Position, half.Target), 1e-3f, "注視点からの距離は変わらない（まわりを回る）");
        Assert.AreEqual(45f, half.Position.Z, 1e-3f, "半周すると注視点の奥側");
        KeyframeEvaluator.TryEvaluate(track, 0, out CameraPose start);
        Assert.AreEqual(-45f, start.Position.Z, 1e-3f, "回転 0 ではモデルの正面（-Z 側）から見る");
    }

    [TestMethod]
    public void CameraPoseRoundTripsThroughTheNode()
    {
        var camera = new CameraNode("カメラ");
        var pose = new CameraPose(new Vector3(1, 12, 3), new Vector3(10, -30, 5), 20, 35);

        camera.ApplyPose(pose);

        Assert.AreEqual(0f, Vector3.Distance(pose.Target, camera.Pose.Target), 1e-3f);
        Assert.AreEqual(pose.Rotation, camera.Pose.Rotation);
        Assert.AreEqual(20f, camera.TargetDistance);
        Assert.AreEqual(35f, camera.FieldOfView);
    }

    [TestMethod]
    public void CameraWithoutKeysIsNotMarkedUnregistered()
    {
        var (_, playback, animation, camera) = Create();

        camera.Position = new Vector3(1, 2, 3);
        Assert.IsFalse(camera.HasUnregisteredChange, "キーの無いカメラを置いても未登録にしない");

        camera.Keys.Set(Key(0, Vector3.Zero, Vector3.Zero));
        camera.Keys.Set(Key(30, Vector3.Zero, new Vector3(0, 90, 0)));
        playback.Seek(10);
        camera.FieldOfView = 60;
        playback.Seek(20);
        Assert.IsTrue(camera.HasUnregisteredChange);
        Assert.AreEqual(60f, camera.FieldOfView, "キーのあるカメラの手の変更は、フレームを動かしても保つ");
        Assert.AreEqual(1, animation.UnregisteredCount);

        animation.RegisterUnregistered();
        Assert.IsTrue(camera.Keys.TryGet(20, out CameraKey key));
        Assert.AreEqual(60f, key.FieldOfView);
        Assert.IsFalse(camera.HasUnregisteredChange);
    }

    [TestMethod]
    public void AutoKeyRegistersCameraKeysEvenWithoutKeys()
    {
        var (_, playback, animation, camera) = Create();
        animation.AutoKey = true;
        playback.Seek(5);

        camera.Rotation = new Vector3(20, 0, 0);

        Assert.AreEqual(5, camera.Keys.Keys.Single().Frame);
        Assert.AreEqual(new Vector3(20, 0, 0), camera.Keys.Keys[0].Rotation);
    }

    [TestMethod]
    public void VmdCameraConversionRoundTripsAndPutsPositiveXBelow()
    {
        byte[] ip = new byte[24];
        for (int p = 0; p < 6; p++)
        {
            ip[(p * 4) + 0] = 10; // x1
            ip[(p * 4) + 1] = 100; // x2
            ip[(p * 4) + 2] = 20; // y1
            ip[(p * 4) + 3] = 110; // y2
        }
        var raw = new VmdCameraKey(12, -45, new Vector3(0, 10, 0), new Vector3(0.5f, 0.2f, 0.1f), ip, 30, false);

        CameraKey key = MmdCamera.FromVmd(raw, 12);
        VmdCameraKey back = MmdCamera.ToVmd(key, 12);

        Assert.AreEqual(45f, key.Distance);
        Assert.AreEqual(BezierCurve.FromMmd(10, 20, 100, 110), key.Interpolation.X);
        CollectionAssert.AreEqual(ip, back.Interpolation);
        Assert.AreEqual(raw.Distance, back.Distance);
        Assert.AreEqual(0f, Vector3.Distance(raw.Rotation, back.Rotation), 1e-6f);
        Assert.IsLessThan(10f, key.Pose.Position.Y, "VMD の回転 X が正なら、注視点より下から見上げる（saba・three.js と同じ扱い）");
    }

    [TestMethod]
    public void CameraMotionImportScalesFramesAndKeepsCutsSharp()
    {
        var (_, _, _, camera) = Create();
        var vmd = new VmdDocument
        {
            CameraKeys =
            [
                new VmdCameraKey(0, -45, Vector3.Zero, Vector3.Zero, new byte[24], 30, false),
                new VmdCameraKey(10, -45, Vector3.Zero, Vector3.Zero, new byte[24], 30, false),
                new VmdCameraKey(11, -20, new Vector3(0, 15, 0), Vector3.Zero, new byte[24], 20, false), // 切り替え
            ],
        };
        var report = new LoadReport();

        CameraImportResult result = CameraMotion.Apply(vmd, camera, frameRate: 60, report);

        Assert.AreEqual(3, result.Keys);
        Assert.AreEqual(22, result.LastFrame);
        Assert.AreEqual(KeyInterpolationKind.Step, camera.Keys.Keys[2].Kind);
        KeyframeEvaluator.TryEvaluate(camera.Keys, 21, out CameraPose between);
        Assert.AreEqual(45f, between.Distance, "切り替えの途中のフレームは前の絵のまま");
        Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("切り替わる")));
    }

    [TestMethod]
    public void ExportedCameraMotionImportsBackTheSame()
    {
        var (_, _, _, camera) = Create();
        var ease = new CameraInterpolation(BezierCurve.FromMmd(64, 0, 64, 127), BezierCurve.Linear, BezierCurve.Linear,
            BezierCurve.FromMmd(20, 40, 90, 127), BezierCurve.Linear, BezierCurve.Linear);
        camera.Keys.SetRange([Key(0, new Vector3(0, 10, 0), new Vector3(5, 0, 0)), Key(40, new Vector3(1, 11, 2), new Vector3(10, 720, 3), 30, 40) with { Interpolation = ease }]);
        var report = new LoadReport();

        byte[] bytes = VmdWriter.Write(CameraMotion.CreateVmd(camera, 30, report));
        VmdDocument read = VmdReader.Read(bytes).Document!;
        var (_, _, _, other) = Create();
        CameraMotion.Apply(read, other, 30, new LoadReport());

        Assert.IsTrue(read.IsCameraMotion);
        Assert.AreEqual("カメラ・照明", read.ModelName.Text);
        Assert.AreEqual(2, other.Keys.Count);
        CameraKey a = camera.Keys.Keys[1], b = other.Keys.Keys[1];
        Assert.AreEqual(a.Interpolation, b.Interpolation);
        Assert.AreEqual(0f, Vector3.Distance(a.Rotation, b.Rotation), 1e-3f, "720° も保つ");
        Assert.AreEqual(0f, Vector3.Distance(a.Target, b.Target), 1e-5f);
        Assert.AreEqual(a.Distance, b.Distance);
        Assert.AreEqual(a.FieldOfView, b.FieldOfView);
    }

    [TestMethod]
    public void CameraKeysAreSavedInTheProjectAndEditable()
    {
        var (scene, playback, animation, camera) = Create();
        camera.TargetDistance = 33; // キーが無いうちの変更は未登録にならない
        camera.Keys.SetRange([Key(0, Vector3.Zero, Vector3.Zero), Key(20, Vector3.One, new Vector3(0, 45, 0)) with { Kind = KeyInterpolationKind.Step }]);
        Assert.IsFalse(camera.HasUnregisteredChange);
        ProjectFile project = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, [])), new LoadReport())!;

        var (scene2, playback2, animation2, camera2) = Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, project, new LoadReport());

        CollectionAssert.AreEqual(camera.Keys.Keys.ToArray(), camera2.Keys.Keys.ToArray());
        Assert.IsFalse(camera2.HasUnregisteredChange, "開いただけでは未登録にならない");

        KeyEditing.Move([new KeyRef(camera2, 20)], 10, duplicate: false);
        Assert.AreEqual(30, camera2.Keys.Keys[1].Frame);
        Assert.HasCount(1, KeyEditing.AtFrame(scene2.Root, 0), "カメラのキーも、まとまりの◆に入る");
    }
}

[TestClass]
public sealed class UndoDisposalTests
{
    private sealed class DisposableAction : IUndoableAction, IDisposable
    {
        public bool Disposed { get; private set; }
        public string Description => "テスト";
        public void Undo() { }
        public void Redo() { }
        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public void DroppedActionsAreDisposed()
    {
        var undo = new UndoService(limit: 2);
        var first = new DisposableAction();
        var second = new DisposableAction();
        var third = new DisposableAction();
        undo.Push(first);
        undo.Push(second);
        undo.Push(third);
        Assert.IsTrue(first.Disposed, "古くなって捨てた操作は破棄する");

        undo.Undo();
        undo.Push(new DisposableAction());
        Assert.IsTrue(third.Disposed, "やり直しの履歴から消えた操作も破棄する");

        undo.Clear();
        Assert.IsTrue(second.Disposed);
    }

    [TestMethod]
    public void RemovedNodesCanBeInsertedBackInPlace()
    {
        var (scene, _, _, model) = AnimationFixtures.Create();
        var other = new ModelNode("2 体目", "b.pmx", new ModelSummary("PMX 2.0", 0, 0, 0, 0, 0, 0, 0, 0), string.Empty, Vector3.Zero, Vector3.One);
        scene.Add(scene.ModelsGroup, other);
        int index = scene.ModelsGroup.Children.IndexOf(model);

        scene.Remove(model);
        scene.Insert(scene.ModelsGroup, index, model);

        Assert.AreEqual(index, scene.ModelsGroup.Children.IndexOf(model));
        var changes = new List<string?>();
        scene.NodeChanged += (_, e) => changes.Add(e.PropertyName);
        model.Bones[0].Position = Vector3.One;
        Assert.Contains(nameof(SceneNode.Position), changes, "戻した後も変更が届く");
    }
}
