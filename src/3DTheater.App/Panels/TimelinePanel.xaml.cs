using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

public partial class TimelinePanel : UserControl
{
    private readonly PlaybackService _playback;
    private readonly EditorContext _context;
    private readonly TimelineCanvas _canvas;
    private bool _updatingBar;

    public TimelinePanel(EditorContext context)
    {
        InitializeComponent();
        _playback = context.Playback;
        _context = context;
        DataContext = new TimelineViewModel(context.Playback, context.Animation, context.Edits, ChangeFrameRate);
        _canvas = new TimelineCanvas(context);
        CanvasHost.Child = _canvas;
        HorizontalBar.Margin = new Thickness(TimelineCanvas.NameColumnWidth, 0, 0, 0);
        _canvas.HorizontalViewChanged += (_, _) => UpdateScrollBar();
        _playback.StateChanged += (_, _) => UpdateScrollBar();
    }

    private void UpdateScrollBar()
    {
        _updatingBar = true;
        double visible = _canvas.VisibleFrameCount;
        HorizontalBar.Minimum = 0;
        HorizontalBar.Maximum = Math.Max(0, _playback.EndFrame - visible + 10);
        HorizontalBar.ViewportSize = visible;
        HorizontalBar.LargeChange = visible * 0.8;
        HorizontalBar.SmallChange = Math.Max(1, visible / 20);
        HorizontalBar.Value = _canvas.FirstVisibleFrame;
        _updatingBar = false;
    }

    private void OnHorizontalScroll(object sender, ScrollEventArgs e)
    {
        if (!_updatingBar)
        {
            _canvas.FirstVisibleFrame = HorizontalBar.Value;
        }
    }

    /// <summary>
    /// 作品のフレームの速さを変える。キーがあれば、位置も合わせて動きの秒数を保つか尋ねる（元に戻せる）。
    /// </summary>
    private void ChangeFrameRate(int frameRate)
    {
        int old = _playback.FrameRate;
        if (frameRate == old)
        {
            return;
        }
        Domain.SceneNode[] keyed = [.. _context.Scene.AllNodes.Where(n => KeyEditing.CountKeys(n) > 0)];
        bool rescale = false;
        if (keyed.Length > 0)
        {
            double scale = frameRate / (double)old;
            string speed = scale > 1 ? $"{scale:0.##} 倍速く" : $"{1 / scale:0.##} 倍ゆっくりに";
            MessageBoxResult answer = MessageBox.Show(
                Window.GetWindow(this),
                $"フレームの速さを {old} fps から {frameRate} fps に変えます。\n\n" +
                "キーの位置も合わせて、動きの秒数を保ちますか？\n\n" +
                $"はい: キーと終わりのフレームを {scale:0.##} 倍の位置へ動かす（動きの秒数は今のまま）\n" +
                $"いいえ: キーはフレームの番号のまま（動きが {speed}なる）",
                "フレームの速さの変更",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (answer is not (MessageBoxResult.Yes or MessageBoxResult.No))
            {
                return; // やめた（窓を閉じた・アプリを閉じた）ときは変えない
            }
            rescale = answer == MessageBoxResult.Yes;
        }

        int lost = _context.Edits.ChangeFrameRate(frameRate, keyed, rescale);
        Logging.AppLog.Info($"フレームの速さを {old} fps から {frameRate} fps に変えました（キーの位置: {(rescale ? "合わせた" : "そのまま")}）");
        if (lost > 0)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"同じフレームに重なったキーが {lost} 個あったため、後ろのキーを使いました。\n元に戻す（Ctrl+Z）で、変える前に戻せます。",
                "フレームの速さの変更", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnGoToStart(object sender, RoutedEventArgs e) => _playback.GoToStart();

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        _playback.Pause();
        _playback.Step(-1);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => _playback.TogglePlay();

    private void OnStepForward(object sender, RoutedEventArgs e)
    {
        _playback.Pause();
        _playback.Step(1);
    }

    private void OnGoToEnd(object sender, RoutedEventArgs e) => _playback.GoToEnd();

    private void OnRegisterAllClick(object sender, RoutedEventArgs e)
    {
        _playback.Pause();
        int count = 0;
        _context.Edits.Change("未登録の変更をすべて登録", _context.Animation.Unregistered, () => count = _context.Animation.RegisterUnregistered(), includeKeys: true);
        Logging.AppLog.Info($"フレーム {_playback.CurrentFrame} に、未登録の変更 {count} 個をキーとして登録しました");
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        int count = _context.Animation.UnregisteredCount;
        _context.Edits.Change("未登録の変更を破棄", _context.Animation.Unregistered, _context.Animation.DiscardUnregistered);
        Logging.AppLog.Info($"未登録の変更 {count} 個を破棄しました");
    }

    private void OnRegisterSelectionClick(object sender, RoutedEventArgs e)
    {
        _playback.Pause();
        IReadOnlyList<Domain.SceneNode> targets = _context.Animation.RegistrationTargets(_context.Selection.Items);
        int count = 0;
        _context.Edits.Change("キーの登録", targets, () => count = _context.Animation.Register(targets), includeKeys: true);
        if (count > 0)
        {
            Logging.AppLog.Info($"フレーム {_playback.CurrentFrame} にキーを {count} 個登録しました");
        }
        else
        {
            Logging.AppLog.Info("登録する項目がありません（ボーン・モーフ・カメラ・照明を選ぶか、モデルを選んで動かしてから登録してください）");
        }
    }
}
