using System.Numerics;

namespace Theater3D.Tracking.Vmc;

/// <summary>
/// 3D Theater VCamera が 1 枚ごとに送る、顔のトラッキングの結果（設計判断記録 98 章）。
/// 向き（度）・真ん中（画素）・大きさ（画素）は、本アプリのカメラのトラッキングと同じ決まり（<see cref="HeadPose"/>）のまま。
/// <see cref="Head"/> は、ほかのアプリ向けの頭のボーンの回転（正面に合わせた後。Unity の座標と決まり）。
/// </summary>
public sealed record VmcFaceFrame(
    int Sequence,
    bool Found,
    float Yaw,
    float Pitch,
    float Roll,
    Vector2 Center,
    float Size,
    IReadOnlyDictionary<string, float> Blendshapes,
    Quaternion Head,
    float ProcessMilliseconds,
    float Time)
{
    /// <summary>手を読んでいるか（読んでいなければ、手の知らせを送らない。受け手は指を動かさない）。</summary>
    public bool HandsTracked { get; init; }

    /// <summary>見つけた手（本人の左右。0〜2 つ。設計判断記録 99 章）。</summary>
    public IReadOnlyList<HandPose> Hands { get; init; } = [];
}

/// <summary>
/// VMC プロトコル（OSC の上の、VTuber 向けのアプリの間の決まり。https://protocol.vmc.info/）の、3D Theater で使う所（設計判断記録 98 章）。
/// <list type="bullet">
/// <item>送り手（VCamera）は、ボーン（Head）の回転と表情（ARKit の 52 個の名前・VRM の基本の表情）を送る。ほかのアプリも受け取れる。</item>
/// <item>3D Theater どうしでは、カメラのトラッキングと同じ決まりの値（<see cref="Face"/>）と、1 枚の番号（<see cref="Frame"/>）も足す。
/// 受け手（本アプリ）は番号を返し（<see cref="Ack"/>）、送り手が往復の時間を測る。ほかのアプリは、知らない宛名を読み捨てる。</item>
/// </list>
/// ボーンの向きは Unity の決まり（x 右・y 上・z 前の左手の座標。キャラクターは +z を向く。オイラー角は z → x → y の順に回す）。
/// </summary>
public static class VmcProtocol
{
    /// <summary>受け手（Marionette）が待ち受ける、初めのポート。</summary>
    public const int MarionettePort = 39539;

    public const string Ok = "/VMC/Ext/OK";
    public const string Time = "/VMC/Ext/T";
    public const string RootPos = "/VMC/Ext/Root/Pos";
    public const string BonePos = "/VMC/Ext/Bone/Pos";
    public const string BlendVal = "/VMC/Ext/Blend/Val";
    public const string BlendApply = "/VMC/Ext/Blend/Apply";

    /// <summary>3D Theater の足し: 顔（i 顔あり・f 左右・f 上下・f 傾き・f 真ん中 x・f 真ん中 y・f 大きさ）。カメラのトラッキングと同じ決まり。</summary>
    public const string Face = "/3DTheater/Face";

    /// <summary>
    /// 3D Theater の足し: 手（i 左右 0 = 本人の左手・1 = 本人の右手・i 手あり・f × 20 指の曲がりと開き（度。<see cref="HandPose"/> の並び））。
    /// 手を読んでいる間は、見つからない手も「手なし」として毎枚送る（設計判断記録 99 章）。
    /// </summary>
    public const string Hand = "/3DTheater/Hand";

    /// <summary>3D Theater の足し: 1 枚の番号（i 番号・f 絵が届いてから送るまでの時間 ms）。</summary>
    public const string Frame = "/3DTheater/Frame";

    /// <summary>3D Theater の足し: 受け手が返す番号（i 番号）。送り手が往復の時間を測る。</summary>
    public const string Ack = "/3DTheater/Ack";

    /// <summary>頭のボーンの位置（親から。メートル）。Unity の人型の首から頭までの長さほど（受け手が位置も使うとき、頭が首にめり込まないように）。</summary>
    public static Vector3 HeadOffset { get; } = new(0f, 0.1f, 0f);

    /// <summary>
    /// カメラのトラッキングの決まりの向き（<see cref="HeadPose"/>。左右は画像の右を向くと正、上下は上を向くと正、傾きは画像の上で時計回りが正）を、
    /// 頭のボーンの回転にする。キャラクターは本人と同じ向きに動く（本人が自分の右を向けば、キャラクターも自分の右を向く。鏡には映さない）。
    /// Unity のオイラー角では x = -上下（正で下を向く）・y = -左右（正でキャラクターの右へ）・z = 傾き（正でキャラクターの左へ傾ける）。
    /// </summary>
    public static Quaternion HeadRotation(float yaw, float pitch, float roll) => FromUnityEuler(-pitch, -yaw, roll);

    /// <summary><see cref="HeadRotation"/> の逆（頭のボーンの回転から、カメラのトラッキングの決まりの向き。度）。</summary>
    public static (float Yaw, float Pitch, float Roll) CameraAngles(Quaternion rotation)
    {
        Vector3 euler = ToUnityEuler(rotation);
        return (-euler.Y, -euler.X, euler.Z);
    }

