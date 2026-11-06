using System.IO;

namespace Theater3D.Formats.Pmx;

public static class PmxPaths
{
    /// <summary>PMX のテクスチャの場所（モデルのファイルからの相対パス。区切りは \ が多い）を、実際のパスにする。</summary>
    public static string? Resolve(string baseDirectory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }
        try
        {
            string normalized = relative.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(baseDirectory, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
