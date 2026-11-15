using System.Globalization;
using System.Numerics;
using System.Text;

namespace Theater3D.Editor;

/// <summary>
/// 顔のトラッキングの 1 枚分（カメラの絵の上の値）。頭の向きは度（左右は画像の右を向くと正・上下は上を向くと正・
/// 傾きは画像の上で時計回りが正）。顔の真ん中と大きさは画像の画素。表情の値は MediaPipe の名前（Left・Right は本人から見た左右）。
/// </summary>
public sealed record FaceSignal(float Yaw, float Pitch, float Roll, Vector2 FaceCenter, float FaceSize, IReadOnlyDictionary<string, float> Blendshapes)
{
    public float Value(string name) => Blendshapes.TryGetValue(name, out float v) ? v : 0f;

    /// <summary>
    /// まばたきの値を 0〜1 に広げる幅（正面のときより、どれだけ閉じたら閉じきりとみなすか）。カメラ（MediaPipe）は閉じても 0.6〜0.8 にしか
    /// 届かないので狭く、スマホ（ARKit）は 1 近くまで届くので広くする。
    /// </summary>
    public float BlinkRange { get; init; } = FaceRetarget.CameraBlinkRange;
}

/// <summary>キャラクターへの当てはめ方（ライブのファイルに保存する）。</summary>
public sealed class FaceRetargetSettings
{
    private bool _mirror;
    private bool _gaze = true;
    private bool _upperBody = true;
    private bool _bodySway = true;
    private bool _perfectSync = true;
    private float _smoothing = 0.5f;
    private Guid? _target;

    public event EventHandler? Changed;

    /// <summary>
    /// 鏡のように動かす（あなたが右目を閉じると、画面で同じ側のキャラクターの目＝キャラクターの左目が閉じる）。
    /// 既定は false で、映像と同じ向きに動かす（あなたの右目 → キャラクターの右目。右を向くと、キャラクターも自分の右へ）。
    /// </summary>
    public bool Mirror { get => _mirror; set => Set(ref _mirror, value); }

    /// <summary>目線（両目のボーン）を動かす。</summary>
    public bool Gaze { get => _gaze; set => Set(ref _gaze, value); }

    /// <summary>首の動きの一部を上半身にも分ける。</summary>
    public bool UpperBody { get => _upperBody; set => Set(ref _upperBody, value); }

    /// <summary>画面の中で顔が動いた分だけ、上半身を傾ける（左右・前後）。</summary>
    public bool BodySway { get => _bodySway; set => Set(ref _bodySway, value); }

    /// <summary>
    /// パーフェクトシンク: キャラクターが iPhone の顔の動きと同じ名前の表情の元（eyeBlinkLeft・jawOpen など 52 個）を持っていれば、
    /// トラッキングの値をそのまま動かす（まばたき・あいうえお などの MMD の表情は動かさない。設計判断記録 84 章）。
    /// </summary>
    public bool PerfectSync { get => _perfectSync; set => Set(ref _perfectSync, value); }

    /// <summary>なめらかさ（0 = すばやいがぶれる、1 = なめらかだが遅れる）。</summary>
    public float Smoothing { get => _smoothing; set => Set(ref _smoothing, Math.Clamp(float.IsFinite(value) ? value : 0.5f, 0f, 1f)); }

    /// <summary>トラッキングを当てはめるモデル（シーンの項目の Id。null なら頭のボーンを持つ最初のモデル）。</summary>
    public Guid? TargetModelId { get => _target; set => Set(ref _target, value); }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _mirror = false;
        _gaze = true;
        _upperBody = true;
        _bodySway = true;
        _perfectSync = true;
        _smoothing = 0.5f;
        _target = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// スマホから受け取るときの設定（ライブのファイルに保存する）。VTube Studio の形式で頼むスマホの IP と、頭の向きを逆にするか
/// （アプリが向きの決まりを公開していないので、逆なら利用者が直せるようにする）。
/// </summary>
public sealed class PhoneTrackingSettings
{
    private string _address = string.Empty;
    private bool _invertYaw;
    private bool _invertPitch;
    private bool _invertRoll;

