using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>
/// グラフエディタの値のグラフ。横がフレーム、縦が値。選んだ項目の値の線（チャンネルごと）と、キーの印、今のフレームの線を描く。
/// 編集しているキーの範囲（前のキーからこのキーまで）を薄く塗り、編集している曲線で動く線を太く描く。
/// クリック: 下の帯のキーの印ならそのキーを選ぶ（Ctrl で足す）、それ以外の所ならそのフレームへ動かす。
/// ホイール = 左右、Ctrl + ホイール = 拡大縮小。
/// </summary>
public sealed class GraphCanvas : FrameworkElement
{
    private const double LeftAxis = 54;
    private const double TopPadding = 10;
    private const double KeyStrip = 30;
    private const double RightPadding = 10;

    private readonly EditorContext _context;
    private readonly GraphEditorViewModel _viewModel;
    private readonly Brush _background;
    private readonly Brush _subText;
    private readonly Brush _accent;
    private readonly Pen _gridPen;
    private readonly Pen _axisPen;
    private readonly Brush _segmentFill = Frozen(Color.FromArgb(0x24, 0xFF, 0xB0, 0x30));
    private readonly Brush _keyBrush = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private readonly Brush _selectedKey = Frozen(Color.FromRgb(0xFF, 0xB0, 0x30));
    private readonly Pen _keyOutline = new(Frozen(Color.FromArgb(0xC0, 0x10, 0x10, 0x10)), 1);
    private readonly Typeface _face = new("Yu Gothic UI");

    private double _firstFrame;
    private double _lastFrame = 100;
    private (SceneNode? Node, int Version, int First, int Last) _cacheKey;
    private IReadOnlyList<GraphSeries> _cache = [];

