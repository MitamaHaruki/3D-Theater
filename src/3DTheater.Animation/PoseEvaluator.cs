using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Animation;

/// <summary>計算の入力。ボーンごとの回転・移動（利用者やモーションが与える値）と、モーフごとの値。</summary>
public sealed class PoseInput
{
    public PoseInput(ModelRig rig)
    {
        Rotations = [.. Enumerable.Repeat(Quaternion.Identity, rig.Bones.Length)];
        Translations = new Vector3[rig.Bones.Length];
        MorphWeights = new float[rig.Morphs.Length];
    }

    public Quaternion[] Rotations { get; }

    public Vector3[] Translations { get; }

    public float[] MorphWeights { get; }
}

/// <summary>計算の結果。毎回作り直さずに使い回す（フレームごとのごみを出さない）。</summary>
public sealed class PoseState
{
    public PoseState(ModelRig rig)
    {
        int n = rig.Bones.Length;
        Global = new Matrix4x4[n];
        GlobalRotation = new Quaternion[n];
        Skinning = new Matrix4x4[n];
        AnimationRotation = new Quaternion[n];
        AnimationTranslation = new Vector3[n];
        IkRotation = new Quaternion[n];
        InheritRotation = new Quaternion[n];
        InheritTranslation = new Vector3[n];
        MorphRotation = new Quaternion[n];
        MorphTranslation = new Vector3[n];
        EffectiveMorphWeights = new float[rig.Morphs.Length];
        VertexOffsets = new Vector3[rig.RestPositions.Length];
        UvOffsets = new Vector2[rig.RestPositions.Length];
        Materials = new MaterialValues[rig.BaseMaterials.Length];
        PhysicsDriven = new bool[n];
        Refreshed = new bool[n];
    }

    /// <summary>ボーンの置き方（モデルの座標で）。</summary>
    public Matrix4x4[] Global { get; }

    public Quaternion[] GlobalRotation { get; }

    /// <summary>頂点を初期の姿勢から今の姿勢へ動かす行列（= 初期位置を引いてから Global）。</summary>
    public Matrix4x4[] Skinning { get; }

    /// <summary>入力とボーンモーフを合わせた回転・移動。</summary>
    public Quaternion[] AnimationRotation { get; }

    public Vector3[] AnimationTranslation { get; }

    public Quaternion[] IkRotation { get; }

    public Quaternion[] InheritRotation { get; }

    public Vector3[] InheritTranslation { get; }

    internal Quaternion[] MorphRotation { get; }

    internal Vector3[] MorphTranslation { get; }

    /// <summary>フリップモーフとグループモーフを当てた後のモーフの値（インパルスモーフの強さにも使う）。</summary>
    public float[] EffectiveMorphWeights { get; }

    public Vector3[] VertexOffsets { get; }

    public Vector2[] UvOffsets { get; }

    public MaterialValues[] Materials { get; }

    /// <summary>物理演算で置き方を決めたボーン（<see cref="SetPhysicsResult"/>）。物理の前の計算で消す。</summary>
    public bool[] PhysicsDriven { get; }

    /// <summary>物理で動いたボーンにつながる子を求め直すときの作業用。</summary>
    internal bool[] Refreshed { get; }

    /// <summary><see cref="VertexOffsets"/> に 0 でない値が入っているかもしれない。</summary>
    internal bool HasVertexOffsets { get; set; }

    internal bool HasUvOffsets { get; set; }

    public Vector3 BonePosition(int bone) => Global[bone].Translation;

    /// <summary>物理演算の結果でボーンを置く（モデルの座標で。拡大を含まないこと）。</summary>
    public void SetPhysicsResult(int bone, Matrix4x4 global)
    {
        Global[bone] = global;
        GlobalRotation[bone] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(global));
        PhysicsDriven[bone] = true;
    }
}

