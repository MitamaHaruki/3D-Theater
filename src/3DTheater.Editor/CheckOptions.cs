using System.Globalization;

namespace Theater3D.Editor;

/// <summary>
/// 確かめのための起動の指定（画面を操作せずに、ほかの PC の Claude などからコマンドだけで確かめを進めるため。設計判断記録 77 章）。
/// <list type="bullet">
/// <item>「--track-camera」: 起動したら、カメラで顔のトラッキングを始める（ライブモード。初めのカメラ）。
/// 「--track-camera=名前の一部」なら、名前にそれを含むカメラ。</item>
/// <item>「--exit-after=秒」: 起動してから、その秒数で閉じる（保存を尋ねない。閉じるときの速さのまとめは書く）。</item>
/// <item>「--scene-folder=フォルダー」: ライブモードのシーンを置くフォルダーを、ドキュメントの代わりにする
/// （確かめで、利用者のシーンに触れないため。設計判断記録 78 章の 4 段目）。</item>
/// </list>
/// </summary>
public sealed record CheckOptions(bool TrackCamera, string? CameraName, TimeSpan? ExitAfter, IReadOnlyList<string> Problems, string? SceneFolder = null)
{
    public const string TrackCameraOption = "--track-camera";
    public const string ExitAfterOption = "--exit-after";
    public const string SceneFolderOption = "--scene-folder";

    /// <summary>閉じるまでの秒数の範囲（10 秒〜1 日）。</summary>
    public const int MinimumExitSeconds = 10;

    public const int MaximumExitSeconds = 24 * 60 * 60;

    public static CheckOptions None { get; } = new(false, null, null, []);

    public bool IsEmpty => !TrackCamera && ExitAfter is null && SceneFolder is null;

    /// <summary>起動の引数（「--」で始まるもの）から読む。読めない指定は <see cref="Problems"/> に入れる（起動は止めない）。</summary>
    public static CheckOptions Parse(IReadOnlyList<string> switches)
    {
        bool track = false;
        string? camera = null;
        TimeSpan? exitAfter = null;
        string? sceneFolder = null;
        var problems = new List<string>();
        foreach (string option in switches)
        {
            (string name, string? value) = Split(option);
            if (name.Equals(TrackCameraOption, StringComparison.OrdinalIgnoreCase))
            {
                track = true;
                camera = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
            }
            else if (name.Equals(ExitAfterOption, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                    && seconds is >= MinimumExitSeconds and <= MaximumExitSeconds)
                {
                    exitAfter = TimeSpan.FromSeconds(seconds);
                }
                else
                {
                    problems.Add($"「{option}」は読めません（{ExitAfterOption}={MinimumExitSeconds}〜{MaximumExitSeconds} の秒数で渡してください）");
                }
            }
            else if (name.Equals(SceneFolderOption, StringComparison.OrdinalIgnoreCase))
            {
                string folder = (value ?? string.Empty).Trim().Trim('"');
                if (folder.Length > 0 && Path.IsPathFullyQualified(folder))
                {
                    sceneFolder = folder;
                }
                else
                {
                    problems.Add($"「{option}」は読めません（{SceneFolderOption}=C:\\フォルダー のように、ドライブから書いてください）");
                }
            }
        }
        return new CheckOptions(track, camera, exitAfter, problems, sceneFolder);
    }

    private static (string Name, string? Value) Split(string option)
    {
        int equals = option.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (option, null) : (option[..equals], option[(equals + 1)..]);
    }
}