    /// <summary>Unity のオイラー角（度。z → x → y の順に回す）から回転。</summary>
    public static Quaternion FromUnityEuler(float x, float y, float z)
    {
        const float ToRadians = MathF.PI / 180f;
        Quaternion qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x * ToRadians);
        Quaternion qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, y * ToRadians);
        Quaternion qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z * ToRadians);
        return Quaternion.Normalize(qy * qx * qz);
    }

    /// <summary>回転から Unity のオイラー角（度。z → x → y の順。x は -90〜90）。</summary>
    public static Vector3 ToUnityEuler(Quaternion rotation)
    {
        const float ToDegrees = 180f / MathF.PI;
        Quaternion q = Quaternion.Normalize(rotation);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, q);
        Vector3 right = Vector3.Transform(Vector3.UnitX, q);
        Vector3 up = Vector3.Transform(Vector3.UnitY, q);
        float x = MathF.Asin(Math.Clamp(-forward.Y, -1f, 1f));
        float y = MathF.Atan2(forward.X, forward.Z);
        float z = MathF.Atan2(right.Y, up.Y);
        return new Vector3(x * ToDegrees, y * ToDegrees, z * ToDegrees);
    }

    /// <summary>
    /// 表情の名前を、パーフェクトシンクの書き方（ARKit の名前の頭を大きな字。eyeBlinkLeft → EyeBlinkLeft）にする。
    /// VSeeFace などのパーフェクトシンクのモデルの表情の名前と同じ。
    /// </summary>
    public static string PerfectSyncName(string name) =>
        name.Length > 0 && char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;

    /// <summary>
    /// 1 枚分の知らせ（バンドルに入れる順）。状態・時刻・根元・頭のボーン・表情（パーフェクトシンクの名前と、VRM の基本の表情）・
    /// 表情を当てる合図と、3D Theater の足し（顔・手・番号）。顔が無いときは、ボーンと表情を送らない（受け手は前の形のまま）。
    /// </summary>
    public static IReadOnlyList<OscMessage> FrameMessages(VmcFaceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var messages = new List<OscMessage>(frame.Blendshapes.Count + 16)
        {
            new(Ok, 1),
            new(Time, frame.Time),
        };
        if (frame.Found)
        {
            messages.Add(new OscMessage(RootPos, "root", 0f, 0f, 0f, 0f, 0f, 0f, 1f));
            Quaternion h = Quaternion.Normalize(frame.Head);
            messages.Add(new OscMessage(BonePos, "Head", HeadOffset.X, HeadOffset.Y, HeadOffset.Z, h.X, h.Y, h.Z, h.W));
            foreach (var (name, value) in frame.Blendshapes)
            {
                if (name.StartsWith('_') || !float.IsFinite(value))
                {
                    continue; // MediaPipe の「_neutral」は送らない
                }
                messages.Add(new OscMessage(BlendVal, PerfectSyncName(name), Math.Clamp(value, 0f, 1f)));
            }
            foreach (var (name, value) in VrmPresets(frame.Blendshapes))
            {
                messages.Add(new OscMessage(BlendVal, name, value));
            }
            messages.Add(new OscMessage(BlendApply));
        }
        messages.Add(new OscMessage(Face, frame.Found ? 1 : 0, frame.Yaw, frame.Pitch, frame.Roll, frame.Center.X, frame.Center.Y, frame.Size));
        if (frame.HandsTracked)
        {
            messages.Add(HandMessage(frame.Hands.FirstOrDefault(h => !h.IsRight), isRight: false));
            messages.Add(HandMessage(frame.Hands.FirstOrDefault(h => h.IsRight), isRight: true));
        }
        messages.Add(new OscMessage(Frame, frame.Sequence, frame.ProcessMilliseconds));
        return messages;
    }

    /// <summary>手 1 つの知らせ（<paramref name="hand"/> が null なら「手なし」）。</summary>
    public static OscMessage HandMessage(HandPose? hand, bool isRight)
    {
        var arguments = new object[2 + HandPose.ValueCount];
        arguments[0] = isRight ? 1 : 0;
        arguments[1] = hand is null ? 0 : 1;
        for (int i = 0; i < HandPose.ValueCount; i++)
        {
            float value = hand is not null && i < hand.Angles.Length ? hand.Angles[i] : 0f;
            arguments[2 + i] = float.IsFinite(value) ? value : 0f;
        }
        return new OscMessage(Hand, arguments);
    }

    /// <summary>
    /// パーフェクトシンクの無いモデル向けの、VRM の基本の表情（まばたき・あ・う・お）。ARKit の値から作る（左右は本人から見た左右）。
    /// </summary>
    public static IEnumerable<(string Name, float Value)> VrmPresets(IReadOnlyDictionary<string, float> arkit)
    {
        ArgumentNullException.ThrowIfNull(arkit);
        float V(string name) => arkit.TryGetValue(name, out float v) && float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
        float left = V("eyeBlinkLeft");
        float right = V("eyeBlinkRight");
        yield return ("Blink_L", left);
        yield return ("Blink_R", right);
        yield return ("A", V("jawOpen"));
        yield return ("U", V("mouthPucker"));
        yield return ("O", V("mouthFunnel"));
    }
}
