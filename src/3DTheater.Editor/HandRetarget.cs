using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Editor;

/// <summary>
/// 手のトラッキングの 1 つの手（本人の左右）。角度は度で、指ごと（親指・人差し指・中指・薬指・小指）に 根元・中・先の曲がりと開きの 4 つ
/// （3D Theater VCamera が送る値。トラッキングの HandPose と同じ並び。設計判断記録 99 章）。
/// </summary>
public sealed record HandSignal(bool IsRight, float[] Angles)
{
    /// <summary>読み取りの確かさ（0〜1。手を横に向けた・指がカメラの方を向いたときは低い）。低いほど強くならす。</summary>
    public float Quality { get; init; } = 1f;

    /// <summary>手首 → 中指の付け根の向き（カメラから見た x 右・y 下・z 奥。手首の当てはめに使う。設計判断記録 100 章）。分からなければ 0。</summary>
    public Vector3 Forward { get; init; }

    /// <summary>掌の向き（掌の側へ。同じ決まり）。分からなければ 0。</summary>
    public Vector3 Normal { get; init; }
}

/// <summary>キャラクターの左右の手に当てる角度（度。<see cref="HandSignal"/> と同じ並び。ずれを差し引き、強め、範囲に収めた後）。</summary>
public sealed record HandOutput(float[] Left, float[] Right)
{
    public const int ValueCount = 20;

    public static HandOutput Rest { get; } = new(new float[ValueCount], new float[ValueCount]);

    /// <summary>握り具合（4 本の指の中の関節の曲がりの平均 ÷ 90°。0 = 開く・1 = 握る）。</summary>
    public static float Grip(float[] angles)
    {
        ArgumentNullException.ThrowIfNull(angles);
        float sum = 0;
        for (int f = 1; f < 5; f++)
        {
            sum += angles[(f * 4) + 1];
        }
        return Math.Clamp(sum / 4f / 90f, 0f, 1f);
    }
}

/// <summary>
/// 手のトラッキングを、キャラクターの指の角度にする（設計判断記録 99 章）。
/// <list type="bullet">
/// <item>トラッキングは、開いた手でも少し曲がって読め、握っても曲がりきらないので、開いた手の分を差し引いて強める（確かめ用の動画で、開いた手と握った手の値から決めた）。</item>
/// <item>指の開きは、根元を大きく曲げるとあてにならないので、曲げるほど弱める。</item>
/// <item>
/// 値を One Euro フィルターでならす。読み取りの確かさが低いとき（手を横に向けた・指がカメラの方を向いたとき）ほど強くならす。
/// 速さでならしを弱める強さは小さくする（読み取りの揺れを速い動きと取り違えて、揺れを通してしまうため。ノートパソコンの確かめで
/// 「手の向きによって指が震える」と分かり、確かめ用の動画で比べて決めた）。
/// </item>
/// <item>手を見失ったら <see cref="FadeOutSeconds"/> かけて伸ばした形（モデルのもとの形）へ戻す。</item>
/// <item>キャラクターは本人と同じ側の手を動かす（鏡のようにする設定なら、反対の手）。</item>
/// </list>
/// </summary>
public sealed class HandRetarget
{
    public const float FadeInSeconds = 0.15f;
    public const float FadeOutSeconds = 0.6f;

    /// <summary>速さでならしを弱める強さ（度/秒あたりの Hz）。</summary>
    public const float SpeedBeta = 0.01f;

    /// <summary>確かさが 0 のときの、ならしの切れ目の割合（確かさ 1 で 1 倍）。</summary>
    public const float LowQualityCutoff = 0.25f;

    // 開いた手で読める分（指ごとに 根元・中・先・開き。度）
    private static readonly float[] Offsets =
    [
        16, 10, 15, 32, // 親指（開きは、開いた手で人差し指から 30° ほど離れている）
        10, 10, 25, -2, // 人差し指（先は伸ばしても曲がって読める）
        8, 16, 6, -5,   // 中指
        9, 12, 7, -10,  // 薬指
        4, 12, 12, -22, // 小指
    ];

