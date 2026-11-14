using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
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

    /// <summary>描いたが、まだ表示に出していない絵があるか（次の画面更新で出す）。</summary>
    bool HasPendingFrame { get; }

    /// <summary>
    /// 1 枚の描画を GPU へ頼む（完了は待たない）。頼んだら true。頼んだ絵は、ふつうは次の画面更新で、GPU が描き終えた頃に表示へ出す
    /// （<see cref="HasPendingFrame"/> が true になる）。
    /// </summary>
    bool Submit();

    /// <summary>頼んだ絵を、同じ画面更新の中で出すか（表示の面を作り直した直後など、出しておける前の絵が無いとき）。</summary>
    bool PresentImmediately { get; }

    /// <summary>
    /// まだ出していない絵を、表示の面へ写すよう GPU に頼む。この後、描画ループが GPU の完了を 1 回だけ待ってから、
    /// 必ず <see cref="Complete"/> を呼ぶ。
    /// </summary>
    void Publish();

    /// <summary>GPU の完了を待った後に呼ばれる。<paramref name="succeeded"/> が false なら表示を更新しない。</summary>
    void Complete(bool succeeded);

    /// <summary>1 回の描画に GPU が掛けた時間（ミリ秒。数回前の描画の値）。まだ測れていなければ null。</summary>
    double? GpuMilliseconds { get; }
}

/// <summary>
/// WPF の画面更新（CompositionTarget.Rendering）に合わせて、変化のあった描画先だけを描く。
/// 再生中は再生位置を進める。何も変わらず再生もしていないときは購読をやめて GPU と CPU を休ませる。
/// 1 回の画面更新では「前の更新で頼んだ絵を表示の面へ写し、GPU の完了を 1 回だけ待って表示に反映 → 変化のあった区画の次の絵を頼む
/// （待たない）」の順に進める。GPU は次の画面更新までの間に描くので、重い効果を使っても UI スレッドはほとんど待たない
/// （表示は 1 回分遅れる。表示の面を作り直した直後の区画だけは、同じ更新の中で待って出す）。
/// UI スレッドで描く（docs/設計判断記録.md 15 章・51 章）。
/// GPU が使えなくなったら（取り外し・ドライバーの更新・応答なし）描画を止め、デバイスを作り直して再開する。
/// </summary>
public sealed class RenderLoop(NativeEngine? engine, PlaybackService playback)
{
    private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    /// <summary>GPU の作り直しを試す回数の上限（間を少しずつ空けて、合わせて 2 分ほど）。</summary>
    public const int MaxRecoveryAttempts = 10;

    private DispatcherTimer? _retryTimer;

    private readonly List<IRenderTarget> _targets = [];
    private readonly List<IRenderTarget> _submitted = [];
    private readonly List<IRenderTarget> _presenting = [];
    private readonly List<IRenderTarget> _published = [];
    private bool _subscribed;
    private long _lastTimestamp;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private int _framesInSample;
    private long _sampleStart;
    private readonly FrameTimer _frameTimer = new();
    private readonly FrameTimer _prepareTimer = new();
    private readonly FrameTimer _submitTimer = new();
    private readonly FrameTimer _waitTimer = new();
    private readonly FrameTimer _presentTimer = new();
    private readonly FrameTimer _gpuTimer = new();
    private double? _lastGpuMilliseconds;

    public event EventHandler<RenderFailedEventArgs>? RenderFailed;

    /// <summary>GPU の状態（使える・作り直し中・作り直せなかった）が変わった。</summary>
    public event EventHandler? DeviceStateChanged;

    public DeviceState DeviceState { get; private set; } = DeviceState.Ready;

    /// <summary>今の作り直しで試した回数（使える状態では 0）。</summary>
    public int RecoveryAttempts { get; private set; }

    /// <summary>描き直す区画があるとき、描画を頼む直前に呼ばれる（モデルの変形を GPU へ送るなど）。</summary>
    public event EventHandler? BeforeRender;

