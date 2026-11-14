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
            CurrentLogFile = Path.Combine(LogDirectory, ChooseFileName($"3DTheater_{DateTime.Now:yyyyMMdd}"));
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
        try
        {
            s_fileOwner?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 持っていない（別のスレッドから呼ばれた）ときは、プロセスが終わると放される
        }
        s_fileOwner?.Dispose();
        s_fileOwner = null;
    }

    // 書いているファイルの持ち主の印（プロセスが終わるまで持つ）
    private static Mutex? s_fileOwner;

    /// <summary>
    /// 書くファイルの名前を決める。同じ日に、もう 1 つの 3D Theater（もう一方のモードなど）が同じファイルに書いていると、
    /// 2 つが別々の位置に書いて、行が上書きされて消える（確かめで分かった）。そのときは「_2」「_3」…の付いたファイルにする。
    /// </summary>
    private static string ChooseFileName(string stem)
    {
        const int MaxFiles = 9;
        for (int n = 1; ; n++)
        {
            string name = n == 1 ? $"{stem}.log" : $"{stem}_{n}.log";
            var owner = new Mutex(false, $@"Local\3DTheater.Log.{name}");
            bool owned;
            try
            {
                owned = owner.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // 前の持ち主が落ちた
            }
            if (owned || n == MaxFiles)
            {
                if (owned)
                {
                    s_fileOwner = owner;
                }
                else
                {
                    owner.Dispose(); // 多すぎるときは、最後のファイルに一緒に書く
                }
                return name;
            }
            owner.Dispose();
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
