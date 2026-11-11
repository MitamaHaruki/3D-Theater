using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class LightMarkerTests
{
    private static readonly Vector2 Size = new(200, 200);

    // 正面（-Z の側）から原点を見る平行投影。width が画面の幅（ワールドの長さ）
    private static Matrix4x4 Front(float width) =>
        Matrix4x4.CreateLookAtLeftHanded(new Vector3(0, 0, -10), Vector3.Zero, Vector3.UnitY)
        * Matrix4x4.CreateOrthographicLeftHanded(width, width, 0.1f, 100);

    private static Vector2 Screen(Vector3 world, Matrix4x4 viewProjection)
    {
        Assert.IsTrue(Picking.TryProject(world, viewProjection, Size, out Vector2 screen));
        return screen;
    }

    [TestMethod]
    public void MarkerShapesMatchTheLightType()
    {
        Assert.HasCount(3 * LightMarkers.Segments, LightMarkers.Lines(new LightNode("点", LightType.Point)), "球（3 つの輪）");
        Assert.HasCount(3 * LightMarkers.Segments + LightMarkers.Segments + 4,
            LightMarkers.Lines(new LightNode("スポット", LightType.Spot)), "小さな球と円すい（輪と 4 本の線）");
        Assert.HasCount(1 + 4 + LightMarkers.Segments,
            LightMarkers.Lines(new LightNode("平行", LightType.Directional)), "矢印（軸・4 本の矢じり・根元の輪）");

        var moved = new LightNode("点", LightType.Point) { Position = new Vector3(5, 1, -2) };
        foreach (var (a, b) in LightMarkers.Lines(moved))
        {
            Assert.AreEqual(LightMarkers.PointRadius, Vector3.Distance(a, moved.Position), 1e-4f, "照明の位置を中心にした球");
            Assert.AreEqual(LightMarkers.PointRadius, Vector3.Distance(b, moved.Position), 1e-4f);
        }
    }

    [TestMethod]
    public void PointLightIsPickedNearItsLinesOrInsideTheSphere()
    {
        Matrix4x4 vp = Front(4f); // 1 ピクセル = 0.02（球の半径 0.6 は 30 ピクセル）
        LightNode[] lights = [new LightNode("点", LightType.Point)];

        Assert.AreEqual(0, LightMarkers.Pick(new Vector2(100, 100), Size, vp, lights, 6), "真ん中");
        Assert.AreEqual(0, LightMarkers.Pick(new Vector2(112, 112), Size, vp, lights, 6), "球の内側（線から 6 ピクセルより遠い所）");
        Assert.AreEqual(0, LightMarkers.Pick(new Vector2(134, 100), Size, vp, lights, 6), "輪の少し外");
        Assert.AreEqual(-1, LightMarkers.Pick(new Vector2(140, 100), Size, vp, lights, 6), "輪から 10 ピクセル");
        Assert.AreEqual(-1, LightMarkers.Pick(new Vector2(100, 100), Size, vp, [], 6), "照明が無い");
    }

    [TestMethod]
    public void TheNearestOfSeveralLightsIsPicked()
    {
        Matrix4x4 vp = Front(20f); // 1 ピクセル = 0.1
        LightNode[] lights =
        [
            new LightNode("左", LightType.Point),
            new LightNode("右", LightType.Point) { Position = new Vector3(1.5f, 0, 0) },
        ];
        // 2 つの球（半径 6 ピクセル）の中心は 100 と 115。どちらにも近い所では、近いほう
        Assert.AreEqual(0, LightMarkers.Pick(new Vector2(104, 100), Size, vp, lights, 6));
        Assert.AreEqual(1, LightMarkers.Pick(new Vector2(111, 100), Size, vp, lights, 6));
        Assert.AreEqual(1, LightMarkers.Pick(new Vector2(115, 100), Size, vp, lights, 6));
    }

    [TestMethod]
    public void DirectionalLightIsPickedAlongItsArrow()
    {
        Matrix4x4 vp = Front(20f);
        var light = new LightNode("平行", LightType.Directional) { Position = new Vector3(-2, 1, 0), Rotation = new Vector3(0, 90, 0) };
        Vector3 direction = SceneLighting.DirectionOf(light.Rotation);
        Assert.AreEqual(0f, direction.Z, 1e-4f, "横向きの矢印（画面に長く写る）");
        LightNode[] lights = [light];

        Assert.AreEqual(0, LightMarkers.Pick(Screen(light.Position + direction * 2f, vp), Size, vp, lights, 6), "矢印の軸の途中");
        Assert.AreEqual(0, LightMarkers.Pick(Screen(light.Position + direction * LightMarkers.ArrowLength, vp), Size, vp, lights, 6), "矢印の先");
        Assert.AreEqual(-1, LightMarkers.Pick(Screen(light.Position + direction * 2f + new Vector3(0, 1.5f, 0), vp), Size, vp, lights, 6),
            "軸から 15 ピクセル");
        Assert.AreEqual(-1, LightMarkers.Pick(Screen(light.Position + direction * 6f, vp), Size, vp, lights, 6), "矢印の先より先");
    }

    [TestMethod]
    public void SpotLightConeLengthFollowsItsRange()
    {
        Matrix4x4 vp = Front(20f);
        var light = new LightNode("スポット", LightType.Spot) { Rotation = new Vector3(0, 90, 0), Range = 30f };
        Vector3 direction = SceneLighting.DirectionOf(light.Rotation);
        LightNode[] lights = [light];
        Vector2 coneEnd = Screen(direction * 6f, vp); // 円すいの底（長さは届く距離か 6 の短いほう）

        Assert.AreEqual(0, LightMarkers.Pick(coneEnd, Size, vp, lights, 6), "円すいの底の輪");
        light.Range = 2f;
        Assert.AreEqual(-1, LightMarkers.Pick(coneEnd, Size, vp, lights, 6), "届く距離を縮めると、円すいも短くなる");
        Assert.AreEqual(0, LightMarkers.Pick(Screen(direction * 2f, vp), Size, vp, lights, 6));
    }

    [TestMethod]
    public void LightsBehindTheCameraAreNotPicked()
    {
        var eye = new Vector3(0, 0, -10);
        Matrix4x4 vp = Matrix4x4.CreateLookAtLeftHanded(eye, Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 3f, 1f, 0.1f, 100f);
        var front = new LightNode("前", LightType.Point);
        var behind = new LightNode("後ろ", LightType.Point) { Position = new Vector3(0, 0, -20) };

        Assert.AreEqual(0, LightMarkers.Pick(new Vector2(100, 100), Size, vp, [front], 6));
        Assert.AreEqual(-1, LightMarkers.Pick(new Vector2(100, 100), Size, vp, [behind], 6), "カメラの後ろ");
        Assert.AreEqual(1, LightMarkers.Pick(new Vector2(100, 100), Size, vp, [behind, front], 6));
    }
}