    public event EventHandler? Changed;

    /// <summary>スマホの IP（VTube Studio・MeowFace に頼むとき）。</summary>
    public string Address { get => _address; set => Set(ref _address, (value ?? string.Empty).Trim()); }

    public bool InvertYaw { get => _invertYaw; set => Set(ref _invertYaw, value); }

    public bool InvertPitch { get => _invertPitch; set => Set(ref _invertPitch, value); }

    public bool InvertRoll { get => _invertRoll; set => Set(ref _invertRoll, value); }

    public void Reset()
    {
        _address = string.Empty;
        _invertYaw = _invertPitch = _invertRoll = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// 当てはめの結果。回転はボーンの初期の向きに掛ける分（モデルの座標。モデルは -Z を向く）。モーフは決まった名前ごとの重み 0〜1。
/// <see cref="PerfectSync"/> は、パーフェクトシンクのときの、iPhone の顔の動きの名前ごとの重み 0〜1（Left・Right はキャラクターの左右）。
/// </summary>
public sealed record FaceOutput(Quaternion UpperBody, Quaternion Neck, Quaternion Head, Quaternion Eyes, IReadOnlyDictionary<string, float> Morphs)
{
    public static FaceOutput Neutral { get; } = new(Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, Quaternion.Identity,
        FaceRetarget.MorphKeys.ToDictionary(k => k, _ => 0f));

    public IReadOnlyDictionary<string, float> PerfectSync { get; init; } = new Dictionary<string, float>();
}

/// <summary>
/// 顔のトラッキングを、MMD のキャラクターのボーン（上半身・首・頭・両目）と表情のモーフに当てはめる計算。
/// - 正面合わせ: <see cref="Calibrate"/> で覚えた値（正面を向いた顔）からの違いを使う（頭の向き・顔の位置・表情の値）
/// - 左右: 既定は映像と同じ向き（本人の右 → キャラクターの右）。鏡のように動かすときは、画面の上で同じ側が動く（首を右へ向けると画面でも右へ）
/// - なめらかさ: 値ごとに One Euro フィルター（速く動くときは遅れを減らし、止まっているときはぶれを抑える）
/// 画面の右はモデルの +X（モデルの左手の側）。モデルは -Z（カメラの方）を向く。
/// </summary>
public sealed class FaceRetarget
{
    // モーフの決まった名前（モデルのモーフの名前は FaceRig で探す）
    public const string Blink = "まばたき";
    public const string WinkLeft = "ウィンク（モデルの左目）";
    public const string WinkRight = "ウィンク（モデルの右目）";
    public const string A = "あ";
    public const string I = "い";
    public const string U = "う";
    public const string E = "え";
    public const string O = "お";
    public const string Smile = "口角";
    public const string BrowUp = "眉を上げる";
    public const string BrowAngry = "眉を寄せる";

    public static IReadOnlyList<string> MorphKeys { get; } = [Blink, WinkLeft, WinkRight, A, I, U, E, O, Smile, BrowUp, BrowAngry];

    /// <summary>
    /// パーフェクトシンクの表情の元の名前（iPhone の顔の動き・ARKit の 52 個。Left・Right は本人から見た左右）。カメラ（MediaPipe）は
    /// tongueOut を出さないので、その表情の元は動かない（スマホは出す）。
    /// </summary>
    public static IReadOnlyList<string> PerfectSyncNames { get; } =
    [
        "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight", "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
        "eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight",
        "eyeLookUpLeft", "eyeLookUpRight", "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight", "jawForward", "jawLeft", "jawOpen",
        "jawRight", "mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight", "mouthFunnel", "mouthLeft",
        "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight", "mouthPucker", "mouthRight", "mouthRollLower",
        "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper", "mouthSmileLeft", "mouthSmileRight", "mouthStretchLeft", "mouthStretchRight",
        "mouthUpperUpLeft", "mouthUpperUpRight", "noseSneerLeft", "noseSneerRight", "tongueOut",
    ];

    /// <summary>首の回転の分け方（上半身を使うとき・使わないとき）。</summary>
    public const float UpperBodyShare = 0.15f;
    public const float NeckShare = 0.35f;

    public const float MaxHeadDegrees = 60f;
    public const float GazeYawDegrees = 22f;
    public const float GazePitchDegrees = 14f;
    public const float MaxSwayDegrees = 12f;

    /// <summary>左右の目の閉じ具合の差が、これより小さければ両目をそろえる（片目だけ少し細めても、ウィンクにしない）。</summary>
    public const float WinkDeadZone = 0.25f;

    /// <summary>頭を横や上下へ向けたとき、この角度から <see cref="EyesLinkedDegrees"/> までに、両目を少しずつそろえていく。</summary>
    public const float EyesFreeDegrees = 10f;

    /// <summary>
    /// 頭をこの角度より横や上下へ向けたら、両目は必ずそろえる（MediaPipe は、横を向くと奥の目を、大きく上や下を向くと片目を、
    /// 閉じ気味に読むことがあるため）。
    /// </summary>
    public const float EyesLinkedDegrees = 30f;

    private readonly Dictionary<string, OneEuroFilter> _filters = [];
    private FaceSignal? _neutral;

    public FaceRetargetSettings Settings { get; }

    public FaceRetarget(FaceRetargetSettings settings) => Settings = settings;

    /// <summary>正面合わせをしたか。</summary>
    public bool IsCalibrated => _neutral is not null;

    /// <summary>今の顔を「正面」として覚える（何枚かの平均を渡す）。</summary>
    public void Calibrate(FaceSignal neutral)
    {
        _neutral = neutral;
        _filters.Clear();
    }

    /// <summary>何枚かの顔の平均（正面合わせに使う）。</summary>
    public static FaceSignal Average(IReadOnlyList<FaceSignal> signals)
    {
        if (signals.Count == 0)
        {
            throw new ArgumentException("顔がありません", nameof(signals));
        }
        float n = signals.Count;
        var blend = new Dictionary<string, float>();
        foreach (string key in signals.SelectMany(s => s.Blendshapes.Keys).Distinct())
        {
            blend[key] = signals.Sum(s => s.Value(key)) / n;
        }
        return new FaceSignal(signals.Sum(s => s.Yaw) / n, signals.Sum(s => s.Pitch) / n, signals.Sum(s => s.Roll) / n,
            signals.Aggregate(Vector2.Zero, (a, s) => a + s.FaceCenter) / n, signals.Sum(s => s.FaceSize) / n, blend) { BlinkRange = signals[^1].BlinkRange };
    }

    /// <summary>
    /// 1 枚分を当てはめる（<paramref name="time"/> は秒。なめらかにするのに使う）。<paramref name="signal"/> が null（顔を見失った）なら、
    /// 正面に向かってなめらかに戻す。<paramref name="perfectSync"/> なら、パーフェクトシンクの値（<see cref="FaceOutput.PerfectSync"/>）も求める。
    /// </summary>
    public FaceOutput Compute(FaceSignal? signal, double time, bool perfectSync = false)
    {
        FaceSignal neutral = _neutral ?? new FaceSignal(0, 0, 0, signal?.FaceCenter ?? Vector2.Zero, signal?.FaceSize ?? 1, new Dictionary<string, float>());
        FaceSignal s = signal ?? neutral;
        bool mirror = Settings.Mirror;
        float smooth = Settings.Smoothing;

        // 頭の向き（正面からの違い）。画面の上の向きにしてから、モデルの回転にする
        float yaw = Clamp(Delta(s.Yaw, neutral.Yaw), MaxHeadDegrees);
        float pitch = Clamp(s.Pitch - neutral.Pitch, MaxHeadDegrees);
        float roll = Clamp(Delta(s.Roll, neutral.Roll), MaxHeadDegrees);
        float turn = Math.Max(MathF.Abs(yaw), MathF.Abs(pitch)); // 正面からどれだけ向きを変えたか（目をそろえるのに使う）
        float screenYaw = Filter("yaw", mirror ? -yaw : yaw, time, smooth);
        float screenRoll = Filter("roll", mirror ? -roll : roll, time, smooth);
        pitch = Filter("pitch", pitch, time, smooth);
        Quaternion head = ModelRotation(screenYaw, pitch, screenRoll);

        float bodyShare = Settings.UpperBody ? UpperBodyShare : 0f;
        Quaternion upperBody = Quaternion.Slerp(Quaternion.Identity, head, bodyShare);
        Quaternion neck = Quaternion.Slerp(Quaternion.Identity, head, NeckShare);
        Quaternion headOnly = Quaternion.Slerp(Quaternion.Identity, head, 1f - bodyShare - NeckShare);

        // 顔の位置で上半身をずらす（左右に動いた分は傾け、近づいた分は前へ傾ける）
        if (Settings.BodySway && neutral.FaceSize > 1e-3f)
        {
            float dx = (s.FaceCenter.X - neutral.FaceCenter.X) / neutral.FaceSize;
            float screenDx = mirror ? -dx : dx; // 画面の右へ動いたら正
            float closer = (s.FaceSize / neutral.FaceSize) - 1f;
            float sway = Filter("sway", Clamp(screenDx * 25f, MaxSwayDegrees), time, smooth);
            float lean = Filter("lean", Clamp(-closer * 30f, MaxSwayDegrees * 0.7f), time, smooth);
            upperBody = Quaternion.Normalize(upperBody * ModelRotation(0, lean, sway));
        }

        // 目線（本人から見て右を見ると正）
        Quaternion eyes = Quaternion.Identity;
        if (Settings.Gaze)
        {
            float right = Relative(s, neutral, "eyeLookOutRight", "eyeLookInLeft") - Relative(s, neutral, "eyeLookInRight", "eyeLookOutLeft");
            float up = Relative(s, neutral, "eyeLookUpLeft", "eyeLookUpRight") - Relative(s, neutral, "eyeLookDownLeft", "eyeLookDownRight");
            float screenGaze = Filter("gazeX", Math.Clamp(mirror ? right : -right, -1f, 1f), time, Math.Max(smooth, 0.6f));
            float gazeUp = Filter("gazeY", Math.Clamp(up, -1f, 1f), time, Math.Max(smooth, 0.6f));
            eyes = ModelRotation(screenGaze * GazeYawDegrees, gazeUp * GazePitchDegrees, 0);
        }

        var morphs = new Dictionary<string, float>(MorphKeys.Count);
        // まばたき: 正面のときの開き具合から、閉じきるまでを 0〜1 に広げる（MediaPipe は閉じても 1 まで届かない）
        (float subjectLeft, float subjectRight) = LinkEyes(BlinkAmount(s.Value("eyeBlinkLeft"), neutral.Value("eyeBlinkLeft"), s.BlinkRange),
            BlinkAmount(s.Value("eyeBlinkRight"), neutral.Value("eyeBlinkRight"), s.BlinkRange), turn);
        float modelLeft = Filter("blinkL", mirror ? subjectRight : subjectLeft, time, smooth * 0.4f);
        float modelRight = Filter("blinkR", mirror ? subjectLeft : subjectRight, time, smooth * 0.4f);
        float both = Math.Min(modelLeft, modelRight);
        morphs[Blink] = both;
        morphs[WinkLeft] = Math.Max(0, modelLeft - both);
        morphs[WinkRight] = Math.Max(0, modelRight - both);

        // 口: あいうえおの近い形（口の開き・すぼめ・とがらせ・横に広げる）
        float jaw = Expression(s, neutral, "jawOpen");
        float funnel = Expression(s, neutral, "mouthFunnel");
        float pucker = Expression(s, neutral, "mouthPucker");
        float stretch = (Expression(s, neutral, "mouthStretchLeft") + Expression(s, neutral, "mouthStretchRight")) * 0.5f;
        float smile = (Expression(s, neutral, "mouthSmileLeft") + Expression(s, neutral, "mouthSmileRight")) * 0.5f;
        float mouthSmooth = smooth * 0.4f;
        morphs[A] = Filter("a", Clamp01((1.4f * jaw) - (0.6f * funnel) - (0.6f * pucker)), time, mouthSmooth);
        morphs[I] = Filter("i", Clamp01((1.6f * stretch) - (0.6f * jaw)), time, mouthSmooth);
        morphs[U] = Filter("u", Clamp01((1.2f * pucker) - (0.8f * jaw)), time, mouthSmooth);
        morphs[E] = Filter("e", Clamp01(Math.Min(jaw, stretch) * 1.5f), time, mouthSmooth);
        morphs[O] = Filter("o", Clamp01(1.4f * funnel), time, mouthSmooth);
        morphs[Smile] = Filter("smile", Clamp01(1.2f * smile), time, mouthSmooth);
        LimitVowels(morphs);

        // 眉
        morphs[BrowUp] = Filter("browUp", Clamp01(1.2f * Math.Max(Expression(s, neutral, "browInnerUp"),
            (Expression(s, neutral, "browOuterUpLeft") + Expression(s, neutral, "browOuterUpRight")) * 0.5f)), time, smooth);
        morphs[BrowAngry] = Filter("browAngry", Clamp01(1.2f * (Expression(s, neutral, "browDownLeft") + Expression(s, neutral, "browDownRight")) * 0.5f),
            time, smooth);

        var output = new FaceOutput(upperBody, neck, headOnly, eyes, morphs);
        return perfectSync ? output with { PerfectSync = ComputePerfectSync(s, neutral, modelLeft, modelRight, time) } : output;
    }

    /// <summary>
    /// パーフェクトシンクの値（設計判断記録 84 章）。表情の値を、正面のときの値から 1 までを 0〜1 にしてそのまま渡す（目を閉じるのは、
    /// まばたきと同じく広げ、左右のそろえ方もまばたきと同じ）。鏡のように動かすときは、左右の名前を入れ替える。目線を動かさないときは、
    /// 目線の表情の元（eyeLook〜）を 0 にする。
    /// </summary>
    private Dictionary<string, float> ComputePerfectSync(FaceSignal s, FaceSignal neutral, float modelLeftBlink, float modelRightBlink, double time)
    {
        bool mirror = Settings.Mirror;
        float smooth = Settings.Smoothing;
        var result = new Dictionary<string, float>(PerfectSyncNames.Count);
        foreach (string name in PerfectSyncNames)
        {
            if (name == "eyeBlinkLeft" || name == "eyeBlinkRight")
            {
                result[name] = name == "eyeBlinkLeft" ? modelLeftBlink : modelRightBlink; // まばたきと同じ値（左右はキャラクターの左右）
                continue;
            }
            if (name.StartsWith("eyeLook", StringComparison.Ordinal) && !Settings.Gaze)
            {
                result[name] = 0f;
                continue;
            }
            string source = mirror ? MirrorSide(name) : name;
            bool fast = name.StartsWith("eye", StringComparison.Ordinal) || name.StartsWith("mouth", StringComparison.Ordinal)
                || name.StartsWith("jaw", StringComparison.Ordinal) || name.StartsWith("tongue", StringComparison.Ordinal);
            result[name] = Filter("ps:" + name, Expression(s, neutral, source), time, fast ? smooth * 0.4f : smooth);
        }
        return result;
    }

    /// <summary>名前の終わりの Left と Right を入れ替える（eyeBlinkLeft → eyeBlinkRight）。</summary>
    public static string MirrorSide(string name) =>
        name.EndsWith("Left", StringComparison.Ordinal) ? name[..^4] + "Right"
        : name.EndsWith("Right", StringComparison.Ordinal) ? name[..^5] + "Left"
        : name;

    /// <summary>
    /// 画面の上の向き（度）を、モデルのボーンの回転にする。左右は画面の右を向くと正（モデルの +X の側）、上下は上を向くと正、
    /// 傾きは画面の上で時計回りが正（頭の上が画面の右へ）。
    /// </summary>
    /// <summary>
    /// 目の回転（<see cref="FaceOutput.Eyes"/>）を、目線の表情の強さにする（両目のボーンが無く、目線を表情で動かすモデル。VRM の lookAt が expression のもの。
    /// 設計判断記録 82 章の 3 段目）。左右はモデルから見て（左 = モデルの +X）。いちばん大きく動かす目線（<see cref="GazeYawDegrees"/>・
    /// <see cref="GazePitchDegrees"/>）で 1。目を回したときの、正面（-Z）の向きから求めるので、両目のボーンを回したときと同じ向きを見る。
    /// </summary>
    public static (float Left, float Right, float Up, float Down) LookWeights(Quaternion eyes)
    {
        const float ToDegrees = 180f / MathF.PI;
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, eyes);
        float yaw = MathF.Atan2(forward.X, -forward.Z) * ToDegrees;              // モデルの左（+X）が正
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)) * ToDegrees;   // 上が正
        return (Clamp01(yaw / GazeYawDegrees), Clamp01(-yaw / GazeYawDegrees), Clamp01(pitch / GazePitchDegrees), Clamp01(-pitch / GazePitchDegrees));
    }

