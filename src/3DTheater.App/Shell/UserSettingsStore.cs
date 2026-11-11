using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Theater3D.App.Logging;
using Theater3D.App.Preview;

namespace Theater3D.App.Shell;

/// <summary>ウィンドウの位置と大きさ（前の版の形式。WPF の座標）。今は <see cref="UserSettings.Windows"/> を使い、読むだけ。</summary>
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

    /// <summary>前の版のメインのウィンドウの位置（読むだけ。初めて起動したときに <see cref="Windows"/> へ移す）。</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>ウィンドウごとの置き場所（メインのウィンドウと、設定などの画面。<see cref="WindowMemory"/>）。</summary>
    public Dictionary<string, SavedWindowPlacement> Windows { get; set; } = [];

    public Dictionary<string, PreviewSettings> Previews { get; set; } = [];

    /// <summary>ギズモの方式・道具・座標（全プレビューで共通の初期設定。指示書 2.6）。</summary>
    public GizmoPreferences Gizmo { get; set; } = new();

    /// <summary>前回の書き出しの設定（次に開いたときの初期値）。</summary>
    public ExportPreferences Export { get; set; } = new();

    /// <summary>描画品質（プレビューの品質）。</summary>
    public RenderingPreferences Rendering { get; set; } = new();
}

/// <summary>描画品質の設定。</summary>
public sealed class RenderingPreferences
{
    public Theater3D.Editor.PreviewQuality PreviewQuality { get; set; } = Theater3D.Editor.PreviewQuality.Standard;
}

/// <summary>前回の書き出し（PNG・動画）の設定。</summary>
public sealed class ExportPreferences
{
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public bool Smooth { get; set; } = true;

    public bool Transparent { get; set; }

    /// <summary>背景の色（#RRGGBB）。</summary>
    public string Background { get; set; } = "#FFFFFF";

    public string? StillPath { get; set; }

    public string? SequencePath { get; set; }

    public string? VideoPath { get; set; }

    public bool VideoHighQuality { get; set; }

    public bool IncludeAudio { get; set; } = true;

    /// <summary>モーションブラーを使う。</summary>
    public bool MotionBlur { get; set; }

    /// <summary>ぶれの長さ（フレームの間隔に対する割合）。</summary>
    public float MotionBlurShutter { get; set; } = 0.5f;

    /// <summary>1 フレームを分けて描く数。</summary>
    public int MotionBlurSamples { get; set; } = 8;
}

public sealed class GizmoPreferences
{
    public Theater3D.Editor.GizmoStyle Style { get; set; } = Theater3D.Editor.GizmoStyle.Mmd;

    public Theater3D.Editor.GizmoTool Tool { get; set; } = Theater3D.Editor.GizmoTool.Move;

    public bool LocalSpace { get; set; }
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
