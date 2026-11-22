using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Editor;

/// <summary>
/// 体のトラッキングの 1 枚分（3D Theater VCamera が送る、肩から腰までの点。メートル。カメラから見た x 右・y 下・z 奥。腰の真ん中が 0。
/// 番号は MediaPipe の体の 33 点の番号。設計判断記録 100 章）。<see cref="Visibility"/> はそれぞれが絵に見えている度合い（0〜1）。
/// </summary>
public sealed record BodySignal(Vector3[] Points, float[] Visibility)
{
    public const int LeftShoulder = 11;
    public const int RightShoulder = 12;
    public const int LeftElbow = 13;
    public const int RightElbow = 14;
    public const int LeftWrist = 15;
    public const int RightWrist = 16;

    public Vector3 Point(int index) => (uint)index < (uint)Points.Length ? Points[index] : Vector3.Zero;

    public float Seen(int index) => (uint)index < (uint)Visibility.Length && float.IsFinite(Visibility[index]) ? Visibility[index] : 0f;
}

/// <summary>
/// キャラクターの片腕に当てる向き（キャラクターの上半身から見た向き。モデルの座標の決まり: +X = キャラクターの左・+Y = 上・-Z = 前）。
/// <see cref="Upper"/> は肩 → ひじ、<see cref="Fore"/> はひじ → 手首、<see cref="HandForward"/> は手首 → 中指の付け根、<see cref="HandNormal"/> は掌の向き。
/// 重みは 0（場面の姿勢のまま）〜 1（この向き）。
/// </summary>
public sealed record ArmOutput(float Weight, Vector3 Upper, Vector3 Fore, float HandWeight, Vector3 HandForward, Vector3 HandNormal)
{
    public static ArmOutput None { get; } = new(0f, -Vector3.UnitY, -Vector3.UnitY, 0f, -Vector3.UnitY, Vector3.UnitZ);
}

/// <summary>上半身の回り（正面に合わせた後。モデルの座標）と、その重み、キャラクターの左右の腕。</summary>
public sealed record BodyOutput(Quaternion Torso, float TorsoWeight, ArmOutput Left, ArmOutput Right)
{
    public static BodyOutput None { get; } = new(Quaternion.Identity, 0f, ArmOutput.None, ArmOutput.None);
}

/// <summary>
/// 体のトラッキングを、キャラクターの上半身と腕の向きにする（設計判断記録 100 章）。
/// <list type="bullet">
/// <item>点をカメラの決まりからモデルの決まりにする（y を逆に。キャラクターはカメラの方 = -Z を向き、本人と同じ側の腕を動かす）。鏡のようにするなら、左右を入れ替える。</item>
/// <item>肩・ひじ・手首の点を One Euro フィルターでならす（奥行きは揺れが 2 倍ほどあるので、強めにならす）。</item>
/// <item>上半身の向き: 左右の肩を結ぶ線と、上（カメラの上）から決める（前後の傾きは、腰が絵に映らないことが多く読めないので使わない）。
/// 正面合わせのときの向きからの回りを、キャラクターの上半身に当てる。</item>
/// <item>腕の向き: 本人の上半身から見た向きにする（キャラクターの上半身が待機モーションなどで動いても、腕は上半身から見た向きを保つ）。</item>
/// <item>手首: 手のトラッキングの手の向き（手首 → 中指の付け根と、掌の向き）を、同じく上半身から見た向きにする。読み取りの確かさが低いほど強くならす。
/// 読み取ったままの手の向きは、ふつうは 1 枚に 3° ほどしか変わらないが、ときどき 50° 以上跳ねる（奥行きの取り違えで掌の向きが裏返る・左右の手の
/// 取り違え）。1〜2 枚だけの跳ねは使わず、続いたときも 1 秒に <see cref="MaxHandDegreesPerSecond"/> までしか回さない。</item>
/// <item>肩と手首が見えている腕だけ動かす（ひじは絵の外でもよい。手を顔の前に上げると、ひじは下へはみ出しがちなため）。手首は、見え始め
/// （<see cref="WristOnAbove"/>）と見えなくなり（<see cref="WristOffBelow"/>）の境を分けて、境の近くで動かす・戻すを繰り返さない。手のトラッキングが
/// その側の手を見つけていれば、手首は見えているとみなす（手を顔の近くへ上げると、体の点のモデルの手首の見えている度合いが下がるため）。
/// 見えなくなっても <see cref="WristHoldSeconds"/> は動かし続ける（絵の端で手を見つけたり見失ったりしても、腕が行き来しないように）。
/// 見えなくなったら <see cref="FadeOutSeconds"/> かけて場面の姿勢（待機モーション・ポーズ。無ければモデルのもとの形）へ戻す。</item>
/// </list>
/// 揺れと遅れの釣り合いは、確かめ用の動画の点（揺れ）と、作った腕の動き（遅れ）で比べて決めた（設計判断記録 100 章）。
/// </summary>
public sealed class BodyRetarget
{
    public const float FadeInSeconds = 0.2f;
    public const float FadeOutSeconds = 0.6f;

