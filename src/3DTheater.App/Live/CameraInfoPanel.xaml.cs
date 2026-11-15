using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// 右上の「カメラ情報」（設計判断記録 78 章の 3 段目）。プレビューに映すカメラを選び、「配信用画面に適用」で配信用の画面と
/// 仮想カメラにも映す（思わぬ事故を減らすため、選んだだけでは配信は変わらない。利用者の案）。
/// 見ているカメラの位置・向き・視野角を、数で見る・変える（変えると元に戻せる）。
/// </summary>
public partial class CameraInfoPanel : UserControl
{
    private readonly EditorContext _context;
    private readonly CameraValues _values;
    private bool _updating;
    private bool _broadcasting;

    internal CameraInfoPanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _values = new CameraValues(context);
        DataContext = _values;
        context.LiveCameras.Changed += (_, _) => Update();
        context.CameraControls.Changed += (_, _) => UpdateHowTo();
        UpdateHowTo();
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.CamerasGroup && e.PropertyName == nameof(SceneNode.Children))
                || (e.Node is CameraNode && e.PropertyName == nameof(SceneNode.Name)))
            {
                Update();
            }
        };
        Update();
    }

    // 動かし方の説明（タッチパッドで操作するかで変える。設計判断記録 78 章の 7 段目）
    private void UpdateHowTo() => HowToText.Text = _context.CameraControls.Touchpad
        ? "タッチパッド: 2 本指でなでるとキャラクターの周りを回り、指を広げる・つまむと近づく・離れます。Shift を押しながらなでると、上下左右にずらします。"
          + "マウスの右ボタンを押している間の操作（マウスで向き、W・A・S・D・Q・E で移動）も使えます。"
        : "プレビューで右ボタンを押している間: マウスで向き、W・A・S・D で前後左右、Q・E で下・上（Shift で速く）。ホイールでズーム、Ctrl＋ホイールでキャラクターへ近づく・離れる、"
          + "中ボタンのドラッグで上下左右にずらします。タッチパッドなら、設定画面の「操作」で「タッチパッドで操作する」を入れます。";

    private void Update()
    {
        SceneDocument scene = _context.Scene;
        CameraNode preview = _context.LiveCameras.PreviewCamera(scene);
        CameraNode onAir = _context.LiveCameras.OnAirCamera(scene);
        _updating = true;
        try
        {
            CameraCombo.Items.Clear();
            foreach (CameraNode camera in scene.CamerasGroup.Children.OfType<CameraNode>())
            {
                CameraCombo.Items.Add(new ComboBoxItem { Content = camera.Name, Tag = camera });
            }
            CameraCombo.SelectedIndex = CameraCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => ReferenceEquals(i.Tag, preview));
        }
        finally
        {
            _updating = false;
        }
        bool same = ReferenceEquals(preview, onAir);
        OnAirText.Text = $"配信中: {onAir.Name}";
        ApplyButton.IsEnabled = !same;
        OnAirNote.Visibility = same && _broadcasting ? Visibility.Visible : Visibility.Collapsed;
        _values.SetCamera(preview);
    }

    /// <summary>
    /// 配信用の画面か仮想カメラを使っているか（使っているときだけ、見ているカメラが配信中なら赤い字で注意を出す。プレビューの赤い枠と同じ）。
    /// </summary>
    public void SetBroadcasting(bool broadcasting)
    {
        if (_broadcasting != broadcasting)
        {
            _broadcasting = broadcasting;
            Update();
        }
    }

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || CameraCombo.SelectedItem is not ComboBoxItem { Tag: CameraNode camera })
        {
            return;
        }
        _context.LiveCameras.Preview = ReferenceEquals(camera, _context.Scene.MainCamera) ? null : camera.Id;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        CameraNode preview = _context.LiveCameras.PreviewCamera(_context.Scene);
        _context.LiveCameras.OnAir = ReferenceEquals(preview, _context.Scene.MainCamera) ? null : preview.Id;
        Logging.AppLog.Info($"配信用の画面・仮想カメラに映すカメラを「{preview.Name}」にしました");
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        CameraNode camera = _context.LiveCameras.PreviewCamera(_context.Scene);
        if (!camera.CanEdit)
        {
            return;
        }
        // MMD の初めのカメラ（シーンを作ったときのライブカメラと同じ）
        CameraNode initial = SceneDocument.CreateDefault().MainCamera;
        _context.Edits.Change($"{camera.Name}を初めの位置に戻す", [camera], () =>
        {
            camera.Position = initial.Position;
            camera.Rotation = initial.Rotation;
            camera.FieldOfView = initial.FieldOfView;
            camera.TargetDistance = initial.TargetDistance;
            camera.Orthographic = false;
        });
    }

    /// <summary>見ているカメラの値（数の欄に結び付ける。変えると元に戻せる。同じ欄を続けて変えたら 1 つにまとめる）。</summary>
    private sealed class CameraValues(EditorContext context) : ObservableObject
    {
        private CameraNode? _camera;

        public void SetCamera(CameraNode camera)
        {
            if (!ReferenceEquals(camera, _camera))
            {
                if (_camera is not null)
                {
                    _camera.PropertyChanged -= OnCameraPropertyChanged;
                }
                _camera = camera;
                _camera.PropertyChanged += OnCameraPropertyChanged;
            }
            OnPropertyChanged(string.Empty);
        }

        public double PositionX { get => _camera?.Position.X ?? 0; set => Edit("位置", c => c.Position = c.Position with { X = (float)value }); }

        public double PositionY { get => _camera?.Position.Y ?? 0; set => Edit("位置", c => c.Position = c.Position with { Y = (float)value }); }

        public double PositionZ { get => _camera?.Position.Z ?? 0; set => Edit("位置", c => c.Position = c.Position with { Z = (float)value }); }

        public double Pitch { get => _camera?.Rotation.X ?? 0; set => Edit("向き", c => c.Rotation = c.Rotation with { X = (float)value }); }

        public double Yaw { get => _camera?.Rotation.Y ?? 0; set => Edit("向き", c => c.Rotation = c.Rotation with { Y = (float)value }); }

        public double Roll { get => _camera?.Rotation.Z ?? 0; set => Edit("向き", c => c.Rotation = c.Rotation with { Z = (float)value }); }

        public double FieldOfView { get => _camera?.FieldOfView ?? 0; set => Edit("視野角", c => c.FieldOfView = (float)value); }

        private void Edit(string what, Action<CameraNode> apply)
        {
            if (_camera is not { CanEdit: true } camera)
            {
                OnPropertyChanged(string.Empty);
                return;
            }
            context.Edits.Change($"{camera.Name}の{what}", [camera], () => apply(camera), mergeKey: $"{camera.Id}:{what}");
        }

        private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
    }
}
