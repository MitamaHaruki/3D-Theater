using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Theater3D.Tracking;

/// <summary>スマホの顔のアプリの送り方。</summary>
public enum PhoneProtocol
{
    /// <summary>
    /// VTube Studio（iPhone）の形式。PC がスマホのポート 21412 へ「何秒送って」と頼み、スマホが JSON で返す。
    /// Android の MeowFace も同じ JSON を送る（PC の IP とポートをスマホに入れて、スマホから送る使い方もある）。
    /// </summary>
    VTubeStudio,

    /// <summary>iFacialMocap（iPhone）の形式。スマホに PC の IP を入れると、スマホが PC のポート 49983 へ文字で送る（FaceMotion3D も同じ）。</summary>
    IFacialMocap,

    /// <summary>
    /// VMC プロトコル（ほかの PC の 3D Theater VCamera・VMC プロトコルを送るアプリ。設計判断記録 98 章）。
    /// <see cref="PhoneFrame.Rotation"/> はカメラのトラッキングと同じ決まりの（左右・上下・傾き）、
    /// <see cref="PhoneFrame.Position"/> は（真ん中 x・真ん中 y・大きさ。画素）で、大きさが 0 なら位置が無い。
    /// </summary>
    Vmc,
}

/// <summary>
/// スマホから受け取った 1 枚分。表情の値は ARKit の名前（eyeBlinkLeft など）で 0〜1。
/// 頭の向き（<see cref="Rotation"/>。度）と位置（<see cref="Position"/>）は、アプリの決まりのまま（x = 上下、y = 左右、z = 傾き）。
/// VMC プロトコルのときは <see cref="PhoneProtocol.Vmc"/> に書いた決まり。
/// </summary>
public sealed record PhoneFrame(PhoneProtocol Protocol, bool FaceFound, Vector3 Rotation, Vector3 Position, IReadOnlyDictionary<string, float> Blendshapes)
{
    /// <summary>
    /// 本アプリと同じ顔のトラッキング（MediaPipe）で読んだ値か（3D Theater VCamera）。まばたきの値の幅を、カメラのときと同じにする。
    /// </summary>
    public bool FromFaceTracker { get; init; }

    public float Blendshape(string name) => Blendshapes.TryGetValue(name, out float value) ? value : 0f;
}

/// <summary>スマホの顔のアプリとの受け渡しの決まり（頼み方と、届いたものの読み方）。</summary>
public static class PhoneMessages
{
    /// <summary>VTube Studio の形式で、スマホが頼みを受けるポート。</summary>
    public const int VTubeStudioPort = 21412;

    /// <summary>VTube Studio の形式で、こちらが受け取るポート（頼みに書く）。</summary>
    public const int VTubeStudioReplyPort = 21413;

    /// <summary>iFacialMocap の形式で、こちらが受け取るポート。</summary>
    public const int IFacialMocapPort = 49983;

    /// <summary>MeowFace がスマホから送るときの、初めのポート。</summary>
    public const int MeowFacePort = 12345;

