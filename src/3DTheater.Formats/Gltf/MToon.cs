using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace Theater3D.Formats.Gltf;

/// <summary>輪郭線の太さの決め方（MToon の outlineWidthMode）。</summary>
internal enum OutlineMode
{
    None,
    /// <summary>世界の中の太さ（m）。遠いほど細く見える。</summary>
    World,
    /// <summary>画面の高さに対する割合。</summary>
    Screen,
}

/// <summary>
/// VRM のアニメ調の材質（MToon）のうち、MMD の材質に移せるもの（設計判断記録 82 章の 5 段目）。色は画面の色の値（sRGB）にそろえてある。
/// </summary>
/// <param name="Shade">影の色（null なら影を付けない）。</param>
/// <param name="Shift">影の境目をずらす量（-1〜1。shadingShift）。</param>
/// <param name="Toony">影の境目のくっきりさ（0〜1。shadingToony）。</param>
/// <param name="OutlineWidth">輪郭線の太さ（World なら m、Screen なら画面の高さに対する割合）。</param>
/// <param name="OutlineTexture">輪郭線の太さを場所ごとに変える画像（glTF のテクスチャの番号。無ければ -1）と、使う色（0 = R、1 = G）。</param>
/// <param name="Matcap">matcap（MMD のスフィアの「足す」と同じ）の画像（glTF のテクスチャの番号。無ければ -1）。</param>
/// <param name="Unsupported">まだ使わないもの（リムライト・発光など。レポートに書く）。</param>
internal sealed record MToonSettings(
    Vector3? Shade,
    float Shift,
    float Toony,
    OutlineMode Outline,
    float OutlineWidth,
    Vector3 OutlineColor,
    int OutlineTexture,
    int OutlineChannel,
    int Matcap,
    IReadOnlyList<string> Unsupported);

/// <summary>MToon の設定を読み、MMD のトゥーン（影の色の画像）と輪郭線の太さにする。</summary>
internal static class MToon
{
    /// <summary>
    /// 世界の中の太さ（m）の輪郭線を、MMD の輪郭線の太さにする倍率（1 m あたりの画素）。MMD の輪郭線は画面の上で太さが決まるので、
    /// 配信でよく使う上半身を写したとき（高さ 1080 の画面に、MMD の 10 ほどの高さが写る）の太さに合わせる（1 mm で 1.25 画素ほど）。
    /// </summary>
    public const float OutlinePixelsPerMeter = 1250f;

    /// <summary>画面の高さに対する割合の輪郭線を、MMD の太さにする（輪郭線の太さ 1 は、高さ 1080 の画面で 1 画素）。</summary>
    public const float ScreenHeightPixels = 1080f;

    /// <summary>トゥーンの画像の大きさ（縦に、光の当たる側から影の側へ並べる）。</summary>
    public const int ToonWidth = 4;

    public const int ToonHeight = 64;

    /// <summary>VRM 1.0 の材質の MToon（VRMC_materials_mtoon）。無ければ null。</summary>
    public static MToonSettings? ReadVrm1(JsonElement material)
    {
        if (!GltfFile.TryGet(material, "extensions", out JsonElement extensions) || !GltfFile.TryGet(extensions, "VRMC_materials_mtoon", out JsonElement m))
        {
            return null;
        }
        var unsupported = new List<string>();
        Vector3? shade = GltfFile.Floats(m, "shadeColorFactor", 3) is { } s ? Srgb(new Vector3(s[0], s[1], s[2])) : null;
        OutlineMode mode = GltfFile.String(m, "outlineWidthMode") switch
        {
            "worldCoordinates" => OutlineMode.World,
            "screenCoordinates" => OutlineMode.Screen,
            _ => OutlineMode.None,
        };
        float[] outline = GltfFile.Floats(m, "outlineColorFactor", 3) ?? [0, 0, 0];
        int outlineTexture = GltfFile.TryGet(m, "outlineWidthMultiplyTexture", out JsonElement ot) ? GltfFile.Int(ot, "index", -1) : -1;
        float[] matcapFactor = GltfFile.Floats(m, "matcapFactor", 3) ?? [1, 1, 1];
        int matcap = GltfFile.TryGet(m, "matcapTexture", out JsonElement mt) && matcapFactor.Any(v => v > 1e-3f) ? GltfFile.Int(mt, "index", -1) : -1;
        if ((GltfFile.Floats(m, "parametricRimColorFactor", 3) ?? [0, 0, 0]).Any(v => v > 1e-3f) || GltfFile.TryGet(m, "rimMultiplyTexture", out _))
        {
            unsupported.Add("リムライト");
        }
        if ((GltfFile.Floats(material, "emissiveFactor", 3) ?? [0, 0, 0]).Any(v => v > 1e-3f))
        {
            unsupported.Add("発光");
        }
        if (GltfFile.TryGet(m, "shadingShiftTexture", out _))
        {
            unsupported.Add("影の境目をずらす画像");
        }
        if (GltfFile.TryGet(m, "uvAnimationMaskTexture", out _) || GltfFile.Float(m, "uvAnimationScrollXSpeedFactor", 0f) != 0f
            || GltfFile.Float(m, "uvAnimationScrollYSpeedFactor", 0f) != 0f || GltfFile.Float(m, "uvAnimationRotationSpeedFactor", 0f) != 0f)
        {
            unsupported.Add("テクスチャを動かす");
        }
        return new MToonSettings(shade, GltfFile.Float(m, "shadingShiftFactor", 0f), GltfFile.Float(m, "shadingToonyFactor", 0.9f), mode,
            GltfFile.Float(m, "outlineWidthFactor", 0f), Srgb(new Vector3(outline[0], outline[1], outline[2])), outlineTexture, OutlineChannel: 1, matcap,
            unsupported);
    }

