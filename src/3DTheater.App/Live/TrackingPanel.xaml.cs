using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Media;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの「トラッキング」パネル。映像（カメラ・確かめ用の動画のファイル）を選んで顔のトラッキングを始め、
/// 映像に顔の点を重ねた絵と、頭の向き・表情の値を出す。キャラクターへの当てはめ方（モデル・正面合わせ・左右・動かす所・なめらかさ）も決める。
/// </summary>
public partial class TrackingPanel : UserControl
{
    private const string FileChoice = "動画のファイル...";

    // 映像の選択肢のうち、スマホのもの
    private enum PhoneChoice
    {
        Request, // VTube Studio・MeowFace に頼む（スマホの IP を入れる）
        Push,    // iFacialMocap・FaceMotion3D・MeowFace から送ってもらう（PC の IP をスマホに入れる）
    }
    private const string AutoTarget = "（頭のあるモデルを自動で選ぶ）";
    private readonly EditorContext _context;
    private readonly TrackingService _tracking;
    private readonly FaceRetargetSettings _settings;
    private readonly PhoneTrackingSettings _phone;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private long _shownSequence = -1;
    private bool _updating;

    internal TrackingPanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _tracking = context.Tracking!;
        _settings = context.LiveTracking;
        _phone = context.LivePhone;
        _phone.Changed += (_, _) => ShowPhoneSettings();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh(), Dispatcher);
        _tracking.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);
        _settings.Changed += (_, _) => ShowSettings();
        Loaded += (_, _) =>
        {
            if (SourceCombo.Items.Count == 0)
            {
                RefreshSources();
            }
            RefreshTargets();
            ShowSettings();
            ShowPhoneSettings();
            ShowPhonePanel();
            UpdateState();
        };
    }

    // ===== 当てはめ方 =====
    private void ShowSettings()
    {
        _updating = true;
        try
        {
            MirrorCheck.IsChecked = _settings.Mirror;
            GazeCheck.IsChecked = _settings.Gaze;
            UpperBodyCheck.IsChecked = _settings.UpperBody;
            BodySwayCheck.IsChecked = _settings.BodySway;
            SmoothingSlider.Value = _settings.Smoothing;
            RefreshTargets();
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshTargets()
    {
        bool wasUpdating = _updating;
        _updating = true;
        try
        {
            TargetCombo.Items.Clear();
            TargetCombo.Items.Add(new ComboBoxItem { Content = AutoTarget });
            foreach (ModelNode model in _context.Scene.ModelsGroup.Children.OfType<ModelNode>())
            {
                TargetCombo.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
            }
            int selected = TargetCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag is Guid id && id == _settings.TargetModelId);
            TargetCombo.SelectedIndex = Math.Max(0, selected);
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    private void OnTargetDropDownOpened(object? sender, EventArgs e) => RefreshTargets();

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || TargetCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        _settings.TargetModelId = item.Tag is Guid id ? id : null;
    }

    private void OnCalibrateClick(object sender, RoutedEventArgs e) => _context.TrackingDriver?.RequestCalibration();

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }
        _settings.Mirror = MirrorCheck.IsChecked == true;
        _settings.Gaze = GazeCheck.IsChecked == true;
        _settings.UpperBody = UpperBodyCheck.IsChecked == true;
        _settings.BodySway = BodySwayCheck.IsChecked == true;
    }

    private void OnSmoothingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating)
        {
            _settings.Smoothing = (float)e.NewValue;
        }
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
        SourceCombo.Items.Add(new ComboBoxItem { Content = "スマホ（VTube Studio・MeowFace。スマホの IP を入れる）", Tag = PhoneChoice.Request });
        SourceCombo.Items.Add(new ComboBoxItem { Content = "スマホ（iFacialMocap・FaceMotion3D・MeowFace。PC の IP をスマホに入れる）", Tag = PhoneChoice.Push });
        SourceCombo.SelectedIndex = selected is ComboBoxItem { Tag: CameraDevice old }
            ? Math.Max(0, SourceCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag is CameraDevice c && c.Id == old.Id))
            : 0;
        AppLog.Info($"カメラの一覧: {SourceCombo.Items.Count - 3} 台");
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
        else if (item.Tag is PhoneChoice choice)
        {
            IPAddress? phone = null;
            if (choice == PhoneChoice.Request && !IPAddress.TryParse(_phone.Address, out phone))
            {
                StatusText.Text = "スマホの IP（スマホのアプリに出ている、例: 192.168.1.20）を入れてください";
                PhoneAddressBox.Focus();
                return;
            }
            _tracking.StartPhone(phone);
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

    // ===== スマホ =====
    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => ShowPhonePanel();

    private void ShowPhonePanel()
    {
        PhoneChoice? choice = (SourceCombo.SelectedItem as ComboBoxItem)?.Tag as PhoneChoice?;
        PhonePanel.Visibility = choice is null ? Visibility.Collapsed : Visibility.Visible;
        if (choice is not { } c)
        {
            return;
        }
        PhoneAddressRow.Visibility = c == PhoneChoice.Request ? Visibility.Visible : Visibility.Collapsed;
        string addresses = string.Join("・", LocalAddresses());
        PhoneHelpText.Text = c == PhoneChoice.Request
            ? "iPhone の VTube Studio は、設定の「3rd Party PC Clients」を入れ、アプリに出ている IP を上に入れて「開始」します。Android の MeowFace も、"
              + $"アプリに出ている IP を入れます。この PC はポート {PhoneMessages.VTubeStudioReplyPort} で受け取ります（PC とスマホは同じ Wi-Fi に）。"
            : $"スマホのアプリの送り先に、この PC の IP（{(addresses.Length > 0 ? addresses : "見つかりません")}）を入れて「開始」します。受け取るポートは "
              + $"{PhoneMessages.IFacialMocapPort}（iFacialMocap・FaceMotion3D）と {PhoneMessages.MeowFacePort}（MeowFace）です（PC とスマホは同じ Wi-Fi に）。";
    }

    // この PC の IPv4 の住所（家の Wi-Fi・有線のもの）
    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                    && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private void OnPhoneAddressChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating)
        {
            _phone.Address = PhoneAddressBox.Text;
        }
    }

    private void OnPhoneOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }
        _phone.InvertYaw = PhoneInvertYawCheck.IsChecked == true;
        _phone.InvertPitch = PhoneInvertPitchCheck.IsChecked == true;
        _phone.InvertRoll = PhoneInvertRollCheck.IsChecked == true;
    }

    private void ShowPhoneSettings()
    {
        bool was = _updating;
        _updating = true;
        try
        {
            if (PhoneAddressBox.Text.Trim() != _phone.Address)
            {
                PhoneAddressBox.Text = _phone.Address;
            }
            PhoneInvertYawCheck.IsChecked = _phone.InvertYaw;
            PhoneInvertPitchCheck.IsChecked = _phone.InvertPitch;
            PhoneInvertRollCheck.IsChecked = _phone.InvertRoll;
        }
        finally
        {
            _updating = was;
        }
    }

    private void UpdateState()
    {
        bool running = _tracking.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        SourceCombo.IsEnabled = !running;
        StatusText.Text = running
            ? $"{(_tracking.SourceName?.StartsWith("スマホ", StringComparison.Ordinal) == true ? "受け取っています" : "読んでいます")}: {_tracking.SourceName}"
            : _tracking.Error is { } error ? $"止まりました: {error}" : "止まっています（映像を選んで「開始」）";
        ShowDriver();
        _timer.Start(); // 止めたときも、キャラクターを正面に戻しきるまで様子を出す
    }

    private void Refresh()
    {
        TrackingSnapshot snapshot = _tracking.Latest;
        ShowDriver();
        if (!_tracking.IsRunning && _context.TrackingDriver?.IsActive != true)
        {
            _timer.Stop(); // 止めた後、キャラクターを正面に戻しきったら更新をやめる
        }
        if (snapshot.Sequence == _shownSequence)
        {
            return;
        }
        _shownSequence = snapshot.Sequence;
        PreviewNote.Visibility = snapshot.Phone is null ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot.Phone is not null)
        {
            Preview.Source = null;
            _bitmap = null;
        }
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

    private void ShowDriver()
    {
        RigText.Text = _context.TrackingDriver?.Status ?? string.Empty;
        AppliedText.Text = _context.TrackingDriver?.Applied ?? string.Empty;
    }

    private static string Describe(TrackingSnapshot s)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        if (s.Phone is { } p)
        {
            string app = p.Protocol == PhoneProtocol.VTubeStudio ? "VTube Studio の形式" : "iFacialMocap の形式";
            return string.Join('\n',
                string.Format(c, "スマホ: {0}（{1}・送り手 {2}・{3:F0} 回/秒）", p.FaceFound ? "顔あり" : "顔が見つかりません", app, s.PhoneSender, s.FramesPerSecond),
                string.Format(c, "頭の向き（アプリの値）: x {0:+0.0;-0.0;0.0}°・y {1:+0.0;-0.0;0.0}°・z {2:+0.0;-0.0;0.0}°", p.Rotation.X, p.Rotation.Y, p.Rotation.Z),
                string.Format(c, "頭の位置（アプリの値）: x {0:+0.00;-0.00;0.00}・y {1:+0.00;-0.00;0.00}・z {2:+0.00;-0.00;0.00}", p.Position.X, p.Position.Y, p.Position.Z),
                string.Format(c, "目を閉じる: 左 {0:F2}・右 {1:F2}（本人から見た左右）", p.Blendshape("eyeBlinkLeft"), p.Blendshape("eyeBlinkRight")),
                string.Format(c, "口を開ける: {0:F2}・すぼめる: {1:F2}・笑顔: 左 {2:F2} 右 {3:F2}", p.Blendshape("jawOpen"), p.Blendshape("mouthPucker"),
                    p.Blendshape("mouthSmileLeft"), p.Blendshape("mouthSmileRight")),
                string.Format(c, "表情の値: {0} 個", p.Blendshapes.Count));
        }
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
