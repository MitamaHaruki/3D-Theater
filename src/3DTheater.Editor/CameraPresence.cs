using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// ライブモードのカメラを足す・外す（元に戻せる。設計判断記録 78 章の 3 段目）。足したカメラはカメラのまとまりの最後に並ぶ。
/// 決まった役目のカメラ（「ライブカメラ」）は外せない。
/// </summary>
public sealed class CameraPresenceAction : IUndoableAction
{
    /// <summary>ライブモードの、決まった役目のカメラの名前（消せない・名前を変えない）。</summary>
    public const string LiveCameraName = "ライブカメラ";

    private readonly SceneDocument _scene;
    private readonly CameraNode _camera;
    private readonly bool _added;
    private readonly int _index;

    private CameraPresenceAction(SceneDocument scene, CameraNode camera, bool added, int index)
    {
        _scene = scene;
        _camera = camera;
        _added = added;
        _index = index;
    }

    public string Description => _added ? $"カメラを追加（{_camera.Name}）" : $"カメラを外す（{_camera.Name}）";

    /// <summary>足すカメラの名前（「カメラ 2」「カメラ 3」…。使っていない番号）。</summary>
    public static string NameFor(SceneDocument scene)
    {
        var used = scene.CamerasGroup.Children.Select(n => n.Name).ToHashSet();
        int number = 2;
        while (used.Contains($"カメラ {number}"))
        {
            number++;
        }
        return $"カメラ {number}";
    }

    /// <summary>新しいカメラを作る。<paramref name="from"/>（今見ているカメラ）と同じ位置・向き・視野角から始める。</summary>
    public static CameraNode Create(SceneDocument scene, CameraNode from) => new(NameFor(scene))
    {
        Position = from.Position,
        Rotation = from.Rotation,
        FieldOfView = from.FieldOfView,
        TargetDistance = from.TargetDistance,
        Orthographic = from.Orthographic,
    };

    /// <summary>カメラを足す（元に戻せる操作として積む）。</summary>
    public static void Add(SceneDocument scene, UndoService undo, CameraNode camera)
    {
        scene.Add(scene.CamerasGroup, camera);
        undo.Push(new CameraPresenceAction(scene, camera, added: true, scene.CamerasGroup.Children.Count - 1));
    }

    /// <summary>足したカメラを外す（元に戻せる）。決まった役目のカメラ・ロックしたカメラは外さず false。</summary>
    public static bool Remove(SceneDocument scene, UndoService undo, CameraNode camera)
    {
        if (ReferenceEquals(camera, scene.MainCamera) || !camera.CanEdit || camera.Parent != scene.CamerasGroup)
        {
            return false;
        }
        int index = scene.CamerasGroup.Children.ToList().IndexOf(camera);
        scene.Remove(camera);
        undo.Push(new CameraPresenceAction(scene, camera, added: false, index));
        return true;
    }

    public void Undo()
    {
        if (_added)
        {
            _scene.Remove(_camera);
        }
        else
        {
            _scene.Insert(_scene.CamerasGroup, Math.Min(_index, _scene.CamerasGroup.Children.Count), _camera);
        }
    }

    public void Redo()
    {
        if (_added)
        {
            _scene.Insert(_scene.CamerasGroup, Math.Min(_index, _scene.CamerasGroup.Children.Count), _camera);
        }
        else
        {
            _scene.Remove(_camera);
        }
    }
}

/// <summary>
/// ライブモードで、プレビューに映すカメラ（カメラ情報で選ぶ）と、配信用の画面・仮想カメラに映すカメラ（「配信用画面に適用」で決める）。
/// どちらも、見つからなければ（外した・決めていない）決まった役目のカメラ（ライブカメラ）。ライブのファイルに保存する。
/// </summary>
public sealed class LiveCameraSettings
{
    private Guid? _preview;
    private Guid? _onAir;

    public event EventHandler? Changed;

    /// <summary>プレビューに映すカメラの Id（null ならライブカメラ）。</summary>
    public Guid? Preview { get => _preview; set => Set(ref _preview, value); }

    /// <summary>配信用の画面・仮想カメラに映すカメラの Id（null ならライブカメラ）。</summary>
    public Guid? OnAir { get => _onAir; set => Set(ref _onAir, value); }

    /// <summary>Id のカメラ（シーンに無ければ、決まった役目のカメラ）。</summary>
    public static CameraNode Resolve(SceneDocument scene, Guid? id) =>
        id is { } wanted ? scene.CamerasGroup.Children.OfType<CameraNode>().FirstOrDefault(c => c.Id == wanted) ?? scene.MainCamera : scene.MainCamera;

    public CameraNode PreviewCamera(SceneDocument scene) => Resolve(scene, Preview);

    public CameraNode OnAirCamera(SceneDocument scene) => Resolve(scene, OnAir);

    /// <summary>プレビューに映しているカメラを、配信にも映すか（「配信用画面に適用」を押せないとき）。</summary>
    public bool IsPreviewOnAir(SceneDocument scene) => ReferenceEquals(PreviewCamera(scene), OnAirCamera(scene));

    public void Reset()
    {
        _preview = null;
        _onAir = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Set(ref Guid? field, Guid? value)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
