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

    /// <summary>どれかの項目の値が変わった。</summary>
    public event EventHandler<SceneNodeChangedEventArgs>? NodeChanged;

    public IEnumerable<SceneNode> AllNodes => Root.DescendantsAndSelf();

    public SceneNode? FindById(Guid id) => AllNodes.FirstOrDefault(n => n.Id == id);

    public T? FindFirst<T>() where T : SceneNode => AllNodes.OfType<T>().FirstOrDefault();

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
        document.Add(root, cameras);
        document.Add(root, lights);
        document.Add(root, models);
        document.Add(root, stage);

        document.Add(cameras, new CameraNode("メインカメラ")
        {
            Position = new Vector3(0f, 2.2f, -6f),
            Rotation = new Vector3(15f, 0f, 0f),
        });
        document.Add(lights, new LightNode("ディレクショナルライト") { Rotation = new Vector3(45f, -30f, 0f) });
        document.Add(models, new TestObjectNode("テスト用立方体") { Position = new Vector3(0f, 0.5f, 0f) });
        document.Add(stage, new SceneNode(SceneNodeKind.Stage, "床グリッド", hasTransform: false));
        document.ModelsGroup = models;
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
