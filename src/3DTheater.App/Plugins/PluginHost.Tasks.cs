using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Plugin.Core;

namespace Theater3D.App.Plugins;

/// <summary>
/// プラグインの時間のかかる処理（進み具合と取り消し）と、ファイルの形式（読み込み・書き出しの追加）。指示書 7.2 の Task・File（SDK 1.3）。
/// 処理は画面のスレッドから始め、すぐに終わらなければ進み具合の窓を出す（その間、アプリの操作はできない）。
/// 処理の例外は受け止めて知らせ、問題の記録に残す。書き出しは一時ファイルに書かせてから置き換える。
/// </summary>
public sealed partial class PluginHost
{
    /// <summary>この時間で終わらなければ、進み具合の窓を出す（すぐに終わる処理で窓がちらつかないように）。</summary>
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromSeconds(0.3);

    private readonly List<FileFormat> _formats = [];

    /// <summary>ファイルの形式が増えた・減った（ファイルのメニューを作り直す）。</summary>
    public event EventHandler? FormatsChanged;

    /// <summary>登録されているファイルの形式（登録順）。</summary>
    public IReadOnlyList<FileFormat> Formats => _formats;

    /// <summary>状態表示に出す（文・エラーか）。メインの窓が渡す。</summary>
    public Action<string, bool>? ShowStatus { get; set; }

    /// <summary>プラグインのファイルの形式 1 つ。</summary>
    public sealed class FileFormat
    {
        internal FileFormat(PluginEntry plugin, PluginFileFormat format, IReadOnlyList<string> extensions)
        {
            Plugin = plugin;
            Format = format;
            Extensions = extensions;
            string patterns = string.Join(";", extensions.Select(e => "*" + e));
            Filter = $"{format.Title} ({patterns})|{patterns}";
            MenuTitle = $"{format.Title}（{patterns}）...";
        }

        public PluginEntry Plugin { get; }

        public PluginFileFormat Format { get; }

        /// <summary>拡張子（小文字）。</summary>
        public IReadOnlyList<string> Extensions { get; }

        /// <summary>ファイルの窓の絞り込み。</summary>
        public string Filter { get; }

        /// <summary>メニューに出す名前。</summary>
        public string MenuTitle { get; }

        public bool CanReadFiles => Format.Import is not null;

        public bool CanWriteFiles => Format.Export is not null;

