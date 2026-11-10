namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class RenderQualityTests
{
    [TestMethod]
    public void PresetsGoFromLightToHigh()
    {
        Assert.AreEqual(new QualityProfile(1, 1024), RenderQuality.For(PreviewQuality.Light), "軽い: なめらかにしない・影は粗め");
        Assert.AreEqual(new QualityProfile(4, 2048), RenderQuality.For(PreviewQuality.Standard), "ふつう: 4 点・今までの影");
        Assert.AreEqual(new QualityProfile(8, 4096), RenderQuality.For(PreviewQuality.High), "きれい: 8 点・細かい影");
        Assert.AreEqual(new QualityProfile(4, 4096), RenderQuality.Export, "書き出しはプレビューに関係なく 4 点・細かい影");
        Assert.AreEqual("ふつう", RenderQuality.Name(PreviewQuality.Standard));
    }

    [TestMethod]
    public void SampleCountShrinksToFitMemory()
    {
        Assert.AreEqual(8, RenderQuality.FitSampleCount(8, 1920, 1080), "フル HD の 8 点は約 127 MiB");
        Assert.AreEqual(8, RenderQuality.FitSampleCount(8, 3840, 2160), "4K の 8 点は約 506 MiB で、まだ収まる");
        Assert.AreEqual(4, RenderQuality.FitSampleCount(8, 4096, 2160), "それより大きいと 4 点");
        Assert.AreEqual(4, RenderQuality.FitSampleCount(4, 5120, 2880), "5K の 4 点は約 450 MiB");
        Assert.AreEqual(2, RenderQuality.FitSampleCount(4, 7680, 4320), "8K（4K をなめらかにして書き出すとき）は 2 点");
        Assert.AreEqual(1, RenderQuality.FitSampleCount(4, 8192, 8192), "1 点でも上限ちょうど");
        Assert.AreEqual(1, RenderQuality.FitSampleCount(1, 640, 480));
        Assert.AreEqual(1, RenderQuality.FitSampleCount(0, 640, 480), "0 以下は 1");
        Assert.AreEqual(4, RenderQuality.FitSampleCount(4, 0, 0), "大きさ 0 のときは減らさない");
    }

    [TestMethod]
    public void QualitySettingNotifiesOnlyOnChange()
    {
        var settings = new PreviewQualitySettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        Assert.AreEqual(PreviewQuality.Standard, settings.Quality, "初期値はふつう");

        settings.Quality = PreviewQuality.Standard;
        Assert.AreEqual(0, changes, "同じ値では知らせない");
        settings.Quality = PreviewQuality.High;
        Assert.AreEqual(1, changes);
        Assert.AreEqual(8, settings.Profile.SampleCount);
        settings.Quality = (PreviewQuality)99;
        Assert.AreEqual(PreviewQuality.High, settings.Quality, "知らない値は無視する（設定のファイルが壊れていたときなど）");
        Assert.AreEqual(1, changes);
    }
}
