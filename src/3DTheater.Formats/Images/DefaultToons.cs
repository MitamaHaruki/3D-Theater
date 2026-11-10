using System.Numerics;

namespace Theater3D.Formats.Images;

/// <summary>
/// 共有トゥーン（MMD 付属の toon01.bmp〜toon10.bmp）の代わりに使う、自前の影の色の段階。
/// MMD 付属の画像は再配布できないので使わない（MMD のフォルダが見つかれば、そちらの画像を使う）。
/// 形は MMD と同じく、上半分が白（光の当たる側）、下半分が影の色で、境目を少しぼかす。影の色は自前で決めたもの。
/// </summary>
public static class DefaultToons
{
    public const int Width = 8;
    public const int Height = 64;

    // 影の色（0〜1）。灰色・肌・濃い灰色・ベージュ・水色・青みの灰色・桃色・黄色み・緑み・紫み
    private static readonly Vector3[] ShadowColors =
    [
        new(0.80f, 0.80f, 0.80f),
        new(0.97f, 0.86f, 0.82f),
        new(0.66f, 0.66f, 0.66f),
        new(0.90f, 0.85f, 0.75f),
        new(0.82f, 0.87f, 0.96f),
        new(0.72f, 0.72f, 0.76f),
        new(0.94f, 0.82f, 0.82f),
        new(0.93f, 0.91f, 0.78f),
        new(0.82f, 0.92f, 0.82f),
        new(0.87f, 0.82f, 0.94f),
    ];

    /// <summary>影の色（0〜9）。</summary>
    public static Vector3 ShadowColor(int index) => ShadowColors[index];

    /// <summary>共有トゥーン <paramref name="index"/>（0 = toon01）の代わりの画像。</summary>
    public static DecodedImage Create(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ShadowColors.Length);
        Vector3 shadow = ShadowColors[index];
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            // 0.45〜0.55 の間で白から影の色へなめらかに変える
            float v = (y + 0.5f) / Height;
            float t = Math.Clamp((v - 0.45f) / 0.1f, 0f, 1f);
            t = t * t * (3f - (2f * t));
            Vector3 color = Vector3.Lerp(Vector3.One, shadow, t) * 255f;
            for (int x = 0; x < Width; x++)
            {
                int o = ((y * Width) + x) * 4;
                pixels[o] = (byte)MathF.Round(color.Z);
                pixels[o + 1] = (byte)MathF.Round(color.Y);
                pixels[o + 2] = (byte)MathF.Round(color.X);
                pixels[o + 3] = 255;
            }
        }
        return new DecodedImage(Width, Height, pixels);
    }
}