    // 強め方（握った手で、MMD の握りこぶしほどになるように）
    private static readonly float[] Gains =
    [
        2.0f, 1.4f, 1.4f, 0.6f, // 親指の付け根の骨は、握っても少ししか動いて読めないので強めに
        1.4f, 1.2f, 1.1f, 0.8f,
        1.4f, 1.2f, 1.1f, 0.8f,
        1.4f, 1.2f, 1.1f, 0.8f,
        1.4f, 1.2f, 1.1f, 0.8f,
    ];

    // 範囲（度。指ごとに 根元・中・先・開きの 下限と上限）
    private static readonly (float Min, float Max)[] ThumbLimits = [(-15, 40), (-10, 60), (-10, 80), (-30, 20)];
    private static readonly (float Min, float Max)[] FingerLimits = [(-15, 90), (-5, 110), (-5, 90), (-20, 20)];

    private readonly OneEuroFilter?[,] _filters = new OneEuroFilter?[2, HandOutput.ValueCount];
    private readonly float[][] _last = [new float[HandOutput.ValueCount], new float[HandOutput.ValueCount]];
    private readonly float[] _weight = new float[2];
    private double _lastTime = double.NaN;

    /// <summary>
    /// 1 枚分を当てる。<paramref name="hands"/> は見つかっている手（無い手は、伸ばした形へ戻していく）。
    /// <paramref name="mirror"/> なら本人の右手をキャラクターの左手に当てる。<paramref name="smoothing"/> は顔と同じなめらかさ（0〜1）。
    /// </summary>
    public HandOutput Compute(IReadOnlyList<HandSignal>? hands, double time, bool mirror, float smoothing)
    {
        float dt = double.IsNaN(_lastTime) ? 0f : (float)Math.Clamp(time - _lastTime, 0, 0.5);
        _lastTime = time;
        var result = new float[2][];
        for (int side = 0; side < 2; side++)
        {
            bool characterRight = side == 1;
            HandSignal? hand = hands?.FirstOrDefault(h => h.Angles.Length >= HandOutput.ValueCount && (mirror ? !h.IsRight : h.IsRight) == characterRight);
            if (hand is not null)
            {
                float[] mapped = Map(hand.Angles);
                float cutoff = 5f - (4.6f * Math.Clamp(smoothing, 0f, 1f)); // 顔と同じ（なめらかさ 0 → 5 Hz、1 → 0.4 Hz）
                float quality = float.IsFinite(hand.Quality) ? Math.Clamp(hand.Quality, 0f, 1f) : 1f;
                cutoff *= LowQualityCutoff + ((1f - LowQualityCutoff) * quality);
                for (int i = 0; i < mapped.Length; i++)
                {
                    OneEuroFilter filter = _filters[side, i] ??= new OneEuroFilter();
                    filter.MinCutoff = cutoff;
                    filter.Beta = SpeedBeta;
                    _last[side][i] = filter.Filter(mapped[i], time);
                }
                _weight[side] = _weight[side] <= 0 && dt <= 0 ? 1f : Math.Min(1f, _weight[side] + (dt / FadeInSeconds));
            }
            else
            {
                _weight[side] = Math.Max(0f, _weight[side] - (dt / FadeOutSeconds));
                if (_weight[side] <= 0)
                {
                    for (int i = 0; i < HandOutput.ValueCount; i++)
                    {
                        _filters[side, i] = null; // 次に見つけたときは、その形から始める
                    }
                }
            }
            float w = Smooth(_weight[side]);
            result[side] = [.. _last[side].Select(v => v * w)];
        }
        return new HandOutput(result[0], result[1]);
    }