    /// <summary>正面合わせに使う枚数（顔と同じ）。</summary>
    public const int CalibrationFrames = 15;

    /// <summary>上半身の回りの上限（度）。</summary>
    public const float MaxTorsoDegrees = 45f;

    /// <summary>点のならしの切れ目（顔のならしの切れ目に対して）。腕の点は顔の向きより揺れるので低め。</summary>
    public const float PointCutoffScale = 0.5f;

    /// <summary>奥行き（z）のならしの切れ目（x・y に対して）。</summary>
    public const float DepthCutoffScale = 0.5f;

    /// <summary>速さでならしを弱める強さ（メートル/秒あたりの Hz）。</summary>
    public const float SpeedBeta = 4f;

    /// <summary>手首が見え始めたとみなす度合い（これ以上）と、見えなくなったとみなす度合い（これより下）。</summary>
    public const float WristOnAbove = 0.6f;
    public const float WristOffBelow = 0.3f;

    /// <summary>手首が見えなくなってから、腕を戻し始めるまでの間（秒）。</summary>
    public const float WristHoldSeconds = 0.25f;

    /// <summary>手の向きが、これより大きく変わった枚は、続くまで使わない（度。手の向き・掌の向き）。</summary>
    public const float HandJumpDegrees = 50f;

    /// <summary>大きく変わった向きが、この枚数続いたら使う（本当に手を回した）。</summary>
    public const int HandJumpFrames = 3;

    /// <summary>手の向きを回す速さの上限（度/秒）。</summary>
    public const float MaxHandDegreesPerSecond = 540f;

    /// <summary>手の読み取りの確かさが 0 のときの、手の向きのならしの切れ目の割合（指と同じ）。</summary>
    public const float LowQualityCutoff = 0.25f;

    // キャラクターの左右（0 = 左・1 = 右）ごとの、肩・ひじ・手首の本人の点の番号（鏡にしないとき）
    private static readonly int[][] Joints =
    [
        [BodySignal.LeftShoulder, BodySignal.LeftElbow, BodySignal.LeftWrist],
        [BodySignal.RightShoulder, BodySignal.RightElbow, BodySignal.RightWrist],
    ];

    private readonly OneEuroFilter?[,,] _points = new OneEuroFilter?[2, 3, 3]; // 左右・肩ひじ手首・xyz
    private readonly OneEuroFilter?[,] _handFilters = new OneEuroFilter?[2, 6]; // 左右・手の向きと掌の向きの xyz
    private readonly float[] _armWeight = new float[2];
    private readonly float[] _handWeight = new float[2];
    private readonly bool[] _wristSeen = new bool[2];
    private readonly double[] _wristLastSeen = [double.NegativeInfinity, double.NegativeInfinity];
    private readonly int[] _handJumps = new int[2];
    private readonly Vector3[] _upper = [-Vector3.UnitY, -Vector3.UnitY];
    private readonly Vector3[] _fore = [-Vector3.UnitY, -Vector3.UnitY];
    private readonly Vector3[] _handForward = [-Vector3.UnitY, -Vector3.UnitY];
    private readonly Vector3[] _handNormal = [Vector3.UnitZ, Vector3.UnitZ];
    private readonly List<Quaternion> _calibration = [];
    private Quaternion? _neutral;
    private Quaternion _torsoFrame = Quaternion.Identity; // 今の本人の上半身の向き（モデルの決まり）
    private Quaternion _torso = Quaternion.Identity;
    private float _torsoWeight;
    private double _lastTime = double.NaN;
    private bool? _lastMirror;

