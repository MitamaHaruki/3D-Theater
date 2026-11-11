using System.Numerics;
using Theater3D.Animation;
using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Pmx;
using Theater3D.Physics;

namespace Theater3D.App;

/// <summary>
/// 読み込んだモデル 1 体の中身。ファイルの中身・読み込みレポート・GPU に載せた形と、ボーン・モーフの計算に使うもの。
/// 姿勢を変えると <see cref="IsPoseDirty"/> が立ち、次の描画の前に変形し直して GPU へ送る（<see cref="PoseUpdater"/>）。
/// GPU の形はエンジンより先に破棄する（<see cref="EditorContext.DisposeModels"/>）。
/// </summary>
public sealed class LoadedModel : IDisposable
{
    internal LoadedModel(PmxDocument document, LoadReport report, NativeMesh? mesh, ModelRig rig, Import.PreparedModel prepared, ModelPhysics? physics)
    {
        Document = document;
        Report = report;
        Mesh = mesh;
        Rig = rig;
        Physics = physics;
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

    /// <summary>剛体とジョイントの物理演算。剛体の無いモデル・物理を使えなかったときは null。</summary>
    public ModelPhysics? Physics { get; private set; }

    /// <summary>物理の焼き込み（0 フレーム目から順に計算した結果）。最初に要るときに作る（<see cref="PhysicsController"/>）。</summary>
    public PhysicsBake? Bake { get; internal set; }

    /// <summary>焼き込みのときに、各フレームのボーン・モーフの値を入れる入れ物（画面の <see cref="Input"/> とは別）。</summary>
    internal PoseInput? BakeInput { get; set; }

    /// <summary>焼き込みに失敗した（作り直さない）。</summary>
    internal bool BakeFailed { get; set; }

    /// <summary>画面の物理・焼き込みに置いた障害物（ほかのモデルの体）の組み合わせ（変わったら置き直す目印）。</summary>
    internal string? LiveObstacleSignature { get; set; }

    internal string? BakeObstacleSignature { get; set; }

    /// <summary>障害物の置き方を入れる入れ物。</summary>
    internal Theater3D.Physics.PhysicsTransform[] ObstacleBuffer { get; set; } = [];

    /// <summary>このモデルを、ほかのモデルの焼き込みの障害物にするときに、そのフレームの姿勢を求める入れ物。</summary>
    internal PoseInput? ObstacleInput { get; set; }

    internal PoseState? ObstacleState { get; set; }

    /// <summary>今の表示が焼き込みの結果か（焼き込みから画面の物理に移るとき、画面の物理を今の姿勢から始め直すため）。</summary>
    internal bool ShowingBakedPhysics { get; set; }

    /// <summary>物理演算で失敗したとき、このモデルの物理をやめる（姿勢は物理なしで計算する）。</summary>
    public void DisablePhysics()
    {
        Physics?.Dispose();
        Physics = null;
        Bake?.Dispose();
        Bake = null;
    }

    public PoseInput Input { get; }

    public PoseState State { get; }

    /// <summary>変形した後の頂点（GPU へ送る形）。</summary>
    public SkinnedVertex[] Vertices { get; }

    /// <summary>変形した後の頂点の位置（モデルの座標で）。選択の当たり判定に使う。</summary>
    public Vector3[] Positions { get; }

    public MaterialState[] MaterialStates { get; }

    public bool HasMaterialMorphs { get; }

    /// <summary>描画エンジンへ送った材質の上書き（モデルの項目の値と違えば、次の変形で送り直す）。</summary>
    public MaterialOverrideSet AppliedMaterialOverrides { get; set; } = MaterialOverrideSet.Empty;

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
        DisablePhysics();
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
        Animation = new AnimationService(Scene, Playback) { Rendering = Rendering }; // 映像効果のキーの当てはめ先
        Undo = new UndoService();
        Edits = new EditService(Undo, Animation, Playback);
        KeySelection = new KeySelectionService();
        Gizmo = new GizmoSettings();
        Gizmo.Changed += (_, _) => RenderLoop.InvalidateAll();
        Undo.Changed += (_, _) => KeySelection.Prune(); // 元に戻したキーは選択から外す
        Physics = new PhysicsController(this);
        Audio = new Audio.AudioService(this);
        Poses = new PoseUpdater(this);
        MorphPresets = Shell.MorphPresetStore.Load();
        EnvironmentImage = new EnvironmentImage(engine, Rendering);
        Rendering.Changed += (_, _) => RenderLoop.InvalidateAll();
        PreviewQuality.Changed += (_, _) => RenderLoop.InvalidateAll();

        Scene.NodeChanged += (_, _) => RenderLoop.InvalidateAll();
        Selection.Changed += (_, _) => RenderLoop.InvalidateAll();
        Playback.FrameChanged += (_, _) => RenderLoop.InvalidateAll();
        Playback.StateChanged += (_, _) => RenderLoop.RequestRender();
    }

    public NativeEngine? Engine { get; }

    /// <summary>作品の描き方（セルフシャドウ・地面の影。プロジェクトに保存する）。</summary>
    public RenderSettings Rendering { get; } = new();

    /// <summary>描き方の設定の、環境の画像を GPU に載せたもの。</summary>
    public EnvironmentImage EnvironmentImage { get; }

    /// <summary>
    /// 書き出しの大きさ（書き出しの画面で入れたもの）。カメラ視点の区画に、この縦横の比の枠を、書き出しと同じ写り方で出す。
    /// </summary>
    public (int Width, int Height) ExportSize { get; private set; } = (1920, 1080);

    /// <summary>書き出しの縦横の比。</summary>
    public float ExportAspect => ExportSize.Width / (float)ExportSize.Height;

    /// <summary>書き出しの大きさが変わった。</summary>
    public event EventHandler? ExportSizeChanged;

