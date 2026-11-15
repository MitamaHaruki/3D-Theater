using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private const float LightPickTolerance = 6;

    private readonly EditorContext _context;
    private readonly ViewportSurface _surface;
    private readonly List<DrawItem> _drawItems = [];
    private readonly LineBuffer _lines = new();
    private readonly GizmoController _gizmo;
    private DragMode _drag;
    private Shell.IViewportTool? _dragTool;
    private Shell.ViewportPointer _lastToolPointer;
    private Point _lastMouse;
    private bool _showGrid = true;
    private bool _showBones = true;
    private bool _isActiveView;
    private bool _isMaximized;
    private bool _liveLook;

    // ライブモードのカメラの動かし方（右ボタンの間。設計判断記録 78 章の 3 段目）
    private int _flySession;   // 右ボタンを押してから離すまでを、1 つの「元に戻す」にまとめる
    private Point _flyAnchor;  // 向きを変えた後にマウスを戻す所（画面の端で止まらないように）
    private long _flyLastTick;

    public ViewportControl(EditorContext context, ViewKind kind)
    {
        InitializeComponent();
        _context = context;
        Camera = new ViewCamera(kind);
        _surface = new ViewportSurface(context, BuildFrame);
        _gizmo = new GizmoController(context);
        SurfaceHost.Child = _surface;

        Camera.Changed += (_, _) => OnCameraChanged();
        SizeChanged += (_, _) => UpdateExportFrame();
        Loaded += (_, _) =>
        {
            context.Scene.NodeChanged += OnSceneNodeChanged;
            context.ExportSizeChanged += OnExportSizeChanged;
            context.Tools.ActiveChanged += OnToolChanged;
            context.Tools.DragCancelRequested += OnToolDragCancelRequested;
        };
        Unloaded += (_, _) =>
        {
            context.Scene.NodeChanged -= OnSceneNodeChanged;
            context.ExportSizeChanged -= OnExportSizeChanged;
            context.Tools.ActiveChanged -= OnToolChanged;
            context.Tools.DragCancelRequested -= OnToolDragCancelRequested;
        };
        _surface.MouseDown += OnSurfaceMouseDown;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseUp += OnSurfaceMouseUp;
        _surface.MouseWheel += OnSurfaceMouseWheel;
        _surface.LostMouseCapture += (_, _) =>
        {
            if (_drag == DragMode.Fly)
            {
                EndFly(); // ほかの窓に移ったなど
                return;
            }
            // ほかの窓に移るなどでマウスを離したときは、そこまでの動きを 1 つの操作として残す
            if (_drag == DragMode.Gizmo)
            {
                _gizmo.EndDrag();
            }
            else if (_drag == DragMode.Tool)
            {
                EndToolDrag(release: true);
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

        /// <summary>プラグインの編集の道具のドラッグ。</summary>
        Tool,

        /// <summary>ライブモード: 右ボタンを押している間（マウスで向き・キーで移動）。</summary>
        Fly,

        /// <summary>ライブモード: 中ボタンのドラッグで、カメラを上下左右にずらす。</summary>
        CameraPan,
    }

    /// <summary>ライブモードで、右ボタンを押してカメラを動かしている最中（W・A・S・D・Q・E をギズモの切り替えに使わない）。</summary>
    public bool IsFlying => _drag == DragMode.Fly;

    // ライブモードのカメラ視点（マウスで、見ているカメラそのものを動かす）
    private bool DrivesLiveCamera => _liveLook && Camera.Kind == ViewKind.Camera && _context.ViewCamera is not null;

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
            if (!_liveLook) // ライブは区画が 1 つなので、選んでいる印の枠は要らない
            {
                Frame.BorderBrush = (System.Windows.Media.Brush)FindResource(value ? "AccentBrush" : "BorderBrush");
            }
        }
    }

    /// <summary>
    /// ライブモードの見た目にする（設計判断記録 78 章）: 見出し（視点の切り替え・大きく表示）・軸の印・枠の線を出さない。
    /// ライブの画面は、プレビューの上にサイドバーなどを重ねるので、その下に隠れる物を置かない。
    /// </summary>
    public void UseLiveLook()
    {
        _liveLook = true;
        HeaderBar.Visibility = Visibility.Collapsed;
        Axes.Visibility = Visibility.Collapsed;
        Frame.BorderThickness = new Thickness(0);
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
        if (_liveLook && _drag is not (DragMode.Tool or DragMode.Gizmo))
        {
            return false; // ライブはいつもカメラの視点（テンキーで視点を切り替えない）
        }
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        if (key == Key.Escape && _drag == DragMode.Tool)
        {
            CancelToolDrag();
            return true;
        }
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
        if (_liveLook)
        {
            return; // ライブはカメラの視点のまま（合わせると自由視点に移ってしまう）
        }
        if (_context.Selection.Primary is { } node && Bounds(node) is { } bounds)
        {
            Camera.Focus(bounds.Center, bounds.Radius, _context.ViewCamera); // カメラ視点なら、自由視点に移ってから合わせる
        }
    }

    /// <summary>
    /// 項目に視点を合わせる（選択は変えない）。カメラ視点の区画はカメラ視点のまま、後でほかの視点に切り替えたときの写り方だけを合わせる
    /// （シーンのカメラは動かさない）。
    /// </summary>
    public void FocusOn(SceneNode node)
    {
        if (Bounds(node) is not { } bounds)
        {
            return;
        }
        if (Camera.Kind == ViewKind.Camera)
        {
            Camera.SetFraming(bounds.Center, bounds.Radius);
        }
        else
        {
            Camera.Focus(bounds.Center, bounds.Radius, _context.ViewCamera);
        }
    }

    // 項目を囲む球（位置の無い項目なら null）
    private (Vector3 Center, float Radius)? Bounds(SceneNode node)
    {
        if (!node.HasTransform)
        {
            return null;
        }
        return node switch
        {
            // ボーンは今の姿勢での位置に、モデルは今の姿勢を囲む球に合わせる
            BoneNode bone when bone.Model is { } model && _context.GetLoaded(model) is { } loaded => (
                Vector3.Transform(loaded.State.BonePosition(bone.Index), model.WorldMatrix),
                Math.Max(0.05f, loaded.DisplaySize * 0.12f)),
            ModelNode model when _context.GetLoaded(model) is { } loaded => (
                Vector3.Transform((loaded.DisplayBoundsMin + loaded.DisplayBoundsMax) * 0.5f, model.WorldMatrix),
                Math.Max(0.01f, loaded.DisplaySize * 0.5f * ModelAttachments.MaxAxisScale(model.WorldMatrix))),
            _ => SceneEvaluator.BoundingSphere(node),
        };
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
        _context.ContextMenus.AppendTo(menu, Shell.MenuPlace.Preview);
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
        ViewMatrices matrices = Camera.GetMatrices(pixelWidth / (float)pixelHeight, _context.ViewCamera, _context.ExportAspect);
        Axes.SetBasis(matrices.Basis);
        // 輪郭線の太さ・映像効果の大きさは、書き出しと同じ見え方になるよう、カメラ視点では書き出しの枠の高さで決める
        int effectHeight = Camera.Kind == ViewKind.Camera
            ? Math.Max(1, (int)MathF.Round(ViewCamera.ExportFrame(pixelWidth, pixelHeight, _context.ExportAspect).Height))
            : pixelHeight;

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
            _drawItems.Add(new DrawItem(model.Mesh!, node.WorldMatrix, drawFlags));
        }
        // 重ね描き: ボーンの線、その上にギズモ（どちらも書き出しには映らない）
        _lines.Clear();
        if (ShowBones)
        {
            BoneOverlay.AppendTo(_lines, _context);
        }
        LightOverlay.AppendTo(_lines, _context);
        if (_context.Tools.Active is null && GizmoAllowed) // プラグインの道具を選んでいる間は、ギズモを出さない（道具を持ちかえたのと同じ）
        {
            _gizmo.Append(_lines, new GizmoController.ViewInfo(matrices.View, matrices.Projection, new Vector2(pixelWidth, pixelHeight) / DpiScale()));
        }
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
        if ((_context.Rendering.SelfShadow || _context.Rendering.GroundShadow) && _context.Rendering.ShadowStyle.RayTraced)
        {
            flags |= FrameFlags.RayTracedShadows; // セルフシャドウと地面の影の両方
        }
        FloorReflection reflection = _context.Rendering.Reflection;
        if (reflection.Enabled)
        {
            flags |= FrameFlags.FloorReflection;
        }
        SceneEnvironment environment = _context.Rendering.Environment;
        NativeEnvironment? environmentImage = _context.EnvironmentImage.Current;
        if (environmentImage is not null && environment.ShowBackground)
        {
            flags |= FrameFlags.EnvironmentBackground; // 固定の視点（平行投影）には、描画エンジンが描かない
        }
        return new FrameParameters(flags, matrices.View, matrices.Projection, gridWorld, testWorld, _drawItems, _lines.Memory,
            lightDirection, lightColor, SceneLighting.EdgePixelScale(effectHeight), _context.Rendering.ShadowStyle.GroundColor,
            ShadowMapSize: _context.PreviewQuality.Profile.ShadowMapSize, Effects: _context.Rendering.ToNative(effectHeight, Camera.Kind == ViewKind.Camera ? _context.ViewCamera : null),
            Lights: EffectsMapping.LightsOf(_context.Scene), ReflectionStrength: reflection.Strength, ReflectionFade: reflection.Fade,
            ShadowSoftness: _context.Rendering.ShadowStyle.SoftnessTangent, Environment: environmentImage,
            EnvironmentIntensity: environment.Intensity, EnvironmentRotation: environment.RotationRadians, ReflectionBlur: reflection.Blur,
            RaySamples: _context.PreviewQuality.Profile.RaySamples);
    }

    private static float MaxScale(SceneNode node) => Math.Max(Math.Abs(node.Scale.X), Math.Max(Math.Abs(node.Scale.Y), Math.Abs(node.Scale.Z)));

    private bool _cancelledByRightClick;

    /// <summary>ギズモの計算に使う、今の区画の見え方（大きさは WPF の単位）。</summary>
    private GizmoController.ViewInfo CurrentView()
    {
        var size = new Vector2((float)Math.Max(1, _surface.ActualWidth), (float)Math.Max(1, _surface.ActualHeight));
        ViewMatrices matrices = Camera.GetMatrices(size.X / size.Y, _context.ViewCamera, _context.ExportAspect);
        return new GizmoController.ViewInfo(matrices.View, matrices.Projection, size);
    }

    /// <summary>
    /// この区画でギズモを出すか。カメラ視点の区画では、見ているカメラ自身のギズモは出さない（透視投影では目の位置にあって写らず、
    /// 平行投影では画面の真ん中に出て、写したい物に重なるため）。
    /// </summary>
    private bool GizmoAllowed => !(Camera.Kind == ViewKind.Camera && ReferenceEquals(_context.Selection.Primary, _context.ViewCamera));

    private float DpiScale() => (float)System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnCameraChanged()
    {
        UpdateHeader();
        UpdateExportFrame();
        Redraw();
    }

    // カメラの平行投影の切り替え（キーの再生でも変わる）を見出しに出す
    private void OnSceneNodeChanged(object? sender, SceneNodeChangedEventArgs e)
    {
        if (e.Node is CameraNode && e.PropertyName is nameof(CameraNode.Orthographic) or nameof(SceneNode.Name))
        {
            UpdateHeader();
        }
    }

    private void OnExportSizeChanged(object? sender, EventArgs e)
    {
        UpdateHeader();
        UpdateExportFrame();
        Redraw();
    }

    /// <summary>カメラ視点のときだけ、書き出しの枠を出す（枠の外を暗くする）。</summary>
    private void UpdateExportFrame()
    {
        double width = SurfaceHost.ActualWidth;
        double height = SurfaceHost.ActualHeight;
        bool show = Camera.Kind == ViewKind.Camera && width > 0 && height > 0;
        ExportFrameShade.Visibility = ExportFrameLine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }
        var (left, top, frameWidth, frameHeight) = ViewCamera.ExportFrame((float)width, (float)height, _context.ExportAspect);
        var frame = new Rect(left, top, frameWidth, frameHeight);
        ExportFrameShade.Data = new System.Windows.Media.GeometryGroup
        {
            FillRule = System.Windows.Media.FillRule.EvenOdd,
            Children =
            {
                new System.Windows.Media.RectangleGeometry(new Rect(0, 0, width, height)),
                new System.Windows.Media.RectangleGeometry(frame),
            },
        };
        ExportFrameLine.Margin = new Thickness(frame.Left, frame.Top, 0, 0);
        ExportFrameLine.Width = Math.Max(0, frame.Width);
        ExportFrameLine.Height = Math.Max(0, frame.Height);
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
            ? $"{_context.ViewCamera?.Name}{(_context.ViewCamera is { Orthographic: true } ? "・平行投影" : "")}・{(_context.IsLive ? "配信" : "書き出し")} {_context.ExportSize.Width} x {_context.ExportSize.Height} の枠"
            : Camera.IsOrthographic ? "平行投影" : "透視投影";
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Activated?.Invoke(this, EventArgs.Empty);
        _lastMouse = e.GetPosition(_surface);

        if (DrivesLiveCamera && _drag == DragMode.None)
        {
            if (e.ChangedButton == MouseButton.Right)
            {
                BeginFly();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Middle)
            {
                _drag = DragMode.CameraPan;
                _flySession++;
                _surface.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        switch (e.ChangedButton)
        {
            case MouseButton.Middle:
                // 視点の操作は再生中でもできる（指示書 3.4）
                _drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragMode.Pan : DragMode.Orbit;
                _surface.CaptureMouse();
                e.Handled = true;
                break;
            case MouseButton.Left when _context.Tools.Active is { } tool && TryBeginToolDrag(tool, _lastMouse):
                _drag = DragMode.Tool;
                _surface.CaptureMouse();
                e.Handled = true;
                break;
            case MouseButton.Left when _context.Tools.Active is null && GizmoAllowed && _gizmo.TryBeginDrag(_lastMouse, CurrentView()):
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
            case MouseButton.Right when _drag == DragMode.Tool:
                CancelToolDrag();
                _cancelledByRightClick = true;
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
        if (_drag == DragMode.Tool && _dragTool is { } tool)
        {
            _lastToolPointer = ToolPointer(e.GetPosition(_surface));
            if (!tool.Move(_lastToolPointer))
            {
                EndToolDrag(release: false); // 道具がやめた（取り消しは道具の側で済んでいる）
            }
            return;
        }
        if (_drag == DragMode.Gizmo)
        {
            _gizmo.Move(e.GetPosition(_surface), CurrentView());
            return;
        }
        if (_drag == DragMode.Fly)
        {
            Point now = e.GetPosition(_surface);
            System.Windows.Vector turn = now - _flyAnchor;
            if (turn.X != 0 || turn.Y != 0)
            {
                EditLiveCamera(c => c.Rotation = CameraFly.Look(c.Rotation, (float)turn.X, (float)turn.Y));
                WarpCursorToAnchor();
            }
            return;
        }
        if (_drag == DragMode.CameraPan)
        {
            Point now = e.GetPosition(_surface);
            System.Windows.Vector shift = now - _lastMouse;
            _lastMouse = now;
            if (_context.ViewCamera is { } panned)
            {
                // 注視点のあたりで、画面の 1 点ぶんがシーンでどれだけの長さか
                float distance = Math.Max(MathF.Abs(panned.TargetDistance), 10f);
                float unitsPerPixel = 2f * distance * MathF.Tan(panned.FieldOfView * MathF.PI / 360f) / (float)Math.Max(1, _surface.ActualHeight);
                EditLiveCamera(c => c.Position = CameraFly.Pan(c.Position, c.Rotation, (float)shift.X, (float)shift.Y, unitsPerPixel));
            }
            return;
        }
        if (_drag == DragMode.None)
        {
            if (_context.Tools.Active is null && GizmoAllowed && _gizmo.UpdateHover(e.GetPosition(_surface), CurrentView()))
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
            Camera.Orbit(delta, _context.ViewCamera);
        }
        else
        {
            Camera.Pan(delta, (float)_surface.ActualHeight, _context.ViewCamera);
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _drag == DragMode.Tool)
        {
            _lastToolPointer = ToolPointer(e.GetPosition(_surface));
            EndToolDrag(release: true);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _drag == DragMode.Gizmo)
        {
            _gizmo.EndDrag();
            _drag = DragMode.None;
            _surface.ReleaseMouseCapture();
            Redraw();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right && _drag == DragMode.Fly)
        {
            EndFly();
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
        else if (e.ChangedButton == MouseButton.Right && !_liveLook)
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

    // ===== ライブモードのカメラの動かし方（右ボタンの間。設計判断記録 78 章の 3 段目） =====

    private void BeginFly()
    {
        _drag = DragMode.Fly;
        _flySession++;
        _flyAnchor = _lastMouse;
        _flyLastTick = Stopwatch.GetTimestamp();
        _surface.CaptureMouse();
        _surface.Cursor = Cursors.None; // 向きを変えている間はマウスの矢印を隠す
        CompositionTarget.Rendering += OnFlyTick;
    }

    private void EndFly()
    {
        if (_drag != DragMode.Fly)
        {
            return;
        }
        _drag = DragMode.None;
        CompositionTarget.Rendering -= OnFlyTick;
        _surface.Cursor = null;
        if (_surface.IsMouseCaptured)
        {
            _surface.ReleaseMouseCapture();
        }
    }

    // 画面を描くたびに、押しているキーの分だけ動かす（押している時間に比例）
    private void OnFlyTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        float seconds = Math.Min((float)Stopwatch.GetElapsedTime(_flyLastTick, now).TotalSeconds, 0.1f); // 止まった後に飛ばない
        _flyLastTick = now;
        FlyKeys keys = PressedFlyKeys();
        if ((keys & ~FlyKeys.Fast) != FlyKeys.None)
        {
            EditLiveCamera(c => c.Position = CameraFly.Move(c.Position, c.Rotation, keys, seconds));
        }
    }

    private static FlyKeys PressedFlyKeys()
    {
        FlyKeys keys = FlyKeys.None;
        void Add(Key key, FlyKeys flag)
        {
            if (Keyboard.IsKeyDown(key))
            {
                keys |= flag;
            }
        }
        Add(Key.W, FlyKeys.Forward);
        Add(Key.S, FlyKeys.Back);
        Add(Key.A, FlyKeys.Left);
        Add(Key.D, FlyKeys.Right);
        Add(Key.E, FlyKeys.Up);
        Add(Key.Q, FlyKeys.Down);
        Add(Key.LeftShift, FlyKeys.Fast);
        Add(Key.RightShift, FlyKeys.Fast);
        return keys;
    }

    // 見ているカメラを動かす（右ボタン・中ボタンを押してから離すまでを、1 つの「元に戻す」にまとめる）
    private void EditLiveCamera(Action<CameraNode> apply)
    {
        if (_context.ViewCamera is not { CanEdit: true } camera)
        {
            return;
        }
        _context.Edits.Change($"{camera.Name}を動かす", [camera], () => apply(camera), mergeKey: $"fly:{camera.Id}:{_flySession}");
    }

    // マウスを押した所へ戻す（向きを変え続けても、画面の端で止まらないように）。戻した所を、次に比べる位置にする
    private void WarpCursorToAnchor()
    {
        Point screen = _surface.PointToScreen(_flyAnchor);
        int x = (int)Math.Round(screen.X);
        int y = (int)Math.Round(screen.Y);
        if (SetCursorPos(x, y))
        {
            _flyAnchor = _surface.PointFromScreen(new Point(x, y));
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    // プラグインの道具に押したことを渡し、受け取ったらドラッグを始める
    private bool TryBeginToolDrag(Shell.IViewportTool tool, Point point)
    {
        _lastToolPointer = ToolPointer(point);
        if (!tool.Press(_lastToolPointer))
        {
            return false;
        }
        _dragTool = tool;
        _context.Tools.IsDragging = true;
        return true;
    }

    // 道具のドラッグを終える（release なら放したことを道具に渡す。そうでなければ渡さない）
    private void EndToolDrag(bool release)
    {
        Shell.IViewportTool? tool = _dragTool;
        _dragTool = null;
        _drag = DragMode.None;
        _context.Tools.IsDragging = false;
        if (release)
        {
            tool?.Release(_lastToolPointer);
        }
        if (_surface.IsMouseCaptured)
        {
            _surface.ReleaseMouseCapture();
        }
        Redraw();
    }

    private void CancelToolDrag()
    {
        Shell.IViewportTool? tool = _dragTool;
        EndToolDrag(release: false);
        tool?.Cancel();
    }

    private void OnToolDragCancelRequested(object? sender, EventArgs e)
    {
        if (_drag == DragMode.Tool)
        {
            CancelToolDrag();
        }
    }

    private void OnToolChanged(object? sender, EventArgs e)
    {
        _gizmo.ClearHover();
        Redraw(); // ギズモを出す・隠す
    }

    // 道具に渡すマウスの位置と視点（選ぶときと同じ視線）
    private Shell.ViewportPointer ToolPointer(Point point)
    {
        var size = new Vector2((float)Math.Max(1, _surface.ActualWidth), (float)Math.Max(1, _surface.ActualHeight));
        ViewMatrices matrices = Camera.GetMatrices(size.X / size.Y, _context.ViewCamera, _context.ExportAspect);
        Ray ray = Picking.CreateRay(new Vector2((float)point.X, (float)point.Y), size, matrices.View, matrices.Projection);
        return new Shell.ViewportPointer(point, new Size(size.X, size.Y), Keyboard.Modifiers, ray, matrices.View * matrices.Projection);
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (DrivesLiveCamera)
        {
            // ライブ: 見ているカメラの視角を変える（本当のズーム。続けて回した分は 1 つの「元に戻す」）
            if (_context.ViewCamera is { Orthographic: false, CanEdit: true } zoomed)
            {
                _context.Edits.Change($"{zoomed.Name}のズーム", [zoomed], () => zoomed.FieldOfView = CameraFly.Zoom(zoomed.FieldOfView, e.Delta),
                    mergeKey: $"zoom:{zoomed.Id}");
            }
            e.Handled = true;
            return;
        }
        Camera.Zoom(e.Delta / 120f, _context.ViewCamera);
        e.Handled = true;
    }

    private void SelectAt(Point point, bool toggle)
    {
        var size = new Vector2((float)_surface.ActualWidth, (float)_surface.ActualHeight);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }
        ViewMatrices matrices = Camera.GetMatrices(size.X / size.Y, _context.ViewCamera, _context.ExportAspect);
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

        // 足した照明の目印（モデルより手前に描くので、モデルより先に調べる）。線の近くか、球の内側なら選ぶ
        LightNode[] lights = [.. _context.Scene.AddedLights.Where(l => l.IsEffectivelyVisible)];
        int light = LightMarkers.Pick(new Vector2((float)point.X, (float)point.Y), size, matrices.View * matrices.Projection, lights, LightPickTolerance);
        if (light >= 0)
        {
            _context.Selection.Select(lights[light], toggle ? SelectMode.Toggle : SelectMode.Replace, this);
            return;
        }

        if (_context.TestObject is { IsEffectivelyVisible: true } testObject)
        {
            Consider(testObject, Picking.IntersectUnitCube(ray, SceneEvaluator.TestObjectWorld(testObject, _context.Playback.CurrentFrame)));
        }
        _context.Poses.UpdateDirtyModels(); // 当たり判定は今の姿勢の形で行う
        _context.Attachments.Update();
        foreach (var (node, model) in _context.VisibleModels)
        {
            Consider(node, Picking.IntersectTriangles(ray, node.WorldMatrix, model.Positions, model.Indices, model.BoundsMin, model.BoundsMax));
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
