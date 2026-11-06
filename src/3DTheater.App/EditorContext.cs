using System.Numerics;
using Theater3D.Animation;
using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Pmx;

namespace Theater3D.App;

/// <summary>
/// 読み込んだモデル 1 体の中身。ファイルの中身・読み込みレポート・GPU に載せた形と、ボーン・モーフの計算に使うもの。
/// 姿勢を変えると <see cref="IsPoseDirty"/> が立ち、次の描画の前に変形し直して GPU へ送る（<see cref="PoseUpdater"/>）。
/// GPU の形はエンジンより先に破棄する（<see cref="EditorContext.DisposeModels"/>）。
/// </summary>
public sealed class LoadedModel : IDisposable
{
    internal LoadedModel(PmxDocument document, LoadReport report, NativeMesh? mesh, ModelRig rig, Import.PreparedModel prepared)
    {
        Document = document;
        Report = report;
        Mesh = mesh;
        Rig = rig;
        Indices = prepared.Indices;
        CoreVertices = prepared.CoreVertices;
        DisplayBoundsMin = prepared.DisplayBoundsMin;
        DisplayBoundsMax = prepared.DisplayBoundsMax;
        Input = new PoseInput(rig);
        State = new PoseState(rig);
        Vertices = new SkinnedVertex[rig.RestPositions.Length];
        Positions = (Vector3[])rig.RestPositions.Clone();
        MaterialStates = new MaterialState[rig.BaseMaterials.Length];
        HasMaterialMorphs = rig.Morphs.Any(m => m.Type == PmxMorphType.Material && m.MaterialOffsets.Length > 0);
        BoundsMin = prepared.BoundsMin;
        BoundsMax = prepared.BoundsMax;
        PoseEvaluator.Evaluate(rig, Input, State); // ボーンの表示のため、初期の姿勢も計算しておく
        IsPoseDirty = true; // 最初の描画の前に 1 回変形し、選択の枠を見た目の箱にそろえる
    }

    public PmxDocument Document { get; }

    public LoadReport Report { get; }

    /// <summary>GPU に載せた形。描画エンジンが無い・載せられなかったときは null。</summary>
    public NativeMesh? Mesh { get; private set; }

    public ModelRig Rig { get; }

    public PoseInput Input { get; }

    public PoseState State { get; }

    /// <summary>変形した後の頂点（GPU へ送る形）。</summary>
    public SkinnedVertex[] Vertices { get; }

    /// <summary>変形した後の頂点の位置（モデルの座標で）。選択の当たり判定に使う。</summary>
    public Vector3[] Positions { get; }

    public MaterialState[] MaterialStates { get; }

    public bool HasMaterialMorphs { get; }

    /// <summary>描画に使っている（壊れた番号を無害にした後の）面の頂点番号。</summary>
    public uint[] Indices { get; }

    /// <summary>今の姿勢のすべての頂点を囲む箱（モデルの座標で）。当たり判定に使う。</summary>
    public Vector3 BoundsMin { get; internal set; }

    public Vector3 BoundsMax { get; internal set; }

    /// <summary>見た目の箱に入れる頂点の印（遠くに隠した部品は false。<see cref="Editor.DisplayBounds"/>）。</summary>
    public bool[] CoreVertices { get; }

    /// <summary>今の姿勢の「見た目の箱」。画面に収めるとき・選択の枠・ボーンの印の大きさに使う。</summary>
    public Vector3 DisplayBoundsMin { get; internal set; }

    public Vector3 DisplayBoundsMax { get; internal set; }

    /// <summary>見た目の大きさ（見た目の箱の対角線の長さ）。</summary>
    public float DisplaySize => (DisplayBoundsMax - DisplayBoundsMin).Length();

    /// <summary>姿勢を変えたが、まだ変形し直していない。</summary>
    public bool IsPoseDirty { get; set; }

    public void Dispose()
    {
        Mesh?.Dispose();
        Mesh = null;
    }
}

/// <summary>
/// 画面のどこからでも共有する編集中の状態（シーン・選択・再生位置・描画）。
/// シーンの値・選択・再生位置のどれかが変わると、全プレビューを描き直す。
/// </summary>
public sealed class EditorContext
{
    public EditorContext(NativeEngine? engine)
    {
        Engine = engine;
        Scene = SceneDocument.CreateDefault();
        Selection = new SelectionService();
        Playback = new PlaybackService();
        RenderLoop = new RenderLoop(engine, Playback);
        Animation = new AnimationService(Scene, Playback);
        Poses = new PoseUpdater(this);

        Scene.NodeChanged += (_, _) => RenderLoop.InvalidateAll();
        Selection.Changed += (_, _) => RenderLoop.InvalidateAll();
        Playback.FrameChanged += (_, _) => RenderLoop.InvalidateAll();
        Playback.StateChanged += (_, _) => RenderLoop.RequestRender();
    }

    public NativeEngine? Engine { get; }

    public SceneDocument Scene { get; }

    public SelectionService Selection { get; }

    public PlaybackService Playback { get; }

    public RenderLoop RenderLoop { get; }

    /// <summary>キーフレームを再生位置に合わせて当てはめ、未登録の変更を管理する。</summary>
    public AnimationService Animation { get; }

    /// <summary>ボーン・モーフの値が変わったモデルを、描画の前に変形し直す。</summary>
    public PoseUpdater Poses { get; }

    /// <summary>
    /// GPU の面を WPF と共有できない環境（ソフトウェア描画など）だと分かったら true。
    /// 以後の区画は、描いた絵を読み出して表示する予備の方式で作る。
    /// </summary>
    public bool SurfaceSharingUnavailable { get; set; }

    /// <summary>読み込んだモデルの中身（シーンの <see cref="ModelNode"/> の Id で引く）。</summary>
    public Dictionary<Guid, LoadedModel> Models { get; } = [];

    /// <summary>シーンの「モデル」の並び順で、見えていて GPU に載っているモデル。</summary>
    public IEnumerable<(ModelNode Node, LoadedModel Model)> VisibleModels =>
        Scene.ModelsGroup.Children.OfType<ModelNode>()
            .Where(n => n.IsEffectivelyVisible && Models.TryGetValue(n.Id, out LoadedModel? m) && m.Mesh is not null)
            .Select(n => (n, Models[n.Id]));

    /// <summary>GPU に載せたモデルを破棄する（エンジンを破棄する前に呼ぶ）。</summary>
    public void DisposeModels()
    {
        foreach (LoadedModel model in Models.Values)
        {
            model.Dispose();
        }
    }

    /// <summary>
    /// 今操作の対象にしているモデル（選んだモデル、または選んだボーン・モーフの持ち主）。
    /// ボーンの表示とモーフのパネルはこのモデルを対象にする。
    /// </summary>
    public ModelNode? ActiveModel => Selection.Primary switch
    {
        ModelNode model => model,
        { } node => node.FindAncestor<ModelNode>(),
        null => null,
    };

    public LoadedModel? GetLoaded(ModelNode? node) => node is not null && Models.TryGetValue(node.Id, out LoadedModel? model) ? model : null;

    public CameraNode? MainCamera => Scene.FindFirst<CameraNode>();

    public TestObjectNode? TestObject => Scene.FindFirst<TestObjectNode>();

    public SceneNode? StageGrid => Scene.AllNodes.FirstOrDefault(n => n.Kind == SceneNodeKind.Stage);
}