    /// <summary>上半身の正面を覚えたか。</summary>
    public bool IsCalibrated => _neutral is not null;

    /// <summary>次に肩が見えた <see cref="CalibrationFrames"/> 枚の上半身の向きの平均を、正面として覚え直す。</summary>
    public void RequestCalibration()
    {
        _neutral = null;
        _calibration.Clear();
    }

    /// <summary>
    /// 1 枚分を当てる。<paramref name="body"/> が null（体が無い・止めた）なら、腕と上半身を場面の姿勢へ戻していく。
    /// <paramref name="hands"/> の手の向き（<see cref="HandSignal.Forward"/>・<see cref="HandSignal.Normal"/>）があれば、手首も当てる。
    /// </summary>
    public BodyOutput Compute(BodySignal? body, IReadOnlyList<HandSignal>? hands, double time, bool mirror, float smoothing)
    {
        float dt = double.IsNaN(_lastTime) ? 0f : (float)Math.Clamp(time - _lastTime, 0, 0.5);
        _lastTime = time;
        float cutoff = (5f - (4.6f * Math.Clamp(smoothing, 0f, 1f))) * PointCutoffScale; // 顔と同じなめらかさの決め方
        if (_lastMirror is { } last && last != mirror)
        {
            // 鏡のようにするかを変えた: 覚えた正面は左右が逆になるので、覚え直す
            RequestCalibration();
            Array.Clear(_points);
            Array.Clear(_handFilters);
        }
        _lastMirror = mirror;

        // 上半身の向き（両方の肩が見えているときだけ求め直す）
        bool torsoSeen = false;
        var points = new Vector3[2, 3];
        if (body is not null)
        {
            for (int side = 0; side < 2; side++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Vector3 p = ToModel(body.Point(Index(side, j, mirror)), mirror);
                    points[side, j] = Filter(_points, side, j, p, time, cutoff, reset: _armWeight[side] <= 0 && _torsoWeight <= 0);
                }
            }
            torsoSeen = Seen(body, 0, 0, mirror) && Seen(body, 1, 0, mirror);
            if (torsoSeen && Frame(points[0, 0] - points[1, 0]) is { } frame)
            {
                _torsoFrame = frame;
                if (_neutral is null)
                {
                    _calibration.Add(frame);
                    if (_calibration.Count >= CalibrationFrames)
                    {
                        _neutral = Average(_calibration);
                        _calibration.Clear();
                    }
                }
                if (_neutral is { } neutral)
                {
                    _torso = Limit(Quaternion.Normalize(frame * Quaternion.Inverse(neutral)), MaxTorsoDegrees);
                }
            }
        }
        _torsoWeight = Step(_torsoWeight, torsoSeen && _neutral is not null, dt);