    public static Quaternion ModelRotation(float screenYaw, float pitch, float screenRoll)
    {
        const float ToRadians = MathF.PI / 180f;
        // モデルは -Z を向く: Y まわりに正に回すと顔は -X（画面の左）へ、Z まわりに正に回すと頭の上は -X（画面の左）へ向く
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-screenYaw * ToRadians, pitch * ToRadians, -screenRoll * ToRadians));
    }

    /// <summary>まばたきの値を 0〜1 にする（正面のときより 0.05 閉じた所から、0.4 閉じた所まで）。</summary>
    public static float BlinkAmount(float value, float neutral, float range = CameraBlinkRange)
    {
        float open = Math.Clamp(neutral, 0f, 0.5f) + 0.05f;
        return Clamp01((value - open) / Math.Max(range, 0.05f));
    }

    /// <summary>
    /// あいうえおの合計を 1 までにする。いちばん強い形はそのまま残し、ほかの形を残りの分に収める（大きく開けたときは「あ」だけになる）。
    /// MMD のモデルの口の形は 1 つずつ作られているので、重ねると口が作りの範囲を超え、歯や舌が顎を突き抜けるため
    /// （スマホは、大きく開けると口を開ける・すぼめる・横に広げるが一緒に大きくなる。利用者の iPhone で見つかった）。
    /// </summary>
    public static void LimitVowels(IDictionary<string, float> morphs)
    {
        string[] vowels = [A, I, U, E, O];
        float Get(string key) => morphs.TryGetValue(key, out float value) ? value : 0f;
        string top = vowels.MaxBy(Get)!;
        float rest = vowels.Where(v => v != top).Sum(Get);
        float budget = Math.Max(0f, 1f - Get(top));
        if (rest <= budget || rest <= 0)
        {
            return;
        }
        float scale = budget / rest;
        foreach (string vowel in vowels)
        {
            if (vowel != top && morphs.ContainsKey(vowel))
            {
                morphs[vowel] *= scale;
            }
        }
    }

    /// <summary>まばたきを広げる幅（カメラ。MediaPipe は閉じても 0.6〜0.8）。</summary>
    public const float CameraBlinkRange = 0.35f;

    /// <summary>まばたきを広げる幅（スマホ。ARKit は閉じると 1 近く）。</summary>
    public const float PhoneBlinkRange = 0.6f;

    /// <summary>
    /// 左右の目の閉じ具合（0〜1）の差を整える。差が <see cref="WinkDeadZone"/> までならそろえ、それより大きい分だけ片目を閉じる。
    /// 頭を横や上下へ向けているときは（<paramref name="turnDegrees"/> は正面から向きを変えた角度）、向けた分だけ差を小さくする。
    /// </summary>
    public static (float Left, float Right) LinkEyes(float left, float right, float turnDegrees)
    {
        float mean = (left + right) * 0.5f;
        float difference = left - right;
        float kept = Math.Max(0f, MathF.Abs(difference) - WinkDeadZone) / (1f - WinkDeadZone);
        kept *= Clamp01(1f - ((MathF.Abs(turnDegrees) - EyesFreeDegrees) / (EyesLinkedDegrees - EyesFreeDegrees)));
        float half = MathF.Sign(difference) * kept * 0.5f;
        return (Clamp01(mean + half), Clamp01(mean - half));
    }

    /// <summary>表情の値を、正面のときの値から 1 までを 0〜1 にする。</summary>
    public static float Expression(FaceSignal s, FaceSignal neutral, string name)
    {
        float n = Math.Clamp(neutral.Value(name), 0f, 0.8f);
        return Clamp01((s.Value(name) - n) / (1f - n));
    }

    private static float Relative(FaceSignal s, FaceSignal neutral, string a, string b) =>
        ((s.Value(a) - neutral.Value(a)) + (s.Value(b) - neutral.Value(b))) * 0.5f;

    private float Filter(string key, float value, double time, float smoothing)
    {
        if (!_filters.TryGetValue(key, out OneEuroFilter? filter))
        {
            filter = new OneEuroFilter();
            _filters[key] = filter;
        }
        // なめらかさ 0 → 最低の切れ目 5 Hz、1 → 0.4 Hz
        filter.MinCutoff = 5f - (4.6f * Math.Clamp(smoothing, 0f, 1f));
        return filter.Filter(value, time);
    }

    private static float Delta(float value, float neutral)
    {
        float d = value - neutral;
        return d > 180 ? d - 360 : d < -180 ? d + 360 : d;
    }

    private static float Clamp(float value, float limit) => Math.Clamp(value, -limit, limit);

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
}

