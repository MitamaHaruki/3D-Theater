using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Preview;

/// <summary>
/// プレビューの 1 区画。独自の視点（<see cref="ViewCamera"/>）を持ち、選択・再生位置・シーンはほかの区画と共有する。
/// 操作: 中ボタンドラッグ = 回転、Shift + 中ボタンドラッグ = 平行移動、ホイール = ズーム、
/// 左クリック = 選択（Ctrl で追加・解除）、ダブルクリック = 選択した物に合わせる、右クリック = メニュー。
/// 選んだモデルのボーンは線で重ねて描き、線の近くをクリックするとボーンを選ぶ。
/// </summary>
public partial class ViewportControl : UserControl, IDisposable
{
    private const double ClickSelectTolerance = 4;
    private const float BonePickTolerance = 6;

    private readonly EditorContext _context;
    private readonly ViewportSurface _surface;
    private readonly List<DrawItem> _drawItems = [];
    private readonly LineBuffer _lines = new();
    private readonly GizmoController _gizmo;
    private DragMode _drag;
    private Point _lastMouse;
    private bool _showGrid = true;
    private bool _showBones = true;
    private bool _isActiveView;
    private bool _isMaximized;

    public ViewportControl(EditorContext context, ViewKind kind)
    {
        InitializeComponent();
        _context = context;
        Camera = new ViewCamera(kind);
        _surface = new ViewportSurface(context, BuildFrame);
        _gizmo = new GizmoController(context);
        SurfaceHost.Child = _surface;

        Camera.Changed += (_, _) => OnCameraChanged();
        _surface.MouseDown += OnSurfaceMouseDown;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseUp += OnSurfaceMouseUp;
        _surface.MouseWheel += OnSurfaceMouseWheel;
        _surface.LostMouseCapture += (_, _) =>
        {
            // ほかの窓に移るなどでマウスを離したときは、そこまでの動きを 1 つの操作として残す
            if (_drag == DragMode.Gizmo)
            {
                _gizmo.EndDrag();
            }
            _drag = DragMode.None;
        };
        _surface.MouseLeave += (_, _) =>
        {
            if (_drag == DragMode.None)
            {
                _gizmo.ClearHover();
                Redraw();
            }
        };
        UpdateHeader();
    }

    private enum DragMode
    {
        None,
        Gizmo,
        Orbit,
        Pan,
    }

    /// <summary>区画をクリックなどで操作した。ショートカットの対象にする。</summary>
    public event EventHandler? Activated;

    public event EventHandler? MaximizeToggled;