    /// <summary>トラッキングの角度を、キャラクターの角度にする（ずれを差し引き、強め、範囲に収める。開きは根元の曲がりで弱める）。</summary>
    public static float[] Map(float[] angles)
    {
        ArgumentNullException.ThrowIfNull(angles);
        var mapped = new float[HandOutput.ValueCount];
        for (int f = 0; f < 5; f++)
        {
            (float Min, float Max)[] limits = f == 0 ? ThumbLimits : FingerLimits;
            for (int j = 0; j < 4; j++)
            {
                int i = (f * 4) + j;
                float raw = i < angles.Length && float.IsFinite(angles[i]) ? angles[i] : Offsets[i];
                float value = (raw - Offsets[i]) * Gains[i];
                if (j == 3 && f > 0)
                {
                    // 指の開き: 根元を曲げるほど弱める（曲げた指の開きは読めない。握ると、伸ばしたときの開きへ戻す）
                    float bend = angles[f * 4] * (MathF.PI / 180f);
                    value *= Math.Clamp((MathF.Cos(bend) - 0.5f) / 0.4f, 0f, 1f); // 25° までそのまま、60° で 0
                }
                mapped[i] = Math.Clamp(value, limits[j].Min, limits[j].Max);
            }
        }
        return mapped;
    }

    private static float Smooth(float t) => t * t * (3f - (2f * t));
}

/// <summary>
/// キャラクターの指のボーン（MMD の名前。VRM・FBX も読み込みでこの名前になる）と、曲げる向き（設計判断記録 99 章）。
/// 曲げる向きは、モデルのもとの形（ボーンの位置）から求める: 指の向きと掌の向き（手首・人差し指と小指の付け根から）。
/// MMD のモデルは -Z を向き、左手が +X にあるので、左手は「人差し指の付け根 × 小指の付け根」が掌の側を向く（右手は逆）。
/// </summary>
public sealed class HandRig
{
    public static IReadOnlyList<string[]> FingerBones { get; } =
    [
        ["親指０", "親指１", "親指２"],
        ["人指１", "人指２", "人指３"],
        ["中指１", "中指２", "中指３"],
        ["薬指１", "薬指２", "薬指３"],
        ["小指１", "小指２", "小指３"],
    ];

    private readonly List<Joint> _joints;

    private HandRig(List<Joint> joints) => _joints = joints;

    /// <summary>見つけた指のボーンの数（左右合わせて。最大 30）。</summary>
    public int BoneCount => _joints.Count;

    /// <summary>左右それぞれ、指のボーンがあるか。</summary>
    public (bool Left, bool Right) Sides => (_joints.Any(j => !j.Right), _joints.Any(j => j.Right));

    // 1 本のボーン: どの手・どの指・どの関節か（親指の付け根の骨が無いモデルは、親指１に根元と中の両方を当てる）と、曲げる軸・開く軸
    private sealed record Joint(int Bone, bool Right, int Finger, int[] Sources, Vector3 BendAxis, Vector3 SpreadAxis, bool Spread);

