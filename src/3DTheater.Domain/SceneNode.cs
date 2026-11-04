using System.Collections.ObjectModel;
using System.Numerics;

namespace Theater3D.Domain;

public enum SceneNodeKind
{
    Group,
    Camera,
    Light,
    Model,
    Stage,
    TestObject,
}

/// <summary>
/// シーン一覧に並ぶ 1 項目。カメラ・照明・モデル・まとめ用のグループなど。
/// 座標は左手系・Y が上（MMD と同じ、仮の決め。docs/設計判断記録.md 参照）。
/// 回転は度で、X = 上下の向き（正で下向き）、Y = 左右の向き、Z = 傾き。Z → X → Y の順に回す。
/// </summary>
public class SceneNode : ObservableObject
{
    private readonly ObservableCollection<SceneNode> _children = [];
    private string _name;
    private bool _isVisible = true;
    private bool _isLocked;
    private Vector3 _position;
    private Vector3 _rotation;
    private Vector3 _scale = Vector3.One;

    public SceneNode(SceneNodeKind kind, string name, bool canRename = true, bool hasTransform = true, bool canScale = true)
    {
        Kind = kind;
        _name = ValidateName(name);
        CanRename = canRename;
        HasTransform = hasTransform;
        CanScale = hasTransform && canScale;
        Children = new ReadOnlyObservableCollection<SceneNode>(_children);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public SceneNodeKind Kind { get; }

    public bool CanRename { get; }

    public bool HasTransform { get; }

    public bool CanScale { get; }

    public SceneNode? Parent { get; private set; }

    public ReadOnlyObservableCollection<SceneNode> Children { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (!CanRename)
            {
                throw new InvalidOperationException($"「{_name}」は名前を変えられません");
            }
            SetProperty(ref _name, ValidateName(value));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>編集ロック。true の間は名前・位置などを変えない（画面側で入力を止める）。</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => SetProperty(ref _isLocked, value);
    }

    public Vector3 Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    /// <summary>回転（度）。</summary>
    public Vector3 Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value);
    }

    public Vector3 Scale
    {
        get => _scale;
        set => SetProperty(ref _scale, value);
    }

    /// <summary>親のどれかが非表示なら、自分が表示でも見えない。</summary>
    public bool IsEffectivelyVisible => IsVisible && (Parent?.IsEffectivelyVisible ?? true);

    /// <summary>親のどれかがロックされていれば、自分も編集できない。</summary>
    public bool IsEffectivelyLocked => IsLocked || (Parent?.IsEffectivelyLocked ?? false);

    public bool CanEdit => !IsEffectivelyLocked;

    public void AddChild(SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent is not null)
        {
            throw new InvalidOperationException($"「{child.Name}」はすでに別の項目の下にあります");
        }
        child.Parent = this;
        _children.Add(child);
    }

    public IEnumerable<SceneNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (SceneNode child in _children)
        {
            foreach (SceneNode node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }

    public Matrix4x4 GetLocalMatrix()
    {
        Vector3 radians = Rotation * (MathF.PI / 180f);
        return Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z)
            * Matrix4x4.CreateTranslation(Position);
    }

    private static string ValidateName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("名前を空にはできません", nameof(name));
        }
        return trimmed;
    }
}

public sealed class CameraNode(string name) : SceneNode(SceneNodeKind.Camera, name, canScale: false)
{
    private float _fieldOfView = 45f;
    private float _nearClip = 0.1f;
    private float _farClip = 1000f;

    /// <summary>縦方向の視野角（度）。</summary>
    public float FieldOfView
    {
        get => _fieldOfView;
        set => SetProperty(ref _fieldOfView, Math.Clamp(value, 1f, 170f));
    }

    public float NearClip
    {
        get => _nearClip;
        set => SetProperty(ref _nearClip, Math.Clamp(value, 0.001f, _farClip * 0.999f));
    }

    public float FarClip
    {
        get => _farClip;
        set => SetProperty(ref _farClip, Math.Max(value, _nearClip * 1.001f));
    }
}

public sealed class LightNode(string name) : SceneNode(SceneNodeKind.Light, name, canScale: false)
{
    private float _intensity = 1f;

    /// <summary>明るさ。描画への反映は Phase 4 で行う。</summary>
    public float Intensity
    {
        get => _intensity;
        set => SetProperty(ref _intensity, Math.Max(0f, value));
    }
}

/// <summary>モデル読み込みができるまでの確認用の立方体（1 辺 1）。</summary>
public sealed class TestObjectNode(string name) : SceneNode(SceneNodeKind.TestObject, name)
{
    private bool _spinWithFrame = true;

    /// <summary>true のとき、再生位置に合わせて Y 軸まわりに回す（タイムラインとの同期を確かめるため）。</summary>
    public bool SpinWithFrame
    {
        get => _spinWithFrame;
        set => SetProperty(ref _spinWithFrame, value);
    }
}
