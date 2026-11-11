using System.Diagnostics;
using System.Numerics;
using System.Windows.Threading;
using Theater3D.Animation;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Physics;

namespace Theater3D.App.Rendering;

/// <summary>
/// 物理演算の ON/OFF と、表示する物理の決め方を受け持つ。
/// - 焼き込み（<see cref="PhysicsBake"/>）: 0 フレーム目から順に計算した結果を、画面の手が空いたときに少しずつ先へ作っておく。
///   作り終えたフレームはその結果を表示するので、再生位置をどう動かしても同じフレームなら同じ姿勢になる。
///   ボーン・モーフのキーや値・モデルの傾き・フレームの速さが変わったら、捨てて作り直す。
/// - 画面の物理: 焼き込みがまだのフレームと、止まっているときに姿勢を変えた直後（しばらく実際の時間で動かす）は、
///   画面用の物理を <see cref="PhysicsClock"/> の進め方（<see cref="Current"/>）で計算する。
/// 焼き込みは UI スレッドで小分けにして行う（シーンの値を別のスレッドから読まないように）。
/// </summary>
public sealed class PhysicsController
{
    /// <summary>手が空いたときの焼き込みで、1 回に使う時間の上限。</summary>
    private static readonly TimeSpan BakeSlice = TimeSpan.FromMilliseconds(8);

    /// <summary>焼き込みが足りないとき、その場で計算して追いつく上限（再生中・止まっているとき）。</summary>
    private const int CatchUpWhilePlaying = 10;
    private const int CatchUpWhilePaused = 90;

    private readonly EditorContext _context;
    private readonly PhysicsClock _clock = new();
    private bool _enabled = true;
    private bool _floorEnabled = true;
    private bool _betweenModels = true;
    private bool _bakeScheduled;
    private bool _wasSettling;
    private long _bakeStarted;
    private bool _bakeLogged = true;
    private int _frameRate;
    private int _endFrame;

    internal PhysicsController(EditorContext context)
    {
        _context = context;
        _frameRate = context.Playback.FrameRate;
        _endFrame = context.Playback.EndFrame;
        context.RenderLoop.Ticking += (_, elapsed) => OnTick(elapsed);
        context.RenderLoop.KeepRunning = () => _enabled && HasPhysicsModels && _clock.IsSettling(DateTime.UtcNow);
        context.Scene.NodeChanged += OnNodeChanged;
        context.Playback.StateChanged += (_, _) => OnPlaybackStateChanged();
    }

    /// <summary>焼き込みの進み具合（全モデルのうち、いちばん遅れているモデル）。焼き込みが無ければ null。</summary>
    public (int Done, int Total)? BakeProgress
    {
        get
        {
            int total = _context.Playback.EndFrame + 1;
            int[] done = [.. PhysicsModels.Select(p => p.Model.Bake?.Count ?? 0)];
            return done.Length == 0 ? null : (Math.Min(done.Min(), total), total);
        }
    }

    /// <summary>モデルを置いた向きに合わせた、モデルの座標での重力（物理はモデルの座標で計算する）。</summary>
    public static Vector3 GravityFor(ModelNode node) =>
        Vector3.Transform(new Vector3(0, -ModelPhysics.GravityStrength, 0), Quaternion.Inverse(SceneRotation.ToQuaternion(node.Rotation)));

    /// <summary>
    /// モデルの座標での床（物理はモデルの座標で計算するので、シーンの床（高さ 0）を、モデルの置き方の逆で直す）。
    /// 拡大は縦（Y）の倍率で直す（縦横で違う拡大は考えない）。<paramref name="enabled"/> が false なら床なし。
    /// </summary>
    public static PhysicsFloor FloorFor(ModelNode node, bool enabled)
    {
        if (!enabled)
        {
            return PhysicsFloor.None;
        }
        return PhysicsFloor.SceneFloorInModel(SceneRotation.ToQuaternion(node.Rotation), node.Position, node.Scale.Y);
    }

    /// <summary>このモデルの今の床（床と当てる設定のとき）。</summary>
    public PhysicsFloor FloorFor(ModelNode node) => FloorFor(node, _floorEnabled);