    /// <summary>画面の更新ごとに、再生位置を進めた後で呼ばれる（前の更新からの時間）。物理演算の進め方を決めるのに使う。</summary>
    public event EventHandler<TimeSpan>? Ticking;

    /// <summary>描き直す区画が無く再生もしていなくても、描画ループを止めない条件（物理が落ち着くまでなど）。</summary>
    public Func<bool>? KeepRunning { get; set; }

    /// <summary>描いた回数・時間・つかえを数える（ライブモードの速さのログが 1 分ごとに読む）。</summary>
    public RenderStats Statistics { get; } = new();

    /// <summary>直近 1 秒ほどで実際に描いた回数。止まっているときは 0。</summary>
    public double FramesPerSecond { get; private set; }

    /// <summary>1 回の描画（全区画）にかかった UI スレッドの時間の平均。</summary>
    public double AverageFrameMilliseconds => _frameTimer.AverageMilliseconds;

    /// <summary>
    /// 1 回の描画の内訳（モデルの変形・描画の依頼・GPU の完了待ち・表示への反映）と、GPU が描くのに掛けた時間（全区画の合計）の平均。
    /// </summary>
    public RenderBreakdown Breakdown => new(
        _prepareTimer.AverageMilliseconds, _submitTimer.AverageMilliseconds, _waitTimer.AverageMilliseconds, _presentTimer.AverageMilliseconds,
        _gpuTimer.AverageMilliseconds);

    public bool IsRunning => _subscribed;

    /// <summary>
    /// true の間はプレビューを描かない（書き出しの間。書き出しのフレームごとにプレビューまで描き直すと遅くなるため）。
    /// false に戻したら、呼び出し側で <see cref="InvalidateAll"/> すること。
    /// </summary>
    public bool IsSuspended { get; set; }

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
        Statistics.AddTick(step.TotalMilliseconds);
        _lastGpuMilliseconds = null;
        playback.Advance(step > MaxStep ? MaxStep : step); // 再生位置が進むと、各区画は Invalidate される
        Ticking?.Invoke(this, step > MaxStep ? MaxStep : step);

        // GPU を作り直している間は描かない（作り直したら全区画を描き直す）
        int rendered = DeviceState == DeviceState.Ready && !IsSuspended ? RenderChangedTargets() : 0;
        LastRenderedTargetCount = rendered;
        if (rendered > 0)
        {
            TimeSpan frame = Stopwatch.GetElapsedTime(now);
            _frameTimer.Add(frame);
            Statistics.AddFrame(frame.TotalMilliseconds, _lastGpuMilliseconds);
            _framesInSample++;
        }
        UpdateFrameRate(now);