/// <summary>
/// One Euro フィルター（Casiez ら 2012）。値の変わる速さに合わせて、ならす強さを変える
/// （止まっているときはぶれを強く抑え、速く動くときは遅れを減らす）。
/// </summary>
public sealed class OneEuroFilter
{
    private bool _started;
    private double _lastTime;
    private float _value;
    private float _derivative;

    /// <summary>止まっているときの切れ目の周波数（Hz。小さいほどなめらか）。</summary>
    public float MinCutoff { get; set; } = 1.5f;

    /// <summary>速さで切れ目を上げる強さ。</summary>
    public float Beta { get; set; } = 0.3f;

    public float DerivativeCutoff { get; set; } = 1f;

    public float Filter(float value, double time)
    {
        if (!_started || time <= _lastTime)
        {
            if (!_started || time < _lastTime)
            {
                _started = true;
                _value = value;
                _derivative = 0;
            }
            _lastTime = time;
            return _value;
        }
        float dt = (float)(time - _lastTime);
        _lastTime = time;
        float derivative = (value - _value) / dt;
        _derivative += Alpha(DerivativeCutoff, dt) * (derivative - _derivative);
        float cutoff = MinCutoff + (Beta * MathF.Abs(_derivative));
        _value += Alpha(cutoff, dt) * (value - _value);
        return _value;
    }

