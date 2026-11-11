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
    /// <summary>音声（曲など）。再生に合わせて鳴らす。</summary>
    Audio,
    /// <summary>モデルの「表示・IK」（モデルの下のいちばん上にある）。モデルの表示と IK の切り替えをキーで持つ。</summary>
    Display,
    /// <summary>映像効果（シーンに 1 つ）。映像効果の値を動かすキーを持つ。</summary>
    Effects,
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

/// <summary>照明の種類。</summary>
public enum LightType
{
    /// <summary>平行光（太陽のように、どこでも同じ向き・同じ強さ）。向きは回転で決める。</summary>
    Directional,

    /// <summary>点光源（電球のように全方向へ。届く距離で 0 になる）。</summary>
    Point,

    /// <summary>スポットライト（回転の向きへの円すい。届く距離で 0 になる）。</summary>
    Spot,
}

/// <summary>
/// 照明。シーンの最初の照明（決まった役目の「light」）は MMD の照明（平行光。セルフシャドウ・地面の影もこれで決まる）。
/// 「照明を追加」で足した照明（点光源・スポットライト・平行光）は、モデルをその色で明るくする（影は、影をレイトレーシングで
/// 求めるときだけ落とす）。色は MMD の初期値 154/255 に、色と明るさを掛けたもの（Editor の SceneLighting）。
/// 位置・回転・色・明るさ・届く距離・広がり・ぼかしはキーで動かせる（<see cref="Keys"/>）。
/// </summary>
public sealed class LightNode : SceneNode
{
    public const float MinRange = 0.1f;
    public const float MaxRange = 1000f;

    private float _intensity = 1f;
    private Vector3 _color = Vector3.One;
    private float _range = 30f;
    private float _spotAngle = 40f;
    private float _spotSoftness = 0.3f;
    private bool _castsShadow = true;
    private bool _hasUnregisteredChange;

    public LightNode(string name, LightType type = LightType.Directional)
        : base(SceneNodeKind.Light, name, canScale: false)
    {
        Type = type;
        Keys.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
    }

    /// <summary>種類（作るときに決め、あとから変えない）。</summary>
    public LightType Type { get; }

    /// <summary>キーフレーム。</summary>
    public KeyTrack<LightKey> Keys { get; } = new();

    /// <summary>キーに登録していない変更がある（キーのある照明を、自動キーが OFF のときに手で変えた）。</summary>
    public bool HasUnregisteredChange
    {
        get => _hasUnregisteredChange;
        set => SetProperty(ref _hasUnregisteredChange, value);
    }

    /// <summary>今の値（キーにするもの）。</summary>
    public LightPose Pose => new(Position, Rotation, Color, Intensity, Range, SpotAngle, SpotSoftness);

    /// <summary>値を当てはめる（キーの再生など）。</summary>
    public void ApplyPose(LightPose pose)
    {
        Position = pose.Position;
        Rotation = pose.Rotation;
        Color = pose.Color;
        Intensity = pose.Intensity;
        Range = pose.Range;
        SpotAngle = pose.SpotAngle;
        SpotSoftness = pose.SpotSoftness;
    }

    /// <summary>明るさ（0 以上）。</summary>
    public float Intensity
    {
        get => _intensity;
        set => SetProperty(ref _intensity, float.IsFinite(value) ? Math.Max(0f, value) : _intensity);
    }

    /// <summary>色（各 0〜1）。</summary>
    public Vector3 Color
    {
        get => _color;
        set => SetProperty(ref _color, float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? Vector3.Clamp(value, Vector3.Zero, Vector3.One)
            : _color);
    }

    /// <summary>届く距離（点光源・スポットライト。ここで 0 になる）。</summary>
    public float Range
    {
        get => _range;
        set => SetProperty(ref _range, float.IsFinite(value) ? Math.Clamp(value, MinRange, MaxRange) : _range);
    }

    /// <summary>スポットライトの円すいの広がり（全体の角度、度。1〜179）。</summary>
    public float SpotAngle
    {
        get => _spotAngle;
        set => SetProperty(ref _spotAngle, float.IsFinite(value) ? Math.Clamp(value, 1f, 179f) : _spotAngle);
    }

    /// <summary>スポットライトの縁のぼかし（0〜1）。</summary>
    public float SpotSoftness
    {
        get => _spotSoftness;
        set => SetProperty(ref _spotSoftness, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : _spotSoftness);
    }

    /// <summary>
    /// 影を落とすか（足した照明。影をレイトレーシングで求めるときだけ、セルフシャドウを受ける材質へ影を落とす）。
    /// MMD の照明（シーンの照明）は、セルフシャドウ・地面の影の設定で影を落とすので、使わない。
    /// </summary>
    public bool CastsShadow
    {
        get => _castsShadow;
        set => SetProperty(ref _castsShadow, value);
    }

