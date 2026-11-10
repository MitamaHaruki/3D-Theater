namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class VisualEffectsTests
{
    [TestMethod]
    public void NoneChangesNothing()
    {
        VisualEffects none = VisualEffects.None;
        Assert.IsFalse(none.Any);
        Assert.AreEqual(0f, none.Exposure);
        Assert.AreEqual(1f, none.Contrast);
        Assert.AreEqual(1f, none.Saturation);
        Assert.AreEqual(0f, none.Temperature);
        Assert.IsTrue((none with { VignetteEnabled = true }).Any);
        Assert.AreEqual(none, none.Normalized(), "初期値は範囲の中");
    }

    [TestMethod]
    public void ValuesAreKeptInRange()
    {
        var wild = new VisualEffects
        {
            Exposure = 10f,
            Contrast = -2f,
            Saturation = float.NaN,
            Temperature = -7f,
            BloomIntensity = float.PositiveInfinity,
            BloomThreshold = 2f,
            BloomSpread = -1f,
            Vignette = 0.5f,
        };
        VisualEffects fixedUp = wild.Normalized();
        Assert.AreEqual(VisualEffects.MaxExposure, fixedUp.Exposure);
        Assert.AreEqual(0f, fixedUp.Contrast);
        Assert.AreEqual(1f, fixedUp.Saturation, "数でない値は初期値");
        Assert.AreEqual(-1f, fixedUp.Temperature);
        Assert.AreEqual(0.6f, fixedUp.BloomIntensity, "無限大も初期値");
        Assert.AreEqual(1f, fixedUp.BloomThreshold);
        Assert.AreEqual(0f, fixedUp.BloomSpread);
        Assert.AreEqual(0.5f, fixedUp.Vignette);
    }

    [TestMethod]
    public void IllustrationValuesAreKeptInRange()
    {
        IllustrationStyle none = IllustrationStyle.None;
        Assert.IsFalse(none.Any);
        Assert.AreEqual(none, none.Normalized(), "初期値は範囲の中");
        Assert.IsTrue((none with { PaperEnabled = true }).Any);

        IllustrationStyle fixedUp = new IllustrationStyle
        {
            LineWidth = 100f,
            LineStrength = float.NaN,
            LineDetail = -1f,
            LineColor = "#abc",
            PaintLevels = 1,
            ShadowColor = "#00ff00",
            ShadowTintStrength = 2f,
            PaperStrength = -0.5f,
            PaperGrain = 0f,
        }.Normalized();
        Assert.AreEqual(IllustrationStyle.MaxLineWidth, fixedUp.LineWidth);
        Assert.AreEqual(none.LineStrength, fixedUp.LineStrength, "数でない値は初期値");
        Assert.AreEqual(0f, fixedUp.LineDetail);
        Assert.AreEqual(none.LineColor, fixedUp.LineColor, "#RRGGBB でない色は初期値");
        Assert.AreEqual(IllustrationStyle.MinPaintLevels, fixedUp.PaintLevels);
        Assert.AreEqual("#00FF00", fixedUp.ShadowColor);
        Assert.AreEqual(1f, fixedUp.ShadowTintStrength);
        Assert.AreEqual(0f, fixedUp.PaperStrength);
        Assert.AreEqual(IllustrationStyle.MinPaperGrain, fixedUp.PaperGrain);
        Assert.AreEqual(new System.Numerics.Vector3(0f, 1f, 0f), IllustrationStyle.ColorOf(fixedUp.ShadowColor));
    }

    [TestMethod]
    public void DepthOfFieldFocusesOnTheTargetByDefault()
    {
        VisualEffects dof = VisualEffects.None with { DofEnabled = true };
        Assert.IsTrue(dof.Any);
        Assert.AreEqual(30f, dof.FocusDistanceFor(30f), "初期値はカメラの注視点にピントを合わせる");
        Assert.AreEqual(12f, (dof with { DofFocusOnTarget = false, DofFocusDistance = 12f }).FocusDistanceFor(30f), "距離を指定するとき");
        Assert.AreEqual(15f, VisualEffects.DofTransitionFor(30f), "ぼけが最大になるまでの距離はピントの距離の半分");
        Assert.AreEqual(1f, VisualEffects.DofTransitionFor(0.5f), "近すぎるときも 1 より短くしない");

        VisualEffects wild = (dof with { DofFocusDistance = 0f, DofFocusRange = -3f, DofBlur = 500f }).Normalized();
        Assert.AreEqual(0.5f, wild.DofFocusDistance);
        Assert.AreEqual(0f, wild.DofFocusRange);
        Assert.AreEqual(VisualEffects.MaxDofBlur, wild.DofBlur);

        var settings = new RenderSettings
        {
            Effects = dof with { DofFocusOnTarget = false, DofFocusDistance = 12.5f, DofFocusRange = 2f, DofBlur = 9f },
        };
        ProjectFile read = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) }), new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Effects, restored.Effects, "被写界深度の値も保存して戻る");
    }

    [TestMethod]
    public void FloorReflectionIsKeptInRangeAndSaved()
    {
        Assert.IsFalse(FloorReflection.Off.Enabled);
        FloorReflection wild = new FloorReflection { Enabled = true, Strength = 3f, Fade = float.NaN }.Normalized();
        Assert.AreEqual(1f, wild.Strength);
        Assert.AreEqual(FloorReflection.Off.Fade, wild.Fade, "数でない値は初期値");
        Assert.AreEqual(FloorReflection.MinFade, (FloorReflection.Off with { Fade = 0f }).Normalized().Fade);

        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.Reflection = FloorReflection.Off with { Enabled = true, Strength = 0.5f };
        settings.Reflection = FloorReflection.Off with { Enabled = true, Strength = 0.5f };
        Assert.AreEqual(1, changes, "同じ値では知らせない");

        string plain = ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(new RenderSettings()) });
        Assert.IsFalse(plain.Contains("reflection", StringComparison.OrdinalIgnoreCase), "使っていなければ書かない");
        ProjectFile read = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(new ProjectFile { Rendering = RenderingEntry.From(settings) }), new Formats.LoadReport())!;
        var restored = new RenderSettings();
        read.Rendering.ApplyTo(restored);
        Assert.AreEqual(settings.Reflection, restored.Reflection);

        restored.Reset();
        Assert.AreEqual(FloorReflection.Off, restored.Reflection, "新しいプロジェクトは映り込みなし");
    }

    [TestMethod]
    public void RenderSettingsNotifyOnlyWhenEffectsChange()
    {
        var settings = new RenderSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        Assert.AreEqual(VisualEffects.None, settings.Effects);

        settings.Effects = VisualEffects.None with { ColorEnabled = true, Saturation = 0f };
        settings.Effects = VisualEffects.None with { ColorEnabled = true, Saturation = 0f };
        Assert.AreEqual(1, changes, "同じ値（別の入れ物）では知らせない");

        settings.Effects = settings.Effects with { Exposure = 99f };
        Assert.AreEqual(VisualEffects.MaxExposure, settings.Effects.Exposure, "範囲に収めて持つ");
        Assert.AreEqual(2, changes);
        settings.Effects = settings.Effects with { Exposure = 50f };
        Assert.AreEqual(2, changes, "収めた結果が同じなら知らせない");

        settings.Illustration = IllustrationStyle.None with { LinesEnabled = true };
        Assert.AreEqual(3, changes, "イラスト風も変わったら知らせる");
        settings.Illustration = IllustrationStyle.None with { LinesEnabled = true, LineColor = "#2b2222" };
        Assert.AreEqual(3, changes, "そろえると同じ色なら知らせない");

        settings.Reset();
        Assert.AreEqual(VisualEffects.None, settings.Effects, "新しいプロジェクトは映像効果なし");
        Assert.AreEqual(IllustrationStyle.None, settings.Illustration, "イラスト風もなし");
        Assert.AreEqual(5, changes, "映像効果とイラスト風の 2 回");
    }
}
