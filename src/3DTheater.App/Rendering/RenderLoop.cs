using System.Diagnostics;
using System.Windows.Media;
using Theater3D.App.Logging;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Rendering;

/// <summary>描画ループに登録できる描画先（プレビューの 1 区画）。</summary>
public interface IRenderTarget
{
    /// <summary>画面に見えていて大きさがあり、描ける状態か。</summary>
    bool CanRender { get; }

    /// <summary>前に描いてから見た目が変わったか。</summary>
    bool NeedsRender { get; }

    void Invalidate();

    /// <summary>1 枚の描画を GPU へ頼む。頼んだら true（あとで必ず <see cref="Complete"/> が呼ばれる）。</summary>
    bool Submit();

    /// <summary>GPU の完了を待った後に呼ばれる。<paramref name="succeeded"/> が false なら表示を更新しない。</summary>
    void Complete(bool succeeded);
}

/// <summary>
/// WPF の画面更新（CompositionTarget.Rendering）に合わせて、変化のあった描画先だけを描く。
/// 再生中は再生位置を進める。何も変わらず再生もしていないときは購読をやめて GPU と CPU を休ませる。
/// 描き方は「全区画の描画を頼む → GPU の完了を 1 回だけ待つ → 全区画を表示に反映」の 3 段階。
/// Phase 1 では UI スレッドで描く。描画専用スレッドへの分離は Phase 2 までの課題。
/// </summary>
public sealed class RenderLoop(NativeEngine? engine, PlaybackService playback)
{
    private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    private readonly List<IRenderTarget> _targets = [];
    private readonly List<IRenderTarget> _submitted = [];
    private bool _subscribed;
    private long _lastTimestamp;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private int _framesInSample;
    private long _sampleStart;
    private readonly FrameTimer _frameTimer = new();

    public event EventHandler<RenderFailedEventArgs>? RenderFailed;

    /// <summary>描き直す区画があるとき、描画を頼む直前に呼ばれる（モデルの変形を GPU へ送るなど）。</summary>
    public event EventHandler? BeforeRender;

    /// <summary>直近 1 秒ほどで実際に描いた回数。止まっているときは 0。</summary>
    public double FramesPerSecond { get; private set; }

    /// <summary>1 回の描画（全区画）にかかった UI スレッドの時間の平均。</summary>
    public double AverageFrameMilliseconds => _frameTimer.AverageMilliseconds;

    public bool IsRunning => _subscribed;

    public int LastRenderedTargetCount { get; private set; }

    public void Register(IRenderTarget target)
    {
        if (!_targets.Contains(target))
        {
            _targets.Add(target);
        }
        RequestRender();
    }

    public void Unregister(IRenderTarget target) => _targets.Remove(target);

    public void InvalidateAll()
    {
        foreach (IRenderTarget target in _targets)
        {
            target.Invalidate();
        }
        RequestRender();
    }

    public void RequestRender()
    {
        if (_subscribed)
        {
            return;
        }
        _subscribed = true;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _sampleStart = _lastTimestamp;
        _framesInSample = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_subscribed)
        {
            return;
        }
        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
        FramesPerSecond = 0;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // 同じ画面更新の中で複数回呼ばれることがあるので 1 回にまとめる
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime)
            {
                return;
            }
            _lastRenderingTime = args.RenderingTime;
        }

        long now = Stopwatch.GetTimestamp();
        TimeSpan step = Stopwatch.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
        playback.Advance(step > MaxStep ? MaxStep : step); // 再生位置が進むと、各区画は Invalidate される

        int rendered = RenderChangedTargets();
        LastRenderedTargetCount = rendered;
        if (rendered > 0)
        {
            _frameTimer.Add(Stopwatch.GetElapsedTime(now));
            _framesInSample++;
        }
        UpdateFrameRate(now);

        if (!playback.IsPlaying && !_targets.Any(t => t.CanRender && t.NeedsRender))
        {
            Stop();
        }
    }

    private int RenderChangedTargets()
    {
        _submitted.Clear();
        if (!_targets.Any(t => t.CanRender && t.NeedsRender))
        {
            return 0;
        }
        try
        {
            BeforeRender?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is NativeEngineException or InvalidOperationException or ArgumentException)
        {
            AppLog.Error("描画の準備（モデルの変形）に失敗しました", ex);
        }
        foreach (IRenderTarget target in _targets.ToArray())
        {
            if (!target.CanRender || !target.NeedsRender)
            {
                continue;
            }
            try
            {
                if (target.Submit())
                {
                    _submitted.Add(target);
                }
            }
            catch (Exception ex)
            {
                Fail(target, ex);
            }
        }
        if (_submitted.Count == 0)
        {
            return 0;
        }

        bool succeeded = true;
        try
        {
            engine?.WaitForGpu();
        }
        catch (NativeEngineException ex)
        {
            succeeded = false;
            AppLog.Error("GPU の描画完了を待てませんでした", ex);
            RenderFailed?.Invoke(this, new RenderFailedEventArgs(null, ex));
        }

        foreach (IRenderTarget target in _submitted)
        {
            try
            {
                target.Complete(succeeded);
            }
            catch (Exception ex)
            {
                Fail(target, ex);
            }
        }
        return _submitted.Count;
    }

    private void Fail(IRenderTarget target, Exception ex)
    {
        // 失敗した区画は外し、ほかの区画は動かし続ける
        AppLog.Error($"描画に失敗したため、この区画の描画を止めます: {target}", ex);
        _targets.Remove(target);
        RenderFailed?.Invoke(this, new RenderFailedEventArgs(target, ex));
    }

    private void UpdateFrameRate(long now)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_sampleStart, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            FramesPerSecond = _framesInSample / elapsed.TotalSeconds;
            _framesInSample = 0;
            _sampleStart = now;
        }
    }
}

public sealed class RenderFailedEventArgs(IRenderTarget? target, Exception exception) : EventArgs
{
    /// <summary>失敗した区画。GPU 全体の失敗なら null。</summary>
    public IRenderTarget? Target { get; } = target;

    public Exception Exception { get; } = exception;
}

/// <summary>かかった時間の移動平均。</summary>
public sealed class FrameTimer
{
    private const int SampleCount = 60;
    private readonly double[] _samples = new double[SampleCount];
    private int _next;
    private int _count;

    public double AverageMilliseconds => _count == 0 ? 0 : _samples.Take(_count).Average();

    public void Add(TimeSpan duration)
    {
        _samples[_next] = duration.TotalMilliseconds;
        _next = (_next + 1) % SampleCount;
        _count = Math.Min(_count + 1, SampleCount);
    }
}
