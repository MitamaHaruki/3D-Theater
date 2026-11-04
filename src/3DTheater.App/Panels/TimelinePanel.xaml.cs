using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

public partial class TimelinePanel : UserControl
{
    private readonly PlaybackService _playback;
    private readonly TimelineCanvas _canvas;
    private bool _updatingBar;

    public TimelinePanel(EditorContext context)
    {
        InitializeComponent();
        _playback = context.Playback;
        DataContext = new TimelineViewModel(context.Playback);
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
}
