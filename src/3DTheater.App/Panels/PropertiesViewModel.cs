using System.ComponentModel;
using Theater3D.App.Logging;
using Theater3D.Domain;

namespace Theater3D.App.Panels;

/// <summary>
/// プロパティ欄の中身。選んだ項目の種類に合わせて、出す欄を切り替える。
/// 値を変えると再生を一時停止する（再生中の編集は止めてから行う。指示書 3.4）。
/// </summary>
public sealed class PropertiesViewModel : ObservableObject
{
    private readonly EditorContext _context;
    private SceneNode? _node;

    public PropertiesViewModel(EditorContext context)
    {
        _context = context;
        context.Selection.Changed += (_, _) => Refresh();
        Refresh();
    }

    public bool HasSelection => _context.Selection.Items.Count > 0;

    public bool IsSingle => _context.Selection.Items.Count == 1;

    public bool IsMultiple => _context.Selection.Items.Count > 1;

    public string SelectionSummary => $"{_context.Selection.Items.Count} 個を選択中です。まとめての編集は今後対応します。";

    public string KindName => _node?.Kind switch
    {
        SceneNodeKind.Camera => "カメラ",
        SceneNodeKind.Light => "照明",
        SceneNodeKind.Model => "モデル",
        SceneNodeKind.TestObject => "確認用の立方体",
        SceneNodeKind.Stage => "ステージ",
        _ => "グループ",
    };

    public string NodeName
    {
        get => _node?.Name ?? string.Empty;
        set
        {
            if (_node is null || !CanRename || value.Trim() == _node.Name)
            {
                return;
            }
            try
            {
                _node.Name = value;
            }
            catch (ArgumentException ex)
            {
                AppLog.Warning($"名前を変えられませんでした: {ex.Message}");
                OnPropertyChanged(nameof(NodeName));
            }
        }
    }

    public bool CanRename => _node is { CanRename: true, CanEdit: true };

    public bool IsEditable => _node?.CanEdit ?? false;

    public bool IsLockedNoteVisible => _node is not null && !_node.CanEdit;

    public bool NodeIsVisible
    {
        get => _node?.IsVisible ?? false;
        set
        {
            if (_node is not null)
            {
                _node.IsVisible = value;
            }
        }
    }

    public bool HasTransform => _node?.HasTransform ?? false;

    public bool CanScale => _node?.CanScale ?? false;

    public bool IsCamera => _node is CameraNode;

    public bool IsLight => _node is LightNode;

    public bool IsTestObject => _node is TestObjectNode;

    public double PositionX { get => Get(n => n.Position.X); set => Edit(n => n.Position = n.Position with { X = (float)value }); }

    public double PositionY { get => Get(n => n.Position.Y); set => Edit(n => n.Position = n.Position with { Y = (float)value }); }

    public double PositionZ { get => Get(n => n.Position.Z); set => Edit(n => n.Position = n.Position with { Z = (float)value }); }

    public double RotationX { get => Get(n => n.Rotation.X); set => Edit(n => n.Rotation = n.Rotation with { X = (float)value }); }

    public double RotationY { get => Get(n => n.Rotation.Y); set => Edit(n => n.Rotation = n.Rotation with { Y = (float)value }); }

    public double RotationZ { get => Get(n => n.Rotation.Z); set => Edit(n => n.Rotation = n.Rotation with { Z = (float)value }); }

    public double ScaleX { get => Get(n => n.Scale.X); set => Edit(n => n.Scale = n.Scale with { X = (float)value }); }

    public double ScaleY { get => Get(n => n.Scale.Y); set => Edit(n => n.Scale = n.Scale with { Y = (float)value }); }

    public double ScaleZ { get => Get(n => n.Scale.Z); set => Edit(n => n.Scale = n.Scale with { Z = (float)value }); }

    public double FieldOfView
    {
        get => (_node as CameraNode)?.FieldOfView ?? 0;
        set => Edit(n => ((CameraNode)n).FieldOfView = (float)value);
    }

    public double NearClip
    {
        get => (_node as CameraNode)?.NearClip ?? 0;
        set => Edit(n => ((CameraNode)n).NearClip = (float)value);
    }

    public double FarClip
    {
        get => (_node as CameraNode)?.FarClip ?? 0;
        set => Edit(n => ((CameraNode)n).FarClip = (float)value);
    }

    public double Intensity
    {
        get => (_node as LightNode)?.Intensity ?? 0;
        set => Edit(n => ((LightNode)n).Intensity = (float)value);
    }

    public bool SpinWithFrame
    {
        get => (_node as TestObjectNode)?.SpinWithFrame ?? false;
        set => Edit(n => ((TestObjectNode)n).SpinWithFrame = value);
    }

    private double Get(Func<SceneNode, float> read) => _node is null ? 0 : read(_node);

    private void Edit(Action<SceneNode> apply)
    {
        if (_node is null || !_node.CanEdit)
        {
            OnPropertyChanged(string.Empty);
            return;
        }
        _context.Playback.Pause();
        apply(_node);
    }

    private void Refresh()
    {
        SceneNode? next = IsSingle ? _context.Selection.Primary : null;
        if (!ReferenceEquals(next, _node))
        {
            if (_node is not null)
            {
                _node.PropertyChanged -= OnNodePropertyChanged;
            }
            _node = next;
            if (_node is not null)
            {
                _node.PropertyChanged += OnNodePropertyChanged;
            }
        }
        OnPropertyChanged(string.Empty); // すべての欄を更新する
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}
