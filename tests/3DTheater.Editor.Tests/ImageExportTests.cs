using System.Numerics;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class ImageExportTests
{
    [TestMethod]
    public void SequenceFilesAreNumberedByFrame()
    {
        Assert.AreEqual(Path.Combine(@"C:\out", "shot_0005.png"), ImageExport.SequencePath(@"C:\out\shot.png", 5, 120), "4 桁にそろえる");
        Assert.AreEqual(Path.Combine(@"C:\out", "shot_00005.png"), ImageExport.SequencePath(@"C:\out\shot", 5, 12345), "最後のフレームの桁数にそろえる");
        Assert.AreEqual(@"C:\out\a.png", ImageExport.WithPngExtension(@"C:\out\a.jpg"));

        var sequence = new ExportOptions { Kind = ExportKind.Sequence, FirstFrame = 8, LastFrame = 10, OutputPath = @"C:\out\shot.png" };
        CollectionAssert.AreEqual(new[] { 8, 9, 10 }, sequence.Frames.ToArray());
        Assert.AreEqual(Path.Combine(@"C:\out", "shot_0010.png"), sequence.Paths[^1]);
        var still = sequence with { Kind = ExportKind.Still };
        CollectionAssert.AreEqual(new[] { 8 }, still.Frames.ToArray(), "静止画は最初のフレームだけ");
        CollectionAssert.AreEqual(new[] { @"C:\out\shot.png" }, still.Paths.ToArray());
    }

    [TestMethod]
    public void MotionBlurSamplesAreSpreadAroundTheFrame()
    {
        var off = new ExportOptions();
        CollectionAssert.AreEqual(new[] { 10f }, off.SampleTimes(10).ToArray(), "使わなければ、そのフレームだけ");

        var blur = new ExportOptions { MotionBlurSamples = 4, Shutter = 0.5f, OutputPath = @"C:\out\shot.png" };
        Assert.IsTrue(blur.UsesMotionBlur);
        CollectionAssert.AreEqual(new[] { 9.8125f, 9.9375f, 10.0625f, 10.1875f }, blur.SampleTimes(10).ToArray(),
            "ぶれの長さの真ん中がそのフレームになるよう、前後に等しく広げる");
        Assert.IsTrue(blur.SampleTimes(0).All(t => t >= 0f), "0 より前は 0 にする");
        Assert.IsFalse((blur with { Shutter = 0f }).UsesMotionBlur, "ぶれの長さ 0 なら使わない");

        Assert.IsEmpty(blur.Validate(100));
        Assert.HasCount(1, (blur with { MotionBlurSamples = 65 }).Validate(100), "分ける数は 64 まで");
        Assert.HasCount(1, (blur with { Shutter = 1.5f }).Validate(100), "ぶれの長さは 0〜1");
    }

    [TestMethod]
    public void SettingsAreValidated()
    {
        var ok = new ExportOptions { Kind = ExportKind.Sequence, Width = 1920, Height = 1080, FirstFrame = 0, LastFrame = 30, OutputPath = @"C:\out\a.png" };
        Assert.HasCount(0, ok.Validate(endFrame: 300));
        Assert.AreEqual(3840, ok.RenderWidth, "なめらかにするときは 2 倍で描く");

        Assert.HasCount(1, (ok with { Width = 0 }).Validate(300));
        Assert.HasCount(1, (ok with { Width = 9000 }).Validate(300), "2 倍で 16384 を超える");
        Assert.HasCount(0, (ok with { Width = 9000, Smooth = false }).Validate(300));
        Assert.HasCount(1, (ok with { LastFrame = 400 }).Validate(300), "作品の終わりより後");
        Assert.HasCount(1, (ok with { FirstFrame = 40 }).Validate(300), "最後が最初より前");
        Assert.HasCount(1, (ok with { OutputPath = " " }).Validate(300));
        Assert.HasCount(0, (ok with { Kind = ExportKind.Still, LastFrame = 0, FirstFrame = 12 }).Validate(300), "静止画は最後のフレームを見ない");
    }

    [TestMethod]
    public void VideoSettingsFollowH264Rules()
    {
        var video = new ExportOptions { Kind = ExportKind.Video, Width = 1920, Height = 1080, FirstFrame = 0, LastFrame = 60, OutputPath = @"C:\out\movie.png", Transparent = true };
        Assert.HasCount(0, video.Validate(300));
        CollectionAssert.AreEqual(new[] { @"C:\out\movie.mp4" }, video.Paths.ToArray(), "拡張子は .mp4");
        Assert.HasCount(61, video.Frames);
        Assert.IsFalse(video.UsesTransparency, "動画は透明にできない");
        Assert.AreEqual(6_220_800, video.VideoBitrate(30), "フル HD・30 fps の標準は約 6 Mbps");
        Assert.AreEqual(12_441_600, (video with { HighQuality = true }).VideoBitrate(30));
        Assert.AreEqual(1_000_000, (video with { Width = 160, Height = 96 }).VideoBitrate(24), "少なすぎるときは 1 Mbps");

        Assert.HasCount(1, (video with { Width = 1919 }).Validate(300), "奇数");
        Assert.HasCount(1, (video with { Width = 4096, Height = 4096 }).Validate(300), "画素の数が多すぎる");
        Assert.HasCount(0, (video with { Width = 2160, Height = 3840 }).Validate(300), "縦長の 4K はよい");
        Assert.HasCount(1, (video with { Width = 32, Height = 32 }).Validate(300), "小さすぎる");
        Assert.HasCount(0, (video with { Width = 64, Height = 64 }).Validate(300));
        Assert.HasCount(1, (video with { LastFrame = 400 }).Validate(300));
    }

    [TestMethod]
    public void DownsamplingAveragesWithoutDarkeningTransparentEdges()
    {
        // 左上だけ不透明な赤、ほかは透明（透明度をかけ合わせた値）
        byte[] source = [0, 0, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var result = new byte[4];
        ImageExport.Downsample(source, 2, 2, 2, result);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 64, 64 }, result, "4 点の平均");

        ImageExport.Unpremultiply(result);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 64 }, result, "割り戻すと、薄い赤ではなく、透明度 25% の赤");

        Assert.ThrowsExactly<ArgumentException>(() => ImageExport.Downsample(source, 2, 2, 3, result));
    }

    [TestMethod]
    public void UnpremultiplyRestoresColors()
    {
        byte[] pixels = [50, 100, 128, 128, 10, 20, 30, 0, 1, 2, 3, 255];
        ImageExport.Unpremultiply(pixels);
        CollectionAssert.AreEqual(new byte[] { 100, 199, 255, 128, 0, 0, 0, 0, 1, 2, 3, 255 }, pixels);
    }

    [TestMethod]
    public void ColorsAreReadAndWrittenAsHex()
    {
        Assert.IsTrue(ImageExport.TryParseColor("#FF8000", out Vector3 color));
        Assert.AreEqual(new Vector3(1f, 128f / 255f, 0f), color);
        Assert.IsTrue(ImageExport.TryParseColor(" 00ff00 ", out _), "# は無くてもよい");
        Assert.IsFalse(ImageExport.TryParseColor("#12345", out _));
        Assert.IsFalse(ImageExport.TryParseColor("白", out _));
        Assert.AreEqual("#FF8000", ImageExport.FormatColor(color));
    }
}