        // 腕と手首（本人の上半身から見た向き）
        Quaternion toTorso = Quaternion.Inverse(_torsoFrame);
        var arms = new ArmOutput[2];
        for (int side = 0; side < 2; side++)
        {
            bool characterRight = side == 1;
            bool handHere = hands?.Any(h => (mirror ? !h.IsRight : h.IsRight) == characterRight) == true;
            float wrist = handHere ? 1f : body?.Seen(Index(side, 2, mirror)) ?? 0f;
            _wristSeen[side] = _wristSeen[side] ? wrist >= WristOffBelow : wrist >= WristOnAbove;
            if (_wristSeen[side])
            {
                _wristLastSeen[side] = time;
            }
            bool armSeen = body is not null && Seen(body, side, 0, mirror) && time - _wristLastSeen[side] <= WristHoldSeconds;
            if (armSeen)
            {
                Vector3 upper = Unit(points[side, 1] - points[side, 0]);
                Vector3 fore = Unit(points[side, 2] - points[side, 1]);
                if (upper != Vector3.Zero && fore != Vector3.Zero)
                {
                    _upper[side] = Vector3.Normalize(Vector3.Transform(upper, toTorso));
                    _fore[side] = Vector3.Normalize(Vector3.Transform(fore, toTorso));
                }
                else
                {
                    armSeen = false;
                }
            }
            _armWeight[side] = Step(_armWeight[side], armSeen, dt);

            HandSignal? hand = armSeen ? hands?.FirstOrDefault(h => (mirror ? !h.IsRight : h.IsRight) == characterRight
                && h.Forward.LengthSquared() > 0.5f && h.Normal.LengthSquared() > 0.5f) : null;
            if (hand is not null)
            {
                Vector3 forward = Unit(Vector3.Transform(ToModel(hand.Forward, mirror), toTorso));
                Vector3 normal = Unit(Vector3.Transform(ToModel(hand.Normal, mirror), toTorso));
                bool reset = _handWeight[side] <= 0;
                // 1〜2 枚だけ大きく変わった向きは使わない（続いたら使うが、回す速さに上限を付けて、少しずつ回す）
                bool jump = !reset && (Degrees(forward, _handForward[side]) > HandJumpDegrees || Degrees(normal, _handNormal[side]) > HandJumpDegrees);
                _handJumps[side] = jump ? _handJumps[side] + 1 : 0;
                if (!jump || _handJumps[side] >= HandJumpFrames)
                {
                    float quality = float.IsFinite(hand.Quality) ? Math.Clamp(hand.Quality, 0f, 1f) : 1f;
                    float handCutoff = cutoff * (LowQualityCutoff + ((1f - LowQualityCutoff) * quality));
                    forward = FilterVector(side, 0, forward, time, handCutoff, reset);
                    normal = FilterVector(side, 3, normal, time, handCutoff, reset);
                    if (!reset)
                    {
                        float step = MaxHandDegreesPerSecond * (dt > 0 ? dt : 1f / 30f);
                        forward = Toward(_handForward[side], forward, step);
                        normal = Toward(_handNormal[side], normal, step);
                    }
                    normal = Unit(normal - (forward * Vector3.Dot(normal, forward)));
                    if (forward != Vector3.Zero && normal != Vector3.Zero)
                    {
                        _handForward[side] = forward;
                        _handNormal[side] = normal;
                    }
                }
            }
            _handWeight[side] = Step(_handWeight[side], hand is not null, dt);
            float armWeight = Smooth(_armWeight[side]);
            arms[side] = new ArmOutput(armWeight, _upper[side], _fore[side], Math.Min(armWeight, Smooth(_handWeight[side])), _handForward[side], _handNormal[side]);
        }
        return new BodyOutput(_torso, Smooth(_torsoWeight), arms[0], arms[1]);
    }

    /// <summary>
    /// 左右の肩を結ぶ線（キャラクターの右肩 → 左肩。モデルの決まり）と上から、上半身の向き（モデルの X・Y・Z をその向きの横・上・後ろへ写す回転）。
    /// 線が上下を向いているなど、決まらなければ null。
    /// </summary>
    public static Quaternion? Frame(Vector3 rightToLeft)
    {
        Vector3 x = Unit(rightToLeft);
        if (x == Vector3.Zero || MathF.Abs(x.Y) > 0.8f)
        {
            return null;
        }
        Vector3 y = Unit(Vector3.UnitY - (x * x.Y));
        Vector3 z = Vector3.Cross(x, y);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    // キャラクターの左右と関節（0 肩・1 ひじ・2 手首）の、本人の点の番号（鏡にするなら反対の側）
    private static int Index(int side, int joint, bool mirror) => Joints[mirror ? 1 - side : side][joint];

    private static bool Seen(BodySignal body, int side, int joint, bool mirror) => body.Seen(Index(side, joint, mirror)) >= 0.5f;

    // カメラの決まり（x 右・y 下・z 奥）→ モデルの決まり（+X = キャラクターの左・+Y = 上・-Z = 前）。キャラクターはカメラの方を向くので、
    // 画像の右（本人の左）がキャラクターの左。鏡のようにするなら、左右を逆にする
    private static Vector3 ToModel(Vector3 camera, bool mirror) => new(mirror ? -camera.X : camera.X, -camera.Y, camera.Z);

    private static Vector3 Filter(OneEuroFilter?[,,] filters, int side, int joint, Vector3 value, double time, float cutoff, bool reset)
    {
        float Axis(int axis, float v)
        {
            if (reset)
            {
                filters[side, joint, axis] = null;
            }
            OneEuroFilter f = filters[side, joint, axis] ??= new OneEuroFilter();
            f.MinCutoff = axis == 2 ? cutoff * DepthCutoffScale : cutoff;
            f.Beta = SpeedBeta;
            return f.Filter(v, time);
        }
        return new Vector3(Axis(0, value.X), Axis(1, value.Y), Axis(2, value.Z));
    }

    private Vector3 FilterVector(int side, int offset, Vector3 value, double time, float cutoff, bool reset)
    {
        float Axis(int axis, float v)
        {
            if (reset)
            {
                _handFilters[side, offset + axis] = null;
            }
            OneEuroFilter f = _handFilters[side, offset + axis] ??= new OneEuroFilter();
            f.MinCutoff = cutoff;
            f.Beta = 0.5f; // 向き（長さ 1）の 1 秒あたりの変わり方で
            return f.Filter(v, time);
        }
        return Unit(new Vector3(Axis(0, value.X), Axis(1, value.Y), Axis(2, value.Z)));
    }

    private static float Step(float weight, bool seen, float dt) => seen
        ? (weight <= 0 && dt <= 0 ? 1f : Math.Min(1f, weight + (dt / FadeInSeconds)))
        : Math.Max(0f, weight - (dt / FadeOutSeconds));

    private static Quaternion Limit(Quaternion q, float maxDegrees)
    {
        q = Quaternion.Normalize(q.W < 0 ? -q : q);
        float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        float max = maxDegrees * (MathF.PI / 180f);
        return angle <= max ? q : Quaternion.Slerp(Quaternion.Identity, q, max / angle);
    }

    private static Quaternion Average(List<Quaternion> rotations)
    {
        Vector4 sum = Vector4.Zero;
        Quaternion first = rotations[0];
        foreach (Quaternion q in rotations)
        {
            var v = new Vector4(q.X, q.Y, q.Z, q.W);
            sum += Quaternion.Dot(q, first) < 0 ? -v : v;
        }
        return Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
    }

    private static float Smooth(float t) => t * t * (3f - (2f * t));

    private static float Degrees(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * (180f / MathF.PI);

    // from を to へ、最大 maxDegrees まで回す
    private static Vector3 Toward(Vector3 from, Vector3 to, float maxDegrees)
    {
        float angle = Degrees(from, to);
        if (angle <= maxDegrees || to == Vector3.Zero)
        {
            return to;
        }
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-12f)
        {
            axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY); // 反対向き: 直角な軸で回す
        }
        return Vector3.Normalize(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), maxDegrees * (MathF.PI / 180f))));
    }

    private static Vector3 Unit(Vector3 v)
    {
        float length = v.Length();
        return length > 1e-6f && float.IsFinite(length) ? v / length : Vector3.Zero;
    }
}

