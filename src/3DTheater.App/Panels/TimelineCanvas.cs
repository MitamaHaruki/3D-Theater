using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>
/// タイムラインの本体。左にトラック名、上に目盛り、右にトラックの帯と再生位置の線を描く。
/// 目盛りをクリック・ドラッグで再生位置を動かす。帯の空いている所をクリックしても、その位置へ動かす（再生中なら一時停止する）。
/// キー: クリックで選ぶ（Ctrl で追加・解除、Shift で追加）、帯の空いている所からドラッグで範囲選択、
/// 選んだキーをドラッグで移動（Ctrl を押しながらドラッグで複製）、ダブルクリックでそのキーのフレームへ、右クリックでメニュー。
/// まとまりの行の◆は中のキーをまとめたもので、選ぶと中のキーすべてを選ぶ。
/// ホイール = 上下、Shift + ホイール = 左右、Ctrl + ホイール = 拡大縮小。
/// 未登録の変更がある項目は名前を橙色にする。
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
    private readonly Dictionary<SceneNode, int[]> _summaryCache = [];
    private readonly List<(SceneNode Node, int Depth)> _rows = [];
    private double _pixelsPerFrame = 4;
    private double _firstFrame;
    private double _scrollY;
    private const double KeyHitTolerance = 6;
    private const double DragThreshold = 3;

    private DragMode _drag;
    private Point _dragStart;
    private int _dragDelta;
    private bool _duplicate;
    private KeyRef[] _toggleOffOnClick = [];
    private readonly Dictionary<SceneNode, HashSet<int>> _selectedFrames = [];

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
    private readonly Brush _key = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
    private readonly Brush _summaryKey = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0xB0, 0xB8, 0xC8)));
    private readonly Brush _unregistered = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x40)));
    private readonly Pen _keyOutline = FrozenPen(Color.FromArgb(0xC0, 0x10, 0x10, 0x10));
    private readonly Brush _selectedKey = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x30)));
    private readonly Brush _movingKey = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0xE6, 0xE6, 0xE6)));
    private readonly Brush _bandFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x4A, 0x9E, 0xFF)));
    private readonly Pen _bandPen = FrozenPen(Color.FromArgb(0xC0, 0x4A, 0x9E, 0xFF));
    private readonly Brush _audioFill = Frozen(new SolidColorBrush(Color.FromArgb(0x28, 0x3F, 0xB9, 0x50)));
    private readonly Pen _wavePen = FrozenPen(Color.FromArgb(0xC0, 0x5F, 0xD0, 0x70));
    private readonly Pen _rulerWavePen = FrozenPen(Color.FromArgb(0x50, 0x5F, 0xD0, 0x70));
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
        context.KeySelection.Changed += (_, _) =>
        {
            RebuildSelectedFrames();
            InvalidateVisual();
        };
        context.Audio.WaveformChanged += (_, _) => InvalidateVisual();
        context.Scene.NodeChanged += (_, e) =>
        {
            // 行の組み直しは項目が増減したときだけ（ボーンやモーフの値を動かすたびには組み直さない）
            if (e.PropertyName == nameof(SceneNode.Children))
            {
                RebuildRows();
            }
            if (e.PropertyName is nameof(SceneNode.Children) or nameof(BoneNode.Keys))
            {
                _summaryCache.Clear();
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
            Brush nameBrush = node is BoneNode { HasUnregisteredChange: true } or MorphNode { HasUnregisteredChange: true } or CameraNode { HasUnregisteredChange: true }
                or LightNode { HasUnregisteredChange: true } or ModelDisplayNode { HasUnregisteredChange: true } or EffectsNode { HasUnregisteredChange: true } or SelfShadowNode { HasUnregisteredChange: true }
                ? _unregistered
                : node.IsEffectivelyVisible ? _text : _disabledText;
            var name = Text(node.Name, 12, nameBrush, dip);
            name.MaxTextWidth = Math.Max(1, NameColumnWidth - x - 22);
            name.Trimming = TextTrimming.CharacterEllipsis;
            name.MaxLineCount = 1;
            dc.DrawText(name, new Point(x + 16, y + 3));
        }

        // 帯の範囲（名前の列より右）
        double laneLeft = NameColumnWidth;
        dc.PushClip(new RectangleGeometry(new Rect(laneLeft, 0, Math.Max(0, width - laneLeft), height)));

        // 目盛りの帯にも音声の波形を薄く描く（音声の行がスクロールで隠れていても、拍の位置が見えるように）
        if (_context.Scene.Audio is { } rulerAudio)
        {
            DrawWaveform(dc, rulerAudio, 0, RulerHeight, width, null, _rulerWavePen);
        }

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

        // キーフレーム（ボーン・モーフの行はその項目のキー、まとまりの行は中のキーをまとめて薄く）。音声の行は波形
        for (int i = 0; i < _rows.Count; i++)
        {
            double y = RulerHeight + (i * RowHeight) - _scrollY;
            if (y + RowHeight < RulerHeight || y > height)
            {
                continue;
            }
            if (_rows[i].Node is AudioNode audio)
            {
                DrawWaveform(dc, audio, y + 2, RowHeight - 4, width, _audioFill, _wavePen);
                continue;
            }
            DrawKeys(dc, _rows[i].Node, y + (RowHeight / 2), Math.Max(0, first), last);
        }

        // 範囲選択の四角
        if (_drag == DragMode.Band)
        {
            dc.DrawRectangle(_bandFill, _bandPen, BandRect());
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

    private enum DragMode
    {
        None,
        Scrub,
        PendingKey,
        Keys,
        PendingEmpty,
        Band,
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point p = e.GetPosition(this);
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (p.X >= NameColumnWidth)
        {
            CaptureMouse();
            _dragStart = p;
            if (p.Y < RulerHeight)
            {
                // 目盛り: 再生位置を動かす
                _drag = DragMode.Scrub;
                _context.Playback.Pause();
                SeekTo(p.X);
            }
            else if (HitKeys(p) is { Count: > 0 } hit)
            {
                if (e.ClickCount == 2)
                {
                    _context.Playback.Pause();
                    _context.Playback.Seek(hit[0].Frame);
                    ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }
                bool allSelected = hit.All(_context.KeySelection.Contains);
                _toggleOffOnClick = [];
                if (modifiers.HasFlag(ModifierKeys.Control) && allSelected)
                {
                    _toggleOffOnClick = [.. hit]; // ドラッグしなければ、離したときに選択から外す
                }
                else if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift))
                {
                    _context.KeySelection.Add(hit);
                }
                else if (!allSelected)
                {
                    _context.KeySelection.Set(hit);
                }
                _drag = DragMode.PendingKey;
                _dragDelta = 0;
            }
            else
            {
                _drag = DragMode.PendingEmpty;
            }
            e.Handled = true;
            return;
        }

        int row = RowAt(p.Y);
        if (p.Y < RulerHeight || row < 0)
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
            _context.Selection.Select(node, modifiers.HasFlag(ModifierKeys.Control) ? SelectMode.Toggle : SelectMode.Replace, this);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        bool moved = (p - _dragStart).Length > DragThreshold;
        switch (_drag)
        {
            case DragMode.Scrub:
                SeekTo(p.X);
                break;
            case DragMode.PendingKey when moved:
                _drag = DragMode.Keys;
                _duplicate = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                _toggleOffOnClick = [];
                _context.Playback.Pause();
                goto case DragMode.Keys;
            case DragMode.Keys:
                int delta = (int)Math.Round((p.X - _dragStart.X) / _pixelsPerFrame);
                delta = KeyEditing.ClampDelta(_context.KeySelection.Items, delta);
                if (delta != _dragDelta)
                {
                    _dragDelta = delta;
                    InvalidateVisual();
                }
                break;
            case DragMode.PendingEmpty when moved:
                _drag = DragMode.Band;
                InvalidateVisual();
                break;
            case DragMode.Band:
                _bandEnd = p;
                InvalidateVisual();
                break;
        }
        if (_drag == DragMode.Band)
        {
            _bandEnd = p;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Point p = e.GetPosition(this);
        ModifierKeys modifiers = Keyboard.Modifiers;
        switch (_drag)
        {
            case DragMode.Keys:
                KeyCommands.Move(_context, _dragDelta, _duplicate);
                break;
            case DragMode.PendingKey when _toggleOffOnClick.Length > 0:
                _context.KeySelection.Remove(_toggleOffOnClick);
                break;
            case DragMode.PendingEmpty:
                // 空いている所のクリック: キーの選択を外し、そのフレームへ動かす
                if (!modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Shift))
                {
                    _context.KeySelection.Clear();
                }
                _context.Playback.Pause();
                SeekTo(p.X);
                break;
            case DragMode.Band:
                SelectInBand(modifiers);
                break;
        }
        bool wasDragging = _drag != DragMode.None;
        _drag = DragMode.None;
        _dragDelta = 0;
        _toggleOffOnClick = [];
        if (wasDragging)
        {
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragMode.None)
        {
            _drag = DragMode.None; // ほかの窓に移ったときなどは、動かさずにやめる
            _dragDelta = 0;
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Point p = e.GetPosition(this);
        if (p.X < NameColumnWidth || p.Y < RulerHeight)
        {
            return;
        }
        if (HitKeys(p) is { Count: > 0 } hit && !hit.All(_context.KeySelection.Contains))
        {
            _context.KeySelection.Set(hit);
        }
        int frame = Math.Max(0, (int)Math.Round(XToFrame(p.X)));
        bool hasKeys = KeyCommands.HasSelection(_context);
        var menu = new System.Windows.Controls.ContextMenu();
        var interpolation = new System.Windows.Controls.MenuItem { Header = "補間", IsEnabled = hasKeys };
        interpolation.Items.Add(MenuItem("線形（一定の速さ）", null, () => KeyCommands.SetInterpolation(_context, InterpolationPreset.Linear), hasKeys));
        interpolation.Items.Add(MenuItem("なめらか（ゆっくり動き出して止まる）", null, () => KeyCommands.SetInterpolation(_context, InterpolationPreset.Smooth), hasKeys));
        interpolation.Items.Add(MenuItem("段階（次のキーまで変えない）", null, () => KeyCommands.SetInterpolation(_context, InterpolationPreset.Step), hasKeys));
        menu.Items.Add(interpolation);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("キーをコピー", "Ctrl+C", () => KeyCommands.Copy(_context), hasKeys));
        menu.Items.Add(MenuItem($"フレーム {frame} に貼り付け", null, () => KeyCommands.Paste(_context, frame), KeyCommands.CanPaste(_context)));
        menu.Items.Add(MenuItem("今のフレームに貼り付け", "Ctrl+V", () => KeyCommands.Paste(_context, _context.Playback.CurrentFrame), KeyCommands.CanPaste(_context)));
        menu.Items.Add(MenuItem("キーを削除", "Delete", () => KeyCommands.Delete(_context), hasKeys));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem($"フレーム {frame} へ移動", null, () =>
        {
            _context.Playback.Pause();
            _context.Playback.Seek(frame);
        }, true));
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, string? gesture, Action action, bool enabled)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private int RowAt(double y)
    {
        int row = (int)Math.Floor((y - RulerHeight + _scrollY) / RowHeight);
        return y < RulerHeight || row < 0 || row >= _rows.Count ? -1 : row;
    }

    // マウスの下のキー（まとまりの行なら、そのフレームの中のキーすべて）
    private IReadOnlyList<KeyRef>? HitKeys(Point p)
    {
        int row = RowAt(p.Y);
        if (row < 0)
        {
            return null;
        }
        SceneNode node = _rows[row].Node;
        int low = (int)Math.Floor(XToFrame(p.X - KeyHitTolerance));
        int high = (int)Math.Ceiling(XToFrame(p.X + KeyHitTolerance));
        double best = double.MaxValue;
        int bestFrame = -1;
        foreach (int frame in FramesOf(node, Math.Max(0, low), high))
        {
            double distance = Math.Abs(FrameToX(frame) - p.X);
            if (distance <= KeyHitTolerance && distance < best)
            {
                best = distance;
                bestFrame = frame;
            }
        }
        if (bestFrame < 0)
        {
            return null;
        }
        return KeyEditing.HasKeys(node) ? [new KeyRef(node, bestFrame)] : KeyEditing.AtFrame(node, bestFrame);
    }

    private IEnumerable<int> FramesOf(SceneNode node, int first, int last)
    {
        switch (node)
        {
            case BoneNode bone:
                var (bs, be) = bone.Keys.Range(first, last);
                for (int i = bs; i < be; i++)
                {
                    yield return bone.Keys.Keys[i].Frame;
                }
                break;
            case MorphNode morph:
                var (ms, me) = morph.Keys.Range(first, last);
                for (int i = ms; i < me; i++)
                {
                    yield return morph.Keys.Keys[i].Frame;
                }
                break;
            case CameraNode camera:
                var (cs, ce) = camera.Keys.Range(first, last);
                for (int i = cs; i < ce; i++)
                {
                    yield return camera.Keys.Keys[i].Frame;
                }
                break;
            case LightNode light:
                var (ls, le) = light.Keys.Range(first, last);
                for (int i = ls; i < le; i++)
                {
                    yield return light.Keys.Keys[i].Frame;
                }
                break;
            case ModelDisplayNode display:
                var (ds, de) = display.Keys.Range(first, last);
                for (int i = ds; i < de; i++)
                {
                    yield return display.Keys.Keys[i].Frame;
                }
                break;
            case EffectsNode effects:
                var (es, ee) = effects.Keys.Range(first, last);
                for (int i = es; i < ee; i++)
                {
                    yield return effects.Keys.Keys[i].Frame;
                }
                break;
            case SelfShadowNode shadow:
                var (ss, se) = shadow.Keys.Range(first, last);
                for (int i = ss; i < se; i++)
                {
                    yield return shadow.Keys.Keys[i].Frame;
                }
                break;
            default:
                int[] frames = SummaryFrames(node);
                int start = Array.BinarySearch(frames, first);
                for (int k = start < 0 ? ~start : start; k < frames.Length && frames[k] <= last; k++)
                {
                    yield return frames[k];
                }
                break;
        }
    }

    private Point _bandEnd;

    private Rect BandRect() => new(_dragStart, _bandEnd);

    private void SelectInBand(ModifierKeys modifiers)
    {
        Rect band = BandRect();
        int firstRow = RowAt(Math.Max(band.Top, RulerHeight));
        int lastRow = RowAt(Math.Min(band.Bottom, RulerHeight + (_rows.Count * RowHeight) - _scrollY - 1));
        var keys = new List<KeyRef>();
        if (firstRow >= 0 && lastRow >= firstRow)
        {
            int firstFrame = Math.Max(0, (int)Math.Ceiling(XToFrame(band.Left)));
            int lastFrame = (int)Math.Floor(XToFrame(band.Right));
            keys.AddRange(KeyEditing.InRange(_rows.Skip(firstRow).Take(lastRow - firstRow + 1).Select(r => r.Node), firstFrame, lastFrame));
        }
        if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift))
        {
            _context.KeySelection.Add(keys);
        }
        else
        {
            _context.KeySelection.Set(keys);
        }
    }

    // まとまりの行で「選ばれている◆」を描くため、選んだキーのフレームを、その項目と親のまとまりごとにまとめておく
    private void RebuildSelectedFrames()
    {
        _selectedFrames.Clear();
        foreach (KeyRef key in _context.KeySelection.Items)
        {
            for (SceneNode? n = key.Node; n is not null; n = n.Parent)
            {
                if (!_selectedFrames.TryGetValue(n, out HashSet<int>? frames))
                {
                    _selectedFrames[n] = frames = [];
                }
                frames.Add(key.Frame);
            }
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

    private void DrawKeys(DrawingContext dc, SceneNode node, double centerY, int firstFrame, int lastFrame)
    {
        int current = _context.Playback.CurrentFrame;
        bool leaf = KeyEditing.HasKeys(node);
        double size = leaf ? 4.5 : 3.5;
        Brush normal = leaf ? _key : _summaryKey;
        _selectedFrames.TryGetValue(node, out HashSet<int>? selected);
        bool moving = _drag == DragMode.Keys && selected is not null;
        double lastX = double.NegativeInfinity;

        int margin = moving ? Math.Abs(_dragDelta) : 0;
        foreach (int frame in FramesOf(node, Math.Max(0, firstFrame - margin), lastFrame + margin))
        {
            bool isSelected = selected?.Contains(frame) == true;
            if (isSelected && moving)
            {
                // 動かしている間は、元の位置を薄く（複製なら普通に）描き、動かす先に選択の色で描く
                Diamond(dc, frame, centerY, size, _duplicate ? normal : _movingKey);
                Diamond(dc, frame + _dragDelta, centerY, size + 0.5, _selectedKey);
                continue;
            }
            double x = FrameToX(frame);
            if (x - lastX < 1.5 && frame != current && !isSelected)
            {
                continue; // 縮めて表示していて重なるときは描かない
            }
            lastX = x;
            Brush brush = isSelected ? _selectedKey : frame == current ? _accent : normal;
            Diamond(dc, frame, centerY, isSelected ? size + 0.5 : size, brush);
        }
    }

    private void Diamond(DrawingContext dc, int frame, double centerY, double size, Brush brush)
    {
        double x = FrameToX(frame);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(x, centerY - size), true, true);
            g.LineTo(new Point(x + size, centerY), false, false);
            g.LineTo(new Point(x, centerY + size), false, false);
            g.LineTo(new Point(x - size, centerY), false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(brush, _keyOutline, geometry);
    }

    // まとまり（モデル・表示枠など）の中にあるキーのフレームの一覧。キーが変わるまで使い回す
    private int[] SummaryFrames(SceneNode node)
    {
        if (_summaryCache.TryGetValue(node, out int[]? cached))
        {
            return cached;
        }
        var frames = new SortedSet<int>();
        foreach (SceneNode child in node.DescendantsAndSelf())
        {
            switch (child)
            {
                case BoneNode bone:
                    foreach (BoneKey key in bone.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
                case MorphNode morph:
                    foreach (MorphKey key in morph.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
                case CameraNode camera:
                    foreach (CameraKey key in camera.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
                case LightNode light:
                    foreach (LightKey key in light.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
                case ModelDisplayNode display:
                    foreach (DisplayKey key in display.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
                case EffectsNode effects:
                    foreach (EffectKey key in effects.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
                case SelfShadowNode shadow:
                    foreach (SelfShadowKey key in shadow.Keys.Keys)
                    {
                        frames.Add(key.Frame);
                    }
                    break;
            }
        }
        int[] result = [.. frames];
        _summaryCache[node] = result;
        return result;
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

    private void SeekTo(double x) => _context.Playback.Seek(Math.Max(0, (int)Math.Round(XToFrame(x))));

    // 音声の波形: 鳴る範囲を薄く塗り（fill があれば）、1 ピクセルごとにいちばん大きな振れ幅を縦線で描く
    private void DrawWaveform(DrawingContext dc, AudioNode audio, double top, double bandHeight, double width, Brush? fill, Pen pen)
    {
        int fps = _context.Playback.FrameRate;
        double startX = Math.Max(NameColumnWidth, FrameToX(audio.StartFrame));
        double endX = Math.Min(width, FrameToX(audio.StartFrame + (audio.Duration.TotalSeconds * fps)));
        if (endX <= startX)
        {
            return;
        }
        if (fill is not null)
        {
            dc.DrawRectangle(fill, null, new Rect(startX, top, endX - startX, bandHeight));
        }
        float[]? peaks = _context.Audio.PeaksFor(audio);
        if (peaks is not { Length: > 0 })
        {
            return; // 波形はまだ求めている途中
        }
        double middle = top + (bandHeight / 2);
        double half = (bandHeight / 2) - 1;
        double peaksPerFrame = Media.AudioClip.PeaksPerSecond / (double)fps;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            for (double x = Math.Floor(startX); x < endX; x++)
            {
                int a = (int)Math.Floor((XToFrame(x) - audio.StartFrame) * peaksPerFrame);
                int b = (int)Math.Ceiling((XToFrame(x + 1) - audio.StartFrame) * peaksPerFrame);
                a = Math.Clamp(a, 0, peaks.Length - 1);
                b = Math.Clamp(Math.Max(b, a + 1), 1, peaks.Length);
                float peak = 0;
                for (int k = a; k < b; k++)
                {
                    peak = Math.Max(peak, peaks[k]);
                }
                double h = Math.Max(0.5, peak * half);
                g.BeginFigure(new Point(x + 0.5, middle - h), false, false);
                g.LineTo(new Point(x + 0.5, middle + h), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

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
