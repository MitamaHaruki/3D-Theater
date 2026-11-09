using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Rendering;

/// <summary>
/// 物理演算の ON/OFF と、画面の更新ごとの進め方（<see cref="PhysicsClock"/>）を受け持つ。
/// 決めた進め方（<see cref="Current"/>）は、同じ画面の更新の中で <see cref="PoseUpdater"/> が使う。
/// 止まっているときに姿勢を変えたら、しばらく描画を続けて、物理が落ち着くまで動かす。
/// </summary>
public sealed class PhysicsController
{
    private readonly EditorContext _context;
    private readonly PhysicsClock _clock = new();
    private bool _enabled = true;

    internal PhysicsController(EditorContext context)
    {
        _context = context;
        context.RenderLoop.Ticking += (_, elapsed) => OnTick(elapsed);
        context.RenderLoop.KeepRunning = () => _enabled && HasPhysicsModels && _clock.IsSettling(DateTime.UtcNow);
        context.Scene.NodeChanged += OnNodeChanged;
    }

    /// <summary>ON/OFF が変わった。</summary>
    public event EventHandler? Changed;

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

    /// <summary>この画面の更新で行う物理の進め方。<see cref="PoseUpdater"/> が使ったら <see cref="PhysicsStep.None"/> に戻す。</summary>
    public PhysicsStep Current { get; private set; }

    private bool HasPhysicsModels => _context.Models.Values.Any(m => m.Physics is not null);

    /// <summary>剛体を今の姿勢に置き直す（メニューの「物理をリセット」、物理の ON/OFF、プロジェクトを開いたとき）。</summary>
    public void Reset()
    {
        _clock.RequestReset();
        foreach (LoadedModel model in _context.Models.Values)
        {
            model.IsPoseDirty = true;
        }
        _context.RenderLoop.InvalidateAll();
    }

    internal void Consume() => Current = PhysicsStep.None;

    private void OnTick(TimeSpan elapsed)
    {
        Current = PhysicsStep.None;
        if (!_enabled || !HasPhysicsModels)
        {
            return;
        }
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
        if (!_enabled || _context.Playback.IsPlaying)
        {
            return;
        }
        bool poseChanged = e.Node switch
        {
            BoneNode or ModelNode => e.PropertyName is nameof(SceneNode.Position) or nameof(SceneNode.Rotation),
            MorphNode => e.PropertyName == nameof(MorphNode.Weight),
            _ => false,
        };
        if (poseChanged && HasPhysicsModels)
        {
            _clock.NoteEdit(DateTime.UtcNow); // しばらく動かして落ち着かせる
            _context.RenderLoop.RequestRender();
        }
    }
}