/// <summary>
/// キャラクターの腕のボーン（MMD の名前: 腕・腕捩・ひじ・手捩・手首）と、腕の向きの当て方（設計判断記録 100 章）。
/// 向きは、モデルのもとの形（ボーンの位置）から求める: 腕（腕 → ひじ）・前腕（ひじ → 手首）・手（手首 → 中指の付け根）と掌の向き。
/// <list type="bullet">
/// <item>腕: もとの腕の向きを、本人の腕の向きへ回す。腕の捩りは、ひじの曲がる向き（本人の肩・ひじ・手首を含む面）で決める（ひじをほとんど
/// 伸ばしているときは、その面が決まらないので、いちばん小さく回した向きへ寄せる）。</item>
/// <item>ひじ: 前腕を、本人の前腕の向きへ（曲がる向きは腕で合わせてあるので、ほぼひじを曲げる向きだけの回転になる）。</item>
/// <item>手首: 手の向きと掌の向きを合わせる。前腕から見た回転を、前腕の軸まわりの捩り（手捩へ）と、それ以外（手首へ）に分け、範囲に収める。</item>
/// <item>腕捩は捩らない（腕の捩りは腕に入れる）。腕捩・手捩の無いモデルは、手首だけで回す。</item>
/// </list>
/// 向きは、キャラクターの上半身（上半身2。無ければ上半身）から見た向きとして頼む（<c>Reference</c>）。
/// </summary>
public sealed class ArmRig
{
    /// <summary>ひじの曲がりの上限（度）。</summary>
    public const float MaxElbowDegrees = 160f;

