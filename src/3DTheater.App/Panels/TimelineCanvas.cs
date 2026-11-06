using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>
/// タイムラインの本体。左にトラック名、上に目盛り、右にトラックの帯と再生位置の線を描く。
/// 目盛りか帯をクリック・ドラッグで再生位置を動かす（再生中なら一時停止する）。
/// ホイール = 上下、Shift + ホイール = 左右、Ctrl + ホイール = 拡大縮小。
/// Phase 1 ではキーフレームはまだ無い（Phase 2 で登録・編集を作る）。
/// </summary>
public sealed class TimelineCanvas : FrameworkElement
{
    public const double NameColumnWidth = 190;
    private const double RulerHeight = 26;
    private const double RowHeight = 22;
    private const double LeftPadding = 10;
    private static readonly int[] TickSteps = [1, 2, 5, 10, 30, 60, 150, 300, 600, 1500, 3000];

    private readonly EditorContext _context;
    private readonly HashSet<SceneNode> _collapsed = [];
    private readonly HashSet<SceneNode> _seen = [];
    private readonly List<(SceneNode Node, int Depth)> _rows = [];
    private double _pixelsPerFrame = 4;
    private double _firstFrame;
    private double _scrollY;
    private bool _scrubbing;

    private readonly Brush _background;
    private readonly Brush _rowAlt;
    private readonly Brush _selection;
    private readonly Brush _text;
    private readonly Brush _subText;
    private readonly Brush _disabledText;
    private readonly Pen _gridPen;
    private readonly Pen _majorPen;
    private readonly Pen _borderPen;
    private readonly Brush _outOfRange;
    private readonly Brush _accent;
    private readonly Typeface _face = new("Yu Gothic UI");
    private readonly Typeface _iconFace;

