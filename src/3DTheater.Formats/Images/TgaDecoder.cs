namespace Theater3D.Formats.Images;

/// <summary>読み込んだ画像。画素は BGRA 8 ビット（透明度をかけ合わせていない値）、上の行から、1 行 = 幅 × 4 バイト。</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Bgra)
{
    public int Stride => Width * 4;
}

/// <summary>
/// TGA（Targa）画像を読む。PMX のテクスチャでよく使われるが、Windows 標準の画像機能（WIC）では読めないため自前で読む。
/// 対応: フルカラー（16・24・32 ビット）、グレースケール（8 ビット）、パレット（8 ビットの番号）と、それぞれの RLE 圧縮。
/// </summary>
public static class TgaDecoder
{
    private const int HeaderSize = 18;
    private const int MaxSize = 16384;

    public static DecodedImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new ModelFormatException("TGA の見出しが足りません（ファイルが壊れている可能性があります）");
        }
        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int colorMapFirst = data[3] | (data[4] << 8);
        int colorMapLength = data[5] | (data[6] << 8);
        int colorMapEntryBits = data[7];
        int width = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int pixelBits = data[16];
        int descriptor = data[17];

        bool rle = imageType is 9 or 10 or 11;
        int baseType = rle ? imageType - 8 : imageType;
        if (baseType is not (1 or 2 or 3))
        {
            throw new ModelFormatException($"この種類の TGA（種類 {imageType}）には対応していません");
        }
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
        {
            throw new ModelFormatException($"TGA の大きさ（{width} x {height}）が範囲外です");
        }

        int offset = HeaderSize + idLength;
        byte[]? palette = null;
        if (colorMapType == 1)
        {
            int entryBytes = (colorMapEntryBits + 7) / 8;
            int paletteBytes = colorMapLength * entryBytes;
            Require(data, offset, paletteBytes);
            palette = new byte[colorMapLength * 4];
            for (int i = 0; i < colorMapLength; i++)
            {
                WriteColor(data.Slice(offset + (i * entryBytes), entryBytes), colorMapEntryBits, palette.AsSpan(i * 4, 4), alphaBits: colorMapEntryBits == 32 ? 8 : 0);
            }
            offset += paletteBytes;
        }

        int bytesPerPixel = (pixelBits + 7) / 8;
        bool supported = baseType switch
        {
            1 => pixelBits == 8 && palette is not null,
            2 => pixelBits is 15 or 16 or 24 or 32,
            _ => pixelBits == 8,
        };
        if (!supported)
        {
            throw new ModelFormatException($"この形式の TGA（種類 {imageType}・{pixelBits} ビット）には対応していません");
        }

        int alphaBits = descriptor & 0x0F;
        var output = new byte[width * height * 4];
        int pixelCount = width * height;
        Span<byte> source = stackalloc byte[4];

        int written = 0;
        int position = offset;
        while (written < pixelCount)
        {
            int run = 1;
            bool repeat = false;
            if (rle)
            {
                Require(data, position, 1);
                byte packet = data[position++];
                run = (packet & 0x7F) + 1;
                repeat = (packet & 0x80) != 0;
            }
            else
            {
                run = pixelCount;
            }
            run = Math.Min(run, pixelCount - written);

            if (repeat)
            {
                Require(data, position, bytesPerPixel);
                data.Slice(position, bytesPerPixel).CopyTo(source);
                position += bytesPerPixel;
                for (int i = 0; i < run; i++)
                {
                    Convert(source[..bytesPerPixel], output.AsSpan((written + i) * 4, 4));
                }
            }
            else
            {
                Require(data, position, run * bytesPerPixel);
                for (int i = 0; i < run; i++)
                {
                    Convert(data.Slice(position + (i * bytesPerPixel), bytesPerPixel), output.AsSpan((written + i) * 4, 4));
                }
                position += run * bytesPerPixel;
            }
            written += run;
        }

        // 画素の並びの向き: ビット 5 が立っていれば上の行から、無ければ下の行から。ビット 4 は右から左
        if ((descriptor & 0x20) == 0)
        {
            FlipVertically(output, width, height);
        }
        if ((descriptor & 0x10) != 0)
        {
            FlipHorizontally(output, width, height);
        }
        return new DecodedImage(width, height, output);

        void Convert(ReadOnlySpan<byte> pixel, Span<byte> destination)
        {
            switch (baseType)
            {
                case 1:
                    int index = pixel[0] - colorMapFirst;
                    if (index >= 0 && (index * 4) + 4 <= palette!.Length)
                    {
                        palette.AsSpan(index * 4, 4).CopyTo(destination);
                    }
                    else
                    {
                        destination.Clear(); // パレットに無い番号は透明な黒
                    }
                    break;
                case 3:
                    destination[0] = destination[1] = destination[2] = pixel[0];
                    destination[3] = 255;
                    break;
                default:
                    WriteColor(pixel, pixelBits, destination, alphaBits);
                    break;
            }
        }
    }

    // TGA の 1 画素（下位バイトから B, G, R, A の順）を BGRA にする
    private static void WriteColor(ReadOnlySpan<byte> pixel, int bits, Span<byte> destination, int alphaBits)
    {
        switch (bits)
        {
            case 15 or 16:
                int value = pixel[0] | (pixel[1] << 8);
                destination[0] = Expand5(value & 0x1F);
                destination[1] = Expand5((value >> 5) & 0x1F);
                destination[2] = Expand5((value >> 10) & 0x1F);
                destination[3] = bits == 16 && alphaBits > 0 && (value & 0x8000) == 0 ? (byte)0 : (byte)255;
                break;
            case 24:
                destination[0] = pixel[0];
                destination[1] = pixel[1];
                destination[2] = pixel[2];
                destination[3] = 255;
                break;
            default:
                destination[0] = pixel[0];
                destination[1] = pixel[1];
                destination[2] = pixel[2];
                // 透明度のビット数が 0 と書かれた 32 ビット画像は、4 バイト目を使っていないものとして不透明にする
                destination[3] = alphaBits > 0 ? pixel[3] : (byte)255;
                break;
        }
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    private static void Require(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (length < 0 || offset < 0 || (long)offset + length > data.Length)
        {
            throw new ModelFormatException("TGA の画素の途中でファイルが終わりました（ファイルが壊れている可能性があります）");
        }
    }

    private static void FlipVertically(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        var temp = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Span<byte> a = pixels.AsSpan(top * stride, stride);
            Span<byte> b = pixels.AsSpan(bottom * stride, stride);
            a.CopyTo(temp);
            b.CopyTo(a);
            temp.CopyTo(b);
        }
    }

    private static void FlipHorizontally(byte[] pixels, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int left = 0, right = width - 1; left < right; left++, right--)
            {
                int a = ((y * width) + left) * 4;
                int b = ((y * width) + right) * 4;
                for (int c = 0; c < 4; c++)
                {
                    (pixels[a + c], pixels[b + c]) = (pixels[b + c], pixels[a + c]);
                }
            }
        }
    }
}
