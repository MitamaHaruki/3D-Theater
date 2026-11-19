using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Theater3D.Tracking.Vmc;

/// <summary>往復の時間のまとめ（ms）。</summary>
public readonly record struct RoundTripSummary(int Sent, int Replied, double Average, double P95, double Max)
{
    /// <summary>返事の無かった数。</summary>
    public int Unanswered => Math.Max(0, Sent - Replied);
}

/// <summary>
/// VMC プロトコルを UDP で送る（送り手。3D Theater VCamera）。1 枚分を 1 つのバンドルにして送り、受け手（3D Theater）が返した番号から
/// 往復の時間を測る（ネットワークの片道は、およそその半分。設計判断記録 98 章）。ほかのアプリへ送ったときは返事が無い（測れないだけ）。
/// </summary>
public sealed class VmcSender : IDisposable
{
    private const int Slots = 256;

    private readonly UdpClient _client;
    private readonly long[] _sentAt = new long[Slots];
    private readonly int[] _sentSequence = new int[Slots];
    private readonly object _lock = new();
    private readonly List<double> _roundTrips = [];
    private readonly Thread _thread;
    private int _sent;
    private long _lastReply;
    private volatile bool _stopping;

    public VmcSender(IPEndPoint destination)
    {
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        // 空いているポートに結び付けておく（送る前から返事を待てるように。受け手は、送ってきたポートへ返す）
        _client = new UdpClient(new IPEndPoint(destination.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        Array.Fill(_sentSequence, -1);
        _thread = new Thread(ReceiveReplies) { IsBackground = true, Name = "VMC プロトコルの返事を受け取る" };
        _thread.Start();
    }

    public IPEndPoint Destination { get; }

    /// <summary>直近の往復の時間（ms。返事が無ければ null）。</summary>
    public double? LastRoundTrip { get; private set; }

    /// <summary>この 3 秒の間に返事があったか（送り先で 3D Theater が受け取っているか）。</summary>
    public bool IsAnswered => Interlocked.Read(ref _lastReply) is var t && t != 0 && Stopwatch.GetElapsedTime(t) < TimeSpan.FromSeconds(3);

    /// <summary>1 枚分を送る。送れなければ（ネットワークが無いなど）false。</summary>
    public bool Send(VmcFaceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        byte[] bundle = Osc.WriteBundle(VmcProtocol.FaceMessages(frame));
        int slot = (int)((uint)frame.Sequence % Slots);
        lock (_lock)
        {
            _sentAt[slot] = Stopwatch.GetTimestamp();
            _sentSequence[slot] = frame.Sequence;
            _sent++;
        }
        try
        {
            _client.Send(bundle, bundle.Length, Destination);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>前に読んでからの、往復の時間のまとめ（読むと数え直す）。</summary>
    public RoundTripSummary TakeSummary()
    {
        lock (_lock)
        {
            int sent = _sent;
            double[] values = [.. _roundTrips];
            _sent = 0;
            _roundTrips.Clear();
            if (values.Length == 0)
            {
                return new RoundTripSummary(sent, 0, 0, 0, 0);
            }
            Array.Sort(values);
            double p95 = values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * 0.95) - 1)];
            return new RoundTripSummary(sent, values.Length, values.Average(), p95, values[^1]);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _client.Dispose();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void ReceiveReplies()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopping)
        {
            byte[] data;
            try
            {
                data = _client.Receive(ref any);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // 送り先で誰も待ち受けていない知らせ。待ち続ける
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return; // しまった（このスレッドの外へ例外を出さない）
            }
            long now = Stopwatch.GetTimestamp();
            foreach (OscMessage message in Osc.Read(data))
            {
                if (message.Address != VmcProtocol.Ack || message.Int(0) is not { } sequence)
                {
                    continue;
                }
                int slot = (int)((uint)sequence % Slots);
                lock (_lock)
                {
                    if (_sentSequence[slot] != sequence)
                    {
                        continue; // 古すぎる返事
                    }
                    _sentSequence[slot] = -1; // 同じ番号の返事を 2 度数えない
                    double ms = Stopwatch.GetElapsedTime(_sentAt[slot], now).TotalMilliseconds;
                    _roundTrips.Add(ms);
                    LastRoundTrip = ms;
                }
                Interlocked.Exchange(ref _lastReply, now);
            }
        }
    }
}
