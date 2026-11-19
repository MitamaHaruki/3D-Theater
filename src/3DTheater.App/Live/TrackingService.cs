using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Theater3D.App.Logging;
using Theater3D.Editor;
using Theater3D.Media;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの顔のトラッキング。読み込み用のスレッドで、カメラ（または確かめ用の動画のファイル）を読み、顔の点・表情の値・頭の向きを求める。
/// スマホの顔のアプリ（VTube Studio・MeowFace・iFacialMocap）から受け取ることもできる（<see cref="StartPhone"/>。絵は無い）。
/// 結果は最新の 1 つだけを持つ（画面は時々読みに来る。<see cref="Latest"/>・<see cref="ReadFrame"/>）。
/// 動画のファイルは、映像の時刻に合わせて実際の速さで読む（終わりで先頭に戻る）。
/// 速さ（処理の時間・回数・顔を見つけた割合）を <see cref="Statistics"/> に数え、止めたときにまとめをログに書く。
/// カメラから映像が届かないときは知らせ（<see cref="Problem"/>）、読み出しに失敗した・止まったときは開き直す（<see cref="VideoWatch"/>。設計判断記録 97 章）。
/// </summary>
public sealed class TrackingService : IDisposable
{
    private static readonly uint DotColor = 0xFF40E0FF; // 顔の点の色（BGRA の並びで 水色）
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly object _captureLock = new();
    private readonly VideoWatch _watch = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private Timer? _watchTimer;
    private volatile bool _stop;
    private volatile bool _stalled; // 映像が届かないので、見張りが読み込みをやめさせた
    private CameraCapture? _capture;
    private TrackingSnapshot _latest = TrackingSnapshot.Empty;
    private byte[]? _frame;

    // 読み込み用のスレッドが今していること（止まらないときに、どこで止まっているかをログに書くため）
    private volatile string _phase = string.Empty;

    /// <summary>始めた・止めた・エラーで止まった（読み込み用のスレッドから知らせることがある）。</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>読んでいる映像の名前（カメラの名前・ファイルの名前）。</summary>
    public string? SourceName { get; private set; }

    /// <summary>速さを数える（ライブモードの速さのログが 1 分ごとに読む）。</summary>
    public TrackingStats Statistics { get; } = new();

    /// <summary>最後に止まったときのエラー（無ければ null）。</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// 今の困りごと（無ければ null）: 動いている間は、映像が届かない・開き直している（<see cref="VideoWatch.Problem"/>）。
    /// エラーで止まった後は、そのエラー（次に始めるまで）。画面の状態の行と、ライブモードの左下の赤い字に出す。
    /// </summary>
    public string? Problem => IsRunning
        ? _watch.Problem(_clock.Elapsed, SourceName)
        : Error is { } error ? $"顔のトラッキングが止まりました: {error}" : null;

    public TrackingSnapshot Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>最新の絵（顔の点を描き込んだもの）を、ロックしたまま <paramref name="read"/> に渡す（画面に写すため）。</summary>
    public void ReadFrame(Action<byte[], int, int> read)
    {
        lock (_lock)
        {
            if (_frame is not null)
            {
                read(_frame, _latest.Width, _latest.Height);
            }
        }
    }

    public void StartCamera(CameraDevice camera) => Start(camera.Name, () => Run(() => CameraCapture.OpenCamera(camera.Id)), watch: true);

    public void StartFile(string path) => Start(Path.GetFileName(path), () => Run(() => CameraCapture.OpenFile(path, loop: true)), watch: true);

    /// <summary>
    /// スマホから受け取る。<paramref name="phone"/> があれば、VTube Studio の形式で、そのスマホに「送って」と頼み続ける（MeowFace も同じ）。
    /// 無ければ、スマホから送られてくるのを待つ（iFacialMocap・FaceMotion3D はポート 49983、MeowFace はポート 12345）。
    /// </summary>
    public void StartPhone(IPAddress? phone)
    {
        string name = phone is null ? "スマホ（iFacialMocap・MeowFace から送る）" : $"スマホ（VTube Studio・MeowFace {phone}）";
        Start(name, () => RunPhone(phone), watch: false);
    }

