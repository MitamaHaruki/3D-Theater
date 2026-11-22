using System.Numerics;

namespace Theater3D.Animation;

/// <summary>
/// ボーンを、決まった向き（モデルの座標の回転）へ向ける頼み（トラッキングの腕と手首。設計判断記録 100 章）。
/// <see cref="Reference"/> が 0 以上なら、向け先 = そのボーンの今の置き方の回転 × <see cref="Rotation"/>（体の向きに付いていく。待機モーションで
/// 上半身が動いても、腕は上半身から見た向きを保つ）。-1 なら、<see cref="Rotation"/> をそのまま親から見た回転にする（腕捩・手捩の捩り）。
/// <see cref="Weight"/> で、場面の回転との間をつなぐ（0 = 場面の回転のまま・1 = 頼んだ向き）。
/// </summary>
public readonly record struct BoneAim(int Bone, int Reference, Quaternion Rotation, float Weight);

/// <summary>
/// 向ける頼み（<see cref="BoneAim"/>）を、計算の入力に書き込む。ボーンの回転は親から見た回転なので、頼んだ向きにするには親の今の置き方が要る。
/// そこで、一度、物理の前までを求めて（場面の回転とトラッキングの重ねで）親の置き方を知り、そこから見た回転を入力に書く。
/// 頼みは親から子の順に並べること（後の頼みの親は、先の頼みで向けた後の置き方で求める）。
/// </summary>
public static class PoseAims
{
    /// <summary>
    /// <paramref name="input"/> の、頼んだボーンの回転を書き換える。<paramref name="state"/> は途中の計算に使うので、呼んだ後に求め直すこと。
    /// 頼みが無ければ何もしない。
    /// </summary>
    public static void Apply(ModelRig rig, PoseInput input, PoseState state, IReadOnlyList<BoneAim> aims)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(aims);
        if (aims.Count == 0)
        {
            return;
        }
        PoseEvaluator.EvaluateBeforePhysics(rig, input, state);
        var aimed = new Dictionary<int, Quaternion>(aims.Count); // 向けたボーンの、向けた後の置き方の回転
        foreach (BoneAim aim in aims)
        {
            if ((uint)aim.Bone >= (uint)rig.Bones.Length || !(aim.Weight > 0) || !IsFinite(aim.Rotation))
            {
                continue;
            }
            RigBone bone = rig.Bones[aim.Bone];
            Quaternion parent = Global(rig, state, aimed, bone.Parent);
            Quaternion local = (uint)aim.Reference < (uint)rig.Bones.Length
                ? Quaternion.Inverse(parent) * state.GlobalRotation[aim.Reference] * aim.Rotation
                : aim.Rotation;
            Quaternion blended = Quaternion.Normalize(Quaternion.Slerp(input.Rotations[aim.Bone], Quaternion.Normalize(local), Math.Min(aim.Weight, 1f)));
            input.Rotations[aim.Bone] = blended;
            // 捩りのボーン（軸の決まったボーン）は、計算で軸まわりの分だけになる
            Quaternion used = bone.FixedAxis is { } axis ? Rotations.Twist(blended, axis) : blended;
            aimed[aim.Bone] = Quaternion.Normalize(parent * used);
        }
    }

    // ボーンの置き方の回転（向けたボーンの子孫なら、向けた後の値から積み直す。そうでなければ、求めた値のまま）
    private static Quaternion Global(ModelRig rig, PoseState state, Dictionary<int, Quaternion> aimed, int index)
    {
        if (index < 0)
        {
            return Quaternion.Identity;
        }
        if (aimed.TryGetValue(index, out Quaternion done))
        {
            return done;
        }
        if (!HasAimedAncestor(rig, aimed, index))
        {
            return state.GlobalRotation[index];
        }
        Quaternion local = state.IkRotation[index] * state.AnimationRotation[index] * state.InheritRotation[index];
        return Quaternion.Normalize(Global(rig, state, aimed, rig.Bones[index].Parent) * local);
    }

    private static bool HasAimedAncestor(ModelRig rig, Dictionary<int, Quaternion> aimed, int index)
    {
        for (int p = rig.Bones[index].Parent; p >= 0; p = rig.Bones[p].Parent)
        {
            if (aimed.ContainsKey(p))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsFinite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}
