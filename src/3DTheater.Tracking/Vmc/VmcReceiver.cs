using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace Theater3D.Tracking.Vmc;

/// <summary>
/// VMC プロトコルで届いた知らせを積み重ねて、今の顔の形にする（設計判断記録 98 章）。
/// 送り手によって、1 枚分が 1 つのバンドルで届くことも、いくつにも分かれて届くこともあるので、届くたびに今の形を作り直す。
/// 表情は「/VMC/Ext/Blend/Apply」が届いたときに当てる（決まりの通り）。
/// </summary>
public sealed class VmcState
{
    // 頭の向きを決めるボーン（体の側から順に掛ける。Hips は体ごとの向きなので入れない）
    private static readonly string[] HeadChain = ["Spine", "Chest", "UpperChest", "Neck", "Head"];

    private readonly Dictionary<string, Quaternion> _bones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _pending = new(StringComparer.Ordinal);
    private Dictionary<string, float> _blend = new(StringComparer.Ordinal);
    private (bool Found, Vector3 Rotation, Vector3 Position)? _face;
    private readonly HandPose?[] _hands = new HandPose?[2]; // 本人の左手・右手（3D Theater VCamera の手の知らせ）

    /// <summary>3D Theater VCamera の 1 枚の番号（届いていなければ null）。受け手が返す。</summary>
    public int? LastSequence { get; private set; }

    /// <summary>知らせ 1 つを当てる。形が変わったら true（ボーン・表情を当てた・顔の知らせ）。</summary>
    public bool Apply(OscMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message.Address)
        {
            case VmcProtocol.BonePos when message.String(0) is { } name && Floats(message, 1, 7) is { } v:
                _bones[name] = Quaternion.Normalize(new Quaternion(v[3], v[4], v[5], v[6]));
                return true;
            case VmcProtocol.BlendVal when message.String(0) is { } name && message.Float(1) is { } value && float.IsFinite(value):
                _pending[name] = Math.Clamp(value, 0f, 1f);
                return false;
            case VmcProtocol.BlendApply:
                _blend = new Dictionary<string, float>(_pending, StringComparer.Ordinal);
                return true;
            case VmcProtocol.Face when message.Int(0) is { } found && Floats(message, 1, 6) is { } f:
                _face = (found != 0, new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]));
                return true;
            case VmcProtocol.Hand when message.Int(0) is { } side and (0 or 1) && message.Int(1) is { } found
                && Floats(message, 2, HandPose.ValueCount) is { } angles:
                _hands[side] = found != 0 ? new HandPose(side == 1, angles) : null;
                return true;
            case VmcProtocol.Frame when message.Int(0) is { } sequence:
                LastSequence = sequence;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 今の形を、スマホと同じ 1 枚分にする（<see cref="PhoneProtocol.Vmc"/>）。
    /// 3D Theater VCamera の顔の知らせがあれば、その向き・位置（カメラのトラッキングと同じ値。正面に合わせる前）を使い、
    /// 無ければ（ほかのアプリ）頭までのボーンの回転から向きを求める（位置は無し）。頭のボーンも表情も無ければ null。
    /// 手の知らせ（3D Theater VCamera）があれば、見つかっている手を <see cref="PhoneFrame.Hands"/> に入れる。
    /// </summary>
    public PhoneFrame? ToFrame()
    {
        Dictionary<string, float> blend = Normalize(_blend);
        HandPose[] hands = [.. _hands.OfType<HandPose>()];
        if (_face is { } face)
        {
            return new PhoneFrame(PhoneProtocol.Vmc, face.Found, face.Rotation, face.Position, blend) { FromFaceTracker = true, Hands = hands };
        }
        bool hasHead = false;
        Quaternion head = Quaternion.Identity;
        foreach (string name in HeadChain)
        {
            if (_bones.TryGetValue(name, out Quaternion rotation))
            {
                head *= rotation; // 親から子へ（子の回転は、親の回転の中で回す）
                hasHead |= name == "Head";
            }
        }
        if (!hasHead && blend.Count == 0)
        {
            return null;
        }
        var (yaw, pitch, roll) = VmcProtocol.CameraAngles(head);
        return new PhoneFrame(PhoneProtocol.Vmc, true, new Vector3(yaw, pitch, roll), Vector3.Zero, blend);
    }

    /// <summary>
    /// 表情の名前を ARKit の名前にそろえる（EyeBlinkLeft → eyeBlinkLeft）。ARKit の値が無い送り手のために、VRM の基本の表情
    /// （Blink・Blink_L・Blink_R・A・U・O と、VRM 1.0 の blink・aa・ou・oh）からも作る（ARKit の値があれば、そちらを使う）。
    /// </summary>
    public static Dictionary<string, float> Normalize(IReadOnlyDictionary<string, float> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var arkit = new Dictionary<string, float>(StringComparer.Ordinal);
        var presets = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (raw, value) in source)
        {
            string name = PhoneMessages.NormalizeName(raw);
            if (FaceBlendshapes.IndexOf(name) >= 0)
            {
                arkit[name] = arkit.TryGetValue(name, out float old) ? Math.Max(old, value) : value;
            }
            else
            {
                presets[name] = value;
            }
        }
        void FromPreset(string arkitName, params string[] names)
        {
            if (arkit.ContainsKey(arkitName))
            {
                return;
            }
            foreach (string n in names)
            {
                if (presets.TryGetValue(n, out float v))
                {
                    arkit[arkitName] = Math.Max(arkit.GetValueOrDefault(arkitName), v);
                }
            }
        }
        FromPreset("eyeBlinkLeft", "blinkLeft", "blink");
        FromPreset("eyeBlinkRight", "blinkRight", "blink");
        FromPreset("jawOpen", "a", "aa");
        FromPreset("mouthPucker", "u", "ou");
        FromPreset("mouthFunnel", "o", "oh");
        return arkit;
    }

    private static float[]? Floats(OscMessage message, int start, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (message.Float(start + i) is not { } v || !float.IsFinite(v))
            {
                return null;
            }
            values[i] = v;
        }
        return values;
    }
}

