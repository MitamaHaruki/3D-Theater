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
            model.Bake.Invalidate(_context.Playback.FrameRate, GravityFor(node), FloorFor(node));
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

    private static void BakeOne(ModelNode node, LoadedModel model, PhysicsBake bake)
    {
        PoseInput input = model.BakeInput!;
        PoseSampler.Sample(node, bake.Count, input.Translations, input.Rotations, input.MorphWeights, input.IkEnabled);
        bake.BakeNext(input);
    }

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
        if (PoseSampler.ModelAffectedBy(e) is not { } changed)
        {
            return;
        }
        if (_context.GetLoaded(changed) is { Bake: not null } loaded)
        {
            Invalidate(changed, loaded); // 焼き込みを作り直す
        }
        if (_enabled && !_context.Playback.IsPlaying && HasPhysicsModels)
        {
            _clock.NoteEdit(DateTime.UtcNow); // しばらく動かして落ち着かせる
            _context.RenderLoop.RequestRender();
        }
    }
}
