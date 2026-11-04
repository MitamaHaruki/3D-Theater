using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>固定視点の向きと、回転・平行移動・ズームの振る舞い（指示書 2.4・2.5）。</summary>
[TestClass]
public sealed class ViewCameraTests
{
    private const float Tolerance = 1e-4f;

    [TestMethod]
    [DataRow(ViewKind.Front, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0f, DisplayName = "正面: -Z 側から +Z を見る。画面の右は +X")]
    [DataRow(ViewKind.Back, 0f, 0f, -1f, -1f, 0f, 0f, 0f, 1f, 0f, DisplayName = "背面: +Z 側から -Z を見る。画面の右は -X")]
    [DataRow(ViewKind.Right, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, DisplayName = "右側面（Blender と同じ）: +X 側から見る。画面の右は +Z")]
    [DataRow(ViewKind.Left, 1f, 0f, 0f, 0f, 0f, -1f, 0f, 1f, 0f, DisplayName = "左側面: -X 側から見る。画面の右は -Z")]
    [DataRow(ViewKind.Top, 0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, DisplayName = "上面: 真上から見下ろす。画面の上は +Z")]
    [DataRow(ViewKind.Bottom, 0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, -1f, DisplayName = "下面: 真下から見上げる。画面の上は -Z")]
    public void FixedViewsLookAlongExactAxes(ViewKind kind, float fx, float fy, float fz, float rx, float ry, float rz, float ux, float uy, float uz)
    {
        var camera = new ViewCamera(kind);
        CameraBasis basis = camera.GetMatrices(1f, null).Basis;

        AssertVector(new Vector3(fx, fy, fz), basis.Forward, "前");
        AssertVector(new Vector3(rx, ry, rz), basis.Right, "右");
        AssertVector(new Vector3(ux, uy, uz), basis.Up, "上");
        Assert.IsTrue(camera.IsOrthographic, "固定視点は平行投影で始まる");
    }

    [TestMethod]
    public void FreeViewStartsInPerspective()
    {
        var camera = new ViewCamera(ViewKind.Free);
        Assert.IsFalse(camera.IsOrthographic);
    }

    [TestMethod]
    public void OrbitFromFixedViewSwitchesToFreePerspective()
    {
        var camera = new ViewCamera(ViewKind.Front);
        camera.Orbit(new Vector2(40f, 0f));

        Assert.AreEqual(ViewKind.Free, camera.Kind);
        Assert.IsFalse(camera.IsOrthographic);
        Assert.AreNotEqual(0f, camera.Yaw, "横に動かした分だけ回る");
    }

    [TestMethod]
    public void PanAndZoomKeepFixedView()
    {
        var camera = new ViewCamera(ViewKind.Top);
        float distanceBefore = camera.Distance;
        Vector3 targetBefore = camera.Target;

        camera.Pan(new Vector2(10f, 5f), 500f);
        camera.Zoom(2f);

        Assert.AreEqual(ViewKind.Top, camera.Kind, "平行移動とズームでは固定視点のまま");
        Assert.IsTrue(camera.IsOrthographic);
        Assert.IsLessThan(distanceBefore, camera.Distance, "ホイールを前に回すと近づく");
        Assert.AreNotEqual(targetBefore, camera.Target);
        Assert.AreEqual(targetBefore.Y, camera.Target.Y, Tolerance, "上面で平行移動しても高さは変わらない");
    }

    [TestMethod]
    public void PanMovesSceneWithTheMouse()
    {
        var camera = new ViewCamera(ViewKind.Front);
        Vector3 before = camera.Target;
        camera.Pan(new Vector2(100f, 0f), 400f);

        // マウスを右へ動かすと、物は右へ動いて見える = 注視点は左（-X）へ動く
        Assert.IsLessThan(before.X, camera.Target.X);
        float expected = camera.VisibleHeight / 400f * 100f;
        Assert.AreEqual(expected, before.X - camera.Target.X, Tolerance, "1 ピクセルあたりの移動量は見えている高さから決まる");
    }

    [TestMethod]
    public void TargetIsAtScreenCenterInBothProjections()
    {
        foreach (ViewKind kind in new[] { ViewKind.Free, ViewKind.Front, ViewKind.Right, ViewKind.Top })
        {
            var camera = new ViewCamera(kind);
            camera.Pan(new Vector2(30f, -20f), 300f);
            ViewMatrices m = camera.GetMatrices(16f / 9f, null);
            Vector4 clip = Vector4.Transform(new Vector4(camera.Target, 1f), m.View * m.Projection);
            Assert.AreEqual(0f, clip.X / clip.W, Tolerance, $"{kind}: 注視点は画面の横の中央");
            Assert.AreEqual(0f, clip.Y / clip.W, Tolerance, $"{kind}: 注視点は画面の縦の中央");
            Assert.IsTrue(clip.Z / clip.W is > 0f and < 1f, $"{kind}: 注視点は描ける奥行きの範囲にある");
        }
    }

    [TestMethod]
    public void ToggleProjectionKeepsFixedDirection()
    {
        var camera = new ViewCamera(ViewKind.Right);
        camera.ToggleProjection();
        Assert.AreEqual(ViewKind.Right, camera.Kind);
        Assert.IsFalse(camera.IsOrthographic);
    }

    [TestMethod]
    public void CameraViewUsesSceneCameraAndLeavesItOnNavigation()
    {
        var sceneCamera = new CameraNode("カメラ") { Position = new Vector3(1f, 2f, -5f), Rotation = new Vector3(10f, 20f, 0f) };
        var camera = new ViewCamera(ViewKind.Camera);

        ViewMatrices m = camera.GetMatrices(1f, sceneCamera);
        AssertVector(sceneCamera.Position, m.Eye, "目の位置");

        camera.Zoom(1f, sceneCamera);
        Assert.AreEqual(ViewKind.Free, camera.Kind, "操作するとカメラ視点から自由視点へ移る");
        Assert.AreEqual(20f * MathF.PI / 180f, camera.Yaw, Tolerance, "カメラの向きを引き継ぐ");
        Assert.AreEqual(new Vector3(1f, 2f, -5f), sceneCamera.Position, "シーンのカメラそのものは動かさない");
    }

    [TestMethod]
    public void ChangedIsRaisedOnEveryOperation()
    {
        var camera = new ViewCamera();
        int count = 0;
        camera.Changed += (_, _) => count++;
        camera.Orbit(new Vector2(1f, 1f));
        camera.Pan(new Vector2(1f, 1f), 100f);
        camera.Zoom(1f);
        camera.SetKind(ViewKind.Top);
        camera.ToggleProjection();
        camera.Reset();
        Assert.AreEqual(6, count);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual, string name)
    {
        Assert.AreEqual(expected.X, actual.X, Tolerance, $"{name}.X");
        Assert.AreEqual(expected.Y, actual.Y, Tolerance, $"{name}.Y");
        Assert.AreEqual(expected.Z, actual.Z, Tolerance, $"{name}.Z");
    }
}