    /// <summary>VTube Studio の形式の頼み（<paramref name="seconds"/> の間、<paramref name="ports"/> へ送ってもらう。0.5〜10 秒）。</summary>
    public static string VTubeStudioRequest(IEnumerable<int> ports, double seconds = 5) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["messageType"] = "iOSTrackingDataRequest",
            ["time"] = Math.Clamp(seconds, 0.5, 10),
            ["sentBy"] = "3D Theater",
            ["ports"] = ports.Take(32).ToArray(),
        });

    /// <summary>届いたものを読む（JSON なら VTube Studio の形式、そうでなければ iFacialMocap の形式）。読めなければ null。</summary>
    public static PhoneFrame? Parse(ReadOnlySpan<byte> data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data).Trim();
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return text.StartsWith('{') ? ParseVTubeStudio(text) : ParseIFacialMocap(text);
    }

    /// <summary>
    /// VTube Studio の形式（JSON）を読む。表情は BlendShapes の k と v（0〜1）、頭は Rotation（度）と Position、顔があるかは FaceFound。
    /// 名前の大文字小文字（EyeBlinkLeft / eyeBlinkLeft）は問わない。
    /// </summary>
    public static PhoneFrame? ParseVTubeStudio(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var blend = new Dictionary<string, float>(StringComparer.Ordinal);
            if (Property(root, "BlendShapes") is { ValueKind: JsonValueKind.Array } shapes)
            {
                foreach (JsonElement entry in shapes.EnumerateArray())
                {
                    if (Property(entry, "k") is { ValueKind: JsonValueKind.String } key && Property(entry, "v") is { ValueKind: JsonValueKind.Number } value)
                    {
                        Merge(blend, NormalizeName(key.GetString()!), value.GetSingle());
                    }
                }
            }
            bool found = Property(root, "FaceFound") is not { ValueKind: JsonValueKind.False };
            return new PhoneFrame(PhoneProtocol.VTubeStudio, found, Vector(root, "Rotation"), Vector(root, "Position"), blend);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// iFacialMocap の形式（文字）を読む。「名前-値」（値は 0〜100。新しい版は「名前&amp;値」）を「|」で区切り、
    /// 頭は「=head#上下,左右,傾き,位置 x,y,z」（度）、目は「rightEye#…」「leftEye#…」。名前の「_L」「_R」は Left・Right にする。
    /// </summary>
    public static PhoneFrame? ParseIFacialMocap(string text)
    {
        var blend = new Dictionary<string, float>(StringComparer.Ordinal);
        Vector3 rotation = default;
        Vector3 position = default;
        bool any = false;
        foreach (string raw in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string entry = raw.TrimStart('=');
            int hash = entry.IndexOf('#');
            if (hash > 0)
            {
                string key = entry[..hash];
                float[] values = Numbers(entry[(hash + 1)..]);
                if (key.Equals("head", StringComparison.OrdinalIgnoreCase) && values.Length >= 3)
                {
                    rotation = new Vector3(values[0], values[1], values[2]);
                    if (values.Length >= 6)
                    {
                        position = new Vector3(values[3], values[4], values[5]);
                    }
                    any = true;
                }
                continue; // 目の向きは表情の値（eyeLook…）で使うので読まない
            }
            int separator = entry.IndexOfAny(['-', '&']);
            if (separator <= 0)
            {
                continue;
            }
            if (float.TryParse(entry[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
            {
                Merge(blend, NormalizeName(entry[..separator]), percent / 100f);
                any = true;
            }
        }
        return any ? new PhoneFrame(PhoneProtocol.IFacialMocap, blend.Count > 0, rotation, position, blend) : null;
    }

    /// <summary>
    /// 表情の名前を ARKit の名前にそろえる（EyeBlinkLeft → eyeBlinkLeft、eyeBlink_L → eyeBlinkLeft）。
    /// MeowFace の左右に分かれた browInnerUpLeft・browInnerUpRight は browInnerUp にまとめる（大きいほう）。
    /// </summary>
    public static string NormalizeName(string name)
    {
        string n = name.Trim();
        if (n.EndsWith("_L", StringComparison.Ordinal))
        {
            n = n[..^2] + "Left";
        }
        else if (n.EndsWith("_R", StringComparison.Ordinal))
        {
            n = n[..^2] + "Right";
        }
        if (n.Length > 0 && char.IsUpper(n[0]))
        {
            n = char.ToLowerInvariant(n[0]) + n[1..];
        }
        return n is "browInnerUpLeft" or "browInnerUpRight" ? "browInnerUp" : n;
    }

    private static void Merge(Dictionary<string, float> blend, string name, float value)
    {
        if (!float.IsFinite(value))
        {
            return;
        }
        value = Math.Clamp(value, 0f, 1f);
        blend[name] = blend.TryGetValue(name, out float old) ? Math.Max(old, value) : value;
    }

    private static float[] Numbers(string text)
    {
        var values = new List<float>(6);
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            values.Add(float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : 0f);
        }
        return [.. values];
    }

    // 名前の大文字小文字を問わずに探す
    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }

    private static Vector3 Vector(JsonElement root, string name)
    {
        if (Property(root, name) is not { ValueKind: JsonValueKind.Object } v)
        {
            return default;
        }
        float Axis(string axis) => Property(v, axis) is { ValueKind: JsonValueKind.Number } n && float.IsFinite(n.GetSingle()) ? n.GetSingle() : 0f;
        return new Vector3(Axis("x"), Axis("y"), Axis("z"));
    }
}

/// <summary>スマホの頭の向き・位置を、カメラで読んだときと同じ決まりにしたもの（<see cref="HeadPose"/> と同じ向き）。</summary>
public readonly record struct PhonePose(float Yaw, float Pitch, float Roll, Vector2 Center, float Size);

/// <summary>
/// スマホの頭の向きを、カメラで読んだときの決まり（左右は画像の右を向くと正、上下は上を向くと正、傾きは画像の上で時計回りが正）にする。
/// 向きの決まりはアプリが公開していない。
/// - VTube Studio: 利用者の iPhone で確かめた（自分の右を向くと x が正、上を向くと y が負、自分の右へ傾けると z が負）。
///   左右 = −x、上下 = −y、傾き = z。x・y の並びは Live2D の「角度 X（左右）・角度 Y（上下）」と同じ。
///   位置は、公式の見本の使い方からセンチメートルとみなし、奥行きは使わない（外れても体が大きく傾かない側にした）。
/// - iFacialMocap: ARKit の顔の決まり（x = 見ている人の右、y = 上、z = 見ている人の方）から決めた（実物ではまだ確かめていない）。
///   左右 = y、上下 = −x、傾き = −z。位置は奥行き（z）の大きさから単位を見分け、顔の幅 15 cm として絵の上の動きに直す。
/// 逆なら、利用者がパネルで逆にする（<paramref name="invertYaw"/> など）。
/// </summary>
public static class PhonePoseMapping
{
    /// <summary>顔の幅（メートル）。</summary>
    public const float FaceWidth = 0.15f;

    /// <summary>顔の幅（センチメートル。VTube Studio の位置の単位とみなす）。</summary>
    public const float FaceWidthCentimeters = 15f;

    public static PhonePose ToCameraConvention(PhoneFrame frame, bool invertYaw, bool invertPitch, bool invertRoll)
    {
        float yawSign = invertYaw ? -1 : 1;
        float pitchSign = invertPitch ? -1 : 1;
        float rollSign = invertRoll ? -1 : 1;
        if (frame.Protocol == PhoneProtocol.Vmc)
        {
            // すでにカメラの決まり。位置（真ん中と大きさ）は、送り手の絵の画素のまま
            Vector3 angles = frame.Rotation;
            Vector3 place = frame.Position;
            return place.Z > 0
                ? new PhonePose(angles.X * yawSign, angles.Y * pitchSign, angles.Z * rollSign, new Vector2(place.X, place.Y), place.Z)
                : new PhonePose(angles.X * yawSign, angles.Y * pitchSign, angles.Z * rollSign, Vector2.Zero, 1f);
        }
        if (frame.Protocol == PhoneProtocol.VTubeStudio)
        {
            float yaw = -frame.Rotation.X * yawSign;
            float pitch = -frame.Rotation.Y * pitchSign;
            float roll = frame.Rotation.Z * rollSign;
            if (frame.Position.X == 0 && frame.Position.Y == 0)
            {
                return new PhonePose(yaw, pitch, roll, Vector2.Zero, 1f); // 位置が無い: 体はずらさない
            }
            // 自分の右へ動くと x が正とみなす（向きと同じ）。絵の上では左へ。上へ動くと y が正で、絵の上（y は下が正）へ
            return new PhonePose(yaw, pitch, roll, new Vector2(-frame.Position.X * yawSign, -frame.Position.Y), FaceWidthCentimeters);
        }

        float y = frame.Rotation.Y * yawSign;
        float p = -frame.Rotation.X * pitchSign;
        float r = -frame.Rotation.Z * rollSign;
        float distance = MathF.Abs(frame.Position.Z);
        if (distance < 0.05f || !float.IsFinite(distance))
        {
            return new PhonePose(y, p, r, Vector2.Zero, 1f); // 位置が分からない: 体はずらさない
        }
        // 奥行きの大きさから単位を見分ける（顔までは 0.2〜1 m ほど: 3 未満ならメートル、300 未満ならセンチ、それより大きければミリ）
        float unit = distance < 3 ? 1 : distance < 300 ? 100 : 1000;
        var center = new Vector2(frame.Position.X / distance * yawSign, -frame.Position.Y / distance);
        return new PhonePose(y, p, r, center, FaceWidth * unit / distance);
    }
}

/// <summary>
/// スマホから UDP で受け取る。<paramref name="listenPorts"/> のそれぞれで待ち受け、届いたものを読んで <see cref="FrameReceived"/> で知らせる
/// （受け取り用のスレッドから）。<paramref name="requestTarget"/> があれば（VTube Studio の形式）、1 秒ごとに「5 秒送って」と頼み続ける
/// （頼みは最初のポートから送り、返事もそのポートで受ける）。
/// </summary>
public sealed class PhoneReceiver : IDisposable
{
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<int> _ports;
    private readonly IPAddress _bindAddress;
    private readonly IPEndPoint? _requestTarget;
    private readonly List<UdpClient> _clients = [];
    private readonly List<Thread> _threads = [];
    private readonly ManualResetEventSlim _stop = new(false);
    private long _packets;
    private long _frames;
    private bool _started;
    private bool _disposed;

    public PhoneReceiver(IReadOnlyList<int> listenPorts, IPAddress bindAddress, IPEndPoint? requestTarget = null)
    {
        if (listenPorts.Count == 0)
        {
            throw new ArgumentException("待ち受けるポートがありません", nameof(listenPorts));
        }
        _ports = listenPorts;
        _bindAddress = bindAddress;
        _requestTarget = requestTarget;
    }

    /// <summary>読めたものが届いた（受け取り用のスレッドから。送り手の場所も）。</summary>
    public event Action<PhoneFrame, IPEndPoint>? FrameReceived;

    /// <summary>届いた数（読めなかったものも含む）。</summary>
    public long Packets => Interlocked.Read(ref _packets);

    /// <summary>読めた数。</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>待ち受けを始める。ポートがほかで使われていれば <see cref="SocketException"/>。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }
        _started = true;
        try
        {
            foreach (int port in _ports)
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                client.Client.Bind(new IPEndPoint(_bindAddress, port));
                _clients.Add(client);
            }
        }
        catch
        {
            CloseClients();
            throw;
        }
        foreach (UdpClient client in _clients)
        {
            var thread = new Thread(() => Receive(client)) { IsBackground = true, Name = "スマホから受け取る" };
            _threads.Add(thread);
            thread.Start();
        }
        if (_requestTarget is not null)
        {
            var thread = new Thread(Request) { IsBackground = true, Name = "スマホに頼む" };
            _threads.Add(thread);
            thread.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop.Set();
        CloseClients(); // 待っている受け取りをほどく
        foreach (Thread thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _stop.Dispose();
    }

    private void CloseClients()
    {
        foreach (UdpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private void Receive(UdpClient client)
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsSet)
        {
            byte[] data;
            try
            {
                data = client.Receive(ref any);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // 送った頼みが届かなかった（スマホのアプリが止まっている）知らせ。待ち続ける
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _packets);
            if (PhoneMessages.Parse(data) is { } frame)
            {
                Interlocked.Increment(ref _frames);
                FrameReceived?.Invoke(frame, new IPEndPoint(any.Address, any.Port));
            }
        }
    }

    private void Request()
    {
        byte[] request = Encoding.UTF8.GetBytes(PhoneMessages.VTubeStudioRequest([_ports[0]]));
        UdpClient client = _clients[0];
        do
        {
            try
            {
                client.Send(request, request.Length, _requestTarget);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // 送れなくても（ネットワークが無いなど）、次の回にまた試す
            }
        }
        while (!_stop.Wait(RequestInterval));
    }
}