        /// <summary>このファイルの拡張子の形式か。</summary>
        public bool Accepts(string path) => Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// プラグインの時間のかかる処理を走らせる（例外は投げない）。すぐに終わらなければ進み具合の窓を出し、
    /// 終わり方を状態表示に出す（エラーなら知らせて、問題の記録に残す）。
    /// </summary>
    public async Task<PluginTaskResult> RunTaskAsync(PluginEntry plugin, string title, Func<IPluginTaskContext, Task> work)
    {
        using var cancellation = new CancellationTokenSource();
        var relay = new ProgressRelay(_owner.Dispatcher);
        Task<PluginTaskOutcome> running = PluginTaskRunner.RunAsync(work, new PluginTaskContext(cancellation.Token, relay.Report));
        if (await Task.WhenAny(running, Task.Delay(ProgressDelay)) != running)
        {
            var window = new PluginTaskWindow(title, plugin.Name, cancellation);
            if (_owner.IsVisible)
            {
                window.Owner = _owner; // 起動の途中（プラグインを始めるとき）はまだ持ち主がいない
            }
            relay.Attach(window);
            // 終わったら閉じる（終わるのが窓を出すより先でも、閉じる知らせは窓を出した後に届く）
            _ = running.ContinueWith(_ => window.Dispatcher.InvokeAsync(window.Finish), TaskScheduler.Default);
            window.ShowDialog();
        }
        PluginTaskOutcome outcome = await running;
        switch (outcome.Result)
        {
            case PluginTaskResult.Completed:
                ShowStatus?.Invoke($"「{title}」が終わりました", false);
                break;
            case PluginTaskResult.Cancelled:
                ShowStatus?.Invoke($"「{title}」を取り消しました", false);
                Logging.AppLog.Info($"プラグイン「{plugin.Name}」の「{title}」を取り消しました");
                break;
            default:
                Manager.ReportProblem(plugin, $"「{title}」でエラーが起きました", outcome.Error);
                ShowStatus?.Invoke($"「{title}」でエラーが起きました", true);
                MessageBox.Show(_owner, $"プラグイン「{plugin.Name}」の「{title}」でエラーが起きました。\n\n{outcome.Error?.Message}",
                    "プラグイン", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
        return outcome.Result;
    }

    /// <summary>今読み込めるか（例外は「できない」として記録する）。</summary>
    public bool CanImport(FileFormat format) => format.CanReadFiles && Check(format, format.Format.CanImport, "読み込めるか");

    /// <summary>今書き出せるか（例外は「できない」として記録する）。</summary>
    public bool CanExport(FileFormat format) => format.CanWriteFiles && Check(format, format.Format.CanExport, "書き出せるか");

    /// <summary>この拡張子を読み込める形式があるか（ドラッグの間に何度も呼ぶので、プラグインには尋ねない）。</summary>
    public bool HasImporterFor(string path) => _formats.Any(f => f.CanReadFiles && f.Accepts(path));

    /// <summary>このファイルを今読み込める形式（先に登録したもの）。</summary>
    public FileFormat? FindImporter(string path) => _formats.FirstOrDefault(f => f.Accepts(path) && CanImport(f));

    /// <summary>読み込む（進み具合の窓を出して）。</summary>
    public Task<PluginTaskResult> ImportAsync(FileFormat format, string path) =>
        RunTaskAsync(format.Plugin, $"{format.Format.Title}の読み込み（{Path.GetFileName(path)}）", context => format.Format.Import!(path, context));

    /// <summary>書き出す（一時ファイルに書かせてから置き換える）。</summary>
    public Task<PluginTaskResult> ExportAsync(FileFormat format, string path) =>
        RunTaskAsync(format.Plugin, $"{format.Format.Title}の書き出し（{Path.GetFileName(path)}）",
            context => PluginTaskRunner.ExportSafelyAsync(path, temporary => format.Format.Export!(temporary, context)));

    /// <summary>書き出しの最初の名前（拡張子なし。ファイル名に使えない文字は _ にする）。</summary>
    public string SuggestExportName(FileFormat format)
    {
        string? name = null;
        try
        {
            name = format.Format.SuggestExportName?.Invoke();
        }
        catch (Exception ex)
        {
            Manager.ReportProblem(format.Plugin, $"形式「{format.Format.Title}」の書き出しの名前を作るときにエラーが起きました", ex);
        }
        name = string.IsNullOrWhiteSpace(name) ? format.Format.Title : name;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private bool Check(FileFormat format, Func<bool>? check, string what)
    {
        try
        {
            return check?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            Manager.ReportProblem(format.Plugin, $"形式「{format.Format.Title}」が{what}を確かめるときにエラーが起きました", ex);
            return false;
        }
    }

    [GeneratedRegex(@"^(\.[A-Za-z0-9_\-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    private void AddFormat(FileFormat format)
    {
        _formats.Add(format);
        FormatsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveFormat(FileFormat format)
    {
        if (_formats.Remove(format))
        {
            FormatsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed partial class Scope : IPluginTasks, IFileFormatRegistry
    {
        public Task<PluginTaskResult> RunAsync(string title, Func<IPluginTaskContext, Task> work)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_host._owner.Dispatcher.CheckAccess())
            {
                throw new InvalidOperationException("時間のかかる処理は、画面のスレッドから始めてください");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(work);
            return _host.RunTaskAsync(_entry, title, work);
        }

        public IDisposable Register(PluginFileFormat format)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(format);
            ArgumentException.ThrowIfNullOrWhiteSpace(format.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(format.Title);
            if (format.Import is null && format.Export is null)
            {
                throw new ArgumentException("読み込み（Import）か書き出し（Export）の少なくとも一方が要ります", nameof(format));
            }
            string[] extensions = [.. (format.Extensions ?? []).Select(e => e?.Trim().ToLowerInvariant() ?? string.Empty).Distinct()];
            if (extensions.Length == 0 || extensions.Any(e => !ExtensionPattern().IsMatch(e)))
            {
                throw new ArgumentException("拡張子は「.csv」のように、「.」から始まる英数字にしてください", nameof(format));
            }
            if (_host._formats.Any(f => f.Plugin == _entry && f.Format.Id == format.Id))
            {
                throw new ArgumentException($"同じ Id（{format.Id}）のファイルの形式がすでにあります", nameof(format));
            }
            var entry = new FileFormat(_entry, format, extensions);
            _host.AddFormat(entry);
            return Track(new Registration(() => _host.RemoveFormat(entry)));
        }
    }
}
