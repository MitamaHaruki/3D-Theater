using System.Numerics;

namespace Theater3D.Editor;

/// <summary>
/// 色の欄のパレット（設計判断記録 87 章）。よく使う色の見本と、色合い・鮮やかさ・明るさ（HSV）での細かい選び方の計算。
/// 色はどれも 0〜1 の RGB（<see cref="ImageExport.FormatColor"/> で「#RRGGBB」にする）。
/// </summary>
public static class ColorPalette
{
    /// <summary>パレットの見本 1 つ（名前は見本に重ねて出す説明）。</summary>
    public readonly record struct Swatch(string Name, string Color);

    /// <summary>1 段に並べる見本の数。</summary>
    public const int SwatchesPerRow = 8;

    /// <summary>
    /// パレットの見本（1 段 8 色。白から黒・はっきりした色・照明に使う光の色（暖かい色から冷たい色））。
    /// </summary>
    public static IReadOnlyList<Swatch> Swatches { get; } =
    [
        new("白", "#FFFFFF"),
        new("明るい灰色", "#E0E0E0"),
        new("灰色", "#BDBDBD"),
        new("中くらいの灰色", "#9E9E9E"),
        new("暗い灰色", "#757575"),
        new("もっと暗い灰色", "#505050"),
        new("ほとんど黒", "#2A2A2A"),
        new("黒", "#000000"),

        new("赤", "#FF4040"),
        new("だいだい", "#FF9A3C"),
        new("黄色", "#FFE14D"),
        new("緑", "#6BD96B"),
        new("水色", "#3FD0C9"),
        new("青", "#4A90FF"),
        new("紫", "#8E6BFF"),
        new("桃色", "#FF6BC1"),

        new("夕日の光", "#FFB46B"),
        new("電球の光", "#FFD3A1"),
        new("暖かい白", "#FFE9CF"),
        new("昼の光", "#FFF6EA"),
        new("晴れた空の光", "#F2F6FF"),
        new("青白い光", "#CFE0FF"),
        new("月明かり", "#A9C3FF"),
        new("夜の光", "#6F86D6"),
    ];

    /// <summary>RGB（各 0〜1）を、色合い（0〜360 度）・鮮やかさ（0〜1）・明るさ（0〜1）にする。灰色の色合いは 0。</summary>
    public static (float Hue, float Saturation, float Value) ToHsv(Vector3 rgb)
    {
        Vector3 c = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        float min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float delta = max - min;
        float hue = 0f;
        if (delta > 1e-6f)
        {
            if (max == c.X)
            {
                hue = 60f * (((c.Y - c.Z) / delta) % 6f);
            }
            else if (max == c.Y)
            {
                hue = 60f * (((c.Z - c.X) / delta) + 2f);
            }
            else
            {
                hue = 60f * (((c.X - c.Y) / delta) + 4f);
            }
            if (hue < 0f)
            {
                hue += 360f;
            }
        }
        return (hue, max <= 1e-6f ? 0f : delta / max, max);
    }

    /// <summary>色合い（度。範囲の外は回して収める）・鮮やかさ・明るさ（0〜1 に収める）から、RGB（各 0〜1）。</summary>
    public static Vector3 FromHsv(float hue, float saturation, float value)
    {
        float h = float.IsFinite(hue) ? ((hue % 360f) + 360f) % 360f : 0f;
        float s = float.IsFinite(saturation) ? Math.Clamp(saturation, 0f, 1f) : 0f;
        float v = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
        float chroma = v * s;
        float x = chroma * (1f - MathF.Abs(((h / 60f) % 2f) - 1f));
        float m = v - chroma;
        Vector3 rgb = (int)(h / 60f) switch
        {
            0 => new(chroma, x, 0f),
            1 => new(x, chroma, 0f),
            2 => new(0f, chroma, x),
            3 => new(0f, x, chroma),
            4 => new(x, 0f, chroma),
            _ => new(chroma, 0f, x),
        };
        return Vector3.Clamp(rgb + new Vector3(m), Vector3.Zero, Vector3.One);
    }
}
