using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>ライブモードのカメラ（設計判断記録 78 章の 3 段目）: 足す・外す、見る・配信するカメラ、右ボタンでの動かし方、保存。</summary>
[TestClass]
public sealed class LiveCameraTests
{
    [TestMethod]
    public void CamerasCanBeAddedAndRemovedButNotTheLiveCamera()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var undo = new UndoService();
        CameraNode live = scene.MainCamera;
        live.Position = new Vector3(1, 2, 3);
        live.Rotation = new Vector3(10, 20, 0);
        live.FieldOfView = 40;

        CameraNode second = CameraPresenceAction.Create(scene, live);
        Assert.AreEqual("カメラ 2", second.Name);
        Assert.AreEqual(live.Position, second.Position, "今見ているカメラと同じ所から始める");
        Assert.AreEqual(live.Rotation, second.Rotation);
        Assert.AreEqual(40f, second.FieldOfView);
        CameraPresenceAction.Add(scene, undo, second);
        Assert.AreEqual("カメラ 3", CameraPresenceAction.NameFor(scene), "使っていない番号");
        CollectionAssert.AreEqual(new[] { second }, scene.AddedCameras.ToArray());

        Assert.IsFalse(CameraPresenceAction.Remove(scene, undo, live), "ライブカメラは外せない");
        Assert.IsTrue(CameraPresenceAction.Remove(scene, undo, second));
        Assert.IsEmpty(scene.AddedCameras.ToArray());
        undo.Undo();
        CollectionAssert.AreEqual(new[] { second }, scene.AddedCameras.ToArray(), "外したのを元に戻す");
        undo.Undo();
        Assert.IsEmpty(scene.AddedCameras.ToArray(), "足したのを元に戻す");
        undo.Redo();
        CollectionAssert.AreEqual(new[] { second }, scene.AddedCameras.ToArray());

