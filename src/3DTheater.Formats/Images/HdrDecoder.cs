using System.Globalization;
using System.Text;

namespace Theater3D.Formats.Images;

/// <summary>
/// 読み込んだ HDR 画像。画素は光の強さそのまま（直線の値。1 より明るくてよい）の RGB を float 3 つずつ、上の行から。
/// 環境の画像（全方向の景色を、横 360 度・縦 180 度に広げたもの）に使う。
/// </summary>
public sealed record HdrImage(int Width, int Height, float[] Rgb)
{
    /// <summary>横が <paramref name="maxWidth"/> 以下になるまで、縦横を半分ずつに縮めたもの（2 x 2 の平均）。</summary>
    public HdrImage Downsampled(int maxWidth)
    {
        HdrImage image = this;
        while (image.Width > maxWidth && image.Width > 1)
        {
            int w = (image.Width + 1) / 2;
            int h = Math.Max(1, (image.Height + 1) / 2);
            float[] rgb = new float[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float sum = 0f;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min((x * 2) + dx, image.Width - 1);
                                int sy = Math.Min((y * 2) + dy, image.Height - 1);
                                sum += image.Rgb[(((sy * image.Width) + sx) * 3) + c];
                            }
                        }
                        rgb[(((y * w) + x) * 3) + c] = sum * 0.25f;
                    }
                }
            }
            image = new HdrImage(w, h, rgb);
        }
        return image;
    }
}

/// <summary>
/// Radiance HDR（.hdr。RGBE）画像を読む。HDRI（環境の画像）でいちばんよく使われる形式。
/// 対応: 見出しの FORMAT=32-bit_rle_rgbe、EXPOSURE（明るさを割り戻す）、上下の向き（-Y・+Y）、
/// 行ごとの RLE 圧縮（新しい形式と古い形式）と圧縮なし。XYZE の色と、縦横を入れ替えた向きには対応しない。
/// </summary>
public static class HdrDecoder
{
    private const int MaxSize = 16384;

    public static HdrImage Decode(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        string first = ReadLine(data, ref offset);
        if (!first.StartsWith("#?", StringComparison.Ordinal))
        {
            throw new ModelFormatException("Radiance HDR の画像ではありません（先頭が #? ではありません）");
        }
        double exposure = 1.0;
        while (true)
        {
            if (offset >= data.Length)
            {
                throw new ModelFormatException("HDR の見出しが途中で終わっています");
            }
            string line = ReadLine(data, ref offset);
            if (line.Length == 0)
            {
                break; // 空の行で見出しが終わる
            }
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal) && line != "FORMAT=32-bit_rle_rgbe")
            {
                throw new ModelFormatException($"この色の形式（{line[7..]}）の HDR には対応していません");
            }
            if (line.StartsWith("EXPOSURE=", StringComparison.Ordinal)
                && double.TryParse(line[9..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double e) && e > 0)
            {
                exposure *= e; // 書いたときに掛けた明るさ（いくつあれば全部を掛ける）
            }
        }

        string[] size = ReadLine(data, ref offset).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (size.Length != 4 || size[2] != "+X" || (size[0] != "-Y" && size[0] != "+Y")
            || !int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            || !int.TryParse(size[3], NumberStyles.None, CultureInfo.InvariantCulture, out int width))
        {
            throw new ModelFormatException("この向きの HDR には対応していません（-Y 高さ +X 幅 の形だけ読めます）");
        }
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
        {
            throw new ModelFormatException($"HDR の大きさ（{width} x {height}）が範囲外です");
        }
        bool bottomUp = size[0] == "+Y";

