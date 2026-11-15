namespace Theater3D.Editor;

/// <summary>
/// ライブモードのシーンの名前（設計判断記録 78 章の 4 段目）。シーン 1 つは、シーンのフォルダーの「名前.3dtlive」。
/// 名前はファイルの名前になるので、ファイルの名前に使えない字と、Windows が使う名前（CON など）は使えない。
/// </summary>
public static class SceneNames
{
    /// <summary>名前の長さの限り（字数）。</summary>
    public const int MaxLength = 80;

    // ファイルの名前に使えない字（どの Windows でも同じもの。Path.GetInvalidFileNameChars と同じ並び）
    private static readonly char[] Invalid = ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 入れた名前を確かめる。使えれば前後の空白を外した名前と null、使えなければ null と、何が駄目かの説明。
    /// </summary>
    public static (string? Name, string? Problem) Validate(string? text)
    {
        string name = (text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return (null, "名前を入れてください");
        }
        if (name.Length > MaxLength)
        {
            return (null, $"名前は {MaxLength} 字までにしてください");
        }
        if (name.IndexOfAny(Invalid) >= 0 || name.Any(c => c < ' '))
        {
            return (null, "次の字は使えません:  \" < > | : * ? \\ /");
        }
        if (name.EndsWith('.'))
        {
            return (null, "名前の終わりに「.」は使えません");
        }
        string stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem))
        {
            return (null, $"「{stem}」は Windows が使う名前なので、使えません");
        }
        return (name, null);
    }
}