        bool idle = IsSuspended || DeviceState != DeviceState.Ready || !_targets.Any(t => (t.CanRender && t.NeedsRender) || t.HasPendingFrame);
        if (!playback.IsPlaying && idle && KeepRunning?.Invoke() != true)
        {
            Stop();
        }
    }

    private int RenderChangedTargets()
    {
        _submitted.Clear();
        double waitMilliseconds = 0;
        double presentMilliseconds = 0;

        // 1. 前の画面更新で頼んだ絵を出す（GPU はもう描き終えている頃なので、待つのはふつう表示の面へ写す分だけ）
        _presenting.Clear();
        _presenting.AddRange(_targets.Where(t => t.HasPendingFrame));
        NativeEngineException? lost = Present(_presenting, ref waitMilliseconds, ref presentMilliseconds);
        if (lost is not null)
        {
            RecordWaitAndPresent(waitMilliseconds, presentMilliseconds);
            OnDeviceLost(lost);
            return 0;
        }
        if (!_targets.Any(t => t.CanRender && t.NeedsRender))
        {
            if (_presenting.Count > 0)
            {
                RecordWaitAndPresent(waitMilliseconds, presentMilliseconds);
            }
            return 0;
        }

        // 2. 変化のあった区画の次の絵を頼む（GPU の完了は待たない）
        long phase = Stopwatch.GetTimestamp();
        try
        {
            BeforeRender?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is NativeEngineException or InvalidOperationException or ArgumentException)
        {
            AppLog.Error("描画の準備（モデルの変形）に失敗しました", ex);
        }
        phase = Lap(_prepareTimer, phase);
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
            catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
            {
                lost = ex; // 区画の失敗ではないので外さない。作り直してから描き直す
                break;
            }
            catch (Exception ex)
            {
                Fail(target, ex);
            }
        }
        if (_submitted.Count == 0 && lost is null)
        {
            RecordWaitAndPresent(waitMilliseconds, presentMilliseconds);
            return 0;
        }
        if (lost is null && _submitted.Count > 0)
        {
            // 頼んだ描画をすぐ GPU へ送る（送らないと、次の更新で待つときまで GPU が描き始めない）
            try
            {
                engine?.Flush();
            }
            catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
            {
                lost = ex;
            }
        }
        Lap(_submitTimer, phase);
        RecordGpuTime();

        // 3. 表示の面を作り直した直後の区画は、出しておける前の絵が無いので、待って同じ更新の中で出す
        if (lost is null)
        {
            _presenting.Clear();
            _presenting.AddRange(_submitted.Where(t => t.PresentImmediately));
            lost = Present(_presenting, ref waitMilliseconds, ref presentMilliseconds);
        }
        RecordWaitAndPresent(waitMilliseconds, presentMilliseconds);
        if (lost is not null)
        {
            OnDeviceLost(lost);
            return 0;
        }
        return _submitted.Count;
    }

    /// <summary>
    /// 区画の絵を表示の面へ写し、GPU の完了を 1 回だけ待って表示に反映する。GPU が使えなくなったら、その例外を返す
    /// （写すよう頼んだ区画には、失敗でも必ず Complete を呼ぶ）。
    /// </summary>
    private NativeEngineException? Present(List<IRenderTarget> targets, ref double waitMilliseconds, ref double presentMilliseconds)
    {
        if (targets.Count == 0)
        {
            return null;
        }
        long start = Stopwatch.GetTimestamp();
        _published.Clear();
        NativeEngineException? lost = null;
        foreach (IRenderTarget target in targets)
        {
            try
            {
                target.Publish();
                _published.Add(target);
            }
            catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
            {
                lost = ex;
                break;
            }
            catch (Exception ex)
            {
                Fail(target, ex);
            }
        }
        long published = Stopwatch.GetTimestamp();
        bool succeeded = lost is null;
        if (succeeded && _published.Count > 0)
        {
            try
            {
                engine?.WaitForGpu();
            }
            catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
            {
                succeeded = false;
                lost = ex;
            }
            catch (NativeEngineException ex)
            {
                succeeded = false;
                AppLog.Error("GPU の描画完了を待てませんでした", ex);
                RenderFailed?.Invoke(this, new RenderFailedEventArgs(null, ex));
            }
        }
        long waited = Stopwatch.GetTimestamp();
        foreach (IRenderTarget target in _published)
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
        waitMilliseconds += Stopwatch.GetElapsedTime(published, waited).TotalMilliseconds;
        presentMilliseconds += Stopwatch.GetElapsedTime(start, published).TotalMilliseconds
            + Stopwatch.GetElapsedTime(waited).TotalMilliseconds;
        return lost;
    }

    /// <summary>描いた区画の、GPU が描くのに掛けた時間（測れた分）を足して記録する。</summary>
    private void RecordGpuTime()
    {
        double sum = 0;
        bool any = false;
        foreach (IRenderTarget target in _submitted)
        {
            try
            {
                if (target.GpuMilliseconds is double milliseconds)
                {
                    sum += milliseconds;
                    any = true;
                }
            }
            catch (NativeEngineException)
            {
                // 測れなくても描画は続ける
            }
        }
        if (any)
        {
            _gpuTimer.Add(TimeSpan.FromMilliseconds(sum));
            _lastGpuMilliseconds = sum;
        }
    }

    private void RecordWaitAndPresent(double waitMilliseconds, double presentMilliseconds)
    {
        _waitTimer.Add(TimeSpan.FromMilliseconds(waitMilliseconds));
        _presentTimer.Add(TimeSpan.FromMilliseconds(presentMilliseconds));
    }

    /// <summary>
    /// GPU を今すぐ作り直す（モデルを GPU に載せる前など）。使える状態なら何もせず true。
    /// <paramref name="force"/> が true なら、使える状態でも作り直す（診断・手での再試行用）。
    /// </summary>
    public bool RecoverNow(bool force = false)
    {
        if (engine is null || engine.IsDisposed)
        {
            return false;
        }
        if (!force && DeviceState == DeviceState.Ready && !engine.IsDeviceLost)
        {
            return true;
        }
        _retryTimer?.Stop();
        if (DeviceState == DeviceState.Ready)
        {
            SetDeviceState(DeviceState.Recovering);
        }
        return TryRecover();
    }

    private void OnDeviceLost(NativeEngineException ex)
    {
        if (DeviceState != DeviceState.Ready)
        {
            return;
        }
        AppLog.Warning("GPU が使えなくなりました（取り外し・ドライバーの更新・応答なしなど）。描画を止めて作り直します", ex);
        SetDeviceState(DeviceState.Recovering);
        // 今の画面更新を終えてから作り直す（D3DImage を開けたままにしない）
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => TryRecover());
    }

    private bool TryRecover()
    {
        if (engine is null || engine.IsDisposed || DeviceState == DeviceState.Ready)
        {
            return DeviceState == DeviceState.Ready;
        }
        RecoveryAttempts++;
        try
        {
            engine.Recover();
        }
        catch (Exception ex) when (ex is NativeEngineException or COMException or InvalidOperationException)
        {
            AppLog.Warning($"GPU を作り直せませんでした（{RecoveryAttempts} 回目）", ex);
            if (RecoveryAttempts >= MaxRecoveryAttempts)
            {
                AppLog.Error("GPU を作り直せないため、描画を止めたままにします");
                SetDeviceState(DeviceState.Failed);
                return false;
            }
            SetDeviceState(DeviceState.Recovering); // 回数の表示を進める
            ScheduleRetry(RetryDelay(RecoveryAttempts));
            return false;
        }
        AppLog.Info($"GPU を作り直して描画を再開しました（{RecoveryAttempts} 回目で成功）");
        RecoveryAttempts = 0;
        SetDeviceState(DeviceState.Ready);
        InvalidateAll();
        return true;
    }

    /// <summary>次に試すまでの間（1・2・4・8 秒、その後は 15 秒ごと）。</summary>
    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Clamp(attempt - 1, 0, 4)));

    private void ScheduleRetry(TimeSpan delay)
    {
        if (_retryTimer is null)
        {
            _retryTimer = new DispatcherTimer(DispatcherPriority.Background);
            _retryTimer.Tick += (_, _) =>
            {
                _retryTimer.Stop();
                TryRecover();
            };
        }
        _retryTimer.Interval = delay;
        _retryTimer.Start();
    }

    private void SetDeviceState(DeviceState state)
    {
        DeviceState = state;
        DeviceStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static long Lap(FrameTimer timer, long since)
    {
        long now = Stopwatch.GetTimestamp();
        timer.Add(Stopwatch.GetElapsedTime(since, now));
        return now;
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

/// <summary>GPU の状態。</summary>
public enum DeviceState
{
    /// <summary>使える。</summary>
    Ready,

    /// <summary>使えなくなったので、作り直している（間を空けて何度か試す）。</summary>
    Recovering,

    /// <summary>何度試しても作り直せなかった（描画は止めたまま）。</summary>
    Failed,
}

/// <summary>1 回の描画の内訳（ミリ秒）。Prepare〜Present は UI スレッドの時間、Gpu は GPU が描くのに掛けた時間（全区画の合計）。</summary>
public readonly record struct RenderBreakdown(double Prepare, double Submit, double Wait, double Present, double Gpu);

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
