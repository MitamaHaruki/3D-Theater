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
        DataContext = new TimelineViewModel(context.Playback, context.Animation);
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
            Logging.AppLog.Info("登録するボーン・モーフがありません（ボーンかモーフを選ぶか、モデルを選んで動かしてから登録してください）");
        }
    }
}
