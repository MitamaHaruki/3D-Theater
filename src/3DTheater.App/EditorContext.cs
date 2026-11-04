using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App;

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

    public CameraNode? MainCamera => Scene.FindFirst<CameraNode>();

    public TestObjectNode? TestObject => Scene.FindFirst<TestObjectNode>();

    public SceneNode? StageGrid => Scene.AllNodes.FirstOrDefault(n => n.Kind == SceneNodeKind.Stage);
}
