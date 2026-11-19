using System.IO;
using System.Text;

namespace Theater3D.VCamera;

/// <summary>
/// 3D Theater VCamera のログ。本アプリと同じフォルダー（%LOCALAPPDATA%\3DTheater\Logs）に、1 日 1 つのファイル（3DTheaterVCamera_日付.log）で書く
/// （確かめ用の zip の「ログを集める」が一緒に集める）。行の形も本アプリと同じ。
/// </summary>
public static class VLog
{
    private static readonly object Lock = new();

    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3DTheater", "Logs");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warning(string message, Exception? ex = null) => Write("WARN ", ex is null ? message : $"{message}\n{ex}");

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        DateTime now = DateTime.Now;
        string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId:D2}] {message}";
        lock (Lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"3DTheaterVCamera_{now:yyyyMMdd}.log"), line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ログが書けなくても止めない
            }
        }
    }
}
