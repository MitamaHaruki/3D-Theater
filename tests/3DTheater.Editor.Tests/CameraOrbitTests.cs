using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>ライブモードの、キャラクターの周りを回るカメラの動かし方（タッチパッド向け。設計判断記録 78 章の 7 段目）。</summary>
[TestClass]
public sealed class CameraOrbitTests
{
    private const float Tolerance = 1e-3f;

    [TestMethod]
    public void PivotIsWhereTheCameraLooksOnTheCharacterAxis()
    {
        // 正面（-Z の側）の高さ 10 から、まっすぐ奥（+Z）を見る
        var position = new Vector3(0, 10, -45);
        AssertNear(new Vector3(0, 10, 0), CameraOrbit.Pivot(position, Vector3.Zero, new Vector3(0, 0, 0), 0, 18));

        // 軸は X・Z だけを使う（高さは視線で決める）。少し見下ろすと、軸の所の視線の高さ
        Vector3 down = CameraOrbit.Pivot(position, new Vector3(10, 0, 0), new Vector3(0, 99, 0), 0, 18);
        AssertNear(new Vector3(0, 10 - (45 * MathF.Tan(10 * MathF.PI / 180)), 0), down);

        // 見上げて頭の上を通るときは、キャラクターの高さに収める
        AssertNear(new Vector3(0, 18, 0), CameraOrbit.Pivot(position, new Vector3(-30, 0, 0), Vector3.Zero, 0, 18));

        // 軸がカメラの後ろにあるときは、カメラの高さ
        AssertNear(new Vector3(0, 10, 0), CameraOrbit.Pivot(position, new Vector3(0, 180, 0), Vector3.Zero, 0, 18));

        // 真下を見ているときは、高さの真ん中
        AssertNear(new Vector3(0, 9, 0), CameraOrbit.Pivot(position, new Vector3(90, 0, 0), Vector3.Zero, 0, 18));
    }

    [TestMethod]
    public void PivotAheadIsTheTargetPoint()
    {
        AssertNear(new Vector3(0, 10, 0), CameraOrbit.PivotAhead(new Vector3(0, 10, -45), Vector3.Zero, 45));
        AssertNear(new Vector3(0, 10, -35), CameraOrbit.PivotAhead(new Vector3(0, 10, -45), Vector3.Zero, 0.5f), "近すぎる注視点なら 10 先");
    }

    [TestMethod]
    public void YawGoesAroundTheVerticalAxis()
    {
        var pivot = new Vector3(0, 10, 0);
        var (position, rotation) = CameraOrbit.Orbit(new Vector3(0, 10, -45), Vector3.Zero, pivot, 90, 0);
        AssertNear(new Vector3(-45, 10, 0), position, "高さと距離はそのまま、横へ回る");
        AssertNear(new Vector3(0, 90, 0), rotation, "中心の方を向いたまま");

        // 4 回で 1 周して戻る（角度は -180〜180 に収める）
        for (int i = 0; i < 3; i++)
        {
            (position, rotation) = CameraOrbit.Orbit(position, rotation, pivot, 90, 0);
        }
        AssertNear(new Vector3(0, 10, -45), position);
        Assert.AreEqual(0f, rotation.Y, Tolerance);
    }

    [TestMethod]
    public void PitchGoesOverTheCharacterAndStopsBeforeTheTop()
    {
        var pivot = new Vector3(0, 10, 0);
        var (position, rotation) = CameraOrbit.Orbit(new Vector3(0, 10, -45), Vector3.Zero, pivot, 0, 30);
        Assert.AreEqual(30f, rotation.X, Tolerance, "見下ろす側へ");
        Assert.IsGreaterThan(10f, position.Y, "カメラは上へ回る");
        Assert.AreEqual(45f, Vector3.Distance(position, pivot), Tolerance, "中心までの距離はそのまま");

        (_, rotation) = CameraOrbit.Orbit(position, rotation, pivot, 0, 80);
        Assert.AreEqual(CameraFly.MaxPitch, rotation.X, Tolerance, "真上を越えて裏返らない");
    }

    [TestMethod]
    public void ThePivotStaysAtTheSameSpotOnTheScreen()
    {
        // 傾きのあるカメラが、中心を画面の端のほうに映しているとき
        var position = new Vector3(5, 12, -30);
        var rotation = new Vector3(8, 15, 4);
        var pivot = new Vector3(2, 9, 3);
        Vector3 before = InCamera(position, rotation, pivot);
        foreach (var (yaw, pitch) in new[] { (30f, 0f), (0f, 20f), (-50f, -15f) })
        {
            var (movedPosition, movedRotation) = CameraOrbit.Orbit(position, rotation, pivot, yaw, pitch);
            AssertNear(before, InCamera(movedPosition, movedRotation, pivot), $"左右 {yaw}・上下 {pitch}");
            Assert.AreEqual(4f, movedRotation.Z, Tolerance, "傾きはそのまま");
        }
    }

