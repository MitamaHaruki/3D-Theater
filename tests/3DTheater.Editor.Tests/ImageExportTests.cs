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
