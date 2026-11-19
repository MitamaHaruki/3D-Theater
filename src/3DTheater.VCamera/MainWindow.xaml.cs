using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Theater3D.Media;

namespace Theater3D.VCamera;

/// <summary>
/// 3D Theater VCamera の窓（設計判断記録 98 章）。カメラ・送り先を選んで、送り始める・止める・正面にする。
/// 閉じても終わらず、通知領域で動き続ける（<see cref="CloseForExit"/> で本当に閉じる）。
/// </summary>
public partial class MainWindow : Window
{
    private const string FileChoice = "動画のファイル...";

    private readonly SendService _service;
    private readonly VSettings _settings;
    private readonly DispatcherTimer _timer;
    private string? _video;
    private bool _exiting;

    internal MainWindow(SendService service, VSettings settings, string? video)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _video = video;
        HostBox.Text = settings.Host;
        PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        StartOnLaunchCheck.IsChecked = settings.StartOnLaunch;
        RefreshSources();
        _service.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateState(), Dispatcher);
        _timer.Start();
        UpdateState();
    }

    /// <summary>送り始める（窓の設定のカメラ・送り先で）。</summary>
    public void StartSending()
    {
        if (!TryDestination(out IPEndPoint? destination))
        {
            return;
        }
        SaveSettings();
        if (_video is { } path)
        {
            _service.StartFile(path, destination);
        }
        else if (SourceCombo.SelectedItem is ComboBoxItem { Tag: CameraDevice camera })
        {
            _service.StartCamera(camera, destination);
        }
        else if (SourceCombo.SelectedItem is ComboBoxItem { Content: FileChoice })
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "顔を撮った動画を選ぶ（カメラの代わり）",
                Filter = "動画 (*.mp4;*.mov;*.m4v;*.wmv;*.avi;*.mkv)|*.mp4;*.mov;*.m4v;*.wmv;*.avi;*.mkv|すべてのファイル (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) == true)
            {
                _service.StartFile(dialog.FileName, destination);
            }
        }
        else
        {
            VLog.Warning("カメラが見つからないため、送り始めません");
            StatusText.Text = "カメラが見つかりません（「一覧を更新」を押すか、カメラをつないでください）";
        }
    }

    /// <summary>送っていれば止め、止めていれば送り始める（通知領域のメニュー）。</summary>
    public void ToggleSending()
    {
        if (_service.IsRunning)
        {
            _service.Stop();
        }
        else
        {
            StartSending();
        }
    }

    /// <summary>アプリを終えるときに、本当に閉じる。</summary>
    public void CloseForExit()
    {
        _exiting = true;
        _timer.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true; // 閉じても通知領域で動き続ける
            Hide();
        }
        base.OnClosing(e);
    }

    private void RefreshSources()
    {
        SourceCombo.Items.Clear();
        try
        {
            foreach (CameraDevice camera in CameraCapture.ListCameras())
            {
                SourceCombo.Items.Add(new ComboBoxItem { Content = camera.Name, Tag = camera });
            }
        }
        catch (Exception ex) when (ex is VideoException or DllNotFoundException)
        {
            VLog.Warning("カメラの一覧を読めませんでした", ex);
        }
        int cameras = SourceCombo.Items.Count;
        SourceCombo.Items.Add(new ComboBoxItem { Content = FileChoice });
        int chosen = 0;
        for (int i = 0; i < cameras; i++)
        {
            if (_settings.Camera is { } name && SourceCombo.Items[i] is ComboBoxItem { Tag: CameraDevice c } && c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                chosen = i;
                break;
            }
        }
        SourceCombo.SelectedIndex = _video is not null ? cameras : chosen;
        VLog.Info($"カメラの一覧: {(cameras == 0 ? "（なし）" : string.Join("・", SourceCombo.Items.Cast<ComboBoxItem>().Take(cameras).Select(i => i.Content)))}");
    }

    private bool TryDestination(out IPEndPoint destination)
    {
        destination = null!;
        string host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            StatusText.Text = "ポートは 1〜65535 の数で入れてください（3D Theater の初めは 39539）";
            return false;
        }
        IPAddress? address = null;
        if (!IPAddress.TryParse(host, out address))
        {
            try
            {
                address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (SocketException)
            {
                address = null;
            }
        }
        if (address is null)
        {
            StatusText.Text = $"送り先「{host}」が見つかりません（3D Theater の PC の IP を入れてください。同じ PC なら 127.0.0.1）";
            return false;
        }
        destination = new IPEndPoint(address, port);
        return true;
    }

    private void SaveSettings()
    {
        _settings.Host = HostBox.Text.Trim();
        if (int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            _settings.Port = port;
        }
        if (SourceCombo.SelectedItem is ComboBoxItem { Tag: CameraDevice camera })
        {
            _settings.Camera = camera.Name;
        }
        _settings.StartOnLaunch = StartOnLaunchCheck.IsChecked == true;
        _settings.Save();
    }

    private void UpdateState()
    {
        bool running = _service.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        SourceCombo.IsEnabled = !running;
        HostBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        CultureInfo c = CultureInfo.CurrentCulture;
        if (_service.Problem is { } problem)
        {
            StatusText.Text = problem;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5A)); // 本アプリのライブモードの赤い字と同じ色
        }
        else
        {
            StatusText.ClearValue(TextBlock.ForegroundProperty);
            var (found, ms, rate) = _service.Latest;
            StatusText.Text = running
                ? string.Format(c, "読んでいます: {0}・{1}・{2:F1} 回/秒・処理 {3:F1} ms", _service.SourceName, found ? "顔あり" : "顔が見つかりません", rate, ms)
                : "止めています";
        }
        if (running)
        {
            var (answered, trip) = _service.Reply;
            ReplyText.Text = answered && trip is { } t
                ? string.Format(c, "送り先 {0}・往復 {1:F1} ms（3D Theater が受け取っています）", _service.Destination, t)
                : $"送り先 {_service.Destination}・返事がありません（3D Theater で「ほかの PC・アプリ（VMC プロトコル）」を開始しているか、IP とファイアウォールを確かめてください。ほかのアプリへ送っているときは、返事はありません）";
        }
        else
        {
            ReplyText.Text = string.Empty;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _video = null;
        RefreshSources();
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => StartSending();

    private void OnStopClick(object sender, RoutedEventArgs e) => _service.Stop();

    private void OnCalibrateClick(object sender, RoutedEventArgs e) => _service.Recalibrate();

    private void OnStartOnLaunchClick(object sender, RoutedEventArgs e) => SaveSettings();
}