    /// <summary>前に始めた映像で、もう一度始められるか（キーの「トラッキングの開始・停止」に使う）。</summary>
    public bool CanRestart => _lastStart is not null;

    /// <summary>前に始めた映像で、もう一度始める（無ければ false）。</summary>
    public bool Restart()
    {
        if (_lastStart is not { } last)
        {
            return false;
        }
        Start(last.Name, last.Run, last.Watch);
        return true;
    }

    private (string Name, Action Run, bool Watch)? _lastStart;

    private void Start(string name, Action run, bool watch)
    {
        Stop();
        _lastStart = (name, run, watch);
        _stop = false;
        _stalled = false;
        SourceName = name;
        Error = null;
        lock (_lock)
        {
            _latest = TrackingSnapshot.Empty;
            _frame = null;
        }
        if (watch)
        {
            // 映像が届かないまま止まっていないかを、1 秒ごとに見る（スマホは見張らない。送ってこないのは、スマホの側の都合のため）
            _watch.Reset(_clock.Elapsed);
            _watchTimer = new Timer(_ => CheckStall(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else
        {
            _watch.Stop();
        }
        _thread = new Thread(() => run()) { IsBackground = true, Name = "顔のトラッキング" };
        _thread.Start();
        AppLog.Info($"顔のトラッキングを始めます: {name}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _watchTimer?.Dispose();
        _watchTimer = null;
        if (_thread is not { } thread)
        {
            return;
        }
        _stop = true;
        lock (_captureLock)
        {
            _capture?.Cancel(); // 届かないフレームを待っていても、すぐ戻らせる
        }
        long stopping = Stopwatch.GetTimestamp();
        if (!thread.Join(StopTimeout))
        {
            AppLog.Warning($"顔のトラッキングのスレッドが {StopTimeout.TotalSeconds:F0} 秒で止まりません（{_phase}）");
        }
        else if (Stopwatch.GetElapsedTime(stopping) is { TotalSeconds: > 1 } took)
        {
            AppLog.Info($"顔のトラッキングを止めるのに {took.TotalSeconds:F1} 秒かかりました");
        }
        LogSummary(); // スレッドが止まりきらなくても、まとめは書く（スレッドが先に書いていれば、何もしない）
        _watch.Stop();
        _thread = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // 見張り（1 秒ごと。タイマーのスレッド）: 映像が届かないまま止まっていれば、読み込みをやめさせて開き直させる
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
                AppLog.Warning($"顔のトラッキング: 映像が {VideoWatch.StallAfter.TotalSeconds:F0} 秒届かないため、映像を開き直します（{_phase}）");
                capture.Cancel();
            }
        }
    }

    // カメラ（動画のファイル）を開いて読む。読み出しに失敗した・映像が止まったときは、間をあけて開き直す（VideoWatch.RetryDelays の回数まで）
    private void Run(Func<CameraCapture> open)
    {
        FaceTracker? tracker = null;
        try
        {
            var rate = new RateCounter();
            bool begun = false;
            while (!_stop)
            {
                string? failure = null;
                CameraCapture? capture = null;
                try
                {
                    _phase = "カメラを開いている";
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
                    if (tracker is null)
                    {
                        _phase = "顔のモデルを読んでいる";
                        tracker = new FaceTracker();
                    }
                    AppLog.Info($"映像を開きました（{capture.Width} × {capture.Height}・{capture.FrameRate:F1} 回/秒{(capture.IsFile ? "・動画のファイル" : string.Empty)}）");
                    if (!begun)
                    {
                        Statistics.Begin(phone: false);
                        begun = true;
                    }
                    _watch.Opened(_clock.Elapsed);
                    ReadFrames(capture, tracker, rate);
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
                        AppLog.Warning($"顔のトラッキング: 映像を読めませんでした（{ex.GetType().Name}: {ex.Message}）");
                    }
                }
                finally
                {
                    if (_stop)
                    {
                        // まとめは、カメラを閉じるより先に書く（閉じるのに時間がかかると、アプリが先に終わって、まとめが抜けるため。ノートパソコンで起きた）
                        LogSummary();
                    }
                    lock (_captureLock)
                    {
                        _capture = null;
                    }
                    CloseCapture(capture);
                    _stalled = false;
                }
                if (_stop || failure is null)
                {
                    break; // 止めた・動画のファイルの終わり（繰り返さないとき）
                }
                if (_watch.Failed(failure, _clock.Elapsed) is not { } delay)
                {
                    Error = $"映像を読めません（{VideoWatch.RetryDelays.Count} 回開き直しても届きませんでした。{failure}）";
                    AppLog.Error($"顔のトラッキングが止まりました: {Error}");
                    break;
                }
                // 動いているままなので、StateChanged は出さない（出すと、始めたときと同じに正面を覚え直してしまう）。画面は Problem を読みに来る
                AppLog.Warning($"顔のトラッキング: {delay.TotalSeconds:F0} 秒後に映像を開き直します（{_watch.Failures}/{VideoWatch.RetryDelays.Count} 回目。{failure}）");
                WaitUnlessStopped(delay);
            }
        }
        finally
        {
            LogSummary();
            tracker?.Dispose();
            _phase = string.Empty;
            if (!_stop)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // 開き直すまで待つ（止められたら、すぐ戻る）
    private void WaitUnlessStopped(TimeSpan delay)
    {
        _phase = "カメラを開き直すのを待っている";
        long start = Stopwatch.GetTimestamp();
        while (!_stop && Stopwatch.GetElapsedTime(start) < delay)
        {
            Thread.Sleep(100);
        }
    }

    // 開いたカメラ（動画のファイル）から、止めるまで・読めなくなるまで読む
    private void ReadFrames(CameraCapture capture, FaceTracker tracker, RateCounter rate)
    {
        var buffers = new[] { new byte[capture.FrameBytes], new byte[capture.FrameBytes] };
        int current = 0;
        long? firstTime = null;
        long wallStart = 0;
        long previousTime = long.MinValue;
        while (!_stop)
        {
            byte[] frame = buffers[current];
            _phase = "映像が届くのを待っている";
            long readStart = Stopwatch.GetTimestamp();
            if (!capture.Read(frame, out long timestamp))
            {
                break;
            }
            _watch.FrameArrived(_clock.Elapsed);
            double waitMilliseconds = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds; // 映像が届くのを待った時間
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
            }
            _phase = "顔を読んでいる";
            FaceTrackingResult result = tracker.Process(new BgraImage(frame, capture.Width, capture.Height));
            Statistics.AddCameraFrame(result.Milliseconds, waitMilliseconds, result.Found, result.Detected);
            if (result.Found)
            {
                DrawDots(frame, capture.Width, capture.Height, result.Landmarks);
            }
            double fps = rate.Tick();
            lock (_lock)
            {
                _latest = new TrackingSnapshot(result, capture.Width, capture.Height, fps, _latest.Sequence + 1);
                _frame = frame;
            }
            current ^= 1; // 画面が写している間に書き換えないよう、交互に使う
        }
    }

    // スマホから受け取る（受け取りは PhoneReceiver のスレッド。ここでは止めるまで待つ）
    private void RunPhone(IPAddress? phone)
    {
        int[] ports = phone is null ? [PhoneMessages.IFacialMocapPort, PhoneMessages.MeowFacePort] : [PhoneMessages.VTubeStudioReplyPort];
        try
        {
            var rate = new RateCounter();
            using var receiver = new PhoneReceiver(ports, IPAddress.Any, phone is null ? null : new IPEndPoint(phone, PhoneMessages.VTubeStudioPort));
            receiver.FrameReceived += (frame, from) =>
            {
                Statistics.AddPhoneFrame(frame.FaceFound);
                lock (_lock)
                {
                    double fps = rate.Tick();
                    _latest = new TrackingSnapshot(null, 0, 0, fps, _latest.Sequence + 1) { Phone = frame, PhoneSender = from.Address.ToString() };
                    _frame = null;
                }
            };
            receiver.Start(); // 初めてのときは、Windows のファイアウォールの確認が出ることがある
            Statistics.Begin(phone: true);
            AppLog.Info($"スマホから受け取ります（ポート {string.Join("・", ports)}{(phone is null ? string.Empty : $"・{phone}:{PhoneMessages.VTubeStudioPort} に頼む")}）");
            while (!_stop)
            {
                Thread.Sleep(100);
            }
            AppLog.Info($"スマホからの受け取りを止めました（届いた {receiver.Packets}・読めた {receiver.Frames}）");
        }
        catch (SocketException ex)
        {
            if (!_stop)
            {
                Error = $"ポート {string.Join("・", ports)} で受け取れません（ほかのアプリが使っている可能性があります）: {ex.Message}";
                AppLog.Error("スマホから受け取れませんでした", ex);
            }
        }
        finally
        {
            LogSummary();
            if (!_stop)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void CloseCapture(CameraCapture? capture)
    {
        if (capture is null)
        {
            return;
        }
        _phase = "カメラを閉じている";
        long closing = Stopwatch.GetTimestamp();
        capture.Dispose();
        TimeSpan took = Stopwatch.GetElapsedTime(closing);
        if (took > TimeSpan.FromSeconds(1))
        {
            AppLog.Info($"カメラを閉じるのに {took.TotalSeconds:F1} 秒かかりました");
        }
    }

    // 止めたときに、この 1 回分の速さのまとめを書く（始められなかったときは書かない。2 回目からは何もしない）
    private void LogSummary()
    {
        if (Statistics.Finish() is { } run)
        {
            AppLog.Info($"[まとめ] 顔のトラッキング: {PerformanceText.Tracking(run)}");
        }
    }

    // 顔の点を 2 × 2 の点で描き込む（画面で、どこを顔と見ているか分かるように）
    private static void DrawDots(byte[] frame, int width, int height, Vector3[] landmarks)
    {
        Span<uint> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        foreach (Vector3 p in landmarks)
        {
            int x = (int)p.X;
            int y = (int)p.Y;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int px = x + dx;
                    int py = y + dy;
                    if ((uint)px < (uint)width && (uint)py < (uint)height)
                    {
                        pixels[(py * width) + px] = DotColor;
                    }
                }
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>1 秒あたりの回数（直近の 1 秒ほどで数える）。</summary>
    private sealed class RateCounter
    {
        private long _start = Stopwatch.GetTimestamp();
        private int _count;
        private double _rate;

        public double Tick()
        {
            _count++;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_start);
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                _rate = _count / elapsed.TotalSeconds;
                _count = 0;
                _start = Stopwatch.GetTimestamp();
            }
            return _rate;
        }
    }
}

/// <summary>トラッキングの最新の結果（カメラなら <see cref="Result"/>、スマホなら <see cref="Phone"/>）。</summary>
public sealed record TrackingSnapshot(FaceTrackingResult? Result, int Width, int Height, double FramesPerSecond, long Sequence)
{
    public static TrackingSnapshot Empty { get; } = new(null, 0, 0, 0, 0);

    /// <summary>スマホから受け取った 1 枚分（カメラのときは null）。</summary>
    public PhoneFrame? Phone { get; init; }

    /// <summary>送ってきたスマホの IP。</summary>
    public string? PhoneSender { get; init; }
}