    protected override object? CaptureExtra(bool includeKeys) =>
        new LightState(Intensity, Color, Range, SpotAngle, SpotSoftness, CastsShadow, HasUnregisteredChange,
            includeKeys ? new KeySnapshot<LightKey>(Keys.ToArray()) : null);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is LightState s)
        {
            if (s.Keys is not null)
            {
                Keys.ReplaceAll(s.Keys.Keys);
            }
            Intensity = s.Intensity;
            Color = s.Color;
            Range = s.Range;
            SpotAngle = s.SpotAngle;
            SpotSoftness = s.SpotSoftness;
            CastsShadow = s.CastsShadow;
            HasUnregisteredChange = s.HasUnregisteredChange;
        }
    }
}

/// <summary>
/// 音声（曲など）。ファイルは参照するだけで書き換えない。<see cref="StartFrame"/> のフレームで鳴り始め、再生に合わせて鳴らす。
/// 表示（<see cref="SceneNode.IsVisible"/>）を切ると音を消す（タイミングを保つため、音量 0 で流し続ける）。
/// </summary>
public sealed class AudioNode : SceneNode
{
    private int _startFrame;
    private float _volume = 1f;

    public AudioNode(string name, string filePath, TimeSpan duration)
        : base(SceneNodeKind.Audio, name, hasTransform: false)
    {
        FilePath = filePath;
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>音声ファイルの場所（絶対パス）。</summary>
    public string FilePath { get; }

    /// <summary>音声の長さ。</summary>
    public TimeSpan Duration { get; }

    /// <summary>鳴り始めるフレーム（負なら、音声の頭を飛ばして 0 フレーム目から途中を鳴らす）。</summary>
    public int StartFrame
    {
        get => _startFrame;
        set => SetProperty(ref _startFrame, Math.Clamp(value, -1_000_000, 1_000_000));
    }

    /// <summary>音量（0〜1）。</summary>
    public float Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : _volume);
    }

    /// <summary>音声が終わるフレーム（鳴り始めのフレーム + 長さ。<paramref name="frameRate"/> は作品のフレームの速さ）。</summary>
    public int EndFrame(int frameRate) => StartFrame + (int)Math.Ceiling(Duration.TotalSeconds * frameRate);

    protected override object? CaptureExtra(bool includeKeys) => new AudioState(StartFrame, Volume);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is AudioState s)
        {
            StartFrame = s.StartFrame;
            Volume = s.Volume;
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

    /// <summary>材質の名前（モデルのファイルの番号順）。材質の上書きの画面と保存に使う。</summary>
    public IReadOnlyList<string> MaterialNames { get; init; } = [];

    private MaterialOverrideSet _materialOverrides = MaterialOverrideSet.Empty;

    /// <summary>材質の上書き（拡張材質。PMX の材質は書き換えない）。元に戻す・やり直しの対象。</summary>
    public MaterialOverrideSet MaterialOverrides
    {
        get => _materialOverrides;
        set => SetProperty(ref _materialOverrides, value ?? MaterialOverrideSet.Empty);
    }

    protected override object? CaptureExtra(bool includeKeys) => new ModelState(MaterialOverrides);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is ModelState s)
        {
            MaterialOverrides = s.MaterialOverrides;
        }
    }

    /// <summary>ボーン（モデルのファイルの番号順）。一覧では表示枠ごとのまとまりの下に並ぶ。</summary>
    public IReadOnlyList<BoneNode> Bones { get; private set; } = [];

    /// <summary>「表示・IK」（<see cref="AttachParts"/> で作る。まだ取り付けていなければ null）。</summary>
    public ModelDisplayNode? Display { get; private set; }

    /// <summary>「表示・IK」でモデルを表示するか（目の印とは別。両方が表示のときだけ描く）。</summary>
    public bool IsShownByDisplay => Display?.Shown ?? true;

    /// <summary>モーフ（モデルのファイルの番号順）。一覧では種類（目・口など）ごとのまとまりの下に並ぶ。</summary>
    public IReadOnlyList<MorphNode> Morphs { get; private set; } = [];

    /// <summary>
    /// ボーンとモーフを、一覧での並び（まとまりの項目）ごと取り付ける。シーンに加える前に 1 回だけ呼ぶ。
    /// <paramref name="groups"/> はモデルの直下に置く項目（「ボーン」「表情」など）で、その下に各ボーン・モーフが入っていること。
    /// いちばん上には「表示・IK」（<see cref="Display"/>）を作って置く。
    /// </summary>
    public void AttachParts(IEnumerable<SceneNode> groups, IReadOnlyList<BoneNode> bones, IReadOnlyList<MorphNode> morphs)
    {
        if (Parent is not null || Children.Count > 0)
        {
            throw new InvalidOperationException("ボーンとモーフは、シーンに加える前に 1 回だけ取り付けられます");
        }
        Display = new ModelDisplayNode([.. bones.Where(b => b.Traits.IsIk)]);
        AddChild(Display);
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

/// <summary>
/// モデルの「表示・IK」（MMD と同じ）。モデルを表示するか（<see cref="Shown"/>）と、IK ボーンごとに IK を解くか（<see cref="Ik"/>）を
/// キーで切り替える（いつも段階）。目の印（<see cref="SceneNode.IsVisible"/>）とは別で、両方が表示のときだけモデルを描く。
/// IK を切った IK ボーンは、リンクのボーンを動かさない（リンクのボーンは自分のキーの回転のまま）。
/// </summary>
public sealed class ModelDisplayNode : SceneNode
{
    public const string DefaultName = "表示・IK";

    private bool _shown = true;
    private IkSwitchSet _ik = IkSwitchSet.AllOn;
    private bool _hasUnregisteredChange;

    public ModelDisplayNode(IReadOnlyList<BoneNode> ikBones)
        : base(SceneNodeKind.Display, DefaultName, canRename: false, hasTransform: false)
    {
        IkBones = ikBones;
        CanToggleVisibility = false;
        Keys.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
    }

    public ModelNode? Model => Parent as ModelNode;

    /// <summary>切り替えられる IK ボーン（モデルのファイルの番号順）。</summary>
    public IReadOnlyList<BoneNode> IkBones { get; }

    /// <summary>モデルを表示するか（MMD の「表示」）。</summary>
    public bool Shown
    {
        get => _shown;
        set => SetProperty(ref _shown, value);
    }

    /// <summary>切った IK。</summary>
    public IkSwitchSet Ik
    {
        get => _ik;
        set => SetProperty(ref _ik, value ?? IkSwitchSet.AllOn);
    }

    public bool IsIkEnabled(int boneIndex) => _ik.IsEnabled(boneIndex);

    /// <summary>キーフレーム。</summary>
    public KeyTrack<DisplayKey> Keys { get; } = new();

    /// <summary>キーに登録していない変更がある（キーのある表示・IK を、自動キーが OFF のときに手で変えた）。</summary>
    public bool HasUnregisteredChange
    {
        get => _hasUnregisteredChange;
        set => SetProperty(ref _hasUnregisteredChange, value);
    }

    /// <summary>キーの値を当てはめる。</summary>
    public void Apply(DisplayKey key)
    {
        Shown = key.Shown;
        Ik = key.Ik ?? IkSwitchSet.AllOn;
    }

    protected override object? CaptureExtra(bool includeKeys) =>
        new DisplayState(Shown, Ik, HasUnregisteredChange, includeKeys ? new KeySnapshot<DisplayKey>(Keys.ToArray()) : null);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is DisplayState s)
        {
            if (s.Keys is not null)
            {
                Keys.ReplaceAll(s.Keys.Keys);
            }
            Shown = s.Shown;
            Ik = s.Ik;
            HasUnregisteredChange = s.HasUnregisteredChange;
        }
    }
}

