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
        SceneNodeKind.Bone => "ボーン",
        SceneNodeKind.Morph => "モーフ",
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
                SceneNode node = _node;
                _context.Edits.Change($"「{node.Name}」の名前", [node], () => node.Name = value);
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
                SceneNode node = _node;
                _context.Edits.Change($"{node.Name}の表示", [node], () => node.IsVisible = value);
            }
        }
    }

    public bool HasTransform => _node?.HasTransform ?? false;

    /// <summary>位置の欄を出すか（移動できないボーンでは出さない）。</summary>
    public bool ShowsPosition => _node is BoneNode bone ? bone.Traits.CanTranslate : HasTransform;

    /// <summary>回転の欄を出すか（回転できないボーンでは出さない）。</summary>
    public bool ShowsRotation => _node is BoneNode bone ? bone.Traits.CanRotate : HasTransform;

    public string PositionLabel => _node is BoneNode ? "移動" : "位置";

    public string TransformHeader => _node is BoneNode ? "ボーンの姿勢（親から見た向き）" : "トランスフォーム";

    public bool CanScale => _node?.CanScale ?? false;

    public bool IsCamera => _node is CameraNode;

    public bool IsLight => _node is LightNode;

    public bool IsTestObject => _node is TestObjectNode;

    public bool IsModel => _node is ModelNode;

    public ModelNode? Model => _node as ModelNode;

    public bool HasModelComment => !string.IsNullOrWhiteSpace(Model?.Comment);

    public bool IsBone => _node is BoneNode;

    public BoneNode? Bone => _node as BoneNode;

    /// <summary>ボーンの性質を短く並べたもの（「回転・移動・IK」など）。</summary>
    public string BoneTraitsText
    {
        get
        {
            if (_node is not BoneNode bone)
            {
                return string.Empty;
            }
            BoneTraits t = bone.Traits;
            var parts = new List<string>();
            if (t.CanRotate)
            {
                parts.Add("回転できる");
            }
            if (t.CanTranslate)
            {
                parts.Add("移動できる");
            }
            if (t.IsIk)
            {
                parts.Add("IK");
            }
            if (t.IsIkLink)
            {
                parts.Add("IK で動く");
            }
            if (t.HasInherit)
            {
                parts.Add("付与あり（ほかのボーンの動きを受け継ぐ）");
            }
            if (t.HasFixedAxis)
            {
                parts.Add("軸が固定");
            }
            if (t.AfterPhysics)
            {
                parts.Add("物理の後に計算");
            }
            if (!t.IsShown)
            {
                parts.Add("MMD では非表示");
            }
            return parts.Count == 0 ? "動かせないボーン" : string.Join("・", parts);
        }
    }

    public string BoneParentText => _node is BoneNode { Traits.ParentName: { Length: > 0 } parent } ? parent : "なし（いちばん上）";

    public bool IsBoneIkLinkNote => _node is BoneNode { Traits.IsIkLink: true };

    /// <summary>選んだボーン・モーフに、キーに登録していない変更がある。</summary>
    public bool HasUnregisteredChange => _node is BoneNode { HasUnregisteredChange: true } or MorphNode { HasUnregisteredChange: true };

    /// <summary>キーの数（ボーン・モーフ）。</summary>
    public string KeyCountText => _node switch
    {
        BoneNode bone => bone.Keys.Count == 0 ? "キーなし" : $"キー {bone.Keys.Count:N0} 個（{bone.Keys.FirstFrame}〜{bone.Keys.LastFrame} フレーム）",
        MorphNode morph => morph.Keys.Count == 0 ? "キーなし" : $"キー {morph.Keys.Count:N0} 個（{morph.Keys.FirstFrame}〜{morph.Keys.LastFrame} フレーム）",
        _ => string.Empty,
    };

    public bool IsMorph => _node is MorphNode;

    public MorphNode? Morph => _node as MorphNode;

    public string MorphCategoryText => _node is MorphNode morph ? Import.ModelPartsBuilder.CategoryName(morph.Category) : string.Empty;

    public double MorphWeight
    {
        get => (_node as MorphNode)?.Weight ?? 0;
        set => Edit("値", n => ((MorphNode)n).Weight = (float)value);
    }

    /// <summary>ボーンを初期の姿勢へ戻す。</summary>
    public void ResetBone() => Edit("初期の姿勢に戻す", n =>
    {
        n.Position = System.Numerics.Vector3.Zero;
        n.Rotation = System.Numerics.Vector3.Zero;
    });

    /// <summary>選んだモデルのボーンとモーフをすべて初期の状態へ戻す。</summary>
    public void ResetModelPose()
    {
        if (_node is ModelNode model && model.CanEdit)
        {
            _context.Playback.Pause();
            _context.Edits.Change($"{model.Name}のポーズを初期化", [.. model.Bones, .. model.Morphs], model.ResetPose);
        }
    }

    public double PositionX { get => Get(n => n.Position.X); set => Edit(PositionLabel, n => n.Position = n.Position with { X = (float)value }); }

    public double PositionY { get => Get(n => n.Position.Y); set => Edit(PositionLabel, n => n.Position = n.Position with { Y = (float)value }); }

    public double PositionZ { get => Get(n => n.Position.Z); set => Edit(PositionLabel, n => n.Position = n.Position with { Z = (float)value }); }

    public double RotationX { get => Get(n => n.Rotation.X); set => Edit("回転", n => n.Rotation = n.Rotation with { X = (float)value }); }

    public double RotationY { get => Get(n => n.Rotation.Y); set => Edit("回転", n => n.Rotation = n.Rotation with { Y = (float)value }); }

    public double RotationZ { get => Get(n => n.Rotation.Z); set => Edit("回転", n => n.Rotation = n.Rotation with { Z = (float)value }); }

    public double ScaleX { get => Get(n => n.Scale.X); set => Edit("拡大", n => n.Scale = n.Scale with { X = (float)value }); }

    public double ScaleY { get => Get(n => n.Scale.Y); set => Edit("拡大", n => n.Scale = n.Scale with { Y = (float)value }); }

    public double ScaleZ { get => Get(n => n.Scale.Z); set => Edit("拡大", n => n.Scale = n.Scale with { Z = (float)value }); }

    public double FieldOfView
    {
        get => (_node as CameraNode)?.FieldOfView ?? 0;
        set => Edit("視野角", n => ((CameraNode)n).FieldOfView = (float)value);
    }

    public double NearClip
    {
        get => (_node as CameraNode)?.NearClip ?? 0;
        set => Edit("クリップ（近）", n => ((CameraNode)n).NearClip = (float)value);
    }

    public double FarClip
    {
        get => (_node as CameraNode)?.FarClip ?? 0;
        set => Edit("クリップ（遠）", n => ((CameraNode)n).FarClip = (float)value);
    }

    public double Intensity
    {
        get => (_node as LightNode)?.Intensity ?? 0;
        set => Edit("明るさ", n => ((LightNode)n).Intensity = (float)value);
    }

    public bool SpinWithFrame
    {
        get => (_node as TestObjectNode)?.SpinWithFrame ?? false;
        set => Edit("回転の確認", n => ((TestObjectNode)n).SpinWithFrame = value);
    }

    private double Get(Func<SceneNode, float> read) => _node is null ? 0 : read(_node);

    // 値を変える。元に戻せるように履歴へ積み、同じ欄を続けて変えたとき（ホイール・つまみ）は 1 つにまとめる
    private void Edit(string what, Action<SceneNode> apply)
    {
        if (_node is null || !_node.CanEdit)
        {
            OnPropertyChanged(string.Empty);
            return;
        }
        _context.Playback.Pause();
        SceneNode node = _node;
        _context.Edits.Change($"{node.Name}の{what}", [node], () => apply(node), mergeKey: $"{node.Id}:{what}");
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
