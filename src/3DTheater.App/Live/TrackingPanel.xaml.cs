using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Editor;
using Theater3D.Media;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの右上の「トラッキング情報」（設計判断記録 78 章の 5 段目）。
/// 映像（カメラ・確かめ用の動画のファイル・スマホ）を選んで顔のトラッキングを始め・止め、正面に合わせ直す。
/// カメラの映像（顔の点つき）を出すかを選べる。出すときは短い様子を、出さないときは読んだ値（頭の向き・表情など）を出す。
/// スマホを選ぶと、IP などを入れる窓（<see cref="PhoneConnectWindow"/>）を出し、「接続」で受け取り始める。
/// キャラクターへの当てはめ方は、サイドバーの「キャラクター」（<see cref="TrackingFitPanel"/>）で決める。
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

    // 映像の選択肢のうち、VMC プロトコル（ほかの PC の 3D Theater VCamera など。設計判断記録 98 章）
    private sealed record VmcChoice(int Port);

    private readonly EditorContext _context;
    private readonly TrackingService _tracking;
    private readonly PhoneTrackingSettings _phone;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private long _shownSequence = -1;
    private bool _refreshingSources;
    private bool _phoneChosen; // 一覧を開いている間に、スマホを選んだ

    internal TrackingPanel(EditorContext context)
    {
        InitializeComponent();
        VideoCheck.IsChecked = true; // 初めは映像を出す（部品がそろってから入れる）
        _context = context;
        _tracking = context.Tracking!;
        _phone = context.LivePhone;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh(), Dispatcher);
        _tracking.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);
        Loaded += (_, _) =>
        {
            if (SourceCombo.Items.Count == 0)
            {
                RefreshSources();
            }
            UpdateState();
        };
    }

    /// <summary>カメラの映像を出すか（利用者の設定に覚える）。</summary>
    public bool ShowVideo
    {
        get => VideoCheck.IsChecked == true;
        set => VideoCheck.IsChecked = value;
    }

    private void OnCalibrateClick(object sender, RoutedEventArgs e) => _context.TrackingDriver?.RequestCalibration();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshSources();

    private void RefreshSources()
    {
        object? selected = SourceCombo.SelectedItem;
        _refreshingSources = true;
        try
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
                AppLog.Warning("カメラの一覧を読めませんでした", ex);
            }
            SourceCombo.Items.Add(new ComboBoxItem { Content = FileChoice });
            SourceCombo.Items.Add(new ComboBoxItem { Content = "スマホ（VTube Studio・MeowFace。スマホの IP を入れる）", Tag = PhoneChoice.Request });
            SourceCombo.Items.Add(new ComboBoxItem { Content = "スマホ（iFacialMocap・FaceMotion3D・MeowFace。PC の IP をスマホに入れる）", Tag = PhoneChoice.Push });
            SourceCombo.Items.Add(new ComboBoxItem
            {
                Content = "ほかの PC・アプリ（VMC プロトコル。3D Theater VCamera など）",
                Tag = new VmcChoice(Theater3D.Tracking.Vmc.VmcProtocol.MarionettePort),
            });
            SourceCombo.SelectedIndex = selected is ComboBoxItem { Tag: CameraDevice old }
                ? Math.Max(0, SourceCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag is CameraDevice c && c.Id == old.Id))
                : 0;
        }
        finally
        {
            _refreshingSources = false;
        }
        AppLog.Info($"カメラの一覧: {SourceCombo.Items.Count - 4} 台");
    }

    // スマホを選んだら、一覧を閉じたときに IP などを入れる窓を出す（利用者と決めた）。キャンセルしたら選んだまま、「開始」でまた出す。
    // 一覧を閉じたまま矢印のキーで選び直したときは出さない（通りすぎるたびに出ないように。「開始」で出す）
    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) =>
        _phoneChosen = !_refreshingSources && SourceCombo.IsDropDownOpen && SourceCombo.SelectedItem is ComboBoxItem { Tag: PhoneChoice };

    private void OnSourceDropDownClosed(object? sender, EventArgs e)
    {
        if (_phoneChosen && SourceCombo.SelectedItem is ComboBoxItem { Tag: PhoneChoice choice } && !_tracking.IsRunning)
        {
            _phoneChosen = false;
            Dispatcher.BeginInvoke(() => StartPhone(choice), DispatcherPriority.Background); // 一覧が閉じきってから
        }
        _phoneChosen = false;
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
            StartPhone(choice);
            return;
        }
        else if (item.Tag is VmcChoice vmc)
        {
            _tracking.StartVmc(vmc.Port); // 送り手（3D Theater VCamera）に、この PC の IP を入れてもらう（状態の行に出す）
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

    // スマホの窓で IP などを確かめてから、受け取り始める
    private void StartPhone(PhoneChoice choice)
    {
        if (_tracking.IsRunning)
        {
            return;
        }
        var window = new PhoneConnectWindow(_phone, choice == PhoneChoice.Request) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true)
        {
            return;
        }
        _tracking.StartPhone(window.Phone);
        _shownSequence = -1;
        _timer.Start();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _tracking.Stop();

    private void OnVideoCheckChanged(object sender, RoutedEventArgs e)
    {
        bool show = ShowVideo;
        VideoBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ValuesText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show)
        {
            Preview.Source = null; // 出さない間は映像を写さない（少し軽くなる）
            _bitmap = null;
        }
        _shownSequence = -1; // 次の回に出し直す
    }

    private void UpdateState()
    {
        bool running = _tracking.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        SourceCombo.IsEnabled = !running;
        UpdateStatusLine();
        _timer.Start(); // 止めたときも、キャラクターを正面に戻しきるまで様子を見る
    }

    // 状態の行。映像が届かない・開き直している・エラーで止まったときは、赤い字で折り返して出す（設計判断記録 97 章）
    private void UpdateStatusLine()
    {
        string? problem = _tracking.Problem;
        string text = problem
            ?? (_tracking.IsRunning
                ? RunningText(_tracking.SourceName)
                : "止まっています（映像を選んで「開始」）");
        if (StatusText.Text == text)
        {
            return;
        }
        StatusText.Text = text;
        StatusText.ToolTip = text;
        if (problem is null)
        {
            StatusText.ClearValue(TextBlock.ForegroundProperty);
            // VMC プロトコルは、送り手に入れてもらう IP が切れないよう折り返す
            StatusText.TextWrapping = _tracking.IsRunning && IsVmc(_tracking.SourceName) ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
        else
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "LiveErrorBrush");
            StatusText.TextWrapping = TextWrapping.Wrap;
        }
    }

    private void Refresh()
    {
        UpdateStatusLine(); // 映像が届かない秒数・開き直すまでの秒数を出し直す
        TrackingSnapshot snapshot = _tracking.Latest;
        if (!_tracking.IsRunning)
        {
            if (_context.TrackingDriver?.IsActive != true)
            {
                _timer.Stop(); // 止めた後、キャラクターを正面に戻しきったら更新をやめる
            }
            // 止めたら、最後の映像と様子を消す（動いているように見えないように）
            Preview.Source = null;
            _bitmap = null;
            PreviewNote.Visibility = Visibility.Collapsed;
            SummaryText.Text = string.Empty;
            ValuesText.Text = string.Empty;
            _shownSequence = -1;
            return;
        }
        if (snapshot.Sequence == _shownSequence)
        {
            return;
        }
        _shownSequence = snapshot.Sequence;
        bool show = ShowVideo;
        PreviewNote.Visibility = show && snapshot.Phone is not null ? Visibility.Visible : Visibility.Collapsed;
        PreviewNote.Text = snapshot.Phone is { Protocol: PhoneProtocol.Vmc } vmc
            ? $"{(vmc.FromFaceTracker ? "3D Theater VCamera" : "VMC プロトコルのアプリ")}から受け取っています（映像はありません）"
            : "スマホから受け取っています（映像はありません）";
        if (show && snapshot.Phone is null)
        {
            _tracking.ReadFrame((pixels, width, height) =>
            {
                if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                    Preview.Source = _bitmap;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            });
        }
        else if (snapshot.Phone is not null)
        {
            Preview.Source = null;
            _bitmap = null;
        }
        SummaryText.Text = Summary(snapshot);
        ValuesText.Text = Describe(snapshot);
    }

    private static bool IsVmc(string? source) => source?.StartsWith("ほかの PC", StringComparison.Ordinal) == true;

    // 動いているときの状態の行（スマホ・VMC プロトコルは「受け取っています」。VMC プロトコルは、送り手に入れてもらう、この PC の IP も）
    private static string RunningText(string? source)
    {
        if (IsVmc(source))
        {
            IReadOnlyList<string> addresses = Theater3D.Tracking.Vmc.LocalNetwork.Addresses();
            return $"受け取っています: {source}。送り手に、この PC の IP（{(addresses.Count > 0 ? string.Join("・", addresses) : "見つかりません")}）を入れてください";
        }
        return $"{(source?.StartsWith("スマホ", StringComparison.Ordinal) == true ? "受け取っています" : "読んでいます")}: {source}";
    }

    // 映像を出しているときの、短い様子（1 行）
    private static string Summary(TrackingSnapshot s)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        if (s.Phone is { Protocol: PhoneProtocol.Vmc } v)
        {
            return string.Format(c, "{0}: {1}・{2:F0} 回/秒（送り手 {3}）", v.FromFaceTracker ? "3D Theater VCamera" : "VMC プロトコル",
                v.FaceFound ? "顔あり" : "顔が見つかりません", s.FramesPerSecond, s.PhoneSender);
        }
        if (s.Phone is { } p)
        {
            return string.Format(c, "スマホ: {0}・{1:F0} 回/秒（送り手 {2}）", p.FaceFound ? "顔あり" : "顔が見つかりません", s.FramesPerSecond, s.PhoneSender);
        }
        if (s.Result is not { } r)
        {
            return string.Empty;
        }
        return string.Format(c, "顔: {0}・処理 {1:F1} ms・{2:F0} 回/秒", r.Found ? "見つけた" : "見つかりません", r.Milliseconds, s.FramesPerSecond);
    }

    // 映像を出さないときの、読んだ値（全部）
    private static string Describe(TrackingSnapshot s)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        if (s.Phone is { Protocol: PhoneProtocol.Vmc } v)
        {
            return string.Join('\n',
                string.Format(c, "{0}: {1}（送り手 {2}・{3:F0} 回/秒）", v.FromFaceTracker ? "3D Theater VCamera" : "VMC プロトコル",
                    v.FaceFound ? "顔あり" : "顔が見つかりません", s.PhoneSender, s.FramesPerSecond),
                string.Format(c, "頭の向き: 左右 {0:+0.0;-0.0;0.0}°・上下 {1:+0.0;-0.0;0.0}°・傾き {2:+0.0;-0.0;0.0}°", v.Rotation.X, v.Rotation.Y, v.Rotation.Z),
                string.Format(c, "目を閉じる: 左 {0:F2}・右 {1:F2}（本人から見た左右）", v.Blendshape("eyeBlinkLeft"), v.Blendshape("eyeBlinkRight")),
                string.Format(c, "口を開ける: {0:F2}・すぼめる: {1:F2}・笑顔: 左 {2:F2} 右 {3:F2}", v.Blendshape("jawOpen"), v.Blendshape("mouthPucker"),
                    v.Blendshape("mouthSmileLeft"), v.Blendshape("mouthSmileRight")),
                string.Format(c, "表情の値: {0} 個", v.Blendshapes.Count));
        }
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