/// <summary>
/// 映像効果（シーンに 1 つ）。映像効果の値（色調補正・ブルーム・周辺減光・被写界深度・霧。<see cref="EffectPose"/>）を動かすキーを持つ。
/// 値そのものは作品の描き方の設定にあり（Editor の RenderSettings）、キーの当てはめと登録は Editor の AnimationService が行う。
/// </summary>
public sealed class EffectsNode : SceneNode
{
    public const string DefaultName = "映像効果";

    private bool _hasUnregisteredChange;

    public EffectsNode()
        : base(SceneNodeKind.Effects, DefaultName, canRename: false, hasTransform: false)
    {
        CanToggleVisibility = false;
        Keys.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
    }

    /// <summary>キーフレーム。</summary>
    public KeyTrack<EffectKey> Keys { get; } = new();

    /// <summary>キーに登録していない変更がある（キーのある映像効果の値を、自動キーが OFF のときに手で変えた）。</summary>
    public bool HasUnregisteredChange
    {
        get => _hasUnregisteredChange;
        set => SetProperty(ref _hasUnregisteredChange, value);
    }

    protected override object? CaptureExtra(bool includeKeys) =>
        new EffectsState(HasUnregisteredChange, includeKeys ? new KeySnapshot<EffectKey>(Keys.ToArray()) : null);

    protected override void RestoreExtra(object? extra)
    {
        if (extra is EffectsState s)
        {
            if (s.Keys is not null)
            {
                Keys.ReplaceAll(s.Keys.Keys);
            }
            HasUnregisteredChange = s.HasUnregisteredChange;
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
            // 長さがほぼ 1 ならそのまま使う（写した値を戻すときに、もう一度そろえ直して値がわずかにずれないように）
            float lengthSquared = value.LengthSquared();
            Quaternion q = !(lengthSquared > 1e-12f) ? Quaternion.Identity
                : MathF.Abs(lengthSquared - 1f) <= 1e-6f ? value
                : Quaternion.Normalize(value);
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
