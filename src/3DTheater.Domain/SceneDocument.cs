using System.ComponentModel;
using System.Numerics;

namespace Theater3D.Domain;

public sealed class SceneNodeChangedEventArgs(SceneNode node, string? propertyName) : EventArgs
{
    public SceneNode Node { get; } = node;

    public string? PropertyName { get; } = propertyName;
}

/// <summary>1 つのシーン。項目の木と、どれかの項目が変わったときの通知を持つ。</summary>
public sealed class SceneDocument
{
    private SceneDocument(SceneNode root)
    {
        Root = root;
        foreach (SceneNode node in root.DescendantsAndSelf())
        {
            node.PropertyChanged += OnNodePropertyChanged;
        }
    }

    public SceneNode Root { get; }

    /// <summary>読み込んだモデルを置くグループ。</summary>
    public SceneNode ModelsGroup { get; private set; } = null!;

    /// <summary>音声を置くグループ（音声は 1 つまで）。</summary>
    public SceneNode AudioGroup { get; private set; } = null!;

    /// <summary>照明を置くグループ（最初の照明は MMD の照明で外せない。「照明を追加」で足した照明はその後ろに並ぶ）。</summary>
    public SceneNode LightsGroup { get; private set; } = null!;

    /// <summary>映像効果（映像効果の値を動かすキーを持つ）。</summary>
    public EffectsNode Effects { get; private set; } = null!;

    /// <summary>セルフ影（セルフシャドウを使うかを切り替えるキーを持つ）。</summary>
    public SelfShadowNode SelfShadow { get; private set; } = null!;

    /// <summary>時刻（サンライトと画面の色合いを決め、時刻を動かすキーを持つ。設計判断記録 88 章）。</summary>
    public TimeOfDayNode TimeOfDay { get; private set; } = null!;

    /// <summary>MMD の照明の初めの名前。太陽として扱う（時刻で高さと色を変える。設計判断記録 87 章）。</summary>
    public const string MainLightName = "サンライト";

    /// <summary>前の版の、MMD の照明の初めの名前（このままのファイルを開くと、<see cref="MainLightName"/> にする）。</summary>
    public const string OldMainLightName = "ディレクショナルライト";

    /// <summary>MMD の照明（決まった役目の「light」）。太陽として扱う。</summary>
    public LightNode MainLight => (LightNode)_fixedNodes["light"];

    /// <summary>カメラを置くグループ（最初のカメラは決まった役目の「camera」で外せない。ライブモードで足したカメラはその後ろに並ぶ）。</summary>
    public SceneNode CamerasGroup => _fixedNodes["group.cameras"];

    /// <summary>決まった役目のカメラ（エディットモードのシーンのカメラ・ライブモードの「ライブカメラ」）。</summary>
    public CameraNode MainCamera => (CameraNode)_fixedNodes["camera"];

    /// <summary>ライブモードで足したカメラ（決まった役目のカメラのほか。並び順）。</summary>
    public IEnumerable<CameraNode> AddedCameras => CamerasGroup.Children.OfType<CameraNode>().Where(c => !ReferenceEquals(c, MainCamera));

    /// <summary>「照明を追加」で足した照明（MMD の照明のほか。並び順）。</summary>
    public IEnumerable<LightNode> AddedLights => LightsGroup.Children.OfType<LightNode>().Where(l => !ReferenceEquals(l, MainLight));

    /// <summary>今の音声（無ければ null）。</summary>
    public AudioNode? Audio => AudioGroup.Children.OfType<AudioNode>().FirstOrDefault();

    /// <summary>どれかの項目の値が変わった。</summary>
    public event EventHandler<SceneNodeChangedEventArgs>? NodeChanged;

    public IEnumerable<SceneNode> AllNodes => Root.DescendantsAndSelf();

    public SceneNode? FindById(Guid id) => AllNodes.FirstOrDefault(n => n.Id == id);

    public T? FindFirst<T>() where T : SceneNode => AllNodes.OfType<T>().FirstOrDefault();

    /// <summary>
    /// 決まった役目の項目（プロジェクトの保存で名前の代わりに使う）。名前は変えられることがあるため。
    /// "camera"・"light"・"testObject"・"stage" と、まとまりの "group.cameras"・"group.lights"・"group.models"・"group.stage"・"group.audio"。
    /// </summary>
    public IReadOnlyDictionary<string, SceneNode> FixedNodes => _fixedNodes;

    private readonly Dictionary<string, SceneNode> _fixedNodes = [];
    private readonly Dictionary<SceneNode, NodeState> _defaults = [];

    /// <summary>項目をシーンから外す（モデルを外す・新しいプロジェクトにするとき）。決まった役目の項目は外せない。</summary>
    public void Remove(SceneNode child)
    {
        if (child.Parent is not { } parent)
        {
            return;
        }
        if (_fixedNodes.ContainsValue(child) || child == Root)
        {
            throw new InvalidOperationException($"「{child.Name}」はシーンから外せません");
        }
        foreach (SceneNode node in child.DescendantsAndSelf())
        {
            node.PropertyChanged -= OnNodePropertyChanged;
        }
        parent.RemoveChild(child);
        NodeChanged?.Invoke(this, new SceneNodeChangedEventArgs(parent, nameof(SceneNode.Children)));
    }

