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
    /// <summary>モデルのボーン（モデルの下にある）。</summary>
    Bone,
    /// <summary>モデルのモーフ（表情など）。</summary>
    Morph,
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

    /// <summary>一覧（シーン一覧・タイムライン）で最初は閉じておくか。ボーンや表情のように数が多いまとまりに使う。</summary>
    public bool StartsCollapsed { get; init; }

    /// <summary>表示 / 非表示を切り替えられるか（モーフのように見た目の無い項目は false）。</summary>
    public bool CanToggleVisibility { get; init; } = true;

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
    public virtual Vector3 Rotation
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

    internal void InsertChild(int index, SceneNode child)
    {
        if (child.Parent is not null)
        {
            throw new InvalidOperationException($"「{child.Name}」はすでに別の項目の下にあります");
        }
        child.Parent = this;
        _children.Insert(Math.Clamp(index, 0, _children.Count), child);
    }

    internal void RemoveChild(SceneNode child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
        }
    }

    /// <summary>親をたどって、最初に見つかった <typeparamref name="T"/> の項目。</summary>
    public T? FindAncestor<T>() where T : SceneNode
    {
        for (SceneNode? p = Parent; p is not null; p = p.Parent)
        {
            if (p is T match)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>編集できる値を写す（元に戻すため）。<paramref name="includeKeys"/> が true ならキーも写す。</summary>
    public NodeState CaptureState(bool includeKeys = false) =>
        new(Name, IsVisible, IsLocked, Position, Rotation, Scale, CaptureExtra(includeKeys));

    /// <summary><see cref="CaptureState"/> で写した値に戻す。</summary>
    public void RestoreState(NodeState state)
    {
        if (CanRename && state.Name != Name)
        {
            Name = state.Name;
        }
        IsVisible = state.IsVisible;
        IsLocked = state.IsLocked;
        if (HasTransform)
        {
            Position = state.Position;
            Scale = state.Scale;
            if (this is not BoneNode)
            {
                Rotation = state.Rotation; // ボーンの回転は四元数で戻す
            }
        }
        RestoreExtra(state.Extra);
    }

    protected virtual object? CaptureExtra(bool includeKeys) => null;

    protected virtual void RestoreExtra(object? extra)
    {
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

/// <summary>
/// シーンのカメラ。位置・回転（度）・視野角で置く。キー（<see cref="Keys"/>）は MMD と同じく注視点・回転・距離・視野角で持ち、
/// 当てはめるときに位置へ直す。注視点は「位置から見ている向きへ <see cref="TargetDistance"/> 進んだ所」。
/// </summary>
public sealed class CameraNode : SceneNode
{
    private float _fieldOfView = 45f;
    private float _nearClip = 0.1f;
    private float _farClip = 1000f;
    private float _targetDistance = 6f;
    private bool _hasUnregisteredChange;

    public CameraNode(string name)
        : base(SceneNodeKind.Camera, name, canScale: false)
    {
        Keys.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
    }

    /// <summary>注視点までの距離（キーを登録するときの注視点を決める）。</summary>
    public float TargetDistance
    {
        get => _targetDistance;
        // MMD では注視点より奥にカメラを置く（負の距離）こともあるので、正の数に限らない
        set => SetProperty(ref _targetDistance, float.IsFinite(value) ? Math.Clamp(value, -100_000f, 100_000f) : _targetDistance);
    }

    /// <summary>今の置き方（注視点・回転・距離・視野角）。</summary>
    public CameraPose Pose
    {
        get
        {
            var forward = Vector3.Transform(Vector3.UnitZ, SceneRotation.ToQuaternion(Rotation));
            return new CameraPose(Position + (forward * TargetDistance), Rotation, TargetDistance, FieldOfView);
        }
    }

    /// <summary>置き方を当てはめる（キーの再生など）。</summary>
    public void ApplyPose(CameraPose pose)
    {
        Rotation = pose.Rotation;
        TargetDistance = pose.Distance;
        FieldOfView = pose.FieldOfView;
        Position = pose.Position;
    }

    /// <summary>キーフレーム。</summary>
    public KeyTrack<CameraKey> Keys { get; } = new();

    /// <summary>キーに登録していない変更がある（キーのあるカメラを、自動キーが OFF のときに手で動かした）。</summary>
    public bool HasUnregisteredChange
    {
        get => _hasUnregisteredChange;
        set => SetProperty(ref _hasUnregisteredChange, value);
    }

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

    protected override object? CaptureExtra(bool includeKeys) =>
        new CameraState(FieldOfView, NearClip, FarClip, TargetDistance, HasUnregisteredChange,
            includeKeys ? new KeySnapshot<CameraKey>(Keys.ToArray()) : null);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is CameraState s)
        {
            if (s.Keys is not null)
            {
                Keys.ReplaceAll(s.Keys.Keys);
            }
            // 近と遠は互いに制限し合うので、遠 → 近 → 遠 の順に戻す
            FieldOfView = s.FieldOfView;
            FarClip = s.FarClip;
            NearClip = s.NearClip;
            FarClip = s.FarClip;
            TargetDistance = s.TargetDistance;
            HasUnregisteredChange = s.HasUnregisteredChange;
        }
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

    protected override object? CaptureExtra(bool includeKeys) => new LightState(Intensity);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is LightState s)
        {
            Intensity = s.Intensity;
        }
    }
}

