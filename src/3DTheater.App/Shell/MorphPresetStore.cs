using System.IO;
using System.Text;
using System.Text.Json;
using Theater3D.App.Logging;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 表情のプリセット（<see cref="MorphPresetLibrary"/>）の保存場所。画面の設定と同じく利用者ごとに 1 つで、プロジェクトには入れない
/// （どのプロジェクト・どのモデルでも、同じ名前のモーフがあれば使えるように）。変わるたびに保存する。
/// </summary>
public static class MorphPresetStore
{
    public static string FilePath => Path.Combine(UserSettingsStore.Directory, "morph-presets.json");

    /// <summary>読み込んで、変わったら保存するようにしたものを返す。読めなければ空の一覧（元のファイルは残す）。</summary>
    public static MorphPresetLibrary Load()
    {
        MorphPresetLibrary library = new();
        bool canSave = true;
        try
        {
            if (File.Exists(FilePath))
            {
                library = MorphPresetLibrary.FromJson(File.ReadAllText(FilePath, Encoding.UTF8));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 壊れたファイルや新しい版のファイルを、空の一覧で上書きしない
            canSave = false;
            AppLog.Warning($"表情のプリセットを読めなかったため、このあいだは保存しません（{FilePath}）", ex);
        }
        library.Changed += (_, _) =>
        {
            if (canSave)
            {
                Save(library);
            }
        };
        return library;
    }

    private static void Save(MorphPresetLibrary library)
    {
        try
        {
            SafeFile.WriteAllText(FilePath, library.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("表情のプリセットを保存できませんでした", ex);
        }
    }
}