    private static float Alpha(float cutoff, float dt)
    {
        float tau = 1f / (2f * MathF.PI * cutoff);
        return 1f / (1f + (tau / dt));
    }
}

/// <summary>
/// キャラクターのボーンとモーフの名前から、当てはめ先を探す（全角・半角・大文字小文字をそろえて比べる）。
/// </summary>
public static class FaceRigNames
{
    public static IReadOnlyList<string> UpperBody { get; } = ["上半身"];
    public static IReadOnlyList<string> Neck { get; } = ["首"];
    public static IReadOnlyList<string> Head { get; } = ["頭"];
    public static IReadOnlyList<string> Eyes { get; } = ["両目"];

    /// <summary>目線を表情で動かすモデルの、目線の表情（VRM の lookUp など。モデルから見た向き）。両目のボーンが無いときだけ使う。</summary>
    public static IReadOnlyList<string> LookUp { get; } = ["視線上"];

    public static IReadOnlyList<string> LookDown { get; } = ["視線下"];

    public static IReadOnlyList<string> LookLeft { get; } = ["視線左"];

    public static IReadOnlyList<string> LookRight { get; } = ["視線右"];

    /// <summary>決まった名前ごとの、モデルのモーフの名前の候補（先にあるものを使う）。ウィンクの左右はモーフの頂点の位置で決める。</summary>
    public static IReadOnlyDictionary<string, string[]> Morphs { get; } = new Dictionary<string, string[]>
    {
        [FaceRetarget.Blink] = ["まばたき"],
        [FaceRetarget.A] = ["あ"],
        [FaceRetarget.I] = ["い"],
        [FaceRetarget.U] = ["う"],
        [FaceRetarget.E] = ["え"],
        [FaceRetarget.O] = ["お"],
        [FaceRetarget.Smile] = ["口角上げ", "にっこり", "にやり"],
        [FaceRetarget.BrowUp] = ["上"],
        [FaceRetarget.BrowAngry] = ["怒り"],
    };