/// <summary>読み込んだモデルの概要（シーン一覧とプロパティに出す）。</summary>
public sealed record ModelSummary(
    string Format,
    int Vertices,
    int Faces,
    int Materials,
    int Textures,
    int Bones,
    int Morphs,
    int RigidBodies,
    int Joints);

/// <summary>
/// 読み込んだモデル 1 体。元のファイルは参照するだけで書き換えない（指示書 4.4）。
/// ファイルの中身（頂点・ボーンなど）はアプリ側で別に持ち、ここには場所と概要だけを持つ。
/// </summary>
public sealed class ModelNode(string name, string filePath, ModelSummary summary, string comment, Vector3 boundsMin, Vector3 boundsMax)
    : SceneNode(SceneNodeKind.Model, name)
{
    public string FilePath { get; } = filePath;

    public ModelSummary Summary { get; } = summary;

    /// <summary>モデルに書かれたコメント（作者・利用条件など）。</summary>
    public string Comment { get; } = comment;

    /// <summary>初期の姿勢の頂点を囲む箱（モデルの座標で）。画面に収めるときに使う。</summary>
    public Vector3 BoundsMin { get; } = boundsMin;

    public Vector3 BoundsMax { get; } = boundsMax;

    /// <summary>ボーン（モデルのファイルの番号順）。一覧では表示枠ごとのまとまりの下に並ぶ。</summary>
    public IReadOnlyList<BoneNode> Bones { get; private set; } = [];

    /// <summary>モーフ（モデルのファイルの番号順）。一覧では種類（目・口など）ごとのまとまりの下に並ぶ。</summary>
    public IReadOnlyList<MorphNode> Morphs { get; private set; } = [];

    /// <summary>
    /// ボーンとモーフを、一覧での並び（まとまりの項目）ごと取り付ける。シーンに加える前に 1 回だけ呼ぶ。
    /// <paramref name="groups"/> はモデルの直下に置く項目（「ボーン」「表情」など）で、その下に各ボーン・モーフが入っていること。
    /// </summary>
    public void AttachParts(IEnumerable<SceneNode> groups, IReadOnlyList<BoneNode> bones, IReadOnlyList<MorphNode> morphs)
    {
        if (Parent is not null || Children.Count > 0)
        {
            throw new InvalidOperationException("ボーンとモーフは、シーンに加える前に 1 回だけ取り付けられます");
        }
        foreach (SceneNode group in groups)
        {
            AddChild(group);
        }
        var parts = DescendantsAndSelf().ToHashSet();
        if (!bones.All(parts.Contains) || !morphs.All(parts.Contains))
        {
            throw new ArgumentException("ボーンとモーフは、取り付ける項目の下に入れてください");
        }
        Bones = bones;
        Morphs = morphs;
    }

    /// <summary>ボーンを初期の姿勢に、モーフを 0 に戻す（ロックした項目は変えない）。</summary>
    public void ResetPose()
    {
        foreach (BoneNode bone in Bones.Where(b => b.CanEdit))
        {
            bone.Position = Vector3.Zero;
            bone.Orientation = Quaternion.Identity;
        }
        foreach (MorphNode morph in Morphs.Where(m => m.CanEdit))
        {
            morph.Weight = 0;
        }
    }
}

/// <summary>ボーンの性質（モデルのファイルから読んだもの。シーンでは変えない）。</summary>
public sealed record BoneTraits(
    bool CanRotate,
    bool CanTranslate,
    bool IsIk,
    bool IsIkLink,
    bool HasInherit,
    bool HasFixedAxis,
    bool AfterPhysics,
    bool IsShown,
    string ParentName);

/// <summary>
/// モデルのボーン 1 本。<see cref="SceneNode.Position"/> は初期の位置からの移動、<see cref="SceneNode.Rotation"/> は回転（度）で、
/// どちらも親のボーンから見た向きで表す（MMD のボーンの移動・回転と同じ）。
/// 名前や親子関係はモデルのファイルのままで、ここでは変えない（指示書 2.7）。
/// </summary>
public sealed class BoneNode : SceneNode
{
    private Quaternion _orientation = Quaternion.Identity;
    private Vector3 _degrees;
    private bool _hasUnregisteredChange;

