using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Theater3D.App.Plugins;

/// <summary>
/// プラグインの時間のかかる処理の進み具合と「取り消す」（<see cref="PluginHost.RunTaskAsync"/>）。
/// 処理が終わるまでは閉じない（×や Esc は「取り消す」と同じ）。
/// </summary>
public partial class PluginTaskWindow : Window
{
    private readonly CancellationTokenSource _cancellation;
    private bool _finished;

    public PluginTaskWindow(string title, string pluginName, CancellationTokenSource cancellation)
    {
        InitializeComponent();
        _cancellation = cancellation;
        Title = title;
        TitleText.Text = title;
        PluginText.Text = $"プラグイン「{pluginName}」";
    }

    /// <summary>進み具合を出す（<paramref name="fraction"/> が null なら「分からない」の動き。<paramref name="message"/> が null なら前のまま）。</summary>
    public void ShowProgress(double? fraction, string? message)
    {
        if (fraction is double f)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = f;
        }
        else
        {
            Progress.IsIndeterminate = true;
        }
        if (message is not null)
        {
            MessageText.Text = message;
        }
    }

    /// <summary>処理が終わったので閉じる。</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true; // 処理が終わるまでは閉じない（取り消しを頼む）
            RequestCancel();
        }
        base.OnClosing(e);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }
        CancelButton.IsEnabled = false;
        CancelButton.Content = "取り消しています…";
        _cancellation.Cancel();
    }
}

/// <summary>
/// 進み具合の知らせを、画面のスレッドの窓へまとめて渡す（どのスレッドから何度知らせても、画面の処理は 1 回ずつ）。
/// 窓を出す前（すぐに終わるかを待つ間）の知らせは覚えておき、窓を出したときに出す。
/// </summary>
internal sealed class ProgressRelay(Dispatcher dispatcher)
{
    private readonly Lock _gate = new();
    private PluginTaskWindow? _window;
    private double? _fraction;
    private string? _message;
    private bool _pending;

    /// <summary>窓を出したときに呼ぶ（画面のスレッド）。それまでの知らせを出す。</summary>
    public void Attach(PluginTaskWindow window)
    {
        double? fraction;
        string? message;
        lock (_gate)
        {
            _window = window;
            fraction = _fraction;
            message = _message;
        }
        window.ShowProgress(fraction, message);
    }

    public void Report(double? fraction, string? message)
    {
        lock (_gate)
        {
            _fraction = fraction;
            _message = message ?? _message;
            if (_pending || _window is null)
            {
                return;
            }
            _pending = true;
        }
        _ = dispatcher.InvokeAsync(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        PluginTaskWindow? window;
        double? fraction;
        string? message;
        lock (_gate)
        {
            window = _window;
            fraction = _fraction;
            message = _message;
            _pending = false;
        }
        window?.ShowProgress(fraction, message);
    }
}