    public TimelineCanvas(EditorContext context)
    {
        _context = context;
        ClipToBounds = true;
        Focusable = true;

        _background = (Brush)Application.Current.FindResource("BackgroundBrush");
        _rowAlt = Frozen(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
        _selection = (Brush)Application.Current.FindResource("SelectionBrush");
        _text = (Brush)Application.Current.FindResource("TextBrush");
        _subText = (Brush)Application.Current.FindResource("SubTextBrush");
        _disabledText = (Brush)Application.Current.FindResource("DisabledTextBrush");
        _accent = (Brush)Application.Current.FindResource("AccentBrush");
        _outOfRange = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)));
        _gridPen = FrozenPen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        _majorPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _borderPen = FrozenPen(((SolidColorBrush)Application.Current.FindResource("BorderBrush")).Color);
        _iconFace = new Typeface(((FontFamily)Application.Current.FindResource("IconFont")), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        context.Playback.FrameChanged += (_, _) => OnFrameChanged();
        context.Playback.StateChanged += (_, _) => InvalidateVisual();
        context.Selection.Changed += (_, _) => InvalidateVisual();
        context.Scene.NodeChanged += (_, e) =>
        {
            // 行の組み直しは項目が増減したときだけ（ボーンやモーフの値を動かすたびには組み直さない）
            if (e.PropertyName == nameof(SceneNode.Children))
            {
                RebuildRows();
            }
            InvalidateVisual();
        };
        RebuildRows();
    }

    /// <summary>左右の表示位置か拡大率が変わった（下のスクロールバーを合わせるため）。</summary>
    public event EventHandler? HorizontalViewChanged;

    public double FirstVisibleFrame
    {
        get => _firstFrame;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, _context.Playback.EndFrame - 1));
            if (Math.Abs(clamped - _firstFrame) < 1e-9)
            {
                return;
            }
            _firstFrame = clamped;
            InvalidateVisual();
            HorizontalViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double VisibleFrameCount => Math.Max(1, (ActualWidth - NameColumnWidth - LeftPadding) / _pixelsPerFrame);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(_background, null, new Rect(0, 0, width, height));

        // トラックの帯と名前
        for (int i = 0; i < _rows.Count; i++)
        {
            double y = RulerHeight + (i * RowHeight) - _scrollY;
            if (y + RowHeight < RulerHeight || y > height)
            {
                continue;
            }
            var (node, depth) = _rows[i];
            var rowRect = new Rect(0, y, width, RowHeight);
            if (_context.Selection.Contains(node))
            {
                dc.DrawRectangle(_selection, null, rowRect);
            }
            else if (i % 2 == 1)
            {
                dc.DrawRectangle(_rowAlt, null, rowRect);
            }

            double x = 6 + (depth * 14);
            if (node.Children.Count > 0)
            {
                string chevron = _collapsed.Contains(node) ? "" : "";
                dc.DrawText(Text(chevron, 9, _subText, dip, _iconFace), new Point(x, y + 6));
            }
            Brush nameBrush = node.IsEffectivelyVisible ? _text : _disabledText;
            var name = Text(node.Name, 12, nameBrush, dip);
            name.MaxTextWidth = Math.Max(1, NameColumnWidth - x - 22);
            name.Trimming = TextTrimming.CharacterEllipsis;
            name.MaxLineCount = 1;
            dc.DrawText(name, new Point(x + 16, y + 3));
        }

        // 帯の範囲（名前の列より右）
        double laneLeft = NameColumnWidth;
        dc.PushClip(new RectangleGeometry(new Rect(laneLeft, 0, Math.Max(0, width - laneLeft), height)));

        int majorStep = ChooseMajorStep();
        int minorStep = Math.Max(1, majorStep / 5);
        int first = (int)Math.Floor(_firstFrame / minorStep) * minorStep;
        int last = (int)Math.Ceiling(_firstFrame + VisibleFrameCount);
        for (int f = Math.Max(0, first); f <= last; f += minorStep)
        {
            double x = FrameToX(f);
            bool major = f % majorStep == 0;
            dc.DrawLine(major ? _majorPen : _gridPen, new Point(x, RulerHeight), new Point(x, height));
            dc.DrawLine(TickPen(major), new Point(x, RulerHeight - (major ? 9 : 4)), new Point(x, RulerHeight));
            if (major)
            {
                dc.DrawText(Text(f.ToString(CultureInfo.InvariantCulture), 11, _subText, dip), new Point(x + 3, 3));
            }
        }

        // 終わりのフレームより後ろは暗くする
        double endX = FrameToX(_context.Playback.EndFrame);
        if (endX < width)
        {
            dc.DrawRectangle(_outOfRange, null, new Rect(endX, 0, width - endX, height));
        }

        // 再生位置
        int current = _context.Playback.CurrentFrame;
        double playX = FrameToX(current);
        dc.DrawLine(new Pen(_accent, 1.5), new Point(playX, RulerHeight - 4), new Point(playX, height));
        var label = Text(current.ToString(CultureInfo.InvariantCulture), 11, Brushes.White, dip);
        double labelWidth = Math.Max(24, label.Width + 10);
        dc.DrawRoundedRectangle(_accent, null, new Rect(playX - (labelWidth / 2), 3, labelWidth, 18), 3, 3);
        dc.DrawText(label, new Point(playX - (label.Width / 2), 4));
        dc.Pop();

        // 区切り線と左上の見出し
        dc.DrawLine(_borderPen, new Point(0, RulerHeight), new Point(width, RulerHeight));
        dc.DrawLine(_borderPen, new Point(laneLeft, 0), new Point(laneLeft, height));
        dc.DrawRectangle(_background, null, new Rect(0, 0, laneLeft, RulerHeight));
        dc.DrawText(Text("トラック", 11, _subText, dip), new Point(8, 5));
        dc.DrawLine(_borderPen, new Point(0, RulerHeight), new Point(laneLeft, RulerHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point p = e.GetPosition(this);
        if (p.X >= NameColumnWidth)
        {
            _scrubbing = true;
            CaptureMouse();
            _context.Playback.Pause();
            SeekTo(p.X);
            e.Handled = true;
            return;
        }

        int row = (int)Math.Floor((p.Y - RulerHeight + _scrollY) / RowHeight);
        if (p.Y < RulerHeight || row < 0 || row >= _rows.Count)
        {
            return;
        }
        var (node, depth) = _rows[row];
        double chevronLeft = 6 + (depth * 14);
        if (node.Children.Count > 0 && p.X >= chevronLeft && p.X < chevronLeft + 16)
        {
            if (!_collapsed.Remove(node))
            {
                _collapsed.Add(node);
            }
            RebuildRows();
            InvalidateVisual();
        }
        else
        {
            _context.Selection.Select(node, Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectMode.Toggle : SelectMode.Replace, this);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_scrubbing)
        {
            SeekTo(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_scrubbing)
        {
            _scrubbing = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double steps = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // マウスの下のフレームを動かさずに拡大縮小する
            double x = e.GetPosition(this).X;
            double frameAtMouse = XToFrame(x);
            _pixelsPerFrame = Math.Clamp(_pixelsPerFrame * Math.Pow(1.25, steps), 0.2, 60);
            FirstVisibleFrame = frameAtMouse - ((x - NameColumnWidth - LeftPadding) / _pixelsPerFrame);
            InvalidateVisual();
            HorizontalViewChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            FirstVisibleFrame -= steps * VisibleFrameCount / 8;
        }
        else
        {
            double maxScroll = Math.Max(0, (_rows.Count * RowHeight) - (ActualHeight - RulerHeight));
            _scrollY = Math.Clamp(_scrollY - (steps * RowHeight * 2), 0, maxScroll);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        HorizontalViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrameChanged()
    {
        // 再生中は、再生位置が見える範囲から出たら追いかける
        int current = _context.Playback.CurrentFrame;
        if (current < _firstFrame || current > _firstFrame + VisibleFrameCount)
        {
            FirstVisibleFrame = current < _firstFrame ? current : current - (VisibleFrameCount * 0.1);
        }
        InvalidateVisual();
    }

    private void SeekTo(double x) => _context.Playback.Seek((int)Math.Round(XToFrame(x)));

    private double FrameToX(double frame) => NameColumnWidth + LeftPadding + ((frame - _firstFrame) * _pixelsPerFrame);

    private double XToFrame(double x) => _firstFrame + ((x - NameColumnWidth - LeftPadding) / _pixelsPerFrame);

    private int ChooseMajorStep()
    {
        foreach (int step in TickSteps)
        {
            if (step * _pixelsPerFrame >= 56)
            {
                return step;
            }
        }
        return TickSteps[^1];
    }

    private void RebuildRows()
    {
        _rows.Clear();
        foreach (SceneNode child in _context.Scene.Root.Children)
        {
            AddRows(child, 0);
        }

        void AddRows(SceneNode node, int depth)
        {
            _rows.Add((node, depth));
            if (_seen.Add(node) && node.StartsCollapsed)
            {
                _collapsed.Add(node); // ボーンや表情のまとまりは、シーン一覧と同じく最初は閉じておく
            }
            if (_collapsed.Contains(node))
            {
                return;
            }
            foreach (SceneNode child in node.Children)
            {
                AddRows(child, depth + 1);
            }
        }
    }

    private Pen TickPen(bool major) => major ? _majorPen : _gridPen;

    private FormattedText Text(string text, double size, Brush brush, double pixelsPerDip, Typeface? face = null) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? _face, size, brush, pixelsPerDip);

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