/// <summary>
/// ボーンとモーフを PMX の決まりに沿って計算する（指示書 4.2・4.3）。全ボーンを一度に処理するのではなく、次の順に行う:
/// 1. モーフの値を求める（フリップモーフで切り替え、グループモーフを展開）。ボーンモーフはボーンの回転・移動に足す
/// 2. 各ボーンの回転・移動を決め、親から子へ置き方を積み上げる
/// 3. 変形の順番（物理後 → 変形階層 → 番号）に、付与（回転・移動を分けてもらう）と IK を解く
/// 4. 頂点モーフ・UV モーフ・材質モーフを求める
/// IK は繰り返し法（CCD）で解き、ひざのように 1 軸だけ制限のあるリンクは、その軸まわりだけで回す。
/// 物理演算を使うときは、3 の「物理後」でないボーンまでを <see cref="EvaluateBeforePhysics"/> で求め、
/// 物理の結果でボーンを置いて（<see cref="PoseState.SetPhysicsResult"/>、<see cref="RefreshPhysicsChildren"/>）から、
/// 残りを <see cref="EvaluateAfterPhysics"/> で求める（MMD と同じ順番）。
/// </summary>
public static class PoseEvaluator
{
    private const float IkConvergence = 1e-4f;

    /// <summary>物理演算を使わずに全部を求める。</summary>
    public static void Evaluate(ModelRig rig, PoseInput input, PoseState state)
    {
        EvaluateBeforePhysics(rig, input, state);
        EvaluateAfterPhysics(rig, state);
    }

    /// <summary>物理の前まで（モーフの値、ボーンの回転・移動、物理後でないボーンの付与と IK）を求める。</summary>
    public static void EvaluateBeforePhysics(ModelRig rig, PoseInput input, PoseState state)
    {
        Array.Clear(state.PhysicsDriven);
        EvaluateMorphWeights(rig, input, state);
        ApplyBoneMorphs(rig, state);

        RigBone[] bones = rig.Bones;
        for (int i = 0; i < bones.Length; i++)
        {
            Quaternion user = input.Rotations[i];
            if (bones[i].FixedAxis is { } axis)
            {
                user = Rotations.Twist(user, axis);
            }
            state.AnimationRotation[i] = Quaternion.Normalize(user * state.MorphRotation[i]);
            state.AnimationTranslation[i] = input.Translations[i] + state.MorphTranslation[i];
            state.IkRotation[i] = Quaternion.Identity;
            state.InheritRotation[i] = Quaternion.Identity;
            state.InheritTranslation[i] = Vector3.Zero;
        }

        foreach (int i in rig.HierarchyOrder)
        {
            UpdateGlobal(rig, state, i);
        }
        SolveDeformOrder(rig, state, 0, rig.AfterPhysicsStart);
    }

    /// <summary>
    /// 物理で置いたボーン（<see cref="PoseState.PhysicsDriven"/>）の子孫のうち、物理で動かないボーンの置き方を、
    /// 親から順に求め直す（髪の先に付いた飾りのボーンなどが、物理で動いた親に付いていくように）。
    /// </summary>
    public static void RefreshPhysicsChildren(ModelRig rig, PoseState state)
    {
        bool[] refreshed = state.Refreshed;
        foreach (int i in rig.HierarchyOrder)
        {
            int parent = rig.Bones[i].Parent;
            if (state.PhysicsDriven[i])
            {
                refreshed[i] = true;
            }
            else if (parent >= 0 && refreshed[parent])
            {
                UpdateGlobal(rig, state, i);
                refreshed[i] = true;
            }
            else
            {
                refreshed[i] = false;
            }
        }
    }

    /// <summary>物理の後（物理後のボーンの付与と IK、頂点を動かす行列、頂点・UV・材質モーフ）を求める。</summary>
    public static void EvaluateAfterPhysics(ModelRig rig, PoseState state)
    {
        SolveDeformOrder(rig, state, rig.AfterPhysicsStart, rig.DeformOrder.Length);
        RigBone[] bones = rig.Bones;
        for (int i = 0; i < bones.Length; i++)
        {
            state.Skinning[i] = Matrix4x4.CreateTranslation(-bones[i].RestPosition) * state.Global[i];
        }

        EvaluateVertexMorphs(rig, state);
        EvaluateMaterialMorphs(rig, state);
    }

    // 変形の順番の first 番目から last の手前までのボーンの、付与と IK を解く
    private static void SolveDeformOrder(ModelRig rig, PoseState state, int first, int last)
    {
        for (int k = first; k < last; k++)
        {
            int i = rig.DeformOrder[k];
            RigBone bone = rig.Bones[i];
            if (bone.HasInherit)
            {
                UpdateInherit(rig, state, i);
                UpdateSubtree(rig, state, i);
            }
            if (bone.IsIk)
            {
                SolveIk(rig, state, i);
            }
        }
    }