    public ViewCamera Camera { get; }

    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            _showGrid = value;
            Redraw();
        }
    }

    /// <summary>選んだモデルのボーンを線で重ねるか。</summary>
    public bool ShowBones
    {
        get => _showBones;
        set
        {
            _showBones = value;
            Redraw();
        }
    }

    public bool IsActiveView
    {
        get => _isActiveView;
        set
        {
            _isActiveView = value;
            Frame.BorderBrush = (System.Windows.Media.Brush)FindResource(value ? "AccentBrush" : "BorderBrush");
        }
    }

    public bool IsMaximized
    {
        get => _isMaximized;
        set
        {
            _isMaximized = value;
            MaximizeButton.Content = value ? "" : "";
            MaximizeButton.ToolTip = value ? "元の分け方に戻す" : "この区画だけを大きく表示する";
        }
    }

    public void Dispose() => _surface.Dispose();

    /// <summary>ウィンドウ全体のショートカットのうち、視点に関わるものを処理する。</summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        if (key == Key.Escape && _drag == DragMode.Gizmo)
        {
            _gizmo.CancelDrag();
            _drag = DragMode.None;
            _surface.ReleaseMouseCapture();
            Redraw();
            return true;
        }
        switch (key)
        {
            case Key.NumPad1:
                Camera.SetKind(ctrl ? ViewKind.Back : ViewKind.Front);
                return true;
            case Key.NumPad3:
                Camera.SetKind(ctrl ? ViewKind.Left : ViewKind.Right);
                return true;
            case Key.NumPad7:
                Camera.SetKind(ctrl ? ViewKind.Bottom : ViewKind.Top);
                return true;
            case Key.NumPad0:
                Camera.SetKind(ViewKind.Camera);
                return true;
            case Key.NumPad5:
                Camera.ToggleProjection();
                return true;
            case Key.Decimal:
                FocusSelection();
                return true;
            case Key.Home:
                Camera.Reset();
                return true;
            default:
                return false;
        }
    }

    public void FocusSelection()
    {
        SceneNode? node = _context.Selection.Primary;
        if (node is null || !node.HasTransform)
        {
            return;
        }
        (Vector3 center, float radius) = node switch
        {
            // ボーンは今の姿勢での位置に、モデルは今の姿勢を囲む球に合わせる
            BoneNode bone when bone.Model is { } model && _context.GetLoaded(model) is { } loaded => (
                Vector3.Transform(loaded.State.BonePosition(bone.Index), model.GetLocalMatrix()),
                Math.Max(0.05f, loaded.DisplaySize * 0.12f)),
            ModelNode model when _context.GetLoaded(model) is { } loaded => (
                Vector3.Transform((loaded.DisplayBoundsMin + loaded.DisplayBoundsMax) * 0.5f, model.GetLocalMatrix()),
                Math.Max(0.01f, loaded.DisplaySize * 0.5f * MaxScale(model))),
            _ => SceneEvaluator.BoundingSphere(node),
        };
        Camera.Focus(center, radius, _context.MainCamera);
    }

    /// <summary>右クリックと見出しのボタンで出すメニュー。</summary>
    public ContextMenu CreateViewMenu()
    {
        var menu = new ContextMenu();
        foreach (ViewKind kind in ViewKinds.All)
        {
            var item = new MenuItem
            {
                Header = kind.DisplayName(),
                IsCheckable = true,
                IsChecked = Camera.Kind == kind,
                InputGestureText = kind.ShortcutText() ?? string.Empty,
            };
            item.Click += (_, _) => Camera.SetKind(kind);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("透視投影と平行投影を切り替える", "Num5", Camera.ToggleProjection, Camera.Kind != ViewKind.Camera));
        menu.Items.Add(CreateMenuItem("選択した物に合わせる", "Num . / ダブルクリック", FocusSelection, _context.Selection.Primary?.HasTransform == true));
        menu.Items.Add(CreateMenuItem("視点を初期状態に戻す", "Home", Camera.Reset, true));
        var grid = new MenuItem { Header = "グリッドを表示", IsCheckable = true, IsChecked = ShowGrid };
        grid.Click += (_, _) => ShowGrid = grid.IsChecked;
        menu.Items.Add(grid);
        var bones = new MenuItem { Header = "選んだモデルのボーンを表示", IsCheckable = true, IsChecked = ShowBones };
        bones.Click += (_, _) => ShowBones = bones.IsChecked;
        menu.Items.Add(bones);
        return menu;
    }

    private static MenuItem CreateMenuItem(string header, string gesture, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private FrameParameters BuildFrame(int pixelWidth, int pixelHeight)
    {
        ViewMatrices matrices = Camera.GetMatrices(pixelWidth / (float)pixelHeight, _context.MainCamera);
        Axes.SetBasis(matrices.Basis);

        FrameFlags flags = FrameFlags.None;
        GridPlane plane = Camera.Kind.GridPlaneFor();
        bool gridVisible = ShowGrid && (_context.StageGrid?.IsEffectivelyVisible ?? true);
        if (gridVisible)
        {
            // 固定視点（正面・側面・上面など）の格子は画面に平行でモデルを貫くので、モデルの後ろに描く。自由視点・カメラ視点は床として奥行きどおりに描く
            if (Camera.Kind.IsFixed())
            {
                flags |= FrameFlags.GridBehind;
            }
            flags |= FrameFlags.ShowGrid | plane switch
            {
                GridPlane.XY => FrameFlags.ShowAxisX | FrameFlags.ShowAxisY,
                GridPlane.YZ => FrameFlags.ShowAxisY | FrameFlags.ShowAxisZ,
                _ => FrameFlags.ShowAxisX | FrameFlags.ShowAxisZ,
            };
        }

        Matrix4x4 testWorld = Matrix4x4.Identity;
        if (_context.TestObject is { } testObject && testObject.IsEffectivelyVisible)
        {
            flags |= FrameFlags.ShowTestObject;
            if (_context.Selection.Contains(testObject))
            {
                flags |= FrameFlags.TestObjectSelected;
            }
            testWorld = SceneEvaluator.TestObjectWorld(testObject, _context.Playback.CurrentFrame);
        }

        Matrix4x4 gridWorld = plane switch
        {
            GridPlane.XY => Matrix4x4.CreateRotationX(-MathF.PI / 2f),
            GridPlane.YZ => Matrix4x4.CreateRotationZ(MathF.PI / 2f),
            _ => Matrix4x4.Identity,
        };

        // 読み込んだモデル（シーン一覧の並び順。ボーンとモーフで変形した形は描画の前に GPU へ送ってある）
        _drawItems.Clear();
        foreach (var (node, model) in _context.VisibleModels)
        {
            DrawFlags drawFlags = _context.Selection.Contains(node) ? DrawFlags.Selected : DrawFlags.None;
            _drawItems.Add(new DrawItem(model.Mesh!, node.GetLocalMatrix(), drawFlags));
        }
        // 重ね描き: ボーンの線、その上にギズモ（どちらも書き出しには映らない）
        _lines.Clear();
        if (ShowBones)
        {
            BoneOverlay.AppendTo(_lines, _context);
        }
        LightOverlay.AppendTo(_lines, _context);
        _gizmo.Append(_lines, new GizmoController.ViewInfo(matrices.View, matrices.Projection, new Vector2(pixelWidth, pixelHeight) / DpiScale()));
        // 照明はシーンの照明の項目から、輪郭線の太さは画面の高さから決める（どちらもトゥーン描画で使う）
        var (lightDirection, lightColor) = SceneLighting.From(_context.MainLight);
        flags |= FrameFlags.UseLight;
        if (_context.Rendering.SelfShadow)
        {
            flags |= FrameFlags.SelfShadow;
        }
        if (_context.Rendering.GroundShadow)
        {
            flags |= FrameFlags.GroundShadow;
        }
        FloorReflection reflection = _context.Rendering.Reflection;
        if (reflection.Enabled)
        {
            flags |= FrameFlags.FloorReflection;
        }
        return new FrameParameters(flags, matrices.View, matrices.Projection, gridWorld, testWorld, _drawItems, _lines.Memory,
            lightDirection, lightColor, SceneLighting.EdgePixelScale(pixelHeight), RenderSettings.GroundShadowColor,
            ShadowMapSize: _context.PreviewQuality.Profile.ShadowMapSize, Effects: _context.Rendering.ToNative(pixelHeight, Camera.Kind == ViewKind.Camera ? _context.MainCamera : null),
            Lights: EffectsMapping.LightsOf(_context.Scene), ReflectionStrength: reflection.Strength, ReflectionFade: reflection.Fade);
    }

    private static float MaxScale(SceneNode node) => Math.Max(Math.Abs(node.Scale.X), Math.Max(Math.Abs(node.Scale.Y), Math.Abs(node.Scale.Z)));

    private bool _cancelledByRightClick;

    /// <summary>ギズモの計算に使う、今の区画の見え方（大きさは WPF の単位）。</summary>
    private GizmoController.ViewInfo CurrentView()
    {
        var size = new Vector2((float)Math.Max(1, _surface.ActualWidth), (float)Math.Max(1, _surface.ActualHeight));
        ViewMatrices matrices = Camera.GetMatrices(size.X / size.Y, _context.MainCamera);
        return new GizmoController.ViewInfo(matrices.View, matrices.Projection, size);
    }

    private float DpiScale() => (float)System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnCameraChanged()
    {
        UpdateHeader();
        Redraw();
    }

    private void Redraw()
    {
        _surface.Invalidate();
        _context.RenderLoop.RequestRender();
    }

    private void UpdateHeader()
    {
        ViewNameText.Text = Camera.Kind.DisplayName();
        ProjectionText.Text = Camera.Kind == ViewKind.Camera
            ? _context.MainCamera?.Name ?? string.Empty
            : Camera.IsOrthographic ? "平行投影" : "透視投影";
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Activated?.Invoke(this, EventArgs.Empty);
        _lastMouse = e.GetPosition(_surface);

        switch (e.ChangedButton)
        {
            case MouseButton.Middle:
                // 視点の操作は再生中でもできる（指示書 3.4）
                _drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragMode.Pan : DragMode.Orbit;
                _surface.CaptureMouse();
                e.Handled = true;
                break;
            case MouseButton.Left when _gizmo.TryBeginDrag(_lastMouse, CurrentView()):
                // ギズモの取っ手をつかんだときは、選択より先にギズモを動かす
                _drag = DragMode.Gizmo;
                _surface.CaptureMouse();
                e.Handled = true;
                break;
            case MouseButton.Left when e.ClickCount == 2:
                FocusSelection();
                e.Handled = true;
                break;
            case MouseButton.Left:
                SelectAt(_lastMouse, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
            case MouseButton.Right when _drag == DragMode.Gizmo:
                // ドラッグ中の右クリックは取り消し
                _gizmo.CancelDrag();
                _drag = DragMode.None;
                _surface.ReleaseMouseCapture();
                _cancelledByRightClick = true;
                Redraw();
                e.Handled = true;
                break;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.Gizmo)
        {
            _gizmo.Move(e.GetPosition(_surface), CurrentView());
            return;
        }
        if (_drag == DragMode.None)
        {
            if (_gizmo.UpdateHover(e.GetPosition(_surface), CurrentView()))
            {
                Redraw();
            }
            return;
        }
        Point position = e.GetPosition(_surface);
        var delta = new Vector2((float)(position.X - _lastMouse.X), (float)(position.Y - _lastMouse.Y));
        _lastMouse = position;
        if (_drag == DragMode.Orbit)
        {
            Camera.Orbit(delta, _context.MainCamera);
        }
        else
        {
            Camera.Pan(delta, (float)_surface.ActualHeight, _context.MainCamera);
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _drag == DragMode.Gizmo)
        {
            _gizmo.EndDrag();
            _drag = DragMode.None;
            _surface.ReleaseMouseCapture();
            Redraw();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right && _cancelledByRightClick)
        {
            _cancelledByRightClick = false; // 取り消しに使った右クリックでは、メニューを出さない
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle && _drag != DragMode.None)
        {
            _drag = DragMode.None;
            _surface.ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            Point position = e.GetPosition(_surface);
            if ((position - _lastMouse).Length <= ClickSelectTolerance)
            {
                ContextMenu menu = CreateViewMenu();
                menu.PlacementTarget = _surface;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        Camera.Zoom(e.Delta / 120f, _context.MainCamera);
        e.Handled = true;
    }

    private void SelectAt(Point point, bool toggle)
    {
        var size = new Vector2((float)_surface.ActualWidth, (float)_surface.ActualHeight);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }
        ViewMatrices matrices = Camera.GetMatrices(size.X / size.Y, _context.MainCamera);
        Ray ray = Picking.CreateRay(new Vector2((float)point.X, (float)point.Y), size, matrices.View, matrices.Projection);

        // 視線に当たったもののうち、いちばん手前を選ぶ
        SceneNode? nearest = null;
        float nearestDistance = float.PositiveInfinity;
        void Consider(SceneNode node, float? distance)
        {
            if (distance is { } d && d < nearestDistance)
            {
                nearest = node;
                nearestDistance = d;
            }
        }

        // 表示しているボーンの線の近くなら、モデルより先にボーンを選ぶ
        if (ShowBones)
        {
            DisplayedBone[] bones = [.. BoneOverlay.Enumerate(_context)];
            (Vector3, Vector3)[] segments = [.. bones.Select(b => (b.Head, b.HasLength ? b.Tail : b.Head))];
            int picked = Picking.PickSegment(new Vector2((float)point.X, (float)point.Y), size, matrices.View * matrices.Projection, segments, BonePickTolerance);
            if (picked >= 0)
            {
                _context.Selection.Select(bones[picked].Node, toggle ? SelectMode.Toggle : SelectMode.Replace, this);
                return;
            }
        }

        if (_context.TestObject is { IsEffectivelyVisible: true } testObject)
        {
            Consider(testObject, Picking.IntersectUnitCube(ray, SceneEvaluator.TestObjectWorld(testObject, _context.Playback.CurrentFrame)));
        }
        _context.Poses.UpdateDirtyModels(); // 当たり判定は今の姿勢の形で行う
        foreach (var (node, model) in _context.VisibleModels)
        {
            Consider(node, Picking.IntersectTriangles(ray, node.GetLocalMatrix(), model.Positions, model.Indices, model.BoundsMin, model.BoundsMax));
        }

        if (nearest is not null)
        {
            _context.Selection.Select(nearest, toggle ? SelectMode.Toggle : SelectMode.Replace, this);
        }
        else if (!toggle)
        {
            _context.Selection.Clear(this);
        }
    }

    private void OnViewMenuClick(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        ContextMenu menu = CreateViewMenu();
        menu.PlacementTarget = ViewMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        MaximizeToggled?.Invoke(this, EventArgs.Empty);
    }
}
