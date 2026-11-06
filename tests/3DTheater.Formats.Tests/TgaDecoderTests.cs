using Theater3D.Formats.Images;

namespace Theater3D.Formats.Tests;

[TestClass]
public sealed class TgaDecoderTests
{
    // 2 x 2 の画像。上の行が 赤・緑、下の行が 青・白
    private static readonly byte[] ExpectedTopDown =
    [
        0, 0, 255, 255, 0, 255, 0, 255,
        255, 0, 0, 255, 255, 255, 255, 255,
    ];

    [TestMethod]
    public void ReadsUncompressed24BitBottomUp()
    {
        // 下の行から書くのが TGA の既定（見出しのビット 5 が 0）
        byte[] pixels = [255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 255, 0];
        DecodedImage image = TgaDecoder.Decode(Tga(2, 24, 0x00, pixels));
        Assert.AreEqual(2, image.Width);
        CollectionAssert.AreEqual(ExpectedTopDown, image.Bgra);
    }

    [TestMethod]
    public void ReadsUncompressed32BitTopDownWithAlpha()
    {
        byte[] pixels = [0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 0];
        DecodedImage image = TgaDecoder.Decode(Tga(2, 32, 0x28, pixels)); // 上の行から・透明度 8 ビット
        Assert.AreEqual(128, image.Bgra[3], "透明度を保つ");
        Assert.AreEqual(0, image.Bgra[15]);
    }

    [TestMethod]
    public void Treats32BitWithoutAlphaBitsAsOpaque()
    {
        byte[] pixels = [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
        DecodedImage image = TgaDecoder.Decode(Tga(2, 32, 0x20, pixels)); // 透明度 0 ビットと書かれている
        Assert.IsTrue(Enumerable.Range(0, 4).All(i => image.Bgra[(i * 4) + 3] == 255));
    }

    [TestMethod]
    public void ReadsRunLengthEncoded()
    {
        // 上の行: 赤 2 つ（繰り返し）、下の行: 青・白（そのまま）
        byte[] packets =
        [
            0x81, 0, 0, 255,                 // 繰り返し 2 個
            0x01, 255, 0, 0, 255, 255, 255,  // そのまま 2 個
        ];
        DecodedImage image = TgaDecoder.Decode(Tga(10, 24, 0x20, packets));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, image.Bgra);
    }

    [TestMethod]
    public void ReadsGrayscaleAndPalette()
    {
        DecodedImage gray = TgaDecoder.Decode(Tga(3, 8, 0x20, [10, 20, 30, 40]));
        CollectionAssert.AreEqual(new byte[] { 10, 10, 10, 255 }, gray.Bgra[..4]);

        // パレット 2 色（赤・青）と、その番号
        byte[] palette = [0, 0, 255, 255, 0, 0];
        byte[] header = Header(1, 8, 0x20, colorMapType: 1, colorMapLength: 2, colorMapEntryBits: 24);
        byte[] data = [.. header, .. palette, 0, 1, 1, 0];
        DecodedImage indexed = TgaDecoder.Decode(data);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, indexed.Bgra[..8]);
    }

    [TestMethod]
    public void Reads16BitColor()
    {
        // 5-5-5: 赤 = 0x7C00
        byte[] pixel = [0x00, 0x7C];
        byte[] data = [.. Header(2, 16, 0x20, width: 1, height: 1), .. pixel];
        DecodedImage image = TgaDecoder.Decode(data);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, image.Bgra);
    }

    [TestMethod]
    public void BrokenOrUnsupportedFilesThrow()
    {
        Assert.ThrowsExactly<ModelFormatException>(() => TgaDecoder.Decode([1, 2, 3]));
        Assert.ThrowsExactly<ModelFormatException>(() => TgaDecoder.Decode(Tga(2, 24, 0x20, [1, 2, 3])), "画素が足りない");
        Assert.ThrowsExactly<ModelFormatException>(() => TgaDecoder.Decode(Tga(32, 24, 0x20, new byte[12])), "知らない種類");
        Assert.ThrowsExactly<ModelFormatException>(() => TgaDecoder.Decode(Tga(10, 24, 0x20, [0x85])), "RLE の途中で終わる");
    }

    private static byte[] Tga(int imageType, int bits, int descriptor, byte[] pixels) => [.. Header(imageType, bits, descriptor), .. pixels];

    private static byte[] Header(int imageType, int bits, int descriptor, int width = 2, int height = 2,
        int colorMapType = 0, int colorMapLength = 0, int colorMapEntryBits = 0) =>
    [
        0, (byte)colorMapType, (byte)imageType,
        0, 0, (byte)colorMapLength, (byte)(colorMapLength >> 8), (byte)colorMapEntryBits,
        0, 0, 0, 0,
        (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8),
        (byte)bits, (byte)descriptor,
    ];
}