/// <summary>
/// VMC プロトコルを UDP で受け取る（受け手。初めのポートは <see cref="VmcProtocol.MarionettePort"/>）。届くたびに今の形を
/// <see cref="FrameReceived"/> で知らせる（受け取り用のスレッドから）。3D Theater VCamera の 1 枚の番号が届いたら、同じ番号を送り手へ返す
/// （<see cref="VmcProtocol.Ack"/>。送り手が往復の時間を測る）。
/// </summary>
public sealed class VmcReceiver : IDisposable
{
    private readonly int _port;
    private readonly IPAddress _bindAddress;
    private readonly VmcState _state = new();
    private UdpClient? _client;
    private Thread? _thread;
    private long _packets;
    private long _frames;
    private volatile bool _stopping;

    public VmcReceiver(int port, IPAddress bindAddress)
    {
        _port = port;
        _bindAddress = bindAddress;
    }

    /// <summary>今の形が変わった（受け取り用のスレッドから。送り手の場所も）。</summary>
    public event Action<PhoneFrame, IPEndPoint>? FrameReceived;

    /// <summary>届いた数（読めなかったものも含む）。</summary>
    public long Packets => Interlocked.Read(ref _packets);

    /// <summary>形にできた数。</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>待ち受けているポート。</summary>
    public int Port => _port;

    /// <summary>待ち受けを始める。ポートがほかで使われていれば <see cref="SocketException"/>。</summary>
    public void Start()
    {
        if (_client is not null)
        {
            return;
        }
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            client.Client.Bind(new IPEndPoint(_bindAddress, _port));
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _thread = new Thread(Receive) { IsBackground = true, Name = "VMC プロトコルを受け取る" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _client?.Dispose(); // 待っている受け取りをほどく
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void Receive()
    {
        UdpClient client = _client!;
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopping)
        {
            byte[] data;
            try
            {
                data = client.Receive(ref any);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // 返した番号が届かなかった（送り手が止まった）知らせ。待ち続ける
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _packets);
            bool changed = false;
            int? sequence = null;
            foreach (OscMessage message in Osc.Read(data))
            {
                changed |= _state.Apply(message);
                if (message.Address == VmcProtocol.Frame)
                {
                    sequence = message.Int(0);
                }
            }
            var from = new IPEndPoint(any.Address, any.Port);
            if (sequence is { } seq)
            {
                try
                {
                    byte[] ack = Osc.Write(new OscMessage(VmcProtocol.Ack, seq));
                    client.Send(ack, ack.Length, from);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // 返せなくても受け取りは続ける（往復の時間が測れないだけ）
                }
            }
            if (changed && _state.ToFrame() is { } frame)
            {
                Interlocked.Increment(ref _frames);
                FrameReceived?.Invoke(frame, from);
            }
        }
    }
}