    /// <summary>手首の、捩り以外の曲がりの上限（度）。</summary>
    public const float MaxWristDegrees = 75f;

    /// <summary>前腕の捩りの上限（度。もとの形から）。</summary>
    public const float MaxTwistDegrees = 120f;

    // ひじの曲がる向きで腕の捩りを決め始める・決めきる曲がり（度）
    private const float PlaneFromDegrees = 10f;
    private const float PlaneFullDegrees = 30f;

    private readonly int _reference;
    private readonly List<Arm> _arms;

    private sealed record Arm(bool Right, int Upper, int UpperTwist, int Elbow, int HandTwist, int Wrist, Vector3 UpperRest, Vector3 BendRest,
        Vector3 ForeRest, Vector3 TwistAxis, Vector3 HandForwardRest, Vector3 HandNormalRest);

    private ArmRig(int reference, List<Arm> arms)
    {
        _reference = reference;
        _arms = arms;
    }

    /// <summary>動かすボーンの数（左右合わせて）。</summary>
    public int BoneCount => _arms.Sum(a => 3 + (a.UpperTwist >= 0 ? 1 : 0) + (a.HandTwist >= 0 ? 1 : 0));

    /// <summary>左右それぞれ、腕のボーンがあるか。</summary>
    public (bool Left, bool Right) Sides => (_arms.Any(a => !a.Right), _arms.Any(a => a.Right));

    public static ArmRig Create(PmxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string[] names = [.. document.Bones.Select(b => b.Name)];
        int reference = FaceRigNames.Find(names, ["上半身2"]);
        if (reference < 0)
        {
            reference = FaceRigNames.Find(names, FaceRigNames.UpperBody);
        }
        var arms = new List<Arm>();
        if (reference < 0)
        {
            return new ArmRig(-1, arms); // 上半身の無いモデル（腕は動かさない）
        }
        Vector3 P(int bone) => document.Bones[bone].Position;
        foreach (bool right in new[] { false, true })
        {
            string side = right ? "右" : "左";
            int Find(string name) => FaceRigNames.Find(names, [side + name]);
            int upper = Find("腕");
            int elbow = Find("ひじ");
            int wrist = Find("手首");
            if (upper < 0 || elbow < 0 || wrist < 0)
            {
                continue;
            }
            Vector3 upperRest = Unit(P(elbow) - P(upper));
            Vector3 foreRest = Unit(P(wrist) - P(elbow));
            if (upperRest == Vector3.Zero || foreRest == Vector3.Zero)
            {
                continue;
            }
            // ひじは前（-Z）へ曲がる
            Vector3 bendRest = Perpendicular(-Vector3.UnitZ, upperRest);
            int upperTwist = Find("腕捩");
            int handTwist = Find("手捩");
            Vector3 twistAxis = handTwist >= 0 && document.Bones[handTwist].Has(PmxBoneFlags.FixedAxis) && Unit(document.Bones[handTwist].FixedAxis) is { } axis
                && axis != Vector3.Zero ? axis : foreRest;
            if (Vector3.Dot(twistAxis, foreRest) < 0)
            {
                twistAxis = -twistAxis;
            }
            // 手: 手首 → 中指の付け根と、掌の向き（左手は「人差し指の付け根 × 小指の付け根」が掌の側。指の当てはめと同じ）
            int middle = Find("中指１");
            int index = Find("人指１");
            int pinky = Find("小指１");
            Vector3 handForward = middle >= 0 && Unit(P(middle) - P(wrist)) is { } m && m != Vector3.Zero ? m : foreRest;
            Vector3 palm = index >= 0 && pinky >= 0 ? Unit(Vector3.Cross(P(index) - P(wrist), P(pinky) - P(wrist))) : Vector3.Zero;
            if (right)
            {
                palm = -palm;
            }
            palm = palm == Vector3.Zero ? Perpendicular(-Vector3.UnitY, handForward) : Perpendicular(palm, handForward);
            arms.Add(new Arm(right, upper, upperTwist, elbow, handTwist, wrist, upperRest, bendRest, foreRest, twistAxis, handForward, palm));
        }
        return new ArmRig(reference, arms);
    }

