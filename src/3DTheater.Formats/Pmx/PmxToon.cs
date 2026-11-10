using System.Globalization;

namespace Theater3D.Formats.Pmx;

/// <summary>材質のトゥーンの指し先。共有トゥーン（toon01〜10）か、モデルのテクスチャか、無しか。</summary>
public readonly record struct ToonReference(int SharedIndex, int TextureIndex)
{
    public static ToonReference None => new(-1, -1);

    public static ToonReference Shared(int index) => new(index, -1);

    public static ToonReference Texture(int index) => new(-1, index);

    public bool IsNone => SharedIndex < 0 && TextureIndex < 0;
}

/// <summary>
/// トゥーンのテクスチャの決め方（MMD と同じ）:
/// - 材質が共有トゥーンを指すときは、番号 0〜9 が toon01.bmp〜toon10.bmp（MMD 付属）
/// - モデルのテクスチャを指していても、そのファイルが無く、名前が toon01.bmp〜toon10.bmp なら共有トゥーンを使う
///   （古いモデルは、MMD 付属のトゥーンをファイル名だけで指していることがある）
/// </summary>
public static class PmxToon
{
    public const int SharedCount = 10;

    /// <summary>共有トゥーンのファイル名（0 → toon01.bmp）。</summary>
    public static string SharedFileName(int index) => $"toon{index + 1:00}.bmp";

    /// <summary>ファイル名が toon01.bmp〜toon10.bmp なら 0〜9（大文字・小文字は問わない）。そうでなければ -1。</summary>
    public static int SharedIndexOfFileName(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/'));
        if (name.Length == 10 && name.StartsWith("toon", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= SharedCount)
        {
            return number - 1;
        }
        return -1;
    }

    /// <summary>材質のトゥーンを決める。<paramref name="textureExists"/> はテクスチャの番号のファイルがあるか。</summary>
    public static ToonReference Resolve(PmxMaterial material, IReadOnlyList<string> textures, Func<int, bool> textureExists)
    {
        if (material.SharedToon)
        {
            return material.ToonIndex is >= 0 and < SharedCount ? ToonReference.Shared(material.ToonIndex) : ToonReference.None;
        }
        int index = material.ToonIndex;
        if ((uint)index >= (uint)textures.Count)
        {
            return ToonReference.None;
        }
        if (!textureExists(index) && SharedIndexOfFileName(textures[index]) is var shared and >= 0)
        {
            return ToonReference.Shared(shared);
        }
        return ToonReference.Texture(index);
    }
}