    /// <summary>ボーンの先端の位置（表示用）。先端をボーンで指定していればそのボーンの位置。</summary>
    public static Vector3 TailPosition(ModelRig rig, PoseState state, int bone)
    {
        RigBone b = rig.Bones[bone];
        return b.TailBone >= 0 ? state.Global[b.TailBone].Translation : Vector3.Transform(b.TailOffset, state.Global[bone]);
    }

    /// <summary>
    /// モーフの値を求める（PMX 2.1 の決まりの順）。
    /// 1 回目: 番号の順に、フリップモーフ（グループモーフの中のものも）で選んだモーフの値を書き換える。
    /// 2 回目: グループモーフの値 × 割合 を、中のモーフ（フリップモーフは除く）に足す。グループモーフの値は 1 回目の後のもの
    /// （フリップモーフでグループモーフを切り替えられる）。
    /// </summary>
    private static void EvaluateMorphWeights(ModelRig rig, PoseInput input, PoseState state)
    {
        float[] values = state.EffectiveMorphWeights;
        Array.Copy(input.MorphWeights, values, values.Length);
        RigMorph[] morphs = rig.Morphs;
        if (rig.HasFlipMorphs)
        {
            foreach (RigMorph morph in morphs)
            {
                if (morph.Type == PmxMorphType.Flip)
                {
                    ApplyFlip(morph, values[morph.Index], values);
                }
                else if (morph.Type == PmxMorphType.Group && values[morph.Index] != 0)
                {
                    float w = values[morph.Index];
                    foreach (MorphGroupOffset offset in morph.GroupOffsets)
                    {
                        if (morphs[offset.Morph].Type == PmxMorphType.Flip)
                        {
                            ApplyFlip(morphs[offset.Morph], w * offset.Weight, values);
                        }
                    }
                }
            }
        }

        // グループモーフは別のグループモーフを中に持たない（読み込み時に外してある）ので、足している途中でグループの値は変わらない
        foreach (RigMorph morph in morphs)
        {
            float w = values[morph.Index];
            if (morph.Type != PmxMorphType.Group || w == 0)
            {
                continue;
            }
            foreach (MorphGroupOffset offset in morph.GroupOffsets)
            {
                if (morphs[offset.Morph].Type != PmxMorphType.Flip)
                {
                    values[offset.Morph] += w * offset.Weight;
                }
            }
        }
    }

    /// <summary>
    /// フリップモーフ: 値 0〜1 を（選び先の数 + 1）に等しく分け、0 の区切りは「どれも選ばない」、それ以外はその番目の選び先の値を
    /// 登録の値で書き換える（掛け算ではない）。範囲の外は端に寄せる。
    /// </summary>
    public static int FlipIndex(int count, float value)
    {
        if (count <= 0 || !float.IsFinite(value))
        {
            return -1;
        }
        value = Math.Clamp(value, 0f, 1f);
        return Math.Clamp((int)((count + 1) * value) - 1, -1, count - 1);
    }

    private static void ApplyFlip(RigMorph flip, float value, float[] values)
    {
        int index = FlipIndex(flip.FlipOffsets.Length, value);
        if (index >= 0)
        {
            MorphGroupOffset target = flip.FlipOffsets[index];
            values[target.Morph] = target.Weight;
        }
    }

    private static void ApplyBoneMorphs(ModelRig rig, PoseState state)
    {
        Array.Fill(state.MorphRotation, Quaternion.Identity);
        Array.Clear(state.MorphTranslation);
        foreach (RigMorph morph in rig.Morphs)
        {
            float w = state.EffectiveMorphWeights[morph.Index];
            if (morph.Type != PmxMorphType.Bone || w == 0)
            {
                continue;
            }
            foreach (MorphBoneOffset offset in morph.BoneOffsets)
            {
                state.MorphTranslation[offset.Bone] += offset.Translation * w;
                state.MorphRotation[offset.Bone] = Quaternion.Normalize(state.MorphRotation[offset.Bone] * Rotations.Scale(offset.Rotation, w));
            }
        }
    }