    /// <summary>VRM 0.x の材質の MToon（VRM の materialProperties の shader が VRM/MToon のもの。色はそのまま画面の色の値）。</summary>
    public static MToonSettings? ReadVrm0(JsonElement properties)
    {
        if (GltfFile.String(properties, "shader") != "VRM/MToon")
        {
            return null;
        }
        JsonElement floats = GltfFile.TryGet(properties, "floatProperties", out JsonElement f) ? f : default;
        JsonElement vectors = GltfFile.TryGet(properties, "vectorProperties", out JsonElement v) ? v : default;
        JsonElement textures = GltfFile.TryGet(properties, "textureProperties", out JsonElement t) ? t : default;
        var unsupported = new List<string>();
        Vector3? shade = GltfFile.Floats(vectors, "_ShadeColor", 3) is { } s ? new Vector3(s[0], s[1], s[2]) : null;
        OutlineMode mode = (int)GltfFile.Float(floats, "_OutlineWidthMode", 0f) switch
        {
            1 => OutlineMode.World,
            2 => OutlineMode.Screen,
            _ => OutlineMode.None,
        };
        // 0.x の太さは cm（世界の中）か、画面の高さの %
        float width = GltfFile.Float(floats, "_OutlineWidth", 0f) / 100f;
        float[] outline = GltfFile.Floats(vectors, "_OutlineColor", 3) ?? [0, 0, 0];
        if ((GltfFile.Floats(vectors, "_RimColor", 3) ?? [0, 0, 0]).Any(c => c > 1e-3f) || GltfFile.Int(textures, "_RimTexture", -1) >= 0)
        {
            unsupported.Add("リムライト");
        }
        if ((GltfFile.Floats(vectors, "_EmissionColor", 3) ?? [0, 0, 0]).Any(c => c > 1e-3f))
        {
            unsupported.Add("発光");
        }
        return new MToonSettings(shade, GltfFile.Float(floats, "_ShadeShift", 0f), GltfFile.Float(floats, "_ShadeToony", 0.9f), mode, width,
            new Vector3(outline[0], outline[1], outline[2]), GltfFile.Int(textures, "_OutlineWidthTexture", -1), OutlineChannel: 0,
            GltfFile.Int(textures, "_SphereAdd", -1), unsupported);
    }

    /// <summary>MMD の輪郭線の太さ（太さ 1 は、高さ 1080 の画面で 1 画素）。</summary>
    public static float EdgeSize(MToonSettings settings) => settings.Outline switch
    {
        OutlineMode.World => Math.Max(0f, settings.OutlineWidth) * OutlinePixelsPerMeter,
        OutlineMode.Screen => Math.Max(0f, settings.OutlineWidth) * ScreenHeightPixels,
        _ => 0f,
    };

    /// <summary>
    /// 影の色の画像（MMD のトゥーン。上が光の当たる側、下が影の側）の、上から下への色。MToon の決まり（光の当たり方に境目のずれを足し、
    /// くっきりさで 0〜1 に広げ、影の色と白を混ぜる）を、MMD のトゥーンの読み方（上から 0〜1 = 光の当たり方 1〜-1）に合わせて並べる。
    /// <paramref name="ratio"/> は影の色 ÷ 材質の色（トゥーンは材質の色に掛けるので）。
    /// </summary>
    public static Vector3[] ToonColumn(Vector3 ratio, float shift, float toony)
    {
        var column = new Vector3[ToonHeight];
        float low = -1f + Math.Clamp(toony, 0f, 1f);
        float high = 1f - Math.Clamp(toony, 0f, 1f);
        for (int y = 0; y < ToonHeight; y++)
        {
            float nl = 1f - 2f * (y + 0.5f) / ToonHeight;
            float shading = nl + shift;
            float lit = high - low > 1e-4f ? Math.Clamp((shading - low) / (high - low), 0f, 1f) : (shading >= low ? 1f : 0f);
            column[y] = Vector3.Lerp(ratio, Vector3.One, lit);
        }
        return column;
    }

    /// <summary>トゥーンの画像を TGA（32 ビット・上が先）にする（ファイルの中の画像と同じく持たせ、テクスチャの読み込みで読む）。</summary>
    public static byte[] ToonTga(Vector3[] column)
    {
        var tga = new byte[18 + ToonWidth * column.Length * 4];
        tga[2] = 2; // 圧縮しない、色そのまま
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12), ToonWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14), (ushort)column.Length);
        tga[16] = 32;
        tga[17] = 0x28; // 上が先・8 ビットの不透明度
        int offset = 18;
        foreach (Vector3 c in column)
        {
            for (int x = 0; x < ToonWidth; x++)
            {
                tga[offset++] = Byte(c.Z);
                tga[offset++] = Byte(c.Y);
                tga[offset++] = Byte(c.X);
                tga[offset++] = 255;
            }
        }
        return tga;
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static Vector3 Srgb(Vector3 linear) =>
        new(GltfConverter.LinearToSrgb(linear.X), GltfConverter.LinearToSrgb(linear.Y), GltfConverter.LinearToSrgb(linear.Z));
}
