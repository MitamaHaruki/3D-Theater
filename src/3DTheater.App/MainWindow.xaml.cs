using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.App.Rendering;
using Theater3D.App.Viewports;
using Theater3D.Engine.Interop;

namespace Theater3D.App;

public partial class MainWindow : Window
{
    private readonly NativeEngine? _engine;
    private readonly RenderLoop _renderLoop = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly HwndViewportHost? _hwndViewport;
    private readonly D3DImageViewport? _d3dImageViewport;
    private bool _videoMemoryUnavailable;
    private string? _renderFailure;

    public MainWindow(NativeEngine? engine, string? engineError)
    {
        InitializeComponent();
        _engine = engine;

        if (engine is not null)
        {
            _hwndViewport = new HwndViewportHost(engine, _renderLoop);
            HwndHostSlot.Child = _hwndViewport;
            _d3dImageViewport = new D3DImageViewport(engine, _renderLoop);
            D3DImageSlot.Child = _d3dImageViewport;
            _renderLoop.RenderFailed += OnRenderFailed;
            _renderLoop.Animating = AnimationMenuItem.IsChecked;
        }
        else
        {
            string message = engineError ?? "描画エンジンを使えません。";
            HwndHostSlot.Child = CreateErrorText(message);
            D3DImageSlot.Child = CreateErrorText(message);
        }

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        _statusTimer.Start();
        Loaded += (_, _) =>
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            AppLog.Info($"メインウィンドウを表示しました（拡大率 {dpi.DpiScaleX * 100:F0}%）");
            UpdateStatus();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _statusTimer.Stop();
        _renderLoop.Stop();

        // 親ウィンドウが消える前に、描画先（子ウィンドウと GPU 資源）を順に片付ける
        try
        {
            _hwndViewport?.Dispose();
            _d3dImageViewport?.Dispose();
        }
        catch (NativeEngineException ex)
        {
            AppLog.Error("描画先の破棄に失敗しました", ex);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        AppLog.Info($"拡大率が変わりました: {oldDpi.DpiScaleX * 100:F0}% → {newDpi.DpiScaleX * 100:F0}%");
    }

    private static TextBlock CreateErrorText(string message) => new()
    {
        Text = message,
        Foreground = Brushes.OrangeRed,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12, 44, 12, 12),
    };

    private void UpdateStatus()
    {
        if (_engine is null || _engine.IsDisposed)
        {
            StatusText.Text = "描画エンジンなしで起動しています";
            return;
        }

        EngineInfo info = _engine.Info;
        string adapter = info.IsSoftware ? $"{info.AdapterName}（ソフトウェア描画）" : info.AdapterName;
        StatusText.Text = _renderFailure ?? $"GPU: {adapter} ・ 機能レベル {info.FeatureLevel}";

        HwndTimeText.Text = FormatFrameTime(_hwndViewport?.AverageFrameMilliseconds);
        D3DImageTimeText.Text = FormatFrameTime(_d3dImageViewport?.AverageFrameMilliseconds);
        FrameRateText.Text = _renderLoop.IsRunning ? $"描画 {_renderLoop.FramesPerSecond:F0} fps" : "描画 停止中";
        VideoMemoryText.Text = FormatVideoMemory();
    }

    private static string FormatFrameTime(double? milliseconds) =>
        milliseconds is > 0 ? $"1 枚 {milliseconds:F2} ms" : string.Empty;

    private string FormatVideoMemory()
    {
        if (_engine is null || _videoMemoryUnavailable)
        {
            return string.Empty;
        }
        try
        {
            VideoMemoryInfo memory = _engine.QueryVideoMemory();
            const double gib = 1024.0 * 1024 * 1024;
            return $"VRAM（このアプリ）{memory.Usage / gib:F2} / {memory.Budget / gib:F1} GB";
        }
        catch (NativeEngineException ex)
        {
            AppLog.Warning("GPU メモリの使用量を取得できないため、表示をやめます", ex);
            _videoMemoryUnavailable = true;
            return string.Empty;
        }
    }

    private void OnRenderFailed(object? sender, RenderFailedEventArgs e)
    {
        bool deviceLost = e.Exception is NativeEngineException { Result: NativeResult.DeviceLost };
        // GPU の取り外しなどからの自動復旧は Phase 1 以降で作る。今は止めて知らせるだけ
        _renderFailure = deviceLost
            ? "GPU が使えなくなったため描画を止めました。アプリを再起動してください"
            : $"描画に失敗したため一部の描画を止めました: {e.Exception.Message}";
        UpdateStatus();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAnimationClick(object sender, RoutedEventArgs e) => _renderLoop.Animating = AnimationMenuItem.IsChecked;

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppLog.LogDirectory, UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            AppLog.Warning("ログフォルダーを開けませんでした", ex);
            MessageBox.Show(this, $"ログフォルダーを開けませんでした。\n{AppLog.LogDirectory}", "3D Theater", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var (major, minor) = _engine is null ? (0, 0) : NativeEngine.ApiVersion;
        string gpu = _engine?.Info.AdapterName ?? "（なし）";
        MessageBox.Show(
            this,
            $"3D Theater {App.VersionText}（Phase 0 検証版）\n描画エンジン API {major}.{minor}\nGPU: {gpu}\n\nログ: {AppLog.LogDirectory}",
            "バージョン情報",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
