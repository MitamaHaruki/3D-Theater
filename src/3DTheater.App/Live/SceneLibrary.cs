using System.IO;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードのシーンの置き場所（設計判断記録 78 章の 4 段目）。シーン 1 つは、このフォルダーの「名前.3dtlive」。
/// 初めは「ドキュメント\3D Theater\シーン」。確かめのときは、起動の指定「--scene-folder=」で変えられる。
/// </summary>
internal static class SceneLibrary
{
    private static string? _folder;

    /// <summary>シーンのフォルダー（無ければ、保存するときに作る）。</summary>
    public static string Folder
    {
        get => _folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "3D Theater", "シーン");
        set => _folder = Path.GetFullPath(value);
    }

    /// <summary>保存してあるシーンの名前（名前の順）。フォルダーが無い・読めなければ空。</summary>
    public static IReadOnlyList<string> Names()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }
            return [.. Directory.EnumerateFiles(Folder, "*" + AppModes.Extension(AppMode.Live))
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.AppLog.Warning($"シーンのフォルダーを読めませんでした: {Folder}", ex);
            return [];
        }
    }

    /// <summary>シーンのファイル。</summary>
    public static string PathFor(string name) => Path.Combine(Folder, name + AppModes.Extension(AppMode.Live));

    /// <summary>シーンのフォルダーのファイルなら、そのシーンの名前（ほかの場所のファイル・無題なら null）。</summary>
    public static string? NameOf(string? path)
    {
        if (path is null)
        {
            return null;
        }
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.Equals(directory?.TrimEnd('\\'), Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(path), AppModes.Extension(AppMode.Live), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path)
            : null;
    }
}
