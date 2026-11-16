using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

/// <summary>サンライトと時刻・色のパレット・数の欄のドラッグ（設計判断記録 87 章）。</summary>
[TestClass]
public sealed class SunAndColorTests
{
    private static (SceneDocument Scene, PlaybackService Playback, AnimationService Animation, RenderSettings Settings, UndoService Undo,
        EditService Edits) Create()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var settings = new RenderSettings();
        animation.Rendering = settings;
        var undo = new UndoService();
        return (scene, playback, animation, settings, undo, new EditService(undo, animation, playback));
    }

    [TestMethod]
    public void MainLightIsTheSun()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        Assert.AreEqual("サンライト", scene.MainLight.Name, "MMD の照明は、初めからサンライト");

        // 前の版の初めの名前のままのファイルはサンライトに、自分で付けた名前はそのまま
        var (scene2, playback, animation, _) = AnimationFixtures.Create();
        ProjectFile old = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"nodes":{"light":{"name":"ディレクショナルライト","visible":true}}}""", new LoadReport())!;
        ProjectApply.ApplyScene(scene2, playback, animation, old, new LoadReport());
        Assert.AreEqual(SceneDocument.MainLightName, scene2.MainLight.Name);
        ProjectFile renamed = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"nodes":{"light":{"name":"窓の光","visible":true}}}""", new LoadReport())!;
        ProjectApply.ApplyScene(scene2, playback, animation, renamed, new LoadReport());
        Assert.AreEqual("窓の光", scene2.MainLight.Name);
    }

    [TestMethod]
    public void TimeOfDaySetsTheSunAndTheColorsAtOnce()
    {
        var (scene, _, _, settings, undo, edits) = Create();
        LightNode sun = scene.MainLight;
        sun.Rotation = new Vector3(54.73561f, 120f, 0f); // 窓に合わせた向き
        sun.IsVisible = false;
        settings.SelfShadow = false;
        settings.Effects = settings.Effects with { BloomEnabled = true };

        TimeOfDayPresets.Apply(edits, settings, sun, TimeOfDay.Evening);
        TimeOfDayPresets.Preset evening = TimeOfDayPresets.Of(TimeOfDay.Evening);
        Assert.AreEqual(evening.Elevation, sun.Rotation.X, "太陽の高さ");
        Assert.AreEqual(120f, sun.Rotation.Y, "太陽の向き（左右）は変えない");
        Assert.AreEqual(evening.Color, ImageExport.FormatColor(sun.Color));
        Assert.AreEqual(evening.Intensity, sun.Intensity);
        Assert.IsTrue(sun.IsVisible, "太陽を出す");
        Assert.IsTrue(settings.SelfShadow, "太陽は影を落とす");
        Assert.IsTrue(settings.Effects.ColorEnabled, "夕は色合いも変える");
        Assert.AreEqual(evening.Temperature, settings.Effects.Temperature);
        Assert.AreEqual(evening.Exposure, settings.Effects.Exposure);
        Assert.IsTrue(settings.Effects.BloomEnabled, "ほかの映像効果はそのまま");
        Assert.IsLessThan(0f, TimeOfDayPresets.Of(TimeOfDay.Night).Exposure, "夜は暗い");
        Assert.IsLessThan(0f, TimeOfDayPresets.Of(TimeOfDay.Night).Temperature, "夜は青っぽい");
        Assert.IsLessThan(TimeOfDayPresets.Of(TimeOfDay.Noon).Elevation, evening.Elevation, "夕の太陽は昼より低い");

        // 元に戻すは 1 回で、太陽も色合いも戻る
        Assert.AreEqual("時刻を「夕」に", undo.UndoDescription);
        undo.Undo();
        Assert.AreEqual(54.73561f, sun.Rotation.X, 1e-4f);
        Assert.IsFalse(sun.IsVisible);
        Assert.IsFalse(settings.SelfShadow);
        Assert.IsFalse(settings.Effects.ColorEnabled);
        Assert.IsFalse(undo.CanUndo, "時刻の変更は 1 つの操作");
        undo.Redo();
        Assert.AreEqual(evening.Elevation, sun.Rotation.X);

        // 昼は MMD の初めの照明と同じで、色合いを変えない
        TimeOfDayPresets.Apply(edits, settings, sun, TimeOfDay.Noon);
        Assert.AreEqual(SceneLighting.MmdRotation.X, sun.Rotation.X, 1e-4f);
        Assert.AreEqual(Vector3.One, sun.Color);
        Assert.AreEqual(1f, sun.Intensity);
        Assert.IsFalse(settings.Effects.ColorEnabled, "昼は色調補正を使わない");
        Assert.AreEqual(0f, settings.Effects.Temperature);
    }

    [TestMethod]
    public void PaletteColorsRoundTripThroughHsv()
    {
        Assert.HasCount(3 * ColorPalette.SwatchesPerRow, ColorPalette.Swatches);
        foreach (ColorPalette.Swatch swatch in ColorPalette.Swatches)
        {
            Assert.IsTrue(ImageExport.TryParseColor(swatch.Color, out Vector3 color), swatch.Name);
            var (h, s, v) = ColorPalette.ToHsv(color);
            Assert.AreEqual(swatch.Color, ImageExport.FormatColor(ColorPalette.FromHsv(h, s, v)), $"{swatch.Name} は色合い・鮮やかさ・明るさから同じ色に戻る");
        }
        Assert.AreEqual((0f, 1f, 1f), ColorPalette.ToHsv(new Vector3(1f, 0f, 0f)));
        Assert.AreEqual(120f, ColorPalette.ToHsv(new Vector3(0f, 1f, 0f)).Hue, 1e-4f);
        Assert.AreEqual(240f, ColorPalette.ToHsv(new Vector3(0f, 0f, 1f)).Hue, 1e-4f);
        Assert.AreEqual((0f, 0f, 0.5f), ColorPalette.ToHsv(new Vector3(0.5f)), "灰色は色合い 0・鮮やかさ 0");
        Assert.AreEqual("#00FFFF", ImageExport.FormatColor(ColorPalette.FromHsv(180f, 1f, 1f)));
        Assert.AreEqual("#FF0000", ImageExport.FormatColor(ColorPalette.FromHsv(360f, 1f, 1f)), "360 度は 0 度と同じ");
        Assert.AreEqual("#FF0000", ImageExport.FormatColor(ColorPalette.FromHsv(-360f, 2f, 5f)), "範囲の外は収める");
        Assert.AreEqual("#000000", ImageExport.FormatColor(ColorPalette.FromHsv(float.NaN, float.NaN, float.NaN)));
    }

    [TestMethod]
    public void DraggingRightRaisesAndLeftLowers()
    {
        double px = NumberDrag.PixelsPerStep;
        Assert.AreEqual(1.0, NumberDrag.ValueAt(1.0, px - 0.5, 0.1), "1 刻みに足りなければ変えない");
        Assert.AreEqual(1.1, NumberDrag.ValueAt(1.0, px, 0.1), "右へ動かすと増える");
        Assert.AreEqual(0.7, NumberDrag.ValueAt(1.0, -3 * px, 0.1), "左へ動かすと減る");
        Assert.AreEqual(-2.0, NumberDrag.ValueAt(1.0, -30 * px, 0.1), "0 より下にもなる（範囲は受け取る側で収める）");
        Assert.AreEqual(1.01, NumberDrag.ValueAt(1.0, px, 0.1, fine: true), "Shift で 1/10");
        Assert.AreEqual(2.0, NumberDrag.ValueAt(1.0, px, 0.1, coarse: true), "Ctrl で 10 倍");
        Assert.AreEqual(55.0, NumberDrag.ValueAt(45.0, 10 * px, 1), "角度などの 1 刻み");
        Assert.AreEqual(0.3, NumberDrag.ValueAt(0.0, 3 * px, 0.1), "小数の誤差は丸める");
        Assert.AreEqual(5.0, NumberDrag.ValueAt(5.0, 100, 0), "刻みが 0 なら変えない");
        Assert.AreEqual(5.0, NumberDrag.ValueAt(5.0, double.NaN, 1));
    }
}