    public static HandRig Create(PmxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string[] names = [.. document.Bones.Select(b => b.Name)];
        var joints = new List<Joint>();
        foreach (bool right in new[] { false, true })
        {
            string side = right ? "右" : "左";
            int Find(string name) => FaceRigNames.Find(names, [side + name]);
            int index1 = Find("人指１");
            int pinky1 = Find("小指１");
            int wrist = Find("手首");
            if (index1 < 0 || pinky1 < 0)
            {
                continue; // 指の無いモデル（その手は動かさない）
            }
            Vector3 indexBase = document.Bones[index1].Position;
            Vector3 pinkyBase = document.Bones[pinky1].Position;
            Vector3 wristPosition = wrist >= 0 ? document.Bones[wrist].Position
                : document.Bones[index1].ParentIndex is int p and >= 0 ? document.Bones[p].Position : (indexBase + pinkyBase) * 0.5f;
            Vector3 palm = Unit(Vector3.Cross(indexBase - wristPosition, pinkyBase - wristPosition));
            if (palm == Vector3.Zero)
            {
                palm = -Vector3.UnitY;
            }
            else if (right)
            {
                palm = -palm;
            }
            Vector3 thumbSide = Unit(indexBase - pinkyBase);

            for (int f = 0; f < FingerBones.Count; f++)
            {
                int[] bones = [.. FingerBones[f].Select(Find)];
                // 親指０ が無ければ、親指１ に根元と中の両方を当てる
                List<(int Bone, int[] Sources)> chain = f == 0 && bones[0] < 0
                    ? [(bones[1], [0, 1]), (bones[2], [2])]
                    : [(bones[0], [0]), (bones[1], [1]), (bones[2], [2])];
                bool first = true;
                for (int k = 0; k < chain.Count; k++)
                {
                    int bone = chain[k].Bone;
                    if (bone < 0)
                    {
                        continue;
                    }
                    Vector3 direction = Direction(document, bone, k + 1 < chain.Count ? chain[k + 1].Bone : -1);
                    if (direction == Vector3.Zero)
                    {
                        continue;
                    }
                    // 曲げる向き: 指は掌の側へ。親指は掌の側と小指の側の間へ（親指の腹の向き）
                    Vector3 toward = f == 0 ? Unit(palm - thumbSide) : palm;
                    Vector3 bend = Unit(Vector3.Cross(direction, Perpendicular(toward, direction)));
                    Vector3 spread = Unit(Vector3.Cross(direction, Perpendicular(thumbSide, direction)));
                    joints.Add(new Joint(bone, right, f, chain[k].Sources, bend, spread, first));
                    first = false;
                }
            }
        }
        return new HandRig(joints);
    }

    /// <summary>
    /// 指のボーンに掛ける回転を <paramref name="bones"/> に足す（ボーンの今の回転の内側に掛ける分）。
    /// 付け根のボーンは、曲げてから開く（開きは掌の面の中で回す）。
    /// </summary>
    public void Build(HandOutput output, List<(int Bone, Quaternion Rotation)> bones)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(bones);
        const float ToRadians = MathF.PI / 180f;
        foreach (Joint joint in _joints)
        {
            float[] angles = joint.Right ? output.Right : output.Left;
            float bend = 0;
            foreach (int source in joint.Sources)
            {
                bend += angles[(joint.Finger * 4) + source];
            }
            Quaternion rotation = Quaternion.CreateFromAxisAngle(joint.BendAxis, bend * ToRadians);
            if (joint.Spread)
            {
                float spread = angles[(joint.Finger * 4) + 3];
                rotation = Quaternion.CreateFromAxisAngle(joint.SpreadAxis, spread * ToRadians) * rotation;
            }
            if (MathF.Abs(rotation.W) < 0.999999f)
            {
                bones.Add((joint.Bone, Quaternion.Normalize(rotation)));
            }
        }
    }

    // ボーンの向き（次の関節へ。最後の関節は、ボーンの先・前の関節の向き）
    private static Vector3 Direction(PmxDocument document, int bone, int next)
    {
        PmxBone b = document.Bones[bone];
        if (next >= 0)
        {
            return Unit(document.Bones[next].Position - b.Position);
        }
        if (b.Has(PmxBoneFlags.TailIsBone) && b.TailBone >= 0 && b.TailBone < document.Bones.Length)
        {
            Vector3 tail = Unit(document.Bones[b.TailBone].Position - b.Position);
            if (tail != Vector3.Zero)
            {
                return tail;
            }
        }
        else if (!b.Has(PmxBoneFlags.TailIsBone) && Unit(b.TailOffset) is { } offset && offset != Vector3.Zero)
        {
            return offset;
        }
        return b.ParentIndex >= 0 ? Unit(b.Position - document.Bones[b.ParentIndex].Position) : Vector3.Zero;
    }

    private static Vector3 Perpendicular(Vector3 v, Vector3 axis) => Unit(v - (axis * Vector3.Dot(v, axis)));

    private static Vector3 Unit(Vector3 v)
    {
        float length = v.Length();
        return length > 1e-6f ? v / length : Vector3.Zero;
    }
}
