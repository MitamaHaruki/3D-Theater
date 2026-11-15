using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// キャラクターへの当てはめ方（鏡のように・目線・パーフェクトシンク・上半身・体をずらす・なめらかさ）と、当てはめ先のボーン・モーフ、今当てはめている値を出す。
/// ライブモードのサイドバーの「キャラクター」に置く（設計判断記録 78 章の 2 段目。前はトラッキングのパネルの中にあった）。
/// </summary>
public partial class TrackingFitPanel : UserControl
{
    // 当てはめている値は動き続けるので、見えている間だけ 1 秒に 10 回ほど読み直す
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly EditorContext _context;
    private readonly FaceRetargetSettings _settings;
    private readonly DispatcherTimer _timer;
    private bool _updating;

    internal TrackingFitPanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _settings = context.LiveTracking;
        _settings.Changed += (_, _) => ShowSettings();
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => ShowDriver(), Dispatcher);
        _timer.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                ShowDriver();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
        ShowSettings();
    }

    private void ShowSettings()
    {
        _updating = true;
        try
        {
            MirrorCheck.IsChecked = _settings.Mirror;
            GazeCheck.IsChecked = _settings.Gaze;
            PerfectSyncCheck.IsChecked = _settings.PerfectSync;
            UpperBodyCheck.IsChecked = _settings.UpperBody;
            BodySwayCheck.IsChecked = _settings.BodySway;
            SmoothingSlider.Value = _settings.Smoothing;
        }
        finally
        {
            _updating = false;
        }
    }

    private void ShowDriver()
    {
        RigText.Text = _context.TrackingDriver?.Status ?? string.Empty;
        AppliedText.Text = _context.TrackingDriver?.Applied ?? string.Empty;
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }
        _settings.Mirror = MirrorCheck.IsChecked == true;
        _settings.Gaze = GazeCheck.IsChecked == true;
        _settings.PerfectSync = PerfectSyncCheck.IsChecked == true;
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
}
