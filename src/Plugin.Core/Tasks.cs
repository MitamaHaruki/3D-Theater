namespace Plugin.Core;

/// <summary>時間のかかる処理の終わり方。</summary>
public enum PluginTaskResult
{
    /// <summary>最後まで終わった。</summary>
    Completed,

    /// <summary>利用者が取り消した。</summary>
    Cancelled,

    /// <summary>エラーで終わった（アプリが知らせて、問題の記録に残す）。</summary>
    Failed,
}

/// <summary>時間のかかる処理に渡すもの（進み具合の知らせと、取り消し）。</summary>
public interface IPluginTaskContext
{
    /// <summary>利用者が「取り消す」を押すと取り消される。処理は時々確かめて、<see cref="OperationCanceledException"/> で終わる。</summary>
    CancellationToken Cancellation { get; }

    /// <summary>
    /// 進み具合を知らせる（どのスレッドからでもよい）。<paramref name="fraction"/> は 0〜1（分からなければ null）、
    /// <paramref name="message"/> は今していること（null なら前のまま）。
    /// </summary>
    void Report(double? fraction, string? message = null);
}

/// <summary>
/// 時間のかかる処理を、進み具合と「取り消す」の窓を出して走らせる（<c>context.GetService&lt;IPluginTasks&gt;()</c>、requires に tasks）。
/// </summary>
public interface IPluginTasks
{
    /// <summary>
    /// <paramref name="work"/> を画面のスレッドから始める。すぐに終わらなければ（0.3 秒）、<paramref name="title"/> と進み具合・「取り消す」の窓を出す
    /// （その間、アプリの操作はできない）。重い計算やファイルの読み書きは、<paramref name="work"/> の中で Task.Run などに分ける。
    /// await の後は画面のスレッドに戻るので、シーンの編集はそこで行う。
    /// <paramref name="work"/> の例外はアプリが知らせて問題の記録に残し、<see cref="PluginTaskResult.Failed"/> を返す（このメソッドは例外を投げない）。
    /// 取り消されて <see cref="OperationCanceledException"/> で終わったら <see cref="PluginTaskResult.Cancelled"/>。
    /// </summary>
    Task<PluginTaskResult> RunAsync(string title, Func<IPluginTaskContext, Task> work);
}

/// <summary>
/// ファイルの形式（読み込み・書き出し）の登録（<c>context.GetService&lt;IFileFormatRegistry&gt;()</c>、requires に files）。
/// 登録した形式は、アプリのファイルのメニューに出る。読み込める形式は、ウィンドウへのドラッグでも読み込む。
/// </summary>
public interface IFileFormatRegistry
{
    /// <summary>登録する（同じ Id は登録できない）。返したものを Dispose すると、その形式だけ外す。</summary>
    IDisposable Register(PluginFileFormat format);
}

/// <summary>
/// ファイルの形式 1 つ。<see cref="Import"/> と <see cref="Export"/> の少なくとも一方が要る。
/// どちらも、アプリが進み具合の窓を出して走らせる（<see cref="IPluginTasks.RunAsync"/> と同じ扱い）。
/// </summary>
public sealed class PluginFileFormat
{
    /// <summary>プラグインの中で一意の名前（英数字・. _ -）。</summary>
    public required string Id { get; init; }

    /// <summary>メニューとファイルの窓に出す名前（例: 「キーの CSV」）。</summary>
    public required string Title { get; init; }

    /// <summary>拡張子（"." から。例: ".csv"）。ファイルの窓の絞り込みとドラッグに使う。</summary>
    public required IReadOnlyList<string> Extensions { get; init; }

    /// <summary>読み込む（ファイルの場所と、進み具合・取り消し）。</summary>
    public Func<string, IPluginTaskContext, Task>? Import { get; init; }

    /// <summary>
    /// 書き出す（書く場所と、進み具合・取り消し）。書く場所はアプリが決めた一時ファイルで、終わったらアプリが選ばれた名前に置き換える
    /// （途中で失敗・取り消したときに、元のファイルを壊さず、書きかけを残さないため）。
    /// </summary>
    public Func<string, IPluginTaskContext, Task>? Export { get; init; }

    /// <summary>今読み込めるか（無ければいつでも）。メニューを開いたときと、ドラッグのときに確かめる。</summary>
    public Func<bool>? CanImport { get; init; }

    /// <summary>今書き出せるか（無ければいつでも）。</summary>
    public Func<bool>? CanExport { get; init; }

    /// <summary>書き出すときに最初に出すファイルの名前（拡張子なし。無ければ形式の名前）。</summary>
    public Func<string?>? SuggestExportName { get; init; }
}

/// <summary>処理の終わり方と、エラーで終わったときの例外。</summary>
public readonly record struct PluginTaskOutcome(PluginTaskResult Result, Exception? Error);

/// <summary>進み具合を、アプリの決めた受け取り手に渡す（アプリの側で作る）。値は 0〜1 に収め、数でなければ「分からない」にする。</summary>
public sealed class PluginTaskContext(CancellationToken cancellation, Action<double?, string?> report) : IPluginTaskContext
{
    public CancellationToken Cancellation => cancellation;

    public void Report(double? fraction, string? message = null) =>
        report(fraction is double f && double.IsFinite(f) ? Math.Clamp(f, 0d, 1d) : null, message);
}

/// <summary>アプリの側で、プラグインの処理を走らせて終わり方を決める（画面に依存しない部分）。</summary>
public static class PluginTaskRunner
{
    /// <summary>
    /// <paramref name="work"/> を走らせ、終わり方を返す（例外は投げない）。取り消しは、取り消しが求められていて
    /// <see cref="OperationCanceledException"/> で終わったときだけ（求められていないのに出た取り消しの例外は、エラーとして扱う）。
    /// </summary>
    public static async Task<PluginTaskOutcome> RunAsync(Func<IPluginTaskContext, Task> work, IPluginTaskContext context)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            Task task = work(context) ?? throw new InvalidOperationException("処理が Task を返しませんでした");
            await task;
            return new PluginTaskOutcome(PluginTaskResult.Completed, null);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            return new PluginTaskOutcome(PluginTaskResult.Cancelled, null);
        }
        catch (Exception ex)
        {
            return new PluginTaskOutcome(PluginTaskResult.Failed, ex);
        }
    }

    /// <summary>
    /// 書き出しを、同じフォルダーの一時ファイル（<see cref="TemporaryPathFor"/>）に書かせてから、<paramref name="path"/> に置き換える
    /// （指示書 8 章「一時ファイル書き込み後の置換」）。<paramref name="write"/> が例外で終わったら一時ファイルを消して投げ直す
    /// （元のファイルはそのまま残る）。一時ファイルが作られていなければエラーにする。
    /// </summary>
    public static async Task ExportSafelyAsync(string path, Func<string, Task> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        string temporary = TemporaryPathFor(path);
        try
        {
            Task task = write(temporary) ?? throw new InvalidOperationException("処理が Task を返しませんでした");
            await task;
            if (!File.Exists(temporary))
            {
                throw new InvalidOperationException("プラグインがファイルを書き出しませんでした");
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>書き出しの一時ファイルの場所（同じフォルダー・同じ拡張子。「~名前_8 桁.拡張子」）。</summary>
    public static string TemporaryPathFor(string path)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? throw new ArgumentException("フォルダーの無い場所です", nameof(path));
        return Path.Combine(folder, $"~{Path.GetFileNameWithoutExtension(full)}_{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(full)}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても、元のファイルは無事なので続ける
        }
    }
}
