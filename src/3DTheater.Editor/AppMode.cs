using System.IO;

namespace Theater3D.Editor;

/// <summary>アプリの使い方（モード）。起動するときに選び、保存するファイルの拡張子も分ける。</summary>
public enum AppMode
{
    /// <summary>エディットモード: キーフレームでモーションを付け、画像・動画に書き出す。</summary>
    Edit,

    /// <summary>ライブモード: 部屋・小物・キャラクターを置き、配信などでその場で動かす。</summary>
    Live,
}

/// <summary>モードごとの拡張子・名前と、起動したときにどのモードで始めるかの決め方。</summary>
public static class AppModes
{
    public const string EditExtension = ".3dtproj";

    public const string LiveExtension = ".3dtlive";

    /// <summary>プロジェクトファイルに書くモードの名前（ライブだけ書く。無ければエディット）。</summary>
    public const string LiveName = "live";

    /// <summary>起動の引数でモードを決める指定。</summary>
    public const string EditOption = "--edit";

    public const string LiveOption = "--live";

    public static string Extension(AppMode mode) => mode == AppMode.Live ? LiveExtension : EditExtension;

    /// <summary>画面に出すモードの名前（「エディット」「ライブ」）。</summary>
    public static string DisplayName(AppMode mode) => mode == AppMode.Live ? "ライブ" : "エディット";

    public static string Option(AppMode mode) => mode == AppMode.Live ? LiveOption : EditOption;

    /// <summary>開く・保存の画面の種類の欄。</summary>
    public static string FileFilter(AppMode mode) => mode == AppMode.Live
        ? "3D Theater のライブ (*.3dtlive)|*.3dtlive|すべてのファイル (*.*)|*.*"
        : "3D Theater のプロジェクト (*.3dtproj)|*.3dtproj|すべてのファイル (*.*)|*.*";

    /// <summary>どちらのモードのファイルも選べる、開く画面の種類の欄（起動したときのモードの選択から開くとき）。</summary>
    public const string AnyFileFilter =
        "3D Theater のファイル (*.3dtproj;*.3dtlive)|*.3dtproj;*.3dtlive|エディットのプロジェクト (*.3dtproj)|*.3dtproj|ライブ (*.3dtlive)|*.3dtlive";

    /// <summary>拡張子から、そのファイルを開くモード。プロジェクトファイルでなければ null。</summary>
    public static AppMode? ModeOfFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(EditExtension, StringComparison.OrdinalIgnoreCase))
        {
            return AppMode.Edit;
        }
        return extension.Equals(LiveExtension, StringComparison.OrdinalIgnoreCase) ? AppMode.Live : null;
    }

    /// <summary>プロジェクトファイルに書かれたモード（拡張子が分からないときに使う）。</summary>
    public static AppMode ModeOfProject(ProjectFile project) =>
        string.Equals(project.Mode, LiveName, StringComparison.OrdinalIgnoreCase) ? AppMode.Live : AppMode.Edit;

    /// <summary>
    /// 起動したときのモードを決める。プロジェクトファイルを渡されたら（ダブルクリックで開いたときなど）、その種類のモード。
    /// 無ければ「--edit」「--live」の指定。どちらも無ければ null（利用者に選んでもらう）。
    /// ファイルの種類と指定が食い違うときは、ファイルを優先し、そのことを <c>Note</c> で返す。
    /// </summary>
    public static (AppMode? Mode, string? Note) Decide(IReadOnlyList<string> files, IReadOnlyList<string> options)
    {
        AppMode? requested = null;
        foreach (string option in options)
        {
            if (option.Equals(EditOption, StringComparison.OrdinalIgnoreCase))
            {
                requested = AppMode.Edit;
            }
            else if (option.Equals(LiveOption, StringComparison.OrdinalIgnoreCase))
            {
                requested = AppMode.Live;
            }
        }
        string? project = files.FirstOrDefault(f => ModeOfFile(f) is not null);
        if (project is null)
        {
            return (requested, null);
        }
        AppMode mode = ModeOfFile(project)!.Value;
        string? note = requested is { } r && r != mode
            ? $"「{Option(r)}」の指定より、開くファイル（{Path.GetFileName(project)}）の種類を優先して、{DisplayName(mode)}モードで起動します"
            : null;
        return (mode, note);
    }
}
