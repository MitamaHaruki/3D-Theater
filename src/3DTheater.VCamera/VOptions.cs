using System.Globalization;

namespace Theater3D.VCamera;

/// <summary>
/// 起動の指定（確かめでコマンドだけで動かすため。本アプリの確かめの指定と同じ書き方）。
/// <list type="bullet">
/// <item>「--camera=名前の一部」: そのカメラを使う。「--video=パス」: カメラの代わりに動画のファイルを読む。</item>
/// <item>「--send=送り先」か「--send=送り先:ポート」: 送り先（3D Theater の PC の IP）。</item>
/// <item>「--start」: 起動したら送り始める。「--hidden」: 窓を出さずに、通知領域で動く。「--exit-after=秒」: その秒数で終わる。</item>
/// </list>
/// </summary>
public sealed record VOptions(string? Camera, string? Video, string? Host, int? Port, bool Start, bool Hidden, TimeSpan? ExitAfter, IReadOnlyList<string> Problems)
{
    public static VOptions Parse(IReadOnlyList<string> args)
    {
        string? camera = null, video = null, host = null;
        int? port = null;
        bool start = false, hidden = false;
        TimeSpan? exitAfter = null;
        var problems = new List<string>();
        foreach (string arg in args)
        {
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            string name = equals < 0 ? arg : arg[..equals];
            string value = equals < 0 ? string.Empty : arg[(equals + 1)..].Trim().Trim('"');
            switch (name.ToLowerInvariant())
            {
                case "--camera":
                    camera = value.Length > 0 ? value : null;
                    break;
                case "--video":
                    video = value.Length > 0 ? value : null;
                    break;
                case "--send":
                    int colon = value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 1 and <= 65535)
                    {
                        host = value[..colon];
                        port = p;
                    }
                    else if (value.Length > 0 && colon < 0)
                    {
                        host = value;
                    }
                    else
                    {
                        problems.Add($"「{arg}」は読めません（--send=192.168.1.10 か --send=192.168.1.10:39539 の形で渡してください）");
                    }
                    break;
                case "--start":
                    start = true;
                    break;
                case "--hidden":
                    hidden = true;
                    break;
                case "--exit-after":
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 5 and <= 86400)
                    {
                        exitAfter = TimeSpan.FromSeconds(seconds);
                    }
                    else
                    {
                        problems.Add($"「{arg}」は読めません（--exit-after=5〜86400 の秒数で渡してください）");
                    }
                    break;
                default:
                    problems.Add($"「{arg}」は知らない指定です");
                    break;
            }
        }
        return new VOptions(camera, video, host, port, start, hidden, exitAfter, problems);
    }
}
