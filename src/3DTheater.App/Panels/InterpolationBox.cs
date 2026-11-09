using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Theater3D.Domain;

namespace Theater3D.App.Panels;

/// <summary>
/// 補間曲線の枠（MMD と同じく、横が時間の進み・縦が値の進みで、どちらも 0〜127）。2 つの制御点をドラッグで動かす。
/// ドラッグの間は <see cref="GraphEditorViewModel.UpdateDrag"/> で曲線を変え、離したら 1 つの操作として確定する。
/// ドラッグ中の右クリック・Esc で取り消す（つかむ前の曲線に戻す）。
/// </summary>
public sealed class InterpolationBox : FrameworkElement
{
    private const double Padding = 10;
    private const double HandleRadius = 6;
    private const double GrabDistance = 12;

    private readonly GraphEditorViewModel _viewModel;
    private readonly Brush _background;
    private readonly Brush _subText;
    private readonly Pen _framePen;
    private readonly Pen _gridPen;
    private readonly Pen _diagonalPen;
    private readonly Pen _curvePen;
    private readonly Pen _handleLinePen;
    private readonly Brush _point1 = Frozen(Color.FromRgb(0xF0, 0x6A, 0x6A));
    private readonly Brush _point2 = Frozen(Color.FromRgb(0x6A, 0x9A, 0xF0));
    private readonly Brush _disabled = Frozen(Color.FromArgb(0x60, 0x00, 0x00, 0x00));
    private readonly Typeface _face = new("Yu Gothic UI");

    private int _dragging; // 0 = なし、1 = 1 つめの点、2 = 2 つめの点
    private BezierCurve _dragCurve;

    public InterpolationBox(GraphEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        Focusable = true;
        _background = (Brush)Application.Current.FindResource("BackgroundBrush");
        _subText = (Brush)Application.Current.FindResource("SubTextBrush");
        _framePen = FrozenPen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1);
        _gridPen = FrozenPen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 1);
        _diagonalPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1);
        _curvePen = FrozenPen(Color.FromRgb(0xF0, 0xC8, 0x40), 2);
        _handleLinePen = FrozenPen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 1);
        viewModel.Changed += (_, _) => InvalidateVisual();
        viewModel.PropertyChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Math.Min(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Rect box = Box();
        dc.DrawRectangle(_background, _framePen, box);
        for (int i = 1; i < 4; i++)
        {
            double x = box.Left + (box.Width * i / 4);
            double y = box.Top + (box.Height * i / 4);
            dc.DrawLine(_gridPen, new Point(x, box.Top), new Point(x, box.Bottom));
            dc.DrawLine(_gridPen, new Point(box.Left, y), new Point(box.Right, y));
        }
        dc.DrawLine(_diagonalPen, box.BottomLeft, box.TopRight);

        BezierCurve curve = _dragging != 0 ? _dragCurve : _viewModel.Curve;
        Point p0 = ToScreen(Vector2.Zero), p3 = ToScreen(Vector2.One);
        Point p1 = ToScreen(curve.Control1), p2 = ToScreen(curve.Control2);

        if (_viewModel.IsStep && _dragging == 0)
        {
            // 段階: 終わりまで変わらず、最後に飛ぶ
            var step = new StreamGeometry();
            using (StreamGeometryContext g = step.Open())
            {
                g.BeginFigure(p0, false, false);
                g.LineTo(new Point(p3.X, p0.Y), true, false);
                g.LineTo(p3, true, false);
            }
            step.Freeze();
            dc.DrawGeometry(null, _curvePen, step);
        }
        else
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(p0, false, false);
                g.BezierTo(p1, p2, p3, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, _curvePen, geometry);
            dc.DrawLine(_handleLinePen, p0, p1);
            dc.DrawLine(_handleLinePen, p3, p2);
            dc.DrawEllipse(_point1, null, p1, HandleRadius, HandleRadius);
            dc.DrawEllipse(_point2, null, p2, HandleRadius, HandleRadius);
        }

        var (x1, y1, x2, y2) = Editor.CurveEditing.ToMmd(curve);
        dc.DrawText(new FormattedText($"({x1}, {y1})  ({x2}, {y2})", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10, _subText, dip),
            new Point(box.Left + 3, box.Top + 2));
        if (!_viewModel.CanEditCurve)
        {
            dc.DrawRectangle(_disabled, null, box);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!_viewModel.CanEditCurve)
        {
            return;
        }
        Point p = e.GetPosition(this);
        BezierCurve curve = _viewModel.Curve;
        double d1 = (p - ToScreen(curve.Control1)).Length;
        double d2 = (p - ToScreen(curve.Control2)).Length;
        int handle = Math.Min(d1, d2) > GrabDistance ? 0 : d1 <= d2 ? 1 : 2;
        if (handle == 0 || !_viewModel.BeginDrag())
        {
            return;
        }
        _dragging = handle;
        _dragCurve = curve;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging == 0)
        {
            return;
        }
        Vector2 v = ToCurve(e.GetPosition(this));
        _dragCurve = _dragging == 1 ? _dragCurve with { Control1 = v } : _dragCurve with { Control2 = v };
        _viewModel.UpdateDrag(_dragCurve);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Finish(commit: true);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_dragging != 0)
        {
            Finish(commit: false);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _dragging != 0)
        {
            Finish(commit: false);
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Finish(commit: true);
    }

    private void Finish(bool commit)
    {
        if (_dragging == 0)
        {
            return;
        }
        _dragging = 0;
        _viewModel.EndDrag(commit);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        InvalidateVisual();
    }

    private Rect Box()
    {
        double side = Math.Max(10, Math.Min(ActualWidth, ActualHeight) - (2 * Padding));
        return new Rect((ActualWidth - side) / 2, (ActualHeight - side) / 2, side, side);
    }

    private Point ToScreen(Vector2 v)
    {
        Rect box = Box();
        return new Point(box.Left + (v.X * box.Width), box.Bottom - (v.Y * box.Height));
    }

    // 画面の位置 → 曲線の制御点（0〜1、MMD と同じ 1/127 刻み）
    private Vector2 ToCurve(Point p)
    {
        Rect box = Box();
        float x = (float)Math.Clamp((p.X - box.Left) / box.Width, 0, 1);
        float y = (float)Math.Clamp((box.Bottom - p.Y) / box.Height, 0, 1);
        return new Vector2(MathF.Round(x * 127) / 127, MathF.Round(y * 127) / 127);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
