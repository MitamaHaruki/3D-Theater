using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor.Tests;

/// <summary>カメラの平行投影（MMD の「パース OFF」）。</summary>
[TestClass]
public sealed class CameraOrthographicTests
{
    private const float Tolerance = 1e-3f;

    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, CameraNode Camera) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        return (scene, playback, animation, scene.FindFirst<CameraNode>()!);
    }

    private static CameraKey Key(int frame, float distance, bool orthographic) =>
        new(frame, new Vector3(0, 10, 0), Vector3.Zero, distance, 30, CameraInterpolation.Linear, Orthographic: orthographic);

    private static Vector3 Ndc(ViewMatrices m, Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), m.View * m.Projection);
        return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
    }

    [TestMethod]
    public void KeysSwitchTheProjectionAsSteps()
    {
        var track = new KeyTrack<CameraKey>();
        track.SetRange([Key(0, 45, false), Key(30, 90, true), Key(60, 45, false)]);

        KeyframeEvaluator.TryEvaluate(track, 15, out CameraPose before);
        KeyframeEvaluator.TryEvaluate(track, 30, out CameraPose at);
        KeyframeEvaluator.TryEvaluate(track, 45, out CameraPose after);
        KeyframeEvaluator.TryEvaluate(track, 60, out CameraPose back);

        Assert.IsFalse(before.Orthographic, "次のキーまでは前のキーの値（補間しない）");
        Assert.AreEqual(67.5f, before.Distance, Tolerance, "距離は補間する");
        Assert.IsTrue(at.Orthographic);
        Assert.IsTrue(after.Orthographic);
        Assert.IsFalse(back.Orthographic);

        var (_, playback, _, camera) = Create();
        camera.Keys.SetRange(track.Keys);
        playback.Seek(40);
        Assert.IsTrue(camera.Orthographic, "再生でカメラの平行投影が切り替わる");
        playback.Seek(10);
        Assert.IsFalse(camera.Orthographic);
        Assert.IsFalse(camera.HasUnregisteredChange, "キーの当てはめは手の変更にしない");
    }

    [TestMethod]
    public void OrthographicHeightComesFromTheDistanceNotTheFieldOfView()
    {
        var camera = new CameraNode("カメラ");
        camera.ApplyPose(new CameraPose(new Vector3(0, 10, 0), Vector3.Zero, 45, 30, Orthographic: true));
        const float aspect = 16f / 9f;

        ViewMatrices m = ViewCamera.SceneCameraMatrices(camera, aspect);
        Assert.IsTrue(m.IsOrthographic);
        Assert.AreEqual(25f, camera.OrthographicHeight, Tolerance, "距離 45 で縦に 25（mmd_tools と同じ 25 / 45）");
        Assert.AreEqual(1f, Ndc(m, new Vector3(0, 22.5f, 0)).Y, Tolerance, "注視点から上に 12.5 が画面の上の端");
        Assert.AreEqual(1f, Ndc(m, new Vector3(0, 22.5f, 30)).Y, Tolerance, "遠くても同じ大きさに写る（遠近が無い）");
        Assert.AreEqual(1f, Ndc(m, new Vector3(12.5f * aspect, 10, 0)).X, Tolerance, "横は縦 × 縦横の比");
        Assert.AreEqual(0f, Vector3.Distance(camera.Position, m.Eye), Tolerance, "目の位置はカメラの位置");
        float depth = Ndc(m, new Vector3(0, 10, 0)).Z;
        Assert.IsTrue(depth is > 0f and < 1f, "注視点は描ける奥行きの範囲にある");

        camera.FieldOfView = 90;
        Assert.AreEqual(1f, Ndc(ViewCamera.SceneCameraMatrices(camera, aspect), new Vector3(0, 22.5f, 0)).Y, Tolerance, "視野角は使わない");
        camera.ApplyPose(camera.Pose with { Distance = 90 });
        Assert.AreEqual(0.5f, Ndc(ViewCamera.SceneCameraMatrices(camera, aspect), new Vector3(0, 22.5f, 0)).Y, Tolerance, "距離を 2 倍にすると半分の大きさ");

        camera.Orthographic = false;
        Assert.IsFalse(ViewCamera.SceneCameraMatrices(camera, aspect).IsOrthographic, "切ると透視投影に戻る");
    }

    [TestMethod]
    public void NegativeDistanceTurnsTheCameraTowardTheTarget()
    {
        // 注視点の奥にカメラを置く（MMD の VMD では距離が正）。mmd_tools と同じく、平行投影では注視点の側を向く
        var camera = new CameraNode("カメラ");
        camera.ApplyPose(new CameraPose(new Vector3(0, 10, 0), Vector3.Zero, -20, 30, Orthographic: true));
        Assert.AreEqual(20f, camera.Position.Z, Tolerance, "カメラは注視点の奥（+Z の側）");

        ViewMatrices m = ViewCamera.SceneCameraMatrices(camera, 1f);
        Vector3 target = Ndc(m, new Vector3(0, 10, 0));
        Assert.AreEqual(0f, target.X, Tolerance);
        Assert.AreEqual(0f, target.Y, Tolerance);
        Assert.IsTrue(target.Z is > 0f and < 1f, "注視点がカメラの前にある");
        Assert.IsLessThan(0f, Ndc(m, new Vector3(1, 10, 0)).X, "後ろから見るので、+X は画面の左");
        Assert.IsGreaterThan(0f, Ndc(m, new Vector3(0, 11, 0)).Y, "上は上のまま");
    }

    [TestMethod]
    [DataRow(0.6f, DisplayName = "区画が書き出しより縦長（枠は横いっぱい）")]
    [DataRow(2.5f, DisplayName = "区画が書き出しより横長（枠は左右の真ん中）")]
    public void CameraViewShowsTheExportFrameExactly(float paneAspect)
    {
        const float frameAspect = 16f / 9f;
        var sceneCamera = new CameraNode("カメラ");
        sceneCamera.ApplyPose(new CameraPose(new Vector3(0, 10, 0), new Vector3(10, 30, 0), 40, 30, Orthographic: true));
        ViewMatrices export = ViewCamera.SceneCameraMatrices(sceneCamera, frameAspect);
        ViewMatrices pane = new ViewCamera(ViewKind.Camera).GetMatrices(paneAspect, sceneCamera, frameAspect);
        var (left, top, width, height) = ViewCamera.ExportFrame(paneAspect, 1f, frameAspect);

        Matrix4x4.Invert(export.View * export.Projection, out Matrix4x4 inverse);
        foreach (Vector2 ndc in new[] { new Vector2(1, 1), new Vector2(-1, 0.5f), new Vector2(0.3f, -1) })
        {
            Vector4 world = Vector4.Transform(new Vector4(ndc, 0.5f, 1f), inverse);
            Vector3 inPane = Ndc(pane, new Vector3(world.X, world.Y, world.Z) / world.W);
            Assert.AreEqual(left + ((ndc.X + 1f) / 2f * width), (inPane.X + 1f) / 2f * paneAspect, 1e-3f, "横の位置");
            Assert.AreEqual(top + ((1f - ndc.Y) / 2f * height), (1f - inPane.Y) / 2f, 1e-3f, "縦の位置");
        }
    }

    [TestMethod]
    [DataRow(45f, DisplayName = "注視点の手前のカメラ")]
    [DataRow(-30f, DisplayName = "注視点の奥のカメラ（向きを回す）")]
    public void LeavingAnOrthographicCameraViewKeepsTheLook(float distance)
    {
        var sceneCamera = new CameraNode("カメラ");
        sceneCamera.ApplyPose(new CameraPose(new Vector3(1, 10, 2), new Vector3(15, -40, 0), distance, 30, Orthographic: true));
        var view = new ViewCamera(ViewKind.Camera);
        ViewMatrices before = view.GetMatrices(1.5f, sceneCamera);
        Vector3 position = sceneCamera.Position;

        view.Pan(Vector2.Zero, 400f, sceneCamera); // 操作すると自由視点へ移る（動かす量は 0）
        ViewMatrices after = view.GetMatrices(1.5f, sceneCamera);

        Assert.AreEqual(ViewKind.Free, view.Kind);
        Assert.IsTrue(view.IsOrthographic, "平行投影のカメラからは平行投影の自由視点へ移る");
        Assert.AreEqual(sceneCamera.OrthographicHeight, view.VisibleHeight, Tolerance, "縦に写る長さを引き継ぐ");
        foreach (Vector3 p in new[] { new Vector3(1, 10, 2), new Vector3(4, 13, -1), new Vector3(-3, 6, 5) })
        {
            Vector3 a = Ndc(before, p), b = Ndc(after, p);
            Assert.AreEqual(a.X, b.X, Tolerance, "同じ所に写る（横）");
            Assert.AreEqual(a.Y, b.Y, Tolerance, "同じ所に写る（縦）");
        }
        Assert.AreEqual(position, sceneCamera.Position, "シーンのカメラは動かさない");
    }

    [TestMethod]
    public void ToggleWithKeysIsUnregisteredAndRegistersTheFlag()
    {
        var (_, playback, animation, camera) = Create();
        camera.Keys.SetRange([Key(0, 45, false), Key(60, 45, false)]);
        playback.Seek(20);

        camera.Orthographic = true;
        Assert.IsTrue(camera.HasUnregisteredChange, "キーのあるカメラで切り替えると未登録の変更");
        playback.Seek(25);
        Assert.IsTrue(camera.Orthographic, "フレームを動かしても保つ");

        animation.RegisterUnregistered();
        Assert.IsTrue(camera.Keys.TryGet(25, out CameraKey key));
        Assert.IsTrue(key.Orthographic, "登録したキーに平行投影が入る");
        playback.Seek(40);
        Assert.IsTrue(camera.Orthographic);
        playback.Seek(10);
        Assert.IsFalse(camera.Orthographic);
    }

    [TestMethod]
    public void ToggleWithoutKeysIsNotUnregisteredButAutoKeyRegisters()
    {
        var (_, playback, animation, camera) = Create();
        camera.Orthographic = true;
        Assert.IsFalse(camera.HasUnregisteredChange, "キーの無いカメラは未登録にしない");
        Assert.AreEqual(0, camera.Keys.Count);

        animation.AutoKey = true;
        playback.Seek(5);
        camera.Orthographic = false;
        Assert.IsFalse(camera.Keys.Keys.Single().Orthographic, "自動キーで、切り替えた値のキーができる");
        Assert.AreEqual(5, camera.Keys.Keys[0].Frame);
    }

    [TestMethod]
    public void UndoRestoresTheProjection()
    {
        var camera = new CameraNode("カメラ");
        NodeState before = camera.CaptureState(includeKeys: true);
        camera.Orthographic = true;
        Assert.AreNotEqual(before, camera.CaptureState(includeKeys: true), "写しが変わったと分かる");

        camera.RestoreState(before);
        Assert.IsFalse(camera.Orthographic);
    }

    [TestMethod]
    public void ProjectSavesTheProjectionAndOldFilesArePerspective()
    {
        var (scene, playback, animation, camera) = Create();
        camera.Orthographic = true;
        camera.Keys.SetRange([Key(0, 45, true), Key(30, 60, false)]);
        animation.ApplyFrame();
        string json = ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, []));
        StringAssert.Contains(json, "\"orthographic\":true", "カメラの平行投影を書く");

        var (scene2, playback2, animation2, camera2) = Create();
        ProjectApply.ApplyScene(scene2, playback2, animation2, ProjectSerializer.Deserialize(json, new LoadReport())!, new LoadReport());
        Assert.IsTrue(camera2.Orthographic);
        CollectionAssert.AreEqual(camera.Keys.Keys.ToArray(), camera2.Keys.Keys.ToArray(), "キーの平行投影の印も保つ");

        // 平行投影の欄の無い古いファイルは透視投影
        camera.Keys.ReplaceAll([]);
        camera.Orthographic = false;
        string old = ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, null, []));
        Assert.IsFalse(old.Contains("\"orthographic\"", StringComparison.Ordinal), "透視投影なら書かない");
        var (scene3, playback3, animation3, camera3) = Create();
        camera3.Orthographic = true;
        ProjectApply.ApplyScene(scene3, playback3, animation3, ProjectSerializer.Deserialize(old, new LoadReport())!, new LoadReport());
        Assert.IsFalse(camera3.Orthographic);
    }

    [TestMethod]
    public void NewProjectIsPerspective()
    {
        var (scene, _, _, camera) = Create();
        camera.Orthographic = true;
        scene.ResetToDefault();
        Assert.IsFalse(camera.Orthographic);
    }

    [TestMethod]
    public void VmdPerspectiveOffKeysAreUsedAndWrittenBack()
    {
        var (_, playback, _, camera) = Create();
        var vmd = new VmdDocument
        {
            CameraKeys =
            [
                new VmdCameraKey(0, -45, new Vector3(0, 10, 0), Vector3.Zero, new byte[24], 30, false),
                new VmdCameraKey(20, -45, new Vector3(0, 10, 0), Vector3.Zero, new byte[24], 30, true),
            ],
        };
        var report = new LoadReport();

        CameraMotion.Apply(vmd, camera, 30, report);
        playback.Seek(25);
        Assert.IsTrue(camera.Orthographic, "パース OFF のキーから平行投影になる");
        playback.Seek(10);
        Assert.IsFalse(camera.Orthographic);
        Assert.IsFalse(report.Issues.Any(i => i.Message.Contains("透視投影で表示")), "使えないという知らせは出さない");

        VmdDocument written = VmdReader.Read(VmdWriter.Write(CameraMotion.CreateVmd(camera, 30, new LoadReport()))).Document!;
        CollectionAssert.AreEqual(new[] { false, true }, written.CameraKeys.OrderBy(k => k.Frame).Select(k => k.Orthographic).ToArray(), "書き出しにも入る");
    }
}