    [TestMethod]
    public void DollyMovesTowardThePivotWithinLimits()
    {
        var pivot = new Vector3(0, 10, 0);
        AssertNear(new Vector3(0, 10, -22.5f), CameraOrbit.Dolly(new Vector3(0, 10, -45), pivot, 0.5f));
        AssertNear(new Vector3(0, 10, -CameraOrbit.MinDistance), CameraOrbit.Dolly(new Vector3(0, 10, -45), pivot, 0.001f), "近づきすぎない");
        AssertNear(new Vector3(0, 10, -90), CameraOrbit.Dolly(new Vector3(0, 10, -45), pivot, 2f));
        AssertNear(new Vector3(0, 10, -0.5f), CameraOrbit.Dolly(new Vector3(0, 10, -0.5f), pivot, 0.5f), "もっと近いときは、それより近づけない");
        AssertNear(pivot, CameraOrbit.Dolly(pivot, pivot, 0.5f), "中心にいるときは動かさない");

        Assert.IsLessThan(1f, CameraOrbit.DollyFactor(120), "奥へ回す・指を広げると近づく");
        Assert.IsGreaterThan(1f, CameraOrbit.DollyFactor(-120));
        Assert.AreEqual(1f, CameraOrbit.DollyFactor(0), Tolerance);
        Assert.AreEqual(CameraOrbit.DollyFactor(120) * CameraOrbit.DollyFactor(120), CameraOrbit.DollyFactor(240), Tolerance, "細かく届いても、合わせて同じだけ");
    }

    [TestMethod]
    public void WheelAmountsBecomeFingerMotion()
    {
        Assert.AreEqual(new Vector2(120, 0), CameraOrbit.FingerMotion(-120, 0, reverse: false), "指を右へ（左右のホイールは負で届く）");
        Assert.AreEqual(new Vector2(0, 120), CameraOrbit.FingerMotion(0, 120, reverse: false), "指を下へ（上下のホイールは正で届く）");
        Assert.AreEqual(new Vector2(-120, -120), CameraOrbit.FingerMotion(-120, 120, reverse: true), "逆にする");
        Assert.AreEqual(CameraOrbit.DegreesPerWheelStep, CameraOrbit.Degrees(120), Tolerance);
        Assert.AreEqual(CameraOrbit.PixelsPerWheelStep / 2, CameraOrbit.Pixels(60), Tolerance);
    }

    [TestMethod]
    public void OrbitSpeedScalesTheTurn()
    {
        // 回る速さ（設計判断記録 93 章）: 同じだけなでて、速さの倍だけ回る
        Assert.AreEqual(CameraOrbit.DegreesPerWheelStep * 2, CameraOrbit.Degrees(120, 2f), Tolerance);
        Assert.AreEqual(CameraOrbit.DegreesPerWheelStep / 2, CameraOrbit.Degrees(120, 0.5f), Tolerance);

        var settings = new CameraControlSettings();
        int changed = 0;
        settings.Changed += (_, _) => changed++;
        Assert.AreEqual(1f, settings.OrbitSpeed, "初めは 1 倍");
        settings.OrbitSpeed = 1.5f;
        Assert.AreEqual(1.5f, settings.OrbitSpeed);
        settings.OrbitSpeed = 10f;
        Assert.AreEqual(CameraControlSettings.MaxOrbitSpeed, settings.OrbitSpeed, "速すぎる値は収める");
        settings.OrbitSpeed = 0f;
        Assert.AreEqual(CameraControlSettings.MinOrbitSpeed, settings.OrbitSpeed, "0 にはしない（回らなくならない）");
        settings.OrbitSpeed = float.NaN;
        Assert.AreEqual(CameraControlSettings.MinOrbitSpeed, settings.OrbitSpeed, "数でない値は入れない");
        settings.OrbitSpeed = CameraControlSettings.MinOrbitSpeed;
        Assert.AreEqual(3, changed, "変わったときだけ知らせる");
    }

    // カメラから見た所（カメラの向きの中での座標。画面のどこに映るかが同じなら同じ）
    private static Vector3 InCamera(Vector3 position, Vector3 rotation, Vector3 point) =>
        Vector3.Transform(point - position, Quaternion.Inverse(SceneRotation.ToQuaternion(rotation)));

    private static void AssertNear(Vector3 expected, Vector3 actual, string message = "")
    {
        Assert.IsLessThan(Tolerance, Vector3.Distance(expected, actual), $"{message} 期待 {expected}・実際 {actual}");
    }
}
