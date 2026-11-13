using System.Diagnostics;

namespace Theater3D.Media;

/// <summary>
/// 仮想カメラへ送る絵の置き場。描く側（画面のスレッド）が BGRA の絵を置き、パイプの受け口（別のスレッド）が NV12 にして持っていく。
/// 入れ物を 3 つ（描く側が書く・最新・変換中）回して使うので、描く側は変換を待たない。
/// 描く側は 1 つのスレッドから使うこと（<see cref="GetWriteBuffer"/> と <see cref="Publish"/>）。
/// </summary>
public sealed class VirtualCameraFrames
{
    /// <summary>最後に頼まれてから、この長さの間は「見られている」とする。</summary>
    public static readonly TimeSpan WantedFor = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly object _convertGate = new();
    private byte[] _back = [];
    private byte[] _ready = [];
    private byte[] _working = [];
    private int _readyWidth;
    private int _readyHeight;
    private ulong _readySequence; // 0 なら、まだ持っていかれていない最新の絵は無い
    private ulong _sequence;
    private byte[] _nv12 = [];
    private int _nv12Width;
    private int _nv12Height;
    private ulong _nv12Sequence;
    private int _preferredWidth = 1920;
    private int _preferredHeight = 1080;
    private int _wantedWidth;
    private int _wantedHeight;
    private long _wantedAt = long.MinValue;

    /// <summary>大きさを聞かれたときに答える大きさ（配信用の画面の大きさ）。</summary>
    public (int Width, int Height) PreferredSize
    {
        get
        {
            lock (_gate)
            {
                return (_preferredWidth, _preferredHeight);
            }
        }
        set
        {
            if (!VirtualCameraProtocol.IsValidSize(value.Width, value.Height))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "仮想カメラの大きさは 16〜4096 の偶数にしてください");
            }
            lock (_gate)
            {
                (_preferredWidth, _preferredHeight) = value;
            }
        }
    }

    /// <summary>直近（<see cref="WantedFor"/> の間）に絵を頼まれていれば、その大きさ。描く側は、頼まれている間だけ描く。</summary>
    public bool TryGetWanted(out int width, out int height)
    {
        lock (_gate)
        {
            width = _wantedWidth;
            height = _wantedHeight;
            return _wantedAt != long.MinValue && Stopwatch.GetElapsedTime(_wantedAt) < WantedFor;
        }
    }

    /// <summary>描く側: 次の絵（BGRA・上の行から・1 行は幅 × 4 バイト）を書き込む場所。書いたら <see cref="Publish"/> を呼ぶ。</summary>
    public byte[] GetWriteBuffer(int width, int height)
    {
        int bytes = checked(width * height * 4);
        if (_back.Length != bytes)
        {
            _back = new byte[bytes];
        }
        return _back;
    }

    /// <summary>描く側: 書き込んだ絵を最新の絵にする。</summary>
    public void Publish(int width, int height)
    {
        lock (_gate)
        {
            (_back, _ready) = (_ready, _back);
            _readyWidth = width;
            _readyHeight = height;
            _readySequence = ++_sequence;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// パイプの受け口: 頼まれた大きさの最新の絵を NV12（色の決まりは <see cref="VirtualCameraProtocol.MatrixFor"/>）にして <paramref name="nv12"/> に写し、その絵の番号を返す。
    /// <paramref name="lastSequence"/> より新しい絵を <paramref name="timeout"/> まで待つ。頼まれた大きさの絵が無ければ 0。
    /// </summary>
    public ulong CopyNv12(int width, int height, ulong lastSequence, TimeSpan timeout, Span<byte> nv12)
    {
        int bytes = VirtualCameraProtocol.Nv12Bytes(width, height);
        if (nv12.Length < bytes)
        {
            throw new ArgumentException("NV12 の場所が小さすぎます", nameof(nv12));
        }
        lock (_convertGate)
        {
            bool convert = false;
            ulong sequence = 0;
            lock (_gate)
            {
                _wantedWidth = width;
                _wantedHeight = height;
                _wantedAt = Stopwatch.GetTimestamp();
                long start = Stopwatch.GetTimestamp();
                while (!HasFresh(width, height) && !HasNewerNv12(width, height, lastSequence))
                {
                    TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }
                    Monitor.Wait(_gate, remaining);
                }
                if (HasFresh(width, height))
                {
                    (_ready, _working) = (_working, _ready);
                    sequence = _readySequence;
                    _readySequence = 0;
                    convert = true;
                }
            }
            if (convert)
            {
                if (_nv12.Length != bytes)
                {
                    _nv12 = new byte[bytes];
                }
                VideoConvert.BgraToNv12(_working, width * 4, width, height, _nv12, VirtualCameraProtocol.MatrixFor(height));
                _nv12Width = width;
                _nv12Height = height;
                _nv12Sequence = sequence;
            }
            if (_nv12Sequence == 0 || _nv12Width != width || _nv12Height != height)
            {
                return 0;
            }
            _nv12.AsSpan(0, bytes).CopyTo(nv12);
            return _nv12Sequence;
        }
    }

    private bool HasFresh(int width, int height) => _readySequence != 0 && _readyWidth == width && _readyHeight == height;

    private bool HasNewerNv12(int width, int height, ulong lastSequence) =>
        _nv12Sequence > lastSequence && _nv12Width == width && _nv12Height == height;
}