    /// <summary>片目を閉じるモーフの候補（どちらの目かは頂点の位置で決める。先にあるものを使う。ｳｨﾝｸ２右 なども同じ名前とみなす）。</summary>
    public static IReadOnlyList<string> Winks { get; } = ["ウィンク", "ウィンク右", "ウィンク左", "ウィンク2", "ウィンク2右", "ウィンク2左"];

    /// <summary>モデルがパーフェクトシンクに対応しているとみなす、表情の元の数（52 個のうち）。少ししか無いモデルは、ふつうの当てはめ方にする。</summary>
    public const int PerfectSyncMinimum = 40;

    /// <summary>
    /// パーフェクトシンクの表情の元（<see cref="FaceRetarget.PerfectSyncNames"/>）→ モデルのモーフの番号。名前は、最後の「.」より前
    /// （Maya の「blendShape1.」・VRoid の「Face_Blendshape.」）を除き、大文字小文字・記号を問わずに比べる。
    /// 終わりの「_L」「_R」も Left・Right とみなす（eyeBlink_L）。同じ名前が幾つかあれば、先のものを使う。
    /// </summary>
    public static Dictionary<string, int> FindPerfectSync(IReadOnlyList<string?> morphNames)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in FaceRetarget.PerfectSyncNames)
        {
            string key = PerfectSyncKey(name);
            wanted[key] = name;
            if (key.EndsWith("left", StringComparison.Ordinal))
            {
                wanted[key[..^4] + "l"] = name;
            }
            else if (key.EndsWith("right", StringComparison.Ordinal))
            {
                wanted[key[..^5] + "r"] = name;
            }
        }
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < morphNames.Count; i++)
        {
            if (morphNames[i] is { } morph && wanted.TryGetValue(PerfectSyncKey(morph), out string? name))
            {
                result.TryAdd(name, i);
            }
        }
        return result;
    }

    private static string PerfectSyncKey(string name)
    {
        int dot = name.LastIndexOf('.');
        string tail = dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        var key = new StringBuilder(tail.Length);
        foreach (char c in tail.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                key.Append(char.ToLowerInvariant(c));
            }
        }
        return key.ToString();
    }

    /// <summary>名前を比べる形にそろえる（ｳｨﾝｸ → ウィンク）。</summary>
    public static string Normalize(string name) => name.Normalize(NormalizationForm.FormKC).Trim().ToLower(CultureInfo.InvariantCulture);

    /// <summary><paramref name="names"/>（番号順）から、候補のうち先に見つかったものの番号（無ければ -1）。</summary>
    public static int Find(IReadOnlyList<string> names, IEnumerable<string> candidates)
    {
        string[] normalized = [.. names.Select(Normalize)];
        foreach (string candidate in candidates)
        {
            int index = Array.IndexOf(normalized, Normalize(candidate));
            if (index >= 0)
            {
                return index;
            }
        }
        return -1;
    }
}
