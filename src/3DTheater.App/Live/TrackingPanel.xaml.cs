using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Media;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの「トラッキング」パネル。映像（カメラ・確かめ用の動画のファイル）を選んで顔のトラッキングを始め、
/// 映像に顔の点を重ねた絵と、頭の向き・表情の値を出す。
/// </summary>
public partial class TrackingPanel : UserControl
{
    private const string FileChoice = "動画のファイル...";
    private readonly TrackingService _tracking;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private long _shownSequence = -1;

    internal TrackingPanel(TrackingService tracking)
    {
        InitializeComponent();
        _tracking = tracking;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh(), Dispatcher);
        tracking.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);
        Loaded += (_, _) =>
        {
            if (SourceCombo.Items.Count == 0)
            {
                RefreshSources();
            }
            UpdateState();
        };
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshSources();

    private void RefreshSources()
    {
        object? selected = SourceCombo.SelectedItem;
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
            AppLog.Warning("カメラの一覧を読めませんでした", ex);
        }
        SourceCombo.Items.Add(new ComboBoxItem { Content = FileChoice });
        SourceCombo.SelectedIndex = selected is ComboBoxItem { Tag: CameraDevice old }
            ? Math.Max(0, SourceCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag is CameraDevice c && c.Id == old.Id))
            : 0;
        AppLog.Info($"カメラの一覧: {SourceCombo.Items.Count - 1} 台");
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        if (item.Tag is CameraDevice camera)
        {
            _tracking.StartCamera(camera);
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "顔を撮った動画を選ぶ（カメラの代わり）",
                Filter = "動画 (*.mp4;*.mov;*.m4v;*.wmv;*.avi;*.mkv)|*.mp4;*.mov;*.m4v;*.wmv;*.avi;*.mkv|すべてのファイル (*.*)|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }
            _tracking.StartFile(dialog.FileName);
        }
        _shownSequence = -1;
        _timer.Start();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _tracking.Stop();

    private void UpdateState()
    {
        bool running = _tracking.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        SourceCombo.IsEnabled = !running;
        StatusText.Text = running
            ? $"読んでいます: {_tracking.SourceName}"
            : _tracking.Error is { } error ? $"止まりました: {error}" : "止まっています（映像を選んで「開始」）";
        if (running)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Refresh()
    {
        TrackingSnapshot snapshot = _tracking.Latest;
        if (snapshot.Sequence == _shownSequence)
        {
            return;
        }
        _shownSequence = snapshot.Sequence;
        _tracking.ReadFrame((pixels, width, height) =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                Preview.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        });
        ValuesText.Text = Describe(snapshot);
    }

    private static string Describe(TrackingSnapshot s)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        if (s.Result is not { } r)
        {
            return string.Empty;
        }
        string rate = string.Format(c, "処理 {0:F1} ms・{1:F0} 回/秒（映像 {2} × {3}）", r.Milliseconds, s.FramesPerSecond, s.Width, s.Height);
        if (!r.Found)
        {
            return $"顔: 見つかりません\n{rate}";
        }
        return string.Join('\n',
            string.Format(c, "顔: 見つけた（顔らしさ {0:F2}{1}）", r.Presence, r.Detected ? "・探し直した" : string.Empty),
            string.Format(c, "頭の向き: 左右 {0:+0.0;-0.0;0.0}°・上下 {1:+0.0;-0.0;0.0}°・傾き {2:+0.0;-0.0;0.0}°", r.Pose.Yaw, r.Pose.Pitch, r.Pose.Roll),
            string.Format(c, "目を閉じる: 左 {0:F2}・右 {1:F2}（本人から見た左右）", r.Blendshape("eyeBlinkLeft"), r.Blendshape("eyeBlinkRight")),
            string.Format(c, "口を開ける: {0:F2}・すぼめる: {1:F2}・笑顔: 左 {2:F2} 右 {3:F2}", r.Blendshape("jawOpen"), r.Blendshape("mouthPucker"),
                r.Blendshape("mouthSmileLeft"), r.Blendshape("mouthSmileRight")),
            string.Format(c, "眉を上げる: {0:F2}・寄せる: 左 {1:F2} 右 {2:F2}", r.Blendshape("browInnerUp"), r.Blendshape("browDownLeft"), r.Blendshape("browDownRight")),
            rate);
    }
}