    public GraphCanvas(EditorContext context, GraphEditorViewModel viewModel)
    {
        _context = context;
        _viewModel = viewModel;
        ClipToBounds = true;
        Focusable = true;
        _background = (Brush)Application.Current.FindResource("BackgroundBrush");
        _subText = (Brush)Application.Current.FindResource("SubTextBrush");
        _accent = (Brush)Application.Current.FindResource("AccentBrush");
        _gridPen = new Pen(Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
        _gridPen.Freeze();
        _axisPen = new Pen(Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
        _axisPen.Freeze();
        _keyOutline.Freeze();

        viewModel.Changed += (_, _) => InvalidateVisual();
        viewModel.TargetChanged += (_, _) => FitToKeys();
        context.Playback.FrameChanged += (_, _) => InvalidateVisual();
        FitToKeys();
    }

    /// <summary>キーの範囲が全部見えるように合わせる（キーが無ければ作品の長さ全体）。</summary>
    public void FitToKeys()
    {
        IReadOnlyList<int> frames = _viewModel.Target is { } node ? CurveEditing.KeyFrames(node) : [];
        if (frames.Count >= 2)
        {
            double span = frames[^1] - frames[0];
            double pad = Math.Max(5, span * 0.05);
            _firstFrame = Math.Max(0, frames[0] - pad);
            _lastFrame = frames[^1] + pad;
        }
        else
        {
            _firstFrame = 0;
            _lastFrame = Math.Max(30, _context.Playback.EndFrame);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(_background, null, new Rect(0, 0, width, height));
        if (_viewModel.Target is not { } node || width < LeftAxis + 40 || height < KeyStrip + 40)
        {
            return;
        }

        int first = (int)Math.Floor(_firstFrame);
        int last = (int)Math.Ceiling(_lastFrame);
        IReadOnlyList<GraphSeries> series = Sample(node, first, last);
        bool[] shown = [.. series.Select((s, i) => i < _viewModel.SeriesItems.Count && _viewModel.SeriesItems[i].IsVisible)];

        // 縦の範囲（見せている線の値が全部入るように）
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < series.Count; i++)
        {
            if (!shown[i])
            {
                continue;
            }
            foreach (float v in series[i].Values)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }
        if (min > max)
        {
            (min, max) = (-1, 1);
        }
        if (max - min < 1e-3f)
        {
            min -= 1;
            max += 1;
        }
        double plotTop = TopPadding;
        double plotBottom = height - KeyStrip - 4;
        double margin = (max - min) * 0.08;
        double vMin = min - margin, vMax = max + margin;
        double Y(double v) => plotBottom - ((v - vMin) / (vMax - vMin) * (plotBottom - plotTop));

        // 編集しているキーの範囲（前のキーからこのキーまで）
        if (_viewModel.SelectedKey is { } key && CurveEditing.PreviousKeyFrame(key) is { } previous)
        {
            double x0 = Math.Max(LeftAxis, FrameToX(previous));
            double x1 = Math.Min(width - RightPadding, FrameToX(key.Frame));
            if (x1 > x0)
            {
                dc.DrawRectangle(_segmentFill, null, new Rect(x0, plotTop, x1 - x0, plotBottom - plotTop));
            }
        }

        // 目盛り（横: フレーム、縦: 値）
        double frameStep = NiceStep((_lastFrame - _firstFrame) / Math.Max(1, (width - LeftAxis) / 70));
        for (double f = Math.Ceiling(_firstFrame / frameStep) * frameStep; f <= _lastFrame; f += frameStep)
        {
            double x = FrameToX(f);
            dc.DrawLine(_gridPen, new Point(x, plotTop), new Point(x, plotBottom));
            dc.DrawText(Text(f.ToString("0", CultureInfo.InvariantCulture), 10, _subText, dip), new Point(x + 2, plotBottom + 2));
        }
        double valueStep = NiceStep((vMax - vMin) / Math.Max(1, (plotBottom - plotTop) / 36));
        for (double v = Math.Ceiling(vMin / valueStep) * valueStep; v <= vMax; v += valueStep)
        {
            double y = Y(v);
            dc.DrawLine(Math.Abs(v) < valueStep * 1e-6 ? _axisPen : _gridPen, new Point(LeftAxis, y), new Point(width - RightPadding, y));
            dc.DrawText(Text(FormatValue(v, valueStep), 10, _subText, dip), new Point(4, y - 7));
        }

        // 値の線（編集している曲線で動く線は太く、ほかは細く）
        int highlight = _viewModel.HighlightChannel;
        dc.PushClip(new RectangleGeometry(new Rect(LeftAxis, plotTop, Math.Max(0, width - LeftAxis - RightPadding), plotBottom - plotTop)));
        for (int i = 0; i < series.Count; i++)
        {
            if (!shown[i])
            {
                continue;
            }
            GraphSeries s = series[i];
            Brush brush = _viewModel.SeriesItems[i].Brush;
            bool strong = highlight < 0 || s.CurveChannel == highlight;
            var pen = new Pen(brush, strong ? 2 : 1) { DashStyle = strong ? DashStyles.Solid : DashStyles.Dash };
            pen.Freeze();
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                for (int k = 0; k < s.Values.Length; k++)
                {
                    var p = new Point(FrameToX(s.FirstFrame + k), Y(s.Values[k]));
                    if (k == 0)
                    {
                        g.BeginFigure(p, false, false);
                    }
                    else
                    {
                        g.LineTo(p, true, false);
                    }
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        // キーの点（線の上）
        IReadOnlyList<int> keys = CurveEditing.KeyFrames(node);
        foreach (int frame in keys)
        {
            if (frame < first || frame > last)
            {
                continue;
            }
            for (int i = 0; i < series.Count; i++)
            {
                if (shown[i])
                {
                    double y = Y(series[i].Values[frame - series[i].FirstFrame]);
                    dc.DrawEllipse(_viewModel.SeriesItems[i].Brush, _keyOutline, new Point(FrameToX(frame), y), 3, 3);
                }
            }
        }
        dc.Pop();

        // 下の帯: キーの印（選んだキーは橙）
        double stripY = height - 9; // フレームの数字の下
        dc.DrawLine(_axisPen, new Point(LeftAxis, height - KeyStrip), new Point(width - RightPadding, height - KeyStrip));
        foreach (int frame in keys)
        {
            double x = FrameToX(frame);
            if (x < LeftAxis - 4 || x > width)
            {
                continue;
            }
            bool selected = _context.KeySelection.Contains(new KeyRef(node, frame));
            dc.DrawGeometry(selected ? _selectedKey : _keyBrush, _keyOutline, Diamond(x, stripY, selected ? 6 : 5));
        }

        // 今のフレーム
        double nowX = FrameToX(_context.Playback.CurrentFrame);
        if (nowX >= LeftAxis && nowX <= width - RightPadding)
        {
            dc.DrawLine(new Pen(_accent, 1.5), new Point(nowX, plotTop), new Point(nowX, height));
        }
        dc.DrawLine(_axisPen, new Point(LeftAxis, plotTop), new Point(LeftAxis, height));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point p = e.GetPosition(this);
        if (_viewModel.Target is not { } node || p.X < LeftAxis)
        {
            return;
        }
        if (p.Y >= ActualHeight - KeyStrip)
        {
            // キーの印をクリック（近いキーを選ぶ）
            int? nearest = CurveEditing.KeyFrames(node).Select(f => (int?)f).MinBy(f => Math.Abs(FrameToX(f!.Value) - p.X));
            if (nearest is { } frame && Math.Abs(FrameToX(frame) - p.X) <= 8)
            {
                _viewModel.SelectKey(frame, add: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true;
                return;
            }
        }
        _context.Playback.Pause();
        _context.Playback.Seek((int)Math.Round(XToFrame(p.X)));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double span = _lastFrame - _firstFrame;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // マウスの位置を中心に拡大縮小
            double anchor = XToFrame(e.GetPosition(this).X);
            double scale = e.Delta > 0 ? 0.8 : 1.25;
            double newSpan = Math.Clamp(span * scale, 10, 100_000);
            double ratio = (anchor - _firstFrame) / span;
            _firstFrame = Math.Max(0, anchor - (newSpan * ratio));
            _lastFrame = _firstFrame + newSpan;
        }
        else
        {
            double shift = span * 0.1 * (e.Delta > 0 ? -1 : 1);
            _firstFrame = Math.Max(0, _firstFrame + shift);
            _lastFrame = _firstFrame + span;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private IReadOnlyList<GraphSeries> Sample(SceneNode node, int first, int last)
    {
        var key = (node, _viewModel.DataVersion, first, last);
        if (key != _cacheKey)
        {
            _cache = CurveEditing.Sample(node, first, last);
            _cacheKey = key;
        }
        return _cache;
    }

    private double FrameToX(double frame) =>
        LeftAxis + ((frame - _firstFrame) / Math.Max(1e-6, _lastFrame - _firstFrame) * Math.Max(1, ActualWidth - LeftAxis - RightPadding));

    private double XToFrame(double x) =>
        _firstFrame + ((x - LeftAxis) / Math.Max(1, ActualWidth - LeftAxis - RightPadding) * (_lastFrame - _firstFrame));

    // 1・2・5 の倍数で、目盛りの間をきりのよい値にする
    private static double NiceStep(double rough)
    {
        if (!(rough > 0) || !double.IsFinite(rough))
        {
            return 1;
        }
        double power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double r = rough / power;
        return (r <= 1 ? 1 : r <= 2 ? 2 : r <= 5 ? 5 : 10) * power;
    }

    private static string FormatValue(double v, double step) =>
        (Math.Abs(v) < step * 1e-6 ? 0 : v).ToString(step >= 1 ? "0" : step >= 0.1 ? "0.0" : "0.00", CultureInfo.InvariantCulture); // 「-0」にしない

    private static StreamGeometry Diamond(double x, double y, double r)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(new Point(x, y - r), true, true);
            c.LineTo(new Point(x + r, y), true, false);
            c.LineTo(new Point(x, y + r), true, false);
            c.LineTo(new Point(x - r, y), true, false);
        }
        g.Freeze();
        return g;
    }

    private FormattedText Text(string text, double size, Brush brush, double dip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _face, size, brush, dip);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
