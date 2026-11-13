using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Theater3D.App.Logging;
using Theater3D.Media;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの顔のトラッキング。読み込み用のスレッドで、カメラ（または確かめ用の動画のファイル）を読み、顔の点・表情の値・頭の向きを求める。
/// スマホの顔のアプリ（VTube Studio・MeowFace・iFacialMocap）から受け取ることもできる（<see cref="StartPhone"/>。絵は無い）。
/// 結果は最新の 1 つだけを持つ（画面は時々読みに来る。<see cref="Latest"/>・<see cref="ReadFrame"/>）。
/// 動画のファイルは、映像の時刻に合わせて実際の速さで読む（終わりで先頭に戻る）。
/// </summary>
public sealed class TrackingService : IDisposable
{
    private static readonly uint DotColor = 0xFF40E0FF; // 顔の点の色（BGRA の並びで 水色）

    private readonly object _lock = new();
    private readonly object _captureLock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private CameraCapture? _capture;
    private TrackingSnapshot _latest = TrackingSnapshot.Empty;
    private byte[]? _frame;

    /// <summary>始めた・止めた・エラーで止まった（読み込み用のスレッドから知らせることがある）。</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>読んでいる映像の名前（カメラの名前・ファイルの名前）。</summary>
    public string? SourceName { get; private set; }

    /// <summary>最後に止まったときのエラー（無ければ null）。</summary>
    public string? Error { get; private set; }

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

    public void StartCamera(CameraDevice camera) => Start(camera.Name, () => Run(() => CameraCapture.OpenCamera(camera.Id)));

    public void StartFile(string path) => Start(Path.GetFileName(path), () => Run(() => CameraCapture.OpenFile(path, loop: true)));

    /// <summary>
    /// スマホから受け取る。<paramref name="phone"/> があれば、VTube Studio の形式で、そのスマホに「送って」と頼み続ける（MeowFace も同じ）。
    /// 無ければ、スマホから送られてくるのを待つ（iFacialMocap・FaceMotion3D はポート 49983、MeowFace はポート 12345）。
    /// </summary>
    public void StartPhone(IPAddress? phone)
    {
        string name = phone is null ? "スマホ（iFacialMocap・MeowFace から送る）" : $"スマホ（VTube Studio・MeowFace {phone}）";
        Start(name, () => RunPhone(phone));
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
        Start(last.Name, last.Run);
        return true;
    }

    private (string Name, Action Run)? _lastStart;

    private void Start(string name, Action run)
    {
        Stop();
        _lastStart = (name, run);
        _stop = false;
        SourceName = name;
        Error = null;
        lock (_lock)
        {
            _latest = TrackingSnapshot.Empty;
            _frame = null;
        }
        _thread = new Thread(() => run()) { IsBackground = true, Name = "顔のトラッキング" };
        _thread.Start();
        AppLog.Info($"顔のトラッキングを始めます: {name}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (_thread is not { } thread)
        {
            return;
        }
        _stop = true;
        lock (_captureLock)
        {
            _capture?.Cancel(); // 届かないフレームを待っていても、すぐ戻らせる
        }
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            AppLog.Warning("顔のトラッキングのスレッドが止まりません");
        }
        _thread = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Run(Func<CameraCapture> open)
    {
        try
        {
            using CameraCapture capture = open();
            lock (_captureLock)
            {
                _capture = capture;
            }
            using var tracker = new FaceTracker();
            AppLog.Info($"映像を開きました（{capture.Width} × {capture.Height}・{capture.FrameRate:F1} 回/秒{(capture.IsFile ? "・動画のファイル" : string.Empty)}）");
            var buffers = new[] { new byte[capture.FrameBytes], new byte[capture.FrameBytes] };
            int current = 0;
            long? firstTime = null;
            long wallStart = 0;
            long previousTime = long.MinValue;
            var rate = new RateCounter();
            while (!_stop)
            {
                byte[] frame = buffers[current];
                if (!capture.Read(frame, out long timestamp))
                {
                    break;
                }
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
                FaceTrackingResult result = tracker.Process(new BgraImage(frame, capture.Width, capture.Height));
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_stop)
            {
                Error = ex.Message;
                AppLog.Error("顔のトラッキングが止まりました", ex);
            }
        }
        finally
        {
            lock (_captureLock)
            {
                _capture = null;
            }
            if (!_stop)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
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
                lock (_lock)
                {
                    double fps = rate.Tick();
                    _latest = new TrackingSnapshot(null, 0, 0, fps, _latest.Sequence + 1) { Phone = frame, PhoneSender = from.Address.ToString() };
                    _frame = null;
                }
            };
            receiver.Start(); // 初めてのときは、Windows のファイアウォールの確認が出ることがある
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
            if (!_stop)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
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