    /// <summary>新しいプロジェクトの状態に戻す（読み込んだモデル・音声・足した照明を外し、カメラ・照明などを初期の値にする）。外したモデルを返す。</summary>
    public IReadOnlyList<ModelNode> ResetToDefault()
    {
        ModelNode[] models = [.. ModelsGroup.Children.OfType<ModelNode>()];
        foreach (ModelNode model in models)
        {
            Remove(model);
        }
        foreach (SceneNode audio in AudioGroup.Children.ToArray())
        {
            Remove(audio);
        }
        foreach (LightNode light in AddedLights.ToArray())
        {
            Remove(light);
        }
        foreach (CameraNode camera in AddedCameras.ToArray())
        {
            Remove(camera);
        }
        foreach (var (node, state) in _defaults)
        {
            node.RestoreState(state);
        }
        return models;
    }

    /// <summary>項目を、親の子の <paramref name="index"/> 番目に入れる（外したモデルを元の場所に戻すとき）。</summary>
    public void Insert(SceneNode parent, int index, SceneNode child)
    {
        parent.InsertChild(index, child);
        foreach (SceneNode node in child.DescendantsAndSelf())
        {
            node.PropertyChanged += OnNodePropertyChanged;
        }
        NodeChanged?.Invoke(this, new SceneNodeChangedEventArgs(parent, nameof(SceneNode.Children)));
    }

    public void Add(SceneNode parent, SceneNode child)
    {
        parent.AddChild(child);
        foreach (SceneNode node in child.DescendantsAndSelf())
        {
            node.PropertyChanged += OnNodePropertyChanged;
        }
        NodeChanged?.Invoke(this, new SceneNodeChangedEventArgs(parent, nameof(SceneNode.Children)));
    }

    /// <summary>新しいプロジェクトの初期状態。モデル読み込みができるまでは確認用の立方体を置く。</summary>
    public static SceneDocument CreateDefault()
    {
        var root = new SceneNode(SceneNodeKind.Group, "シーン", canRename: false, hasTransform: false);
        var document = new SceneDocument(root);

        var cameras = Group("カメラ");
        var lights = Group("照明");
        var models = Group("モデル");
        var stage = Group("ステージ");
        var audio = Group("音声");
        var effects = new EffectsNode();
        var selfShadow = new SelfShadowNode();
        var timeOfDay = new TimeOfDayNode();
        document.Add(root, cameras);
        document.Add(root, lights);
        document.Add(root, timeOfDay);
        document.Add(root, effects);
        document.Add(root, selfShadow);
        document.Add(root, models);
        document.Add(root, stage);
        document.Add(root, audio);

        // MMD の初期のカメラと同じ（注視点 (0, 10, 0)、距離 45、視野角 30）。モデルの全身が収まる
        document.Add(cameras, new CameraNode("メインカメラ")
        {
            FieldOfView = 30f,
            TargetDistance = 45f,
            Position = new Vector3(0f, 10f, -45f),
            Rotation = Vector3.Zero,
        });
        // MMD の初期の照明と同じ向き（光が進む向き (-0.5, -1, 0.5)。下向き 54.7°、左右 -45°）。太陽として扱う（設計判断記録 87 章）
        document.Add(lights, new LightNode(MainLightName) { Rotation = new Vector3(54.73561f, -45f, 0f) });
        document.Add(models, new TestObjectNode("テスト用立方体") { Position = new Vector3(0f, 0.5f, 0f) });
        var grid = new SceneNode(SceneNodeKind.Stage, "床グリッド", hasTransform: false);
        document.Add(stage, grid);
        document.ModelsGroup = models;
        document.AudioGroup = audio;
        document.LightsGroup = lights;
        document.Effects = effects;
        document.SelfShadow = selfShadow;
        document.TimeOfDay = timeOfDay;

        document._fixedNodes["group.cameras"] = cameras;
        document._fixedNodes["group.lights"] = lights;
        document._fixedNodes["group.models"] = models;
        document._fixedNodes["group.stage"] = stage;
        document._fixedNodes["group.audio"] = audio;
        document._fixedNodes["camera"] = cameras.Children[0];
        document._fixedNodes["light"] = lights.Children[0];
        document._fixedNodes["testObject"] = models.Children[0];
        document._fixedNodes["stage"] = grid;
        document._fixedNodes["effects"] = effects;
        document._fixedNodes["selfShadow"] = selfShadow;
        document._fixedNodes["timeOfDay"] = timeOfDay;
        foreach (SceneNode node in document._fixedNodes.Values.Append(root))
        {
            // キーも含めて覚える（新しいプロジェクトに戻すと、カメラ・照明・映像効果のキーも消える）
            document._defaults[node] = node.CaptureState(includeKeys: true);
        }
        return document;

        static SceneNode Group(string name) => new(SceneNodeKind.Group, name, canRename: false, hasTransform: false);
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SceneNode node)
        {
            NodeChanged?.Invoke(this, new SceneNodeChangedEventArgs(node, e.PropertyName));
        }
    }
}
