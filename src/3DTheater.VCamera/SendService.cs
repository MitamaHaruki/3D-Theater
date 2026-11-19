using System.Diagnostics;
using System.Globalization;
using System.Net;
using Theater3D.Media;
using Theater3D.Tracking;
using Theater3D.Tracking.Vmc;

namespace Theater3D.VCamera;

/// <summary>
/// カメラ（確かめ用の動画のファイル）を読んで顔を読み取り、1 枚ごとに VMC プロトコルで送る（設計判断記録 98 章）。読み込み用のスレッドで動く。
/// 映像が届かないときは知らせ、読めなくなった・止まったときは開き直す（本アプリと同じ <see cref="VideoWatch"/> の決まり。97 章）。
/// 1 分ごとに、読み取りの速さと往復の時間をログに書く。
/// </summary>
public sealed class SendService : IDisposable
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);

    private readonly object _captureLock = new();
    private readonly VideoWatch _watch = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private Timer? _watchTimer;
    private volatile bool _stop;
    private volatile bool _stalled;
    private volatile bool _recalibrate;
    private CameraCapture? _capture;
    private VmcSender? _sender;

    // 1 分ごとのまとめ（読み込み用のスレッドだけが書く）
    private readonly List<double> _process = [];
    private int _frames;
    private int _found;
    private long _periodStart;

    /// <summary>始めた・止めた（読み込み用のスレッドから知らせることがある）。</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>読んでいる映像の名前。</summary>
    public string? SourceName { get; private set; }

    /// <summary>送り先。</summary>
    public IPEndPoint? Destination { get; private set; }

    /// <summary>最後に止まったときのエラー（無ければ null）。</summary>
    public string? Error { get; private set; }

    /// <summary>今の困りごと（映像が届かない・開き直している・エラーで止まった。無ければ null）。</summary>
    public string? Problem => IsRunning ? _watch.Problem(_clock.Elapsed, SourceName) : Error is { } e ? $"止まりました: {e}" : null;

    /// <summary>直近の 1 枚（顔があったか・処理の時間）。</summary>
    public (bool Found, double Milliseconds, double Rate) Latest { get; private set; }

    /// <summary>送り先から返事があるか（3D Theater が受け取っているか）と、直近の往復の時間（ms）。</summary>
    public (bool Answered, double? RoundTrip) Reply => _sender is { } s ? (s.IsAnswered, s.LastRoundTrip) : (false, null);

    public void StartCamera(CameraDevice camera, IPEndPoint destination) =>
        Start(camera.Name, () => CameraCapture.OpenCamera(camera.Id), destination);

    public void StartFile(string path, IPEndPoint destination) =>
        Start(System.IO.Path.GetFileName(path), () => CameraCapture.OpenFile(path, loop: true), destination);

    /// <summary>正面を覚え直す（ほかのアプリへ送る頭の向きの正面。3D Theater は自分で合わせる）。</summary>
    public void Recalibrate() => _recalibrate = true;

    private void Start(string name, Func<CameraCapture> open, IPEndPoint destination)
    {
        Stop();
        _stop = false;
        _stalled = false;
        Error = null;
        SourceName = name;
        Destination = destination;
        _sender = new VmcSender(destination);
        _watch.Reset(_clock.Elapsed);
        _watchTimer = new Timer(_ => CheckStall(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _thread = new Thread(() => Run(open)) { IsBackground = true, Name = "顔を読んで送る" };
        _thread.Start();
        VLog.Info($"送り始めます: {name} → {destination}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _watchTimer?.Dispose();
        _watchTimer = null;
        if (_thread is { } thread)
        {
            _stop = true;
            lock (_captureLock)
            {
                _capture?.Cancel();
            }
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                VLog.Warning("読み込み用のスレッドが 5 秒で止まりません");
            }
            _thread = null;
        }
        _sender?.Dispose();
        _sender = null;
        _watch.Stop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    private void CheckStall()
    {
        if (_stop || _stalled || !_watch.IsStalled(_clock.Elapsed))
        {
            return;
        }
        lock (_captureLock)
        {
            if (_capture is { } capture)
            {
                _stalled = true;
                VLog.Warning($"映像が {VideoWatch.StallAfter.TotalSeconds:F0} 秒届かないため、開き直します");
                capture.Cancel();
            }
        }
    }

    private void Run(Func<CameraCapture> open)
    {
        FaceTracker? tracker = null;
        var encoder = new VmcFaceEncoder();
        _periodStart = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop)
            {
                string? failure = null;
                CameraCapture? capture = null;
                try
                {
                    _watch.Opening(_clock.Elapsed);
                    capture = open();
                    lock (_captureLock)
                    {
                        _capture = capture;
                    }
                    if (_stop)
                    {
                        break;
                    }
                    tracker ??= new FaceTracker();
                    VLog.Info($"映像を開きました（{capture.Width} × {capture.Height}・{capture.FrameRate:F1} 回/秒{(capture.IsFile ? "・動画のファイル" : string.Empty)}）");
                    _watch.Opened(_clock.Elapsed);
                    ReadAndSend(capture, tracker, encoder);
                    if (!_stop && _stalled)
                    {
                        failure = $"{VideoWatch.StallAfter.TotalSeconds:F0} 秒届きません";
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (_stop)
                    {
                        break;
                    }
                    failure = _stalled ? $"{VideoWatch.StallAfter.TotalSeconds:F0} 秒届きません" : ex.Message;
                    if (!_stalled)
                    {
                        VLog.Warning($"映像を読めませんでした（{ex.GetType().Name}: {ex.Message}）");
                    }
                }
                finally
                {
                    lock (_captureLock)
                    {
                        _capture = null;
                    }
                    capture?.Dispose();
                    _stalled = false;
                }
                if (_stop || failure is null)
                {
                    break;
                }
                if (_watch.Failed(failure, _clock.Elapsed) is not { } delay)
                {
                    Error = $"映像を読めません（{VideoWatch.RetryDelays.Count} 回開き直しても届きませんでした。{failure}）";
                    VLog.Error(Error);
                    break;
                }
                VLog.Warning($"{delay.TotalSeconds:F0} 秒後に映像を開き直します（{_watch.Failures}/{VideoWatch.RetryDelays.Count} 回目。{failure}）");
                long start = Stopwatch.GetTimestamp();
                while (!_stop && Stopwatch.GetElapsedTime(start) < delay)
                {
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            WriteSummary(force: true);
            tracker?.Dispose();
            if (!_stop)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ReadAndSend(CameraCapture capture, FaceTracker tracker, VmcFaceEncoder encoder)
    {
        var frame = new byte[capture.FrameBytes];
        long? firstTime = null;
        long wallStart = 0;
        long previousTime = long.MinValue;
        long rateStart = Stopwatch.GetTimestamp();
        int rateCount = 0;
        double rate = 0;
        while (!_stop)
        {
            if (!capture.Read(frame, out long timestamp))
            {
                break;
            }
            long arrived = Stopwatch.GetTimestamp();
            _watch.FrameArrived(_clock.Elapsed);
            if (capture.IsFile)
            {
                // 動画のファイルは、映像の時刻に合わせて待つ（先頭に戻ったら測り直す）
                if (firstTime is null || timestamp < previousTime)
                {
                    firstTime = timestamp;
                    wallStart = Stopwatch.GetTimestamp();
                }
                previousTime = timestamp;
                TimeSpan due = TimeSpan.FromTicks(timestamp - firstTime.Value) - Stopwatch.GetElapsedTime(wallStart);
                if (due > TimeSpan.Zero)
                {
                    Thread.Sleep(due);
                }
                arrived = Stopwatch.GetTimestamp();
            }
            if (_recalibrate)
            {
                _recalibrate = false;
                encoder.Recalibrate();
                VLog.Info("正面を覚え直します");
            }
            FaceTrackingResult result = tracker.Process(new BgraImage(frame, capture.Width, capture.Height));
            float process = (float)Stopwatch.GetElapsedTime(arrived).TotalMilliseconds;
            VmcFaceFrame face = encoder.Encode(result, process, (float)_clock.Elapsed.TotalSeconds);
            _sender?.Send(face);

            rateCount++;
            if (Stopwatch.GetElapsedTime(rateStart) is { TotalSeconds: >= 1 } elapsed)
            {
                rate = rateCount / elapsed.TotalSeconds;
                rateCount = 0;
                rateStart = Stopwatch.GetTimestamp();
            }
            Latest = (result.Found, result.Milliseconds, rate);
            _frames++;
            _found += result.Found ? 1 : 0;
            _process.Add(process);
            WriteSummary(force: false);
        }
    }

    // 1 分ごと（と止めたとき）に、読み取りの速さと往復の時間をログに書く
    private void WriteSummary(bool force)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_periodStart);
        if (!force && elapsed < SummaryInterval)
        {
            return;
        }
        if (_frames > 0 && _sender is { } sender)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            double[] process = [.. _process];
            Array.Sort(process);
            double p95 = process[Math.Min(process.Length - 1, (int)Math.Ceiling(process.Length * 0.95) - 1)];
            RoundTripSummary trip = sender.TakeSummary();
            string tripText = trip.Replied > 0
                ? string.Format(c, "往復 平均 {0:F1} ms・95% {1:F1} ms・最大 {2:F1} ms・返事なし {3} 回", trip.Average, trip.P95, trip.Max, trip.Unanswered)
                : "返事なし（3D Theater が受け取っていないか、ほかのアプリへ送っている）";
            VLog.Info(string.Format(c, "[速さ] {0}: 読み取り {1:F1} 回/秒（絵が届いてから送るまで 平均 {2:F1} ms・95% {3:F1} ms・顔あり {4:F0}%）／送った {5} 回・{6}（送り先 {7}）",
                elapsed.TotalSeconds >= 59.5 ? "1 分" : $"{elapsed.TotalSeconds:F0} 秒", _frames / Math.Max(1, elapsed.TotalSeconds), process.Average(), p95,
                100.0 * _found / _frames, trip.Sent, tripText, Destination));
        }
        _frames = 0;
        _found = 0;
        _process.Clear();
        _periodStart = Stopwatch.GetTimestamp();
    }
}
