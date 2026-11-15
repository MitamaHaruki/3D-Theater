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
/// <item>「--preview-quality=light・standard・high」（軽い・ふつう・きれい）: プレビューの品質をその回だけ変える（設定には残さない）。</item>
/// <item>「--window=maximized」か「--window=幅x高さ」（拡大率 100% のときの大きさ）: 窓をその回だけ最大化する・その大きさにする
/// （覚えている窓の置き場所は変えない）。描画の速さを、決まった大きさで測るため（設計判断記録 81 章）。</item>
/// </list>
/// </summary>
public sealed record CheckOptions(bool TrackCamera, string? CameraName, TimeSpan? ExitAfter, IReadOnlyList<string> Problems, string? SceneFolder = null,
    PreviewQuality? Quality = null, CheckWindow? Window = null)
{
    public const string TrackCameraOption = "--track-camera";
    public const string ExitAfterOption = "--exit-after";
    public const string SceneFolderOption = "--scene-folder";
    public const string PreviewQualityOption = "--preview-quality";
    public const string WindowOption = "--window";

    /// <summary>閉じるまでの秒数の範囲（10 秒〜1 日）。</summary>
    public const int MinimumExitSeconds = 10;

    public const int MaximumExitSeconds = 24 * 60 * 60;

    public static CheckOptions None { get; } = new(false, null, null, []);

    public bool IsEmpty => !TrackCamera && ExitAfter is null && SceneFolder is null && Quality is null && Window is null;

    /// <summary>起動の引数（「--」で始まるもの）から読む。読めない指定は <see cref="Problems"/> に入れる（起動は止めない）。</summary>
    public static CheckOptions Parse(IReadOnlyList<string> switches)
    {
        bool track = false;
        string? camera = null;
        TimeSpan? exitAfter = null;
        string? sceneFolder = null;
        PreviewQuality? quality = null;
        CheckWindow? window = null;
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
            else if (name.Equals(PreviewQualityOption, StringComparison.OrdinalIgnoreCase))
            {
                quality = ParseQuality(value);
                if (quality is null)
                {
                    problems.Add($"「{option}」は読めません（{PreviewQualityOption}=light・standard・high のどれかで渡してください）");
                }
            }
            else if (name.Equals(WindowOption, StringComparison.OrdinalIgnoreCase))
            {
                window = CheckWindow.Parse(value);
                if (window is null)
                {
                    problems.Add($"「{option}」は読めません（{WindowOption}=maximized か {WindowOption}=1280x800 の形で渡してください）");
                }
            }
        }
        return new CheckOptions(track, camera, exitAfter, problems, sceneFolder, quality, window);
    }

    private static PreviewQuality? ParseQuality(string? value) => (value ?? string.Empty).Trim().Trim('"').ToLowerInvariant() switch
    {
        "light" or "軽い" => PreviewQuality.Light,
        "standard" or "ふつう" => PreviewQuality.Standard,
        "high" or "きれい" => PreviewQuality.High,
        _ => null,
    };

    private static (string Name, string? Value) Split(string option)
    {
        int equals = option.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (option, null) : (option[..equals], option[(equals + 1)..]);
    }
}

/// <summary>確かめのときの窓の大きさ（最大化するか、拡大率 100% のときの幅と高さ）。</summary>
public sealed record CheckWindow(bool Maximized, int Width = 0, int Height = 0)
{
    public const int MinimumWidth = 640;
    public const int MinimumHeight = 480;
    public const int MaximumSide = 7680;

    /// <summary>「maximized」か「幅x高さ」（x は × でも）。読めなければ null。</summary>
    public static CheckWindow? Parse(string? value)
    {
        string text = (value ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
        if (text is "maximized" or "最大化")
        {
            return new CheckWindow(true);
        }
        string[] parts = text.Split(['x', '×'], 2);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            && width is >= MinimumWidth and <= MaximumSide && height is >= MinimumHeight and <= MaximumSide)
        {
            return new CheckWindow(false, width, height);
        }
        return null;
    }

    public override string ToString() => Maximized ? "最大化" : $"{Width} × {Height}";
}
