using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Theater3D.App.Logging;
using Theater3D.App.Preview;

namespace Theater3D.App.Shell;

/// <summary>ウィンドウの位置と大きさ。</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);

/// <summary>
/// 利用者ごとの画面の設定（プロジェクトとは別に保存する。指示書 8 章）。
/// ドッキングの並びは AvalonDock の XML、それ以外（プレビューの分け方・ウィンドウ位置）は JSON にする。
/// </summary>
public sealed class UserSettings
{
    /// <summary>形式を変えたら上げる。合わない古い設定は読まずに初期配置で起動する。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public WindowPlacement? Window { get; set; }

    public Dictionary<string, PreviewSettings> Previews { get; set; } = [];
}

public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "3DTheater", "UserSettings");

    public static string LayoutFile => Path.Combine(Directory, "layout.xml");

    public static string SettingsFile => Path.Combine(Directory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return new UserSettings();
            }
            UserSettings? settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsFile, Encoding.UTF8), JsonOptions);
            if (settings is null || settings.Version != UserSettings.CurrentVersion)
            {
                AppLog.Warning("画面の設定の形式が古いため、初期状態で起動します");
                return new UserSettings();
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warning("画面の設定を読めなかったため、初期状態で起動します", ex);
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            SafeFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("画面の設定を保存できませんでした", ex);
        }
    }
}

/// <summary>保存の途中で止まってもファイルが壊れないよう、一時ファイルに書いてから置き換える。</summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string content) => Write(path, temp => File.WriteAllText(temp, content, new UTF8Encoding(false)));

    public static void Write(string path, Action<string> writeToTemporaryFile)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        writeToTemporaryFile(temp);
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