    // 付与（ほかのボーンの回転・移動を分けてもらう）。肩キャンセルのように重みが負のこともある
    private static void UpdateInherit(ModelRig rig, PoseState state, int index)
    {
        RigBone bone = rig.Bones[index];
        RigBone source = rig.Bones[bone.InheritSource];
        int s = source.Index;
        if (bone.InheritRotation)
        {
            Quaternion r = bone.InheritLocal || !source.InheritRotation ? state.AnimationRotation[s] : state.InheritRotation[s];
            if (state.PhysicsDriven[s])
            {
                // 物理で置いたボーンからは、物理で決まった回転（親から見た回転）を受け継ぐ（物理後のボーンが物理に付いていくように）
                Quaternion parent = source.Parent >= 0 ? state.GlobalRotation[source.Parent] : Quaternion.Identity;
                r = Quaternion.Normalize(Quaternion.Inverse(parent) * state.GlobalRotation[s]);
            }
            else if (source.IsIkLink)
            {
                r = state.IkRotation[s] * r; // 足 D のように、IK で回されたボーンの回転も受け継ぐ
            }
            state.InheritRotation[index] = Rotations.Scale(r, bone.InheritWeight);
        }
        if (bone.InheritTranslation)
        {
            Vector3 t = bone.InheritLocal || !source.InheritTranslation ? state.AnimationTranslation[s] : state.InheritTranslation[s];
            state.InheritTranslation[index] = t * bone.InheritWeight;
        }
    }

    private static void UpdateGlobal(ModelRig rig, PoseState state, int index)
    {
        RigBone bone = rig.Bones[index];
        Quaternion rotation = state.IkRotation[index] * state.AnimationRotation[index] * state.InheritRotation[index];
        Vector3 translation = bone.RestOffset + state.AnimationTranslation[index] + state.InheritTranslation[index];
        Matrix4x4 local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        if (bone.Parent >= 0)
        {
            state.Global[index] = local * state.Global[bone.Parent];
            state.GlobalRotation[index] = Quaternion.Normalize(state.GlobalRotation[bone.Parent] * rotation);
        }
        else
        {
            state.Global[index] = local;
            state.GlobalRotation[index] = rotation;
        }
    }

    private static void UpdateSubtree(ModelRig rig, PoseState state, int index)
    {
        // 物理で置いたボーンは動かさない（その子は、物理の結果に付いていく）
        if (!state.PhysicsDriven[index])
        {
            UpdateGlobal(rig, state, index);
        }
        foreach (int child in rig.Bones[index].Children)
        {
            UpdateSubtree(rig, state, child);
        }
    }

