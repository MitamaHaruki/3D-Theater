using System.Text;
using Theater3D.Formats.Images;

namespace Theater3D.Formats.Tests;

[TestClass]
public sealed class HdrDecoderTests
{
    [TestMethod]
    public void ReadsUncompressedPixels()
    {
        // 1 行目: 1.0 の赤・0.5 の緑、2 行目: 4.0 の青・黒
        float[] pixels = [1f, 0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 4f, 0f, 0f, 0f];
        HdrImage image = HdrDecoder.Decode(Hdr(2, 2, pixels, rle: false));
        Assert.AreEqual(2, image.Width);
        Assert.AreEqual(2, image.Height);
        CollectionAssert.AreEqual(pixels, image.Rgb, "2 のべき乗の値はそのまま読める");
    }

    [TestMethod]
    public void ReadsRunLengthCompressedRows()
    {
        // 幅 10（新しい RLE を使える幅）。同じ値が続く所はくり返し、違う所はそのまま並べて圧縮する
        const int width = 10;
        float[] pixels = new float[width * 2 * 3];
        for (int x = 0; x < width; x++)
        {
            pixels[x * 3] = x < 6 ? 2f : 0.25f;          // 1 行目の赤: くり返しが 2 つ
            pixels[((width + x) * 3) + 1] = 1f + (x * 0.125f); // 2 行目の緑: 1 つずつ違う
        }
        HdrImage image = HdrDecoder.Decode(Hdr(width, 2, pixels, rle: true));
        CollectionAssert.AreEqual(pixels, image.Rgb);
    }

    [TestMethod]
    public void BottomUpRowsAreFlippedAndExposureIsUndone()
    {
        float[] pixels = [1f, 1f, 1f, 2f, 2f, 2f]; // ファイルの中では下の行から
        byte[] file = Hdr(1, 2, pixels, rle: false, orientation: "+Y 2 +X 1", extraHeader: "EXPOSURE=2.0\n");
        HdrImage image = HdrDecoder.Decode(file);
        CollectionAssert.AreEqual(new[] { 1f, 1f, 1f, 0.5f, 0.5f, 0.5f }, image.Rgb, "上下を入れ替え、明るさを 2 で割り戻す");
    }

    [TestMethod]
    public void RejectsOtherFilesAndBrokenData()
    {
        Assert.ThrowsExactly<ModelFormatException>(() => HdrDecoder.Decode(Encoding.ASCII.GetBytes("P6\n2 2\n255\n")), "HDR ではない");
        Assert.ThrowsExactly<ModelFormatException>(() => HdrDecoder.Decode(Hdr(2, 1, new float[6], rle: false, orientation: "+X 2 -Y 1")), "縦横を入れ替えた向き");
        Assert.ThrowsExactly<ModelFormatException>(
            () => HdrDecoder.Decode(Hdr(2, 1, new float[6], rle: false, extraHeader: "FORMAT=32-bit_rle_xyze\n")), "XYZE の色");
        byte[] cut = Hdr(2, 2, new float[12], rle: false);
        Assert.ThrowsExactly<ModelFormatException>(() => HdrDecoder.Decode(cut.AsSpan(0, cut.Length - 3)), "途中で終わっている");
    }

    [TestMethod]
    public void DownsamplingAveragesTwoByTwo()
    {
        var image = new HdrImage(4, 2, [1f, 0f, 0f, 3f, 0f, 0f, 0f, 2f, 0f, 0f, 2f, 0f, 1f, 0f, 0f, 3f, 0f, 0f, 0f, 6f, 0f, 0f, 2f, 0f]);
        HdrImage half = image.Downsampled(2);
        Assert.AreEqual(2, half.Width);
        Assert.AreEqual(1, half.Height);
        CollectionAssert.AreEqual(new[] { 2f, 0f, 0f, 0f, 3f, 0f }, half.Rgb);
        Assert.AreSame(image, image.Downsampled(4), "幅が収まっていれば縮めない");
    }

    // テスト用の HDR ファイル。値は RGBE（共通の指数）で書く
    private static byte[] Hdr(int width, int height, float[] rgb, bool rle, string? orientation = null, string extraHeader = "")
    {
        var output = new List<byte>();
        output.AddRange(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n{extraHeader}\n{orientation ?? $"-Y {height} +X {width}"}\n"));
        for (int y = 0; y < height; y++)
        {
            var row = new byte[width * 4];
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 3;
                Rgbe(rgb[i], rgb[i + 1], rgb[i + 2]).CopyTo(row, x * 4);
            }
            if (!rle)
            {
                output.AddRange(row);
                continue;
            }
            output.AddRange([2, 2, (byte)(width >> 8), (byte)(width & 0xFF)]);
            for (int channel = 0; channel < 4; channel++)
            {
                int x = 0;
                while (x < width)
                {
                    int run = 1;
                    while (x + run < width && run < 127 && row[((x + run) * 4) + channel] == row[(x * 4) + channel])
                    {
                        run++;
                    }
                    if (run >= 2)
                    {
                        output.Add((byte)(128 + run));
                        output.Add(row[(x * 4) + channel]);
                        x += run;
                    }
                    else
                    {
                        output.Add(1);
                        output.Add(row[(x * 4) + channel]);
                        x++;
                    }
                }
            }
        }
        return [.. output];
    }

    private static byte[] Rgbe(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        if (max < 1e-32f)
        {
            return [0, 0, 0, 0];
        }
        int exponent = (int)Math.Floor(Math.Log2(max)) + 1;
        float scale = MathF.ScaleB(1f, 8 - exponent); // 値 × 256 / 2^exponent
        return [(byte)(r * scale), (byte)(g * scale), (byte)(b * scale), (byte)(exponent + 128)];
    }
}
