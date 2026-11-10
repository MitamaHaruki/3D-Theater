using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class SceneLightingTests
{
    private static void AreClose(Vector3 expected, Vector3 actual, string? message = null)
    {
        if (Vector3.Distance(expected, actual) > 1e-4f)
        {
            Assert.Fail($"{message} 期待 {expected}、実際 {actual}");
        }
    }

    [TestMethod]
    public void DefaultSceneLightMatchesMmd()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var (direction, color) = SceneLighting.From(scene.FindFirst<LightNode>());

        AreClose(SceneLighting.MmdDirection, direction, "新しいシーンの照明は、MMD の初期の向き (-0.5, -1, 0.5)");
        AreClose(new Vector3(154f / 255f), color, "明るさ 1 で MMD の初期の色");
        AreClose(SceneLighting.MmdDirection, SceneLighting.DirectionOf(SceneLighting.MmdRotation));
    }

    [TestMethod]
    public void RotationIntensityAndVisibilityChangeTheLight()
    {
        var light = new LightNode("照明") { Rotation = new Vector3(90, 0, 0), Intensity = 0.5f };
        var (direction, color) = SceneLighting.From(light);
        AreClose(-Vector3.UnitY, direction, "X を 90° 回すと真下へ進む光");
        AreClose(new Vector3(0.5f * 154f / 255f), color);

        light.IsVisible = false;
        AreClose(Vector3.Zero, SceneLighting.From(light).Color, "照明を隠すと照明なし");

        var (fallbackDirection, fallbackColor) = SceneLighting.From(null);
        AreClose(SceneLighting.MmdDirection, fallbackDirection, "照明の項目が無ければ MMD の初期の照明");
        AreClose(new Vector3(154f / 255f), fallbackColor);
    }

    [TestMethod]
    public void EdgeWidthGrowsOnlyAboveFullHd()
    {
        Assert.AreEqual(1f, SceneLighting.EdgePixelScale(300), "小さなプレビューでも 1 ピクセル");
        Assert.AreEqual(1f, SceneLighting.EdgePixelScale(1080));
        Assert.AreEqual(2f, SceneLighting.EdgePixelScale(2160), "4K の書き出しでは 2 倍");
    }
}
