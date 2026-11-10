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

        settings.Reset();
        Assert.AreEqual(VisualEffects.None, settings.Effects, "新しいプロジェクトは映像効果なし");
        Assert.AreEqual(3, changes);
    }
}