    private static void SolveIk(ModelRig rig, PoseState state, int ikIndex)
    {
        RigBone ik = rig.Bones[ikIndex];
        IReadOnlyList<RigIkLink> links = ik.IkLinks;
        Span<Vector3> previousAngles = stackalloc Vector3[links.Count];
        Span<float> planeAngles = stackalloc float[links.Count];
        Span<Quaternion> best = stackalloc Quaternion[links.Count];
        previousAngles.Clear();
        planeAngles.Clear();

        foreach (RigIkLink link in links)
        {
            state.IkRotation[link.Bone] = Quaternion.Identity;
        }
        for (int c = links.Count - 1; c >= 0; c--)
        {
            UpdateSubtree(rig, state, links[c].Bone);
        }

        // 最初から届いていれば動かさない（初期の姿勢でひざが勝手に曲がらないように）
        float bestDistance = Vector3.Distance(state.Global[ik.IkTarget].Translation, state.Global[ikIndex].Translation);
        if (bestDistance < IkConvergence)
        {
            return;
        }
        bestDistance = float.MaxValue;
        for (int iteration = 0; iteration < ik.IkLoopCount; iteration++)
        {
            SolveOnce(rig, state, ik, iteration, previousAngles, planeAngles);
            float distance = Vector3.Distance(state.Global[ik.IkTarget].Translation, state.Global[ikIndex].Translation);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                for (int c = 0; c < links.Count; c++)
                {
                    best[c] = state.IkRotation[links[c].Bone];
                }
                if (distance < IkConvergence)
                {
                    break;
                }
            }
            else
            {
                // 前より遠ざかったら、いちばん近かった状態に戻して終える
                for (int c = 0; c < links.Count; c++)
                {
                    state.IkRotation[links[c].Bone] = best[c];
                }
                for (int c = links.Count - 1; c >= 0; c--)
                {
                    UpdateSubtree(rig, state, links[c].Bone);
                }
                break;
            }
        }
    }

    private static void SolveOnce(ModelRig rig, PoseState state, RigBone ik, int iteration, Span<Vector3> previousAngles, Span<float> planeAngles)
    {
        for (int c = 0; c < ik.IkLinks.Count; c++)
        {
            RigIkLink link = ik.IkLinks[c];
            int l = link.Bone;
            if (l == ik.IkTarget)
            {
                continue;
            }
            if (link.SingleAxis >= 0)
            {
                SolvePlane(rig, state, ik, link, iteration, ref planeAngles[c]);
                continue;
            }

            if (!LocalDirections(state, l, state.Global[ik.Index].Translation, state.Global[ik.IkTarget].Translation, out Vector3 toIk, out Vector3 toTarget))
            {
                continue;
            }
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(toTarget, toIk), -1f, 1f));
            if (angle < 1e-5f)
            {
                continue;
            }
            Vector3 axis = Vector3.Cross(toTarget, toIk);
            if (axis.LengthSquared() < 1e-12f)
            {
                continue;
            }
            angle = Math.Min(angle, ik.IkLimitAngle);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
            Quaternion chain = state.IkRotation[l] * state.AnimationRotation[l] * rotation;

            if (link.HasLimits)
            {
                Vector3 euler = Rotations.ToEulerXyz(chain, previousAngles[c]);
                euler = Vector3.Clamp(euler, link.MinAngle, link.MaxAngle);
                Vector3 limit = new(ik.IkLimitAngle);
                euler = Vector3.Clamp(euler - previousAngles[c], -limit, limit) + previousAngles[c];
                chain = Rotations.FromEulerXyz(euler);
                previousAngles[c] = euler;
            }

            state.IkRotation[l] = Quaternion.Normalize(chain * Quaternion.Inverse(state.AnimationRotation[l]));
            UpdateSubtree(rig, state, l);
        }
    }

    // 1 軸だけに制限のあるリンク（ひざなど）は、その軸まわりの角度だけを動かす
    private static void SolvePlane(ModelRig rig, PoseState state, RigBone ik, RigIkLink link, int iteration, ref float planeAngle)
    {
        int l = link.Bone;
        if (!LocalDirections(state, l, state.Global[ik.Index].Translation, state.Global[ik.IkTarget].Translation, out Vector3 toIk, out Vector3 toTarget))
        {
            return;
        }
        Vector3 axis = link.SingleAxis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        float min = link.SingleAxis switch { 0 => link.MinAngle.X, 1 => link.MinAngle.Y, _ => link.MinAngle.Z };
        float max = link.SingleAxis switch { 0 => link.MaxAngle.X, 1 => link.MaxAngle.Y, _ => link.MaxAngle.Z };

        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(toTarget, toIk), -1f, 1f));
        angle = Math.Min(angle, ik.IkLimitAngle);
        float plus = Vector3.Dot(Vector3.Transform(toTarget, Quaternion.CreateFromAxisAngle(axis, angle)), toIk);
        float minus = Vector3.Dot(Vector3.Transform(toTarget, Quaternion.CreateFromAxisAngle(axis, -angle)), toIk);
        float next = planeAngle + (plus > minus ? angle : -angle);

        if (iteration == 0 && (next < min || next > max))
        {
            // 最初の 1 回で制限の外へ向かうなら、逆向きに曲げる（ひざが逆に曲がらないように）
            if (-next > min && -next < max)
            {
                next = -next;
            }
            else
            {
                float half = (min + max) * 0.5f;
                if (MathF.Abs(half - next) > MathF.Abs(half + next))
                {
                    next = -next;
                }
            }
        }
        next = Math.Clamp(next, min, max);
        planeAngle = next;

        state.IkRotation[l] = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, next) * Quaternion.Inverse(state.AnimationRotation[l]));
        UpdateSubtree(rig, state, l);
    }

    // リンクのボーンから見た、IK の位置と目標の位置の向き（リンクの座標で）
    private static bool LocalDirections(PoseState state, int link, Vector3 ikPosition, Vector3 targetPosition, out Vector3 toIk, out Vector3 toTarget)
    {
        toIk = toTarget = default;
        if (!Matrix4x4.Invert(state.Global[link], out Matrix4x4 inverse))
        {
            return false;
        }
        Vector3 ik = Vector3.Transform(ikPosition, inverse);
        Vector3 target = Vector3.Transform(targetPosition, inverse);
        if (ik.LengthSquared() < 1e-12f || target.LengthSquared() < 1e-12f)
        {
            return false;
        }
        toIk = Vector3.Normalize(ik);
        toTarget = Vector3.Normalize(target);
        return true;
    }

    private static void EvaluateVertexMorphs(ModelRig rig, PoseState state)
    {
        // 頂点の数だけある配列なので、前も今も効いているモーフが無ければ消し直さない
        if (state.HasVertexOffsets)
        {
            Array.Clear(state.VertexOffsets);
        }
        if (state.HasUvOffsets)
        {
            Array.Clear(state.UvOffsets);
        }
        state.HasVertexOffsets = state.HasUvOffsets = false;
        foreach (RigMorph morph in rig.Morphs)
        {
            float w = state.EffectiveMorphWeights[morph.Index];
            if (w == 0)
            {
                continue;
            }
            if (morph.Type == PmxMorphType.Vertex && morph.VertexOffsets.Length > 0)
            {
                state.HasVertexOffsets = true;
                foreach (MorphVertexOffset o in morph.VertexOffsets)
                {
                    state.VertexOffsets[o.Vertex] += o.Offset * w;
                }
            }
            else if (morph.Type == PmxMorphType.Uv && morph.UvOffsets.Length > 0)
            {
                state.HasUvOffsets = true;
                foreach (MorphUvOffset o in morph.UvOffsets)
                {
                    state.UvOffsets[o.Vertex] += o.Offset * w;
                }
            }
        }
    }

    // 材質モーフ: 先に乗算をすべて掛け、次に加算をすべて足す
    private static void EvaluateMaterialMorphs(ModelRig rig, PoseState state)
    {
        Array.Copy(rig.BaseMaterials, state.Materials, state.Materials.Length);
        for (int pass = 0; pass < 2; pass++)
        {
            bool add = pass == 1;
            foreach (RigMorph morph in rig.Morphs)
            {
                float w = state.EffectiveMorphWeights[morph.Index];
                if (morph.Type != PmxMorphType.Material || w == 0)
                {
                    continue;
                }
                foreach (MorphMaterialOffset o in morph.MaterialOffsets)
                {
                    if (o.Add != add)
                    {
                        continue;
                    }
                    int first = o.Material < 0 ? 0 : o.Material;
                    int last = o.Material < 0 ? state.Materials.Length - 1 : o.Material;
                    for (int m = first; m <= last; m++)
                    {
                        ref MaterialValues v = ref state.Materials[m];
                        if (add)
                        {
                            v.Diffuse += o.Values.Diffuse * w;
                            v.Specular += o.Values.Specular * w;
                            v.SpecularPower += o.Values.SpecularPower * w;
                            v.Ambient += o.Values.Ambient * w;
                            v.TextureAdd += o.Values.TextureAdd * w;
                            v.SphereAdd += o.Values.SphereAdd * w;
                            v.ToonAdd += o.Values.ToonAdd * w;
                            v.EdgeColor += o.Values.EdgeColor * w;
                            v.EdgeSize += o.Values.EdgeSize * w;
                        }
                        else
                        {
                            v.Diffuse *= Vector4.Lerp(Vector4.One, o.Values.Diffuse, w);
                            v.Specular *= Vector3.Lerp(Vector3.One, o.Values.Specular, w);
                            v.SpecularPower *= 1f + ((o.Values.SpecularPower - 1f) * w);
                            v.Ambient *= Vector3.Lerp(Vector3.One, o.Values.Ambient, w);
                            v.TextureMultiply *= Vector4.Lerp(Vector4.One, o.Values.TextureMultiply, w);
                            v.SphereMultiply *= Vector4.Lerp(Vector4.One, o.Values.SphereMultiply, w);
                            v.ToonMultiply *= Vector4.Lerp(Vector4.One, o.Values.ToonMultiply, w);
                            v.EdgeColor *= Vector4.Lerp(Vector4.One, o.Values.EdgeColor, w);
                            v.EdgeSize *= 1f + ((o.Values.EdgeSize - 1f) * w);
                        }
                    }
                }
            }
        }
    }
}