        scene.ResetToDefault();
        Assert.IsEmpty(scene.AddedCameras.ToArray(), "新しいシーンでは無い");
        Assert.AreSame(live, scene.MainCamera);
    }

    [TestMethod]
    public void PreviewAndOnAirFallBackToTheLiveCamera()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var undo = new UndoService();
        CameraNode second = CameraPresenceAction.Create(scene, scene.MainCamera);
        CameraPresenceAction.Add(scene, undo, second);
        var settings = new LiveCameraSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;

        Assert.AreSame(scene.MainCamera, settings.PreviewCamera(scene), "決めていなければライブカメラ");
        Assert.IsTrue(settings.IsPreviewOnAir(scene));
        settings.Preview = second.Id;
        Assert.AreSame(second, settings.PreviewCamera(scene));
        Assert.AreSame(scene.MainCamera, settings.OnAirCamera(scene), "配信は「配信用画面に適用」まで変わらない");
        Assert.IsFalse(settings.IsPreviewOnAir(scene));
        settings.OnAir = second.Id;
        Assert.IsTrue(settings.IsPreviewOnAir(scene));
        Assert.AreEqual(2, changes);

        CameraPresenceAction.Remove(scene, undo, second);
        Assert.AreSame(scene.MainCamera, settings.PreviewCamera(scene), "外したらライブカメラ");
        Assert.AreSame(scene.MainCamera, settings.OnAirCamera(scene));
        undo.Undo();
        Assert.AreSame(second, settings.OnAirCamera(scene), "元に戻すと、また配信に映る");
    }

    [TestMethod]
    public void LookingTurnsTheWayTheMouseMoves()
    {
        Vector3 turned = CameraFly.Look(Vector3.Zero, 100, 0);
        Assert.AreEqual(15f, turned.Y, 1e-4f, "右へ動かすと右を向く（左右の角度が増える）");
        Vector3 down = CameraFly.Look(Vector3.Zero, 0, 100);
        Assert.AreEqual(15f, down.X, 1e-4f, "下へ動かすと見下ろす");
        Assert.AreEqual(89f, CameraFly.Look(Vector3.Zero, 0, 10000).X, 1e-4f, "真下を越えない");
        Assert.AreEqual(-89f, CameraFly.Look(Vector3.Zero, 0, -10000).X, 1e-4f, "真上を越えない");
        Assert.AreEqual(-170f, CameraFly.Look(new Vector3(0, 170, 5), 20 / CameraFly.DegreesPerPixel, 0).Y, 1e-3f, "-180〜180 度に収める");
        Assert.AreEqual(5f, CameraFly.Look(new Vector3(0, 170, 5), 1, 1).Z, "傾きはそのまま");
    }

    [TestMethod]
    public void KeysMoveAlongTheView()
    {
        // 初めは +Z を向いている（右は +X、上は +Y）
        Vector3 forward = CameraFly.Move(Vector3.Zero, Vector3.Zero, FlyKeys.Forward, 1f);
        AssertNear(new Vector3(0, 0, CameraFly.UnitsPerSecond), forward, "W で前へ");
        AssertNear(new Vector3(-CameraFly.UnitsPerSecond, 0, 0), CameraFly.Move(Vector3.Zero, Vector3.Zero, FlyKeys.Left, 1f), "A で左へ");
        AssertNear(new Vector3(0, CameraFly.UnitsPerSecond * 0.5f, 0), CameraFly.Move(Vector3.Zero, Vector3.Zero, FlyKeys.Up, 0.5f), "E で上へ（時間の分だけ）");
        AssertNear(new Vector3(0, -CameraFly.UnitsPerSecond * CameraFly.FastFactor, 0),
            CameraFly.Move(Vector3.Zero, Vector3.Zero, FlyKeys.Down | FlyKeys.Fast, 1f), "Q で下へ・Shift で速く");
        AssertNear(Vector3.Zero, CameraFly.Move(Vector3.Zero, Vector3.Zero, FlyKeys.Forward | FlyKeys.Back, 1f), "逆どうしは打ち消す");
        AssertNear(Vector3.One, CameraFly.Move(Vector3.One, Vector3.Zero, FlyKeys.Forward, float.NaN), "時間がおかしければ動かない");

        // 右を向いて（左右 90 度）見下ろしている（上下 90 度近く）ときの前は、+X と下の間
        Vector3 turned = CameraFly.Move(Vector3.Zero, new Vector3(45, 90, 0), FlyKeys.Forward, 1f);
        Assert.IsGreaterThan(0f, turned.X, "右を向いた前は +X");
        Assert.IsLessThan(0f, turned.Y, "見下ろした前は下");
        Assert.AreEqual(CameraFly.UnitsPerSecond, turned.Length(), 1e-3f, "斜めでも速さは同じ");
        Vector3 diagonal = CameraFly.Move(Vector3.Zero, Vector3.Zero, FlyKeys.Forward | FlyKeys.Right, 1f);
        Assert.AreEqual(CameraFly.UnitsPerSecond, diagonal.Length(), 1e-3f, "前と右を一緒に押しても速くならない");
    }

    [TestMethod]
    public void WheelZoomsAndMiddleDragPans()
    {
        Assert.AreEqual(28f, CameraFly.Zoom(30f, 120), 1e-4f, "奥へ回すと寄る（視野角を狭く）");
        Assert.AreEqual(34f, CameraFly.Zoom(30f, -240), 1e-4f);
        Assert.AreEqual(CameraFly.MinFieldOfView, CameraFly.Zoom(6f, 1200));
        Assert.AreEqual(CameraFly.MaxFieldOfView, CameraFly.Zoom(119f, -1200));
        AssertNear(new Vector3(-1, 2, 0), CameraFly.Pan(Vector3.Zero, Vector3.Zero, 10, 20, 0.1f), "右へドラッグすると、カメラは左へ（物が右へついて来る）");
    }

    [TestMethod]
    public void CamerasAreSavedWithTheChosenOnes()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var undo = new UndoService();
        CameraNode second = CameraPresenceAction.Create(scene, scene.MainCamera);
        second.Name = "引きの絵";
        second.Position = new Vector3(5, 6, 7);
        second.Rotation = new Vector3(10, -30, 0);
        second.FieldOfView = 50;
        CameraPresenceAction.Add(scene, undo, second);
        CameraNode third = CameraPresenceAction.Create(scene, scene.MainCamera);
        CameraPresenceAction.Add(scene, undo, third);
        var settings = new LiveCameraSettings { Preview = third.Id, OnAir = second.Id };

        ProjectFile project = ProjectCapture.Capture(scene, new PlaybackService(), new AnimationService(scene, new PlaybackService()), null, []);
        project.Live = LiveEntry.From(new LiveOutputSettings(), cameras: settings, scene: scene);
        Assert.AreEqual(2, project.Live.PreviewCamera);
        Assert.AreEqual(1, project.Live.OnAirCamera);
        var report = new Theater3D.Formats.LoadReport();
        ProjectFile read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project), report)!;

        SceneDocument opened = SceneDocument.CreateDefault();
        ProjectApply.ApplyScene(opened, new PlaybackService(), new AnimationService(opened, new PlaybackService()), read, report);
        CameraNode[] cameras = [.. opened.AddedCameras];
        Assert.HasCount(2, cameras);
        Assert.AreEqual("引きの絵", cameras[0].Name);
        Assert.AreEqual(new Vector3(5, 6, 7), cameras[0].Position);
        Assert.AreEqual(new Vector3(10, -30, 0), cameras[0].Rotation);
        Assert.AreEqual(50f, cameras[0].FieldOfView);
        var reopened = new LiveCameraSettings();
        read.Live!.ApplyTo(reopened, opened);
        Assert.AreSame(cameras[1], reopened.PreviewCamera(opened));
        Assert.AreSame(cameras[0], reopened.OnAirCamera(opened));

        var old = new LiveCameraSettings { Preview = Guid.NewGuid() };
        new LiveEntry().ApplyTo(old, opened);
        Assert.IsNull(old.Preview, "前の版のファイル（番号が無い）はライブカメラ");
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, string message)
    {
        Assert.AreEqual(expected.X, actual.X, 1e-3f, message);
        Assert.AreEqual(expected.Y, actual.Y, 1e-3f, message);
        Assert.AreEqual(expected.Z, actual.Z, 1e-3f, message);
    }
}
