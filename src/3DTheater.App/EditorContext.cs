using System.Numerics;
using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Pmx;

namespace Theater3D.App;

/// <summary>
/// 読み込んだモデル 1 体の中身。ファイルの中身・読み込みレポート・GPU に載せた形と、選択の当たり判定に使う頂点の位置。
/// GPU の形はエンジンより先に破棄する（<see cref="EditorContext.DisposeModels"/>）。
/// </summary>
public sealed class LoadedModel(PmxDocument document, LoadReport report, NativeMesh? mesh, Vector3[] positions, uint[] indices) : IDisposable
{
    public PmxDocument Document { get; } = document;

    public LoadReport Report { get; } = report;

    /// <summary>GPU に載せた形。描画エンジンが無い・載せられなかったときは null。</summary>
    public NativeMesh? Mesh { get; private set; } = mesh;

    public Vector3[] Positions { get; } = positions;

    /// <summary>描画に使っている（壊れた番号を無害にした後の）面の頂点番号。</summary>
    public uint[] Indices { get; } = indices;

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

    public CameraNode? MainCamera => Scene.FindFirst<CameraNode>();

    public TestObjectNode? TestObject => Scene.FindFirst<TestObjectNode>();

    public SceneNode? StageGrid => Scene.AllNodes.FirstOrDefault(n => n.Kind == SceneNodeKind.Stage);
}