        float scale = (float)(1.0 / exposure);
        float[] rgb = new float[width * height * 3];
        byte[] row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            ReadScanline(data, ref offset, row, width);
            int destinationRow = bottomUp ? height - 1 - y : y;
            for (int x = 0; x < width; x++)
            {
                int e = row[(x * 4) + 3];
                float f = e == 0 ? 0f : MathF.ScaleB(1f, e - (128 + 8)) * scale;
                int d = ((destinationRow * width) + x) * 3;
                rgb[d] = row[x * 4] * f;
                rgb[d + 1] = row[(x * 4) + 1] * f;
                rgb[d + 2] = row[(x * 4) + 2] * f;
            }
        }
        return new HdrImage(width, height, rgb);
    }

    // 1 行（RGBE × 幅）を読む。新しい RLE（2, 2, 幅の上位, 幅の下位 で始まり、色ごとに分けて圧縮）・圧縮なし・古い RLE
    private static void ReadScanline(ReadOnlySpan<byte> data, ref int offset, byte[] row, int width)
    {
        Require(data, offset, 4);
        if (width is >= 8 and < 32768 && data[offset] == 2 && data[offset + 1] == 2 && (data[offset + 2] & 0x80) == 0)
        {
            if (((data[offset + 2] << 8) | data[offset + 3]) != width)
            {
                throw new ModelFormatException("HDR の行の幅が合いません（ファイルが壊れている可能性があります）");
            }
            offset += 4;
            for (int channel = 0; channel < 4; channel++)
            {
                int x = 0;
                while (x < width)
                {
                    Require(data, offset, 1);
                    int count = data[offset++];
                    if (count > 128)
                    {
                        count -= 128;
                        if (x + count > width)
                        {
                            throw new ModelFormatException("HDR の圧縮が行の幅を超えています（ファイルが壊れている可能性があります）");
                        }
                        Require(data, offset, 1);
                        byte value = data[offset++];
                        for (int i = 0; i < count; i++)
                        {
                            row[((x + i) * 4) + channel] = value;
                        }
                    }
                    else
                    {
                        if (count == 0 || x + count > width)
                        {
                            throw new ModelFormatException("HDR の圧縮が壊れています");
                        }
                        Require(data, offset, count);
                        for (int i = 0; i < count; i++)
                        {
                            row[((x + i) * 4) + channel] = data[offset + i];
                        }
                        offset += count;
                    }
                    x += count;
                }
            }
            return;
        }

        // 圧縮なし（と古い RLE: 1, 1, 1, n は前の画素を n 回くり返す。続くと n を 8 ビットずつ上へずらす）
        int written = 0;
        int shift = 0;
        while (written < width)
        {
            Require(data, offset, 4);
            byte r = data[offset];
            byte g = data[offset + 1];
            byte b = data[offset + 2];
            byte e = data[offset + 3];
            offset += 4;
            if (r == 1 && g == 1 && b == 1 && written > 0)
            {
                int repeat = e << shift;
                if (written + repeat > width)
                {
                    throw new ModelFormatException("HDR の圧縮が行の幅を超えています（ファイルが壊れている可能性があります）");
                }
                for (int i = 0; i < repeat; i++)
                {
                    Array.Copy(row, (written - 1) * 4, row, written * 4, 4);
                    written++;
                }
                shift += 8;
                continue;
            }
            row[written * 4] = r;
            row[(written * 4) + 1] = g;
            row[(written * 4) + 2] = b;
            row[(written * 4) + 3] = e;
            written++;
            shift = 0;
        }
    }

    private static string ReadLine(ReadOnlySpan<byte> data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != (byte)'\n')
        {
            offset++;
            if (offset - start > 4096)
            {
                throw new ModelFormatException("HDR の見出しの行が長すぎます（HDR の画像ではない可能性があります）");
            }
        }
        string line = Encoding.ASCII.GetString(data[start..offset]).TrimEnd('\r');
        if (offset < data.Length)
        {
            offset++; // 改行を飛ばす
        }
        return line;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset + count > data.Length)
        {
            throw new ModelFormatException("HDR の画素が途中で終わっています（ファイルが壊れている可能性があります）");
        }
    }
}