    /// <summary>
    /// 物理で動く剛体（髪・スカートなど）を、ほかのモデルの体（ボーン追従の剛体）にも当てるか（初期値は ON。プロジェクトに保存する）。
    /// ほかのモデルの体は、そのモデルの動きどおりに置く障害物になる（ほかのモデルの髪どうしは当たらない）。変えると、剛体を置き直して
    /// 焼き込みも作り直す。
    /// </summary>
    public bool BetweenModels
    {
        get => _betweenModels;
        set
        {
            if (_betweenModels == value)
            {
                return;
            }
            _betweenModels = value;
            AppLog.Info(value ? "物理のモデルどうしの当たりを ON にしました" : "物理のモデルどうしの当たりを OFF にしました");
            Reset();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>モデルの置き方（物理の座標の行き来に使う）。</summary>
    internal static ModelPlacement PlacementOf(ModelNode node) => new(node.Position, SceneRotation.ToQuaternion(node.Rotation), node.Scale.Y);

    // このモデルの物理に障害物として置く、ほかのモデル（見えていて、ボーン追従の剛体のあるもの）
    private IEnumerable<(ModelNode Node, LoadedModel Model)> ObstacleModels(ModelNode node) =>
        _betweenModels
            ? PhysicsModels.Where(p => p.Node != node && p.Node.IsEffectivelyVisible && p.Model.Physics!.FollowBodyCount > 0)
            : [];

    /// <summary>障害物の組み合わせの目印（どのモデルの体を、どの大きさで置くか）。</summary>
    internal string ObstacleSignature(ModelNode node)
    {
        ModelPlacement placement = PlacementOf(node);
        return string.Join(";", ObstacleModels(node).Select(o => $"{o.Node.Id}:{placement.ScaleFrom(PlacementOf(o.Node)):R}"));
    }

    /// <summary>障害物の形とグループ（このモデルの座標で）。</summary>
    internal PhysicsBodyDesc[] ObstacleDescs(ModelNode node)
    {
        ModelPlacement placement = PlacementOf(node);
        return [.. ObstacleModels(node).SelectMany(o => o.Model.Physics!.FollowBodyDescs(placement.ScaleFrom(PlacementOf(o.Node))))];
    }

    /// <summary>画面の物理: 障害物の今の置き方（ほかのモデルの今の姿勢から。このモデルの座標で）。</summary>
    internal void ObstacleTargetsNow(ModelNode node, Span<PhysicsTransform> destination)
    {
        ModelPlacement placement = PlacementOf(node);
        int offset = 0;
        foreach (var (other, loaded) in ObstacleModels(node))
        {
            int count = loaded.Physics!.FollowBodyCount;
            Span<PhysicsTransform> part = destination.Slice(offset, count);
            loaded.Physics.FollowBodyTransforms(loaded.State, part);
            ToThisModel(part, placement, PlacementOf(other));
            offset += count;
        }
    }

    /// <summary>
    /// 焼き込み: <paramref name="frame"/> の障害物の置き方。ほかのモデルの、そのフレームのキーの姿勢から求める（ほかのモデルの揺れは
    /// 使わないので、どの順に焼き込んでも、何度焼き直しても同じになる）。
    /// </summary>
    private void ObstacleTargetsAt(ModelNode node, int frame, Span<PhysicsTransform> destination)
    {
        ModelPlacement placement = PlacementOf(node);
        int offset = 0;
        foreach (var (other, loaded) in ObstacleModels(node))
        {
            int count = loaded.Physics!.FollowBodyCount;
            PoseInput input = loaded.ObstacleInput ??= new PoseInput(loaded.Rig);
            PoseState state = loaded.ObstacleState ??= new PoseState(loaded.Rig);
            PoseSampler.Sample(other, frame, input.Translations, input.Rotations, input.MorphWeights, input.IkEnabled);
            PoseEvaluator.EvaluateBeforePhysics(loaded.Rig, input, state);
            Span<PhysicsTransform> part = destination.Slice(offset, count);
            loaded.Physics.FollowBodyTransforms(state, part);
            ToThisModel(part, placement, PlacementOf(other));
            offset += count;
        }
    }

    private static void ToThisModel(Span<PhysicsTransform> transforms, ModelPlacement self, ModelPlacement other)
    {
        for (int i = 0; i < transforms.Length; i++)
        {
            transforms[i] = self.FromModel(transforms[i], other);
        }
    }

    /// <summary>ON/OFF（物理演算・床との当たり）が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 物理で動く剛体（髪・スカートなど）を、床（高さ 0）に当てるか（初期値は ON。プロジェクトに保存する）。
    /// 変えると、剛体を置き直して焼き込みも作り直す。
    /// </summary>
    public bool FloorEnabled
    {
        get => _floorEnabled;
        set
        {
            if (_floorEnabled == value)
            {
                return;
            }
            _floorEnabled = value;
            AppLog.Info(value ? "物理の床との当たりを ON にしました" : "物理の床との当たりを OFF にしました");
            Reset();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>物理演算を使うか（初期値は ON。プロジェクトに保存する）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }
            _enabled = value;
            AppLog.Info(value ? "物理演算を ON にしました" : "物理演算を OFF にしました");
            Reset();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 書き出しの間は true にする。どのフレームも焼き込みの結果で描き（姿勢を変えた直後でも）、足りなければいくつでもその場で計算する
    /// （プレビューと書き出しで同じ姿勢になるように）。
    /// </summary>
    internal bool ForceBaked { get; set; }

    /// <summary>この画面の更新で行う物理の進め方。<see cref="PoseUpdater"/> が使ったら <see cref="PhysicsStep.None"/> に戻す。</summary>
    public PhysicsStep Current { get; private set; }

    private bool HasPhysicsModels => _context.Models.Values.Any(m => m.Physics is not null);

    private IEnumerable<(ModelNode Node, LoadedModel Model)> PhysicsModels =>
        _context.Scene.ModelsGroup.Children.OfType<ModelNode>()
            .Where(n => _context.GetLoaded(n)?.Physics is not null)
            .Select(n => (n, _context.Models[n.Id]));

    /// <summary>
    /// 剛体を今の姿勢に置き直し、焼き込みも作り直す（メニューの「物理をリセット」、物理の ON/OFF、プロジェクトを開いたとき）。
    /// </summary>
    public void Reset()
    {
        _clock.RequestReset();
        foreach (var (node, model) in PhysicsModels)
        {
            Invalidate(node, model);
        }
        foreach (LoadedModel model in _context.Models.Values)
        {
            model.IsPoseDirty = true;
        }
        _context.RenderLoop.InvalidateAll();
    }

    /// <summary>
    /// 焼き込んだ結果で、物理演算のボーンを置く（<see cref="PoseEvaluator.EvaluateBeforePhysics"/> の後に呼ぶ）。
    /// 少しだけ足りなければその場で計算して追いつく。使えないとき（足りない・止まっていて姿勢を変えた直後）は false。
    /// </summary>
    internal bool TryApplyBaked(ModelNode node, LoadedModel model, float frame)
    {
        bool playing = _context.Playback.IsPlaying;
        if (!_enabled || (!ForceBaked && !playing && _clock.IsSettling(DateTime.UtcNow)))
        {
            return false; // 姿勢を変えた直後は、画面の物理で揺れを見せる
        }
        PhysicsBake? bake = EnsureBake(node, model);
        if (bake is null)
        {
            return false;
        }
        int needed = (int)MathF.Ceiling(frame); // 小数のフレーム（モーションブラー）は、後ろのフレームまで要る
        int missing = needed - bake.Count + 1;
        if (missing > 0 && (ForceBaked || missing <= (playing ? CatchUpWhilePlaying : CatchUpWhilePaused)))
        {
            try
            {
                while (bake.Count <= needed)
                {
                    BakeOne(node, model, bake);
                }
            }
            catch (PhysicsException ex)
            {
                FailBake(node, model, ex);
                return false;
            }
        }
        return bake.TryApply(frame, model.State);
    }

    private PhysicsBake? EnsureBake(ModelNode node, LoadedModel model)
    {
        if (model.Bake is { } existing)
        {
            return existing;
        }
        if (model.BakeFailed || model.Physics is null)
        {
            return null;
        }
        try
        {
            // 画面の物理とは別の世界で計算する。やり直すたびに世界を作り直す（何度焼き直しても、同じ入力なら同じ揺れになるように）
            if (ModelPhysics.Create(model.Document, model.Rig) is not { } first)
            {
                return null;
            }
            bool used = false;
            model.Bake = new PhysicsBake(() =>
            {
                if (!used)
                {
                    used = true;
                    return first;
                }
                return ModelPhysics.Create(model.Document, model.Rig) ?? throw new PhysicsException(-1, "物理の世界を作り直せませんでした");
            }, model.Rig);
            model.BakeInput = new PoseInput(model.Rig);
            Invalidate(node, model);
            return model.Bake;
        }
        catch (PhysicsException ex)
        {
            FailBake(node, model, ex);
            return null;
        }
    }

    private void Invalidate(ModelNode node, LoadedModel model)
    {
        if (model.Bake is null)
        {
            return;
        }
        try
        {
            model.BakeObstacleSignature = ObstacleSignature(node);
            model.Bake.Invalidate(_context.Playback.FrameRate, GravityFor(node), FloorFor(node), ObstacleDescs(node));
        }
        catch (PhysicsException ex)
        {
            FailBake(node, model, ex);
            return;
        }
        _bakeStarted = Stopwatch.GetTimestamp();
        _bakeLogged = false;
        ScheduleBake();
    }

    private void BakeOne(ModelNode node, LoadedModel model, PhysicsBake bake)
    {
        if (model.BakeObstacleSignature != ObstacleSignature(node))
        {
            Invalidate(node, model); // ほかのモデルを足した・外した・隠した（障害物の組み合わせが変わった）
        }
        PoseInput input = model.BakeInput!;
        PoseSampler.Sample(node, bake.Count, input.Translations, input.Rotations, input.MorphWeights, input.IkEnabled);
        PhysicsTransform[] obstacles = ObstacleBuffer(model, ObstacleDescsCount(node));
        ObstacleTargetsAt(node, bake.Count, obstacles);
        bake.BakeNext(input, obstacles);
    }

    private int ObstacleDescsCount(ModelNode node) => ObstacleModels(node).Sum(o => o.Model.Physics!.FollowBodyCount);

    internal static PhysicsTransform[] ObstacleBuffer(LoadedModel model, int count)
    {
        if (model.ObstacleBuffer.Length != count)
        {
            model.ObstacleBuffer = new PhysicsTransform[count];
        }
        return model.ObstacleBuffer;
    }

    /// <summary>画面の物理に置く障害物の数（<see cref="ObstacleTargetsNow"/> の入れ物の大きさ）。</summary>
    internal int ObstacleCount(ModelNode node) => ObstacleDescsCount(node);

    private static void FailBake(ModelNode node, LoadedModel model, Exception ex)
    {
        AppLog.Error($"モデル「{node.Name}」の物理の焼き込みに失敗したため、焼き込みをやめます（画面の物理だけで計算します）", ex);
        model.Bake?.Dispose();
        model.Bake = null;
        model.BakeFailed = true;
    }

    private void ScheduleBake()
    {
        if (_bakeScheduled || !_enabled)
        {
            return;
        }
        _bakeScheduled = true;
        // 入力や描画より後回しにして、画面の手が空いたときに進める
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, RunBakeSlice);
    }

    private void RunBakeSlice()
    {
        _bakeScheduled = false;
        if (!_enabled)
        {
            return;
        }
        long start = Stopwatch.GetTimestamp();
        int target = _context.Playback.EndFrame;
        int current = _context.Playback.CurrentFrame;
        bool remaining = false;
        bool reachedCurrent = false;
        foreach (var (node, model) in PhysicsModels.ToArray())
        {
            if (EnsureBake(node, model) is not { } bake)
            {
                continue;
            }
            try
            {
                while (bake.Count <= target && Stopwatch.GetElapsedTime(start) < BakeSlice)
                {
                    BakeOne(node, model, bake);
                    if (bake.Count - 1 == current && !model.ShowingBakedPhysics)
                    {
                        reachedCurrent = true;
                        model.IsPoseDirty = true;
                    }
                }
            }
            catch (PhysicsException ex)
            {
                FailBake(node, model, ex);
                continue;
            }
            remaining |= bake.Count <= target;
        }
        if (reachedCurrent)
        {
            _context.RenderLoop.InvalidateAll(); // 今のフレームの焼き込みができたので、その結果に切り替える
        }
        if (remaining)
        {
            ScheduleBake();
        }
        else if (!_bakeLogged)
        {
            _bakeLogged = true;
            AppLog.Info($"物理の焼き込みができました（{target + 1:N0} フレーム、作り直し始めてから {Stopwatch.GetElapsedTime(_bakeStarted).TotalMilliseconds:F0} ms）");
        }
    }

    private void OnPlaybackStateChanged()
    {
        PlaybackService playback = _context.Playback;
        if (playback.FrameRate != _frameRate)
        {
            _frameRate = playback.FrameRate;
            foreach (var (node, model) in PhysicsModels)
            {
                Invalidate(node, model); // 1 フレームの時間が変わるので作り直す
            }
        }
        if (playback.EndFrame > _endFrame)
        {
            ScheduleBake(); // 長くした分を続けて作る
        }
        _endFrame = playback.EndFrame;
    }

    internal void Consume() => Current = PhysicsStep.None;

    private void OnTick(TimeSpan elapsed)
    {
        Current = PhysicsStep.None;
        if (!_enabled || !HasPhysicsModels)
        {
            return;
        }
        bool settling = _clock.IsSettling(DateTime.UtcNow);
        if (_wasSettling && !settling)
        {
            // 姿勢を変えた後の揺れが終わったので、焼き込みの結果（できていれば）に切り替える
            foreach (var (_, model) in PhysicsModels)
            {
                model.IsPoseDirty = true;
            }
            _context.RenderLoop.InvalidateAll();
        }
        _wasSettling = settling;
        PlaybackService playback = _context.Playback;
        PhysicsStep step = _clock.Next(playback.CurrentFrame, playback.IsPlaying, playback.FrameRate, elapsed, DateTime.UtcNow);
        if (step.Kind == PhysicsStepKind.None)
        {
            return;
        }
        Current = step;
        foreach (LoadedModel model in _context.Models.Values.Where(m => m.Physics is not null))
        {
            model.IsPoseDirty = true;
        }
        _context.RenderLoop.InvalidateAll();
    }

    private void OnNodeChanged(object? sender, SceneNodeChangedEventArgs e)
    {
        // 手で姿勢・キーを変えた（ほかのフレームの姿勢も変わる）ときだけ反応する。
        // 再生位置を動かしたときのキーの当てはめには反応しない（焼き込みの結果をそのまま見せる）
        bool modelsChanged = e.Node == _context.Scene.ModelsGroup && e.PropertyName == nameof(SceneNode.Children)
            || e.Node is ModelNode && e.PropertyName == nameof(SceneNode.IsVisible);
        if (_betweenModels && modelsChanged)
        {
            // ほかのモデルの体を障害物にしているので、モデルを足した・外した・隠したら、全部の焼き込みを作り直す
            foreach (var (node, model) in PhysicsModels.Where(p => p.Model.Bake is not null))
            {
                Invalidate(node, model);
            }
        }
        if (PoseSampler.ModelAffectedBy(e) is not { } changed)
        {
            return;
        }
        if (_context.GetLoaded(changed) is { Bake: not null } loaded)
        {
            Invalidate(changed, loaded); // 焼き込みを作り直す
        }
        if (_betweenModels)
        {
            // このモデルの体は、ほかのモデルの物理の障害物なので、ほかのモデルの焼き込みも作り直す
            foreach (var (node, model) in PhysicsModels.Where(p => p.Node != changed && p.Model.Bake is not null))
            {
                Invalidate(node, model);
            }
        }
        if (_enabled && !_context.Playback.IsPlaying && HasPhysicsModels)
        {
            _clock.NoteEdit(DateTime.UtcNow); // しばらく動かして落ち着かせる
            _context.RenderLoop.RequestRender();
        }
    }
}
