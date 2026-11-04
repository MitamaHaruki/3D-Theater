using System.Diagnostics;
using System.Windows.Media;
using Theater3D.App.Logging;

namespace Theater3D.App.Rendering;

/// <summary>描画ループに登録できる描画先。</summary>
public interface IRenderTarget
{
    /// <summary>大きさが変わったなど、アニメーションが止まっていても描き直しが必要なとき true。</summary>
    bool NeedsRender { get; }

    void RenderFrame(float timeSeconds);
}

/// <summary>
/// WPF の画面更新（CompositionTarget.Rendering）に合わせて、登録された描画先を描く。
/// アニメーションが止まっていて描き直しも要らないときは購読をやめ、GPU と CPU を休ませる。
/// Phase 0 では UI スレッドで描く。描画専用スレッドへの分離は Phase 1 以降の課題。
/// </summary>
public sealed class RenderLoop
{
    private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    private readonly List<IRenderTarget> _targets = [];
    private bool _subscribed;
    private bool _animating;
    private double _animationSeconds;
    private long _lastTimestamp;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private int _framesInSample;
    private long _sampleStart;

    public event EventHandler<RenderFailedEventArgs>? RenderFailed;

    public bool Animating
    {
        get => _animating;
        set
        {
            _animating = value;
            if (value)
            {
                Subscribe();
            }
        }
    }

    /// <summary>直近 1 秒ほどで実際に描いたフレーム数。止まっているときは 0。</summary>
    public double FramesPerSecond { get; private set; }

    public bool IsRunning => _subscribed;

    public void Add(IRenderTarget target)
    {
        if (!_targets.Contains(target))
        {
            _targets.Add(target);
        }
        Subscribe();
    }

    public void Remove(IRenderTarget target) => _targets.Remove(target);

    public void RequestRender() => Subscribe();

    public void Stop()
    {
        _animating = false;
        Unsubscribe();
    }

    private void Subscribe()
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

    private void Unsubscribe()
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
        if (_animating)
        {
            TimeSpan step = Stopwatch.GetElapsedTime(_lastTimestamp, now);
            _animationSeconds += (step > MaxStep ? MaxStep : step).TotalSeconds;
        }
        _lastTimestamp = now;

        bool rendered = false;
        foreach (IRenderTarget target in _targets.ToArray())
        {
            if (!_animating && !target.NeedsRender)
            {
                continue;
            }
            try
            {
                target.RenderFrame((float)_animationSeconds);
                rendered = true;
            }
            catch (Exception ex)
            {
                // 失敗した描画先は外し、ほかの描画先は動かし続ける
                AppLog.Error($"描画に失敗したため、この描画先を止めます: {target.GetType().Name}", ex);
                _targets.Remove(target);
                RenderFailed?.Invoke(this, new RenderFailedEventArgs(target, ex));
            }
        }

        UpdateFrameRate(now, rendered);

        if (!_animating && !_targets.Any(t => t.NeedsRender))
        {
            Unsubscribe();
        }
    }

    private void UpdateFrameRate(long now, bool rendered)
    {
        if (rendered)
        {
            _framesInSample++;
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_sampleStart, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            FramesPerSecond = _framesInSample / elapsed.TotalSeconds;
            _framesInSample = 0;
            _sampleStart = now;
        }
    }
}

public sealed class RenderFailedEventArgs(IRenderTarget target, Exception exception) : EventArgs
{
    public IRenderTarget Target { get; } = target;

    public Exception Exception { get; } = exception;
}

/// <summary>1 フレームの描画にかかった時間（CPU 側）の移動平均。</summary>
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
