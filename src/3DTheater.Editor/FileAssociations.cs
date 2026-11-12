namespace Theater3D.Editor;

/// <summary>
/// レジストリに書く値 1 つ。<see cref="Key"/> は「HKEY_CURRENT_USER\Software\Classes」から見た場所、
/// <see cref="Name"/> は値の名前（null なら既定の値）。
/// </summary>
public sealed record RegistryValue(string Key, string? Name, string Value);

/// <summary>
/// プロジェクトファイル（.3dtproj・.3dtlive）をダブルクリックで開けるようにする登録（ファイルの関連付け）の中身。
/// 今の利用者だけ（HKEY_CURRENT_USER\Software\Classes）に書くので、管理者の権限は要らない。
/// 開くときは「アプリ "ファイル"」で起動し、アプリが拡張子でモードを決める（<see cref="AppModes.Decide"/>）。
/// </summary>
public static class FileAssociations
{
    public const string ClassesKey = @"Software\Classes";

    /// <summary>関連付けるファイルの種類（拡張子・ProgID・種類の名前）。</summary>
    public static IReadOnlyList<(string Extension, string ProgId, string Description)> Types { get; } =
    [
        (AppModes.EditExtension, "3DTheater.Project", "3D Theater のプロジェクト（エディット）"),
        (AppModes.LiveExtension, "3DTheater.LiveProject", "3D Theater のライブ"),
    ];

    /// <summary>開くときに起動する命令（パスに空白があっても渡せるよう、どちらも引用符で囲む）。</summary>
    public static string OpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>登録する値の一覧。</summary>
    public static IReadOnlyList<RegistryValue> Entries(string exePath)
    {
        var values = new List<RegistryValue>();
        foreach (var (extension, progId, description) in Types)
        {
            values.Add(new RegistryValue(extension, null, progId));
            values.Add(new RegistryValue($@"{extension}\OpenWithProgids", progId, string.Empty));
            values.Add(new RegistryValue(progId, null, description));
            values.Add(new RegistryValue(progId, "FriendlyTypeName", description));
            values.Add(new RegistryValue($@"{progId}\shell\open\command", null, OpenCommand(exePath)));
        }
        return values;
    }
}
