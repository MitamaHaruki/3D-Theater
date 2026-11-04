using System.Diagnostics;
using System.IO;
using System.Text;

namespace Theater3D.App.Logging;

/// <summary>
/// アプリ全体のログ。%LOCALAPPDATA%\3DTheater\Logs に日付ごとのファイルで残す。
/// どのスレッドから呼んでもよい。毎フレームの出力には使わないこと。
/// </summary>
public static class AppLog
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);
    private static readonly object Gate = new();
    private static StreamWriter? s_writer;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3DTheater", "Logs");

    public static string? CurrentLogFile { get; private set; }

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            DeleteOldLogs();
            CurrentLogFile = Path.Combine(LogDirectory, $"3DTheater_{DateTime.Now:yyyyMMdd}.log");
            var stream = new FileStream(CurrentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            lock (Gate)
            {
                // 異常終了しても直前までの記録が残るよう、書くたびにファイルへ出す
                s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくても起動は続ける
            System.Diagnostics.Debug.WriteLine($"ログファイルを開けませんでした: {ex}");
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            s_writer?.Dispose();
            s_writer = null;
        }
    }

    public static void Debug(string message) => Write("DEBUG", message, null);

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN ", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId:D2}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                s_writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // ディスクがいっぱいなどで書けないときは諦める（アプリは止めない）
            }
        }
    }

    private static void DeleteOldLogs()
    {
        DateTime limit = DateTime.Now - RetentionPeriod;
        foreach (string file in Directory.EnumerateFiles(LogDirectory, "3DTheater_*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < limit)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"古いログを消せませんでした: {file}: {ex.Message}");
            }
        }
    }
}