    /// <summary>
    /// 腕を向ける頼みを <paramref name="aims"/> に足す（親から子の順。Reference が -1 の頼みは、親から見た回転をそのまま）。
    /// 重みが 0 の腕は足さない（場面の姿勢のまま）。
    /// </summary>
    public void Build(BodyOutput output, List<(int Bone, int Reference, Quaternion Rotation, float Weight)> aims)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(aims);
        foreach (Arm arm in _arms)
        {
            ArmOutput o = arm.Right ? output.Right : output.Left;
            if (!(o.Weight > 0))
            {
                continue;
            }
            (Quaternion upper, Quaternion elbow) = Solve(arm, o.Upper, o.Fore);
            aims.Add((arm.Upper, _reference, upper, o.Weight));
            if (arm.UpperTwist >= 0)
            {
                aims.Add((arm.UpperTwist, -1, Quaternion.Identity, o.Weight));
            }
            aims.Add((arm.Elbow, _reference, elbow, o.Weight));
            if (!(o.HandWeight > 0))
            {
                continue;
            }
            // 手: もとの手の向きと掌の向きを、本人の手の向きへ。前腕から見た回転を、捩り（手捩）とそれ以外（手首）に分けて範囲に収める
            Quaternion hand = FromFrames(arm.HandForwardRest, arm.HandNormalRest, o.HandForward, o.HandNormal);
            Quaternion relative = Quaternion.Normalize(Quaternion.Inverse(elbow) * hand);
            Quaternion twist = LimitTwist(Twist(relative, arm.TwistAxis), arm.TwistAxis, MaxTwistDegrees);
            Quaternion swing = Limit(Quaternion.Normalize(relative * Quaternion.Inverse(twist)), MaxWristDegrees);
            if (arm.HandTwist >= 0)
            {
                aims.Add((arm.HandTwist, -1, twist, o.HandWeight));
            }
            aims.Add((arm.Wrist, _reference, Quaternion.Normalize(elbow * swing * twist), o.HandWeight));
        }
    }

    // 腕とひじの向け先（上半身から見た回転）
    private static (Quaternion Upper, Quaternion Elbow) Solve(Arm arm, Vector3 upper, Vector3 fore)
    {
        upper = Unit(upper);
        fore = Unit(fore);
        if (upper == Vector3.Zero || fore == Vector3.Zero)
        {
            return (Quaternion.Identity, Quaternion.Identity);
        }
        // いちばん小さく回した向きと、そのときのひじの曲がる向き
        Quaternion swing = FromTo(arm.UpperRest, upper);
        Vector3 bendSwing = Perpendicular(Vector3.Transform(arm.BendRest, swing), upper);
        // 本人のひじの曲がる向き（肩・ひじ・手首を含む面）。曲がりが小さいほど、いちばん小さく回した向きへ寄せる
        float bendDegrees = MathF.Acos(Math.Clamp(Vector3.Dot(upper, fore), -1f, 1f)) * (180f / MathF.PI);
        Vector3 bend = bendSwing;
        Vector3 measured = Perpendicular(fore, upper);
        if (measured != Vector3.Zero && bendSwing != Vector3.Zero)
        {
            float t = Math.Clamp((bendDegrees - PlaneFromDegrees) / (PlaneFullDegrees - PlaneFromDegrees), 0f, 1f);
            t = t * t * (3f - (2f * t));
            float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(bendSwing, measured), upper), Vector3.Dot(bendSwing, measured));
            bend = Vector3.Transform(bendSwing, Quaternion.CreateFromAxisAngle(upper, angle * t));
        }
        Quaternion upperRotation = bendSwing == Vector3.Zero ? swing : FromFrames(arm.UpperRest, arm.BendRest, upper, bend);
        // ひじ: 腕を向けた後の前腕を、本人の前腕へ（曲がりは上限まで）
        Vector3 current = Vector3.Transform(arm.ForeRest, upperRotation);
        Quaternion toFore = Limit(FromTo(current, fore), MaxElbowDegrees);
        return (upperRotation, Quaternion.Normalize(toFore * upperRotation));
    }

    /// <summary>a を b へ向ける、いちばん小さい回転（反対向きなら、直角な軸で半回転）。</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Unit(a);
        b = Unit(b);
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        Vector3 axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-12f)
        {
            if (dot > 0)
            {
                return Quaternion.Identity;
            }
            Vector3 other = MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            return Quaternion.CreateFromAxisAngle(Unit(Vector3.Cross(a, other)), MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    /// <summary>向き a0 を a へ、それに直角な向き b0 を b（a に直角にしたもの）へ写す回転。</summary>
    public static Quaternion FromFrames(Vector3 a0, Vector3 b0, Vector3 a, Vector3 b) =>
        Quaternion.Normalize(Basis(a, b) * Quaternion.Inverse(Basis(a0, b0)));

    // X・Y・Z を x・y（x に直角にしたもの）・x × y へ写す回転
    private static Quaternion Basis(Vector3 x, Vector3 y)
    {
        x = Unit(x);
        y = Perpendicular(y, x);
        if (x == Vector3.Zero || y == Vector3.Zero)
        {
            return Quaternion.Identity;
        }
        Vector3 z = Vector3.Cross(x, y);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    // 回転のうち、axis まわりの捩りの分
    private static Quaternion Twist(Quaternion q, Vector3 axis)
    {
        var v = new Vector3(q.X, q.Y, q.Z);
        Vector3 p = axis * Vector3.Dot(v, axis);
        var twist = new Quaternion(p, q.W);
        return twist.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(twist);
    }

    private static Quaternion LimitTwist(Quaternion twist, Vector3 axis, float maxDegrees)
    {
        twist = twist.W < 0 ? -twist : twist;
        float angle = 2f * MathF.Atan2(Vector3.Dot(new Vector3(twist.X, twist.Y, twist.Z), axis), twist.W);
        float max = maxDegrees * (MathF.PI / 180f);
        return Quaternion.CreateFromAxisAngle(axis, Math.Clamp(angle, -max, max));
    }

    private static Quaternion Limit(Quaternion q, float maxDegrees)
    {
        q = Quaternion.Normalize(q.W < 0 ? -q : q);
        float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        float max = maxDegrees * (MathF.PI / 180f);
        return angle <= max ? q : Quaternion.Slerp(Quaternion.Identity, q, max / angle);
    }

    private static Vector3 Perpendicular(Vector3 v, Vector3 axis) => Unit(v - (axis * Vector3.Dot(v, axis)));

    private static Vector3 Unit(Vector3 v)
    {
        float length = v.Length();
        return length > 1e-6f && float.IsFinite(length) ? v / length : Vector3.Zero;
    }
}
