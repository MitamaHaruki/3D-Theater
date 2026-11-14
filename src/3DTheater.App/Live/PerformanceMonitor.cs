using System.Diagnostics;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの速さと重さを、1 分ごとにログへ書く（設計判断記録 77 章）。
/// 「[速さ]」に描画・顔のトラッキング・仮想カメラの回数と時間を、「[重さ]」に CPU・メモリ・GPU のメモリ・電源を書き、
/// 閉じるときに「[まとめ]」（平均・最大・メモリの増え方・電池の減り方）を書く。
/// ほかの PC で確かめてもらったとき、「ログを集める」だけで、速さとメモリの様子が分かるようにするため。
/// UI スレッドで動かす（描画の数え方は UI スレッドのもの）。
/// 書くのは、ふつうの優先度のタイマー。優先度を低くすると、GPU が追いつかず描画で手がいっぱいのときに後回しになり、
/// 1 分ごとのはずが 1 分 14 秒ごとなどになった（ノートパソコンで分かった）。1 分に 1 回、プロセスの値を読むだけなので、描画の邪魔はほとんどしない。
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // 閉じるときの最後の区切りが、これより短ければ書かない
    private static readonly TimeSpan MinimumLast = TimeSpan.FromSeconds(5);

    private const double Megabyte = 1024.0 * 1024;
    private const double Gigabyte = 1024.0 * 1024 * 1024;

    private readonly EditorContext _context;
    private readonly DispatcherTimer _timer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly MemoryTrend _memory = new();
    private readonly long _started;
    private readonly TimeSpan _startCpu;
    private long _lastWrite;
    private TimeSpan _lastCpu;
    private long _lastCameraFrames;
    private double _cpuMax;
    private double? _gpuPeak;
    private PowerState? _firstPower;
    private PowerState? _lastPower;
    private bool _gpuUnavailable;
    private bool _disposed;

    internal PerformanceMonitor(EditorContext context)
    {
        _context = context;
        _started = Stopwatch.GetTimestamp();
        _lastWrite = _started;
        _startCpu = _process.TotalProcessorTime;
        _lastCpu = _startCpu;
        _firstPower = SystemInfo.ReadPower();
        _lastPower = _firstPower;
        string power = _firstPower is { } p ? $"・電源 {p.Describe()}" : string.Empty;
        AppLog.Info($"[速さ] 速さと重さを {PerformanceText.Duration(Interval)}ごとに書きます（論理 CPU {Environment.ProcessorCount} 個{power}）");
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Normal, (_, _) => Write(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    private void Write()
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan wall = Stopwatch.GetElapsedTime(_lastWrite, now);
        _lastWrite = now;

        RenderPeriod render = _context.RenderLoop.Statistics.TakeInterval();
        TrackingPeriod? tracking = _context.Tracking?.Statistics.TakeInterval();
        AppLog.Info(PerformanceText.Speed(wall, render, tracking, ReadVirtualCamera(wall)));
        AppLog.Info(PerformanceText.Load(SampleLoad(wall, now)));
    }

    private VirtualCameraPeriod? ReadVirtualCamera(TimeSpan wall)
    {
        if (_context.VirtualCamera is not { } camera)
        {
            return null;
        }
        long frames = camera.FramesSent;
        long sent = frames >= _lastCameraFrames ? frames - _lastCameraFrames : frames; // 入れ直すと 0 から数え直す
        _lastCameraFrames = frames;
        if (!camera.IsOn && sent == 0)
        {
            return null;
        }
        return new VirtualCameraPeriod(wall > TimeSpan.Zero ? sent / wall.TotalSeconds : 0, camera.IsInUse);
    }

    private LoadSample SampleLoad(TimeSpan wall, long now)
    {
        _process.Refresh();
        TimeSpan cpuTime = _process.TotalProcessorTime;
        double cpu = PerformanceMath.CpuPercent(cpuTime - _lastCpu, wall, Environment.ProcessorCount);
        _lastCpu = cpuTime;
        _cpuMax = Math.Max(_cpuMax, cpu);

        double privateMegabytes = _process.PrivateMemorySize64 / Megabyte;
        _memory.Add(Stopwatch.GetElapsedTime(_started, now), privateMegabytes);

        double? gpu = ReadVideoMemory();
        if (gpu is double g)
        {
            _gpuPeak = Math.Max(_gpuPeak ?? g, g);
        }
        PowerState? power = SystemInfo.ReadPower();
        if (power is not null)
        {
            _firstPower ??= power;
            _lastPower = power;
        }
        return new LoadSample(
            cpu, privateMegabytes, _process.WorkingSet64 / Megabyte, GC.GetTotalMemory(forceFullCollection: false) / Megabyte,
            _process.HandleCount, _process.Threads.Count, gpu, power);
    }

    private double? ReadVideoMemory()
    {
        if (_gpuUnavailable || _context.Engine is not { IsDisposed: false } engine)
        {
            return null;
        }
        try
        {
            return engine.QueryVideoMemory().Usage / Gigabyte;
        }
        catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
        {
            return null; // 作り直した後にまた読む
        }
        catch (NativeEngineException ex)
        {
            AppLog.Warning("GPU のメモリの使用量を読めないため、速さのログには書きません", ex);
            _gpuUnavailable = true;
            return null;
        }
    }

    /// <summary>最後の区切りと、まとめを書く（アプリを閉じるとき。顔のトラッキングを止めるより先に呼ぶ）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        try
        {
            if (Stopwatch.GetElapsedTime(_lastWrite) >= MinimumLast)
            {
                Write();
            }
            TimeSpan duration = Stopwatch.GetElapsedTime(_started);
            _process.Refresh();
            double cpuMean = PerformanceMath.CpuPercent(_process.TotalProcessorTime - _startCpu, duration, Environment.ProcessorCount);
            var summary = new SessionSummary(
                duration, _context.RenderLoop.Statistics.Total, _context.Tracking?.Statistics.Total, cpuMean, Math.Max(_cpuMax, cpuMean),
                _memory, _gpuPeak, _firstPower, _lastPower);
            AppLog.Info(PerformanceText.Summary(summary));
        }
        finally
        {
            _process.Dispose();
        }
    }
}