    /// <summary>書き出しの大きさを変える（1 以上でなければ変えない）。</summary>
    public void SetExportSize(int width, int height)
    {
        if (width < 1 || height < 1 || (width, height) == ExportSize)
        {
            return;
        }
        ExportSize = (width, height);
        ExportSizeChanged?.Invoke(this, EventArgs.Empty);
        RenderLoop.InvalidateAll();
    }

    /// <summary>プレビューの品質（なめらかさ・影の細かさ。利用者ごとの設定で、プロジェクトには入れない）。</summary>
    public PreviewQualitySettings PreviewQuality { get; } = new();

    /// <summary>表情のプリセット（利用者ごと。変わるたびに保存する）。</summary>
    public MorphPresetLibrary MorphPresets { get; }

    public SceneDocument Scene { get; }

    public SelectionService Selection { get; }

    public PlaybackService Playback { get; }

    public RenderLoop RenderLoop { get; }

    /// <summary>元に戻す・やり直しの履歴。</summary>
    public UndoService Undo { get; }

    /// <summary>シーンの項目の編集の入口（元に戻せるように変える前後の値を残す）。画面からの編集はすべてここを通す。</summary>
    public EditService Edits { get; }

    /// <summary>プレビューの道具（プラグインの編集の道具を選んでいるか）。</summary>
    public Shell.ViewportTools Tools { get; } = new();

    /// <summary>右クリックのメニューに外から足す項目（プラグイン）。</summary>
    public Shell.ContextMenuExtensions ContextMenus { get; } = new();

    /// <summary>ギズモの方式・道具・座標（全プレビューで共通）。</summary>
    public GizmoSettings Gizmo { get; }

    /// <summary>タイムラインで選んでいるキー。</summary>
    public KeySelectionService KeySelection { get; }

    /// <summary>コピーしたキー（アプリの中だけで使う）。</summary>
    public KeyClipboard? KeyClipboard { get; set; }

    /// <summary>キーフレームを再生位置に合わせて当てはめ、未登録の変更を管理する。</summary>
    public AnimationService Animation { get; }

    /// <summary>ボーン・モーフの値が変わったモデルを、描画の前に変形し直す。</summary>
    public PoseUpdater Poses { get; }

    /// <summary>物理演算の ON/OFF と進め方。</summary>
    public PhysicsController Physics { get; }

    /// <summary>音声の再生と、再生位置の時計。</summary>
    public Audio.AudioService Audio { get; }

    /// <summary>プロジェクトを開いたときに見つからなかった音声（保存し直しても消さずに、そのまま書き戻す）。</summary>
    public AudioEntry? MissingAudio { get; set; }

    /// <summary>GPU が使えないなめらかさを、少ない数に減らしたことをログに残したか（1 度だけ残す）。</summary>
    public bool SampleCountFallbackReported { get; set; }

    /// <summary>
    /// GPU の面を WPF と共有できない環境（ソフトウェア描画など）だと分かったら true。
    /// 以後の区画は、描いた絵を読み出して表示する予備の方式で作る。
    /// </summary>
    public bool SurfaceSharingUnavailable { get; set; }

    /// <summary>読み込んだモデルの中身（シーンの <see cref="ModelNode"/> の Id で引く）。</summary>
    public Dictionary<Guid, LoadedModel> Models { get; } = [];

    /// <summary>シーンの「モデル」の並び順で、見えていて（目の印と表示・IK の表示の両方）GPU に載っているモデル。</summary>
    public IEnumerable<(ModelNode Node, LoadedModel Model)> VisibleModels =>
        Scene.ModelsGroup.Children.OfType<ModelNode>()
            .Where(n => n.IsEffectivelyVisible && n.IsShownByDisplay && Models.TryGetValue(n.Id, out LoadedModel? m) && m.Mesh is not null)
            .Select(n => (n, Models[n.Id]));

    /// <summary>プロジェクトを開いたときに見つからなかったモデル（保存し直しても消さずに、そのまま書き戻す）。</summary>
    public List<ModelEntry> MissingModels { get; } = [];

    /// <summary>新しいプロジェクトの状態に戻す（モデルを外して GPU の資源を手放し、履歴・選択・再生の設定を初期にする）。</summary>
    public void ResetProject()
    {
        Playback.Stop();
        Selection.Clear();
        KeySelection.Clear();
        KeyClipboard = null;
        using (Animation.SuspendTracking())
        {
            foreach (ModelNode model in Scene.ResetToDefault())
            {
                if (Models.Remove(model.Id, out LoadedModel? loaded))
                {
                    loaded.Dispose();
                }
            }
        }
        MissingModels.Clear();
        MissingAudio = null;
        Playback.FrameRate = 30;
        Playback.EndFrame = 300;
        Playback.Loop = true;
        Animation.AutoKey = false;
        Physics.Enabled = true;
        Physics.FloorEnabled = true;
        Physics.BetweenModels = true;
        Rendering.Reset();
        Animation.NotifyStateChanged();
        Undo.Clear();
        RenderLoop.InvalidateAll();
    }

    /// <summary>GPU に載せたモデルと環境の画像を破棄する（エンジンを破棄する前に呼ぶ）。</summary>
    public void DisposeModels()
    {
        foreach (LoadedModel model in Models.Values)
        {
            model.Dispose();
        }
        EnvironmentImage.Dispose();
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

    /// <summary>描画に使う照明（最初の照明の項目）。</summary>
    public LightNode? MainLight => Scene.MainLight;

    public TestObjectNode? TestObject => Scene.FindFirst<TestObjectNode>();

    public SceneNode? StageGrid => Scene.AllNodes.FirstOrDefault(n => n.Kind == SceneNodeKind.Stage);
}
