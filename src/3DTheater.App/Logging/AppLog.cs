using System.Diagnostics;
using System.IO;
using System.Text;

namespace Theater3D.App.Logging;

public sealed record LogEntry(DateTime Time, string Level, string Message);

/// <summary>
/// アプリ全体のログ。%LOCALAPPDATA%\3DTheater\Logs に日付ごとのファイルで残す。
/// どのスレッドから呼んでもよい。毎フレームの出力には使わないこと。
/// </summary>
public static class AppLog
{
    private const int RecentCapacity = 500;
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);
    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> s_recent = new();
    private static StreamWriter? s_writer;

    /// <summary>1 行書かれた。どのスレッドから届くかは決まっていない。</summary>
    public static event Action<LogEntry>? Written;

    /// <summary>最近のログ（ログパネルを後から開いたときのため）。</summary>
    public static IReadOnlyList<LogEntry> RecentEntries
    {
        get
        {
            lock (Gate)
            {
                return [.. s_recent];
            }
        }
    }

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
        DateTime now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId:D2}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        var entry = new LogEntry(now, level.Trim(), exception is null ? message : $"{message}（{exception.Message}）");
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            s_recent.Enqueue(entry);
            if (s_recent.Count > RecentCapacity)
            {
                s_recent.Dequeue();
            }
            try
            {
                s_writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // ディスクがいっぱいなどで書けないときは諦める（アプリは止めない）
            }
        }
        Written?.Invoke(entry);
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
