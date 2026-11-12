using Theater3D.App.Logging;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの時計。タイムラインの再生位置は使わず、起動してからの実際の時間で、
/// モデルごとの待機モーションを繰り返し当てはめる（<see cref="Editor.AnimationService.ApplyLive"/>）。
/// 物理も実際の時間でいつも進める（<see cref="Rendering.PhysicsController"/> がライブの進め方にする）ので、描画の繰り返しを止めない。
/// </summary>
public sealed class LiveRuntime
{
    private readonly EditorContext _context;
    private bool _animating;

    internal LiveRuntime(EditorContext context)
    {
        _context = context;
        context.RenderLoop.Ticking += (_, elapsed) => OnTick(elapsed);
        // 物理・待機モーション・（後の段の）トラッキングはいつも動くので、描画の繰り返しを止めない（描くのは変わった区画だけ）
        context.RenderLoop.KeepRunning = () => true;
        context.RenderLoop.RequestRender();
    }

    /// <summary>起動してからの時間（秒）。待機モーションのループの位置を決める。</summary>
    public double Seconds { get; private set; }

    /// <summary>待機モーションを当てはめているモデルがあるか。</summary>
    public bool IsAnimating => _animating;

    private void OnTick(TimeSpan elapsed)
    {
        Seconds += elapsed.TotalSeconds;
        bool animating = _context.Animation.ApplyLive(Seconds);
        if (animating != _animating)
        {
            _animating = animating;
            AppLog.Info(animating ? "待機モーションの繰り返しを始めました" : "待機モーションのあるモデルが無くなりました");
        }
    }
}