    public BoneNode(string name, int index, BoneTraits traits)
        : base(SceneNodeKind.Bone, name, canRename: false, hasTransform: true, canScale: false)
    {
        Index = index;
        Traits = traits;
        Keys.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
    }

    public int Index { get; }

    public BoneTraits Traits { get; }

    public ModelNode? Model => FindAncestor<ModelNode>();

    /// <summary>
    /// 回転（四元数）。計算とキーはこちらを使う（角度にすると同じ回転でも表し方が複数あり、キーの補間がずれるため）。
    /// 変わると <see cref="SceneNode.Rotation"/> の変更として知らせる。
    /// </summary>
    public Quaternion Orientation
    {
        get => _orientation;
        set
        {
            Quaternion q = value.LengthSquared() > 1e-12f ? Quaternion.Normalize(value) : Quaternion.Identity;
            if (q == _orientation)
            {
                return;
            }
            _orientation = q;
            _degrees = SceneRotation.ToDegrees(q);
            OnPropertyChanged(nameof(Rotation));
        }
    }

    /// <summary>回転を度で（プロパティ欄で使う）。<see cref="Orientation"/> と同じ回転を表す。</summary>
    public override Vector3 Rotation
    {
        get => _degrees;
        set
        {
            if (value == _degrees)
            {
                return;
            }
            _degrees = value;
            _orientation = SceneRotation.ToQuaternion(value);
            OnPropertyChanged(nameof(Rotation));
        }
    }

    /// <summary>キーフレーム。</summary>
    public KeyTrack<BoneKey> Keys { get; } = new();

    /// <summary>キーに登録していない変更がある（自動キーが OFF のとき、手で動かした）。</summary>
    public bool HasUnregisteredChange
    {
        get => _hasUnregisteredChange;
        set => SetProperty(ref _hasUnregisteredChange, value);
    }

    /// <summary>初期の姿勢から動かしているか。</summary>
    public bool IsPosed => Position != Vector3.Zero || _orientation != Quaternion.Identity;

    protected override object? CaptureExtra(bool includeKeys) =>
        new BoneState(_orientation, _hasUnregisteredChange, includeKeys ? new KeySnapshot<BoneKey>(Keys.ToArray()) : null);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is BoneState s)
        {
            if (s.Keys is not null)
            {
                Keys.ReplaceAll(s.Keys.Keys);
            }
            Orientation = s.Orientation;
            HasUnregisteredChange = s.HasUnregisteredChange;
        }
    }
}

/// <summary>モーフを並べるときの分け方（PMX の「パネル」）。</summary>
public enum MorphCategory
{
    Eyebrow,
    Eye,
    Mouth,
    Other,
    System,
}

/// <summary>モデルのモーフ（表情など）1 つ。値は 0（効かない）〜 1（すべて効く）。</summary>
public sealed class MorphNode : SceneNode
{
    private float _weight;
    private bool _hasUnregisteredChange;

    public MorphNode(string name, int index, MorphCategory category, string typeName)
        : base(SceneNodeKind.Morph, name, canRename: false, hasTransform: false)
    {
        Index = index;
        Category = category;
        TypeName = typeName;
        Keys.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
    }

    public int Index { get; }

    public MorphCategory Category { get; }

    /// <summary>モーフの種類の名前（頂点・ボーン・材質など）。</summary>
    public string TypeName { get; }

    /// <summary>キーフレーム。</summary>
    public KeyTrack<MorphKey> Keys { get; } = new();

    /// <summary>キーに登録していない変更がある（自動キーが OFF のとき、手で動かした）。</summary>
    public bool HasUnregisteredChange
    {
        get => _hasUnregisteredChange;
        set => SetProperty(ref _hasUnregisteredChange, value);
    }

    public float Weight
    {
        get => _weight;
        set => SetProperty(ref _weight, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f);
    }

    public ModelNode? Model => FindAncestor<ModelNode>();

    protected override object? CaptureExtra(bool includeKeys) =>
        new MorphState(_weight, _hasUnregisteredChange, includeKeys ? new KeySnapshot<MorphKey>(Keys.ToArray()) : null);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is MorphState s)
        {
            if (s.Keys is not null)
            {
                Keys.ReplaceAll(s.Keys.Keys);
            }
            Weight = s.Weight;
            HasUnregisteredChange = s.HasUnregisteredChange;
        }
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

    protected override object? CaptureExtra(bool includeKeys) => new TestObjectState(SpinWithFrame);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is TestObjectState s)
        {
            SpinWithFrame = s.SpinWithFrame;
        }
    }
}
