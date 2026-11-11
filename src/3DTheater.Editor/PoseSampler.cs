using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 好きなフレームの、モデルのボーン・モーフの値を求める（シーンの値は書き換えない）。物理の焼き込みで、今のフレーム以外の姿勢を求めるのに使う。
/// 決まりは <see cref="AnimationService"/> と同じ: 未登録の変更がある項目とキーの無い項目は今の値、それ以外はキーの値。
/// </summary>
public static class PoseSampler
{
    /// <summary>
    /// <paramref name="frame"/> の値を、ボーン・モーフの番号の位置に書く（番号が範囲外の項目は飛ばす）。
    /// <paramref name="ikEnabled"/> を渡せば、表示・IK の IK の切り替えも、ボーンの番号の位置に書く（IK ボーン以外は true）。
    /// </summary>
    public static void Sample(ModelNode model, float frame, Span<Vector3> translations, Span<Quaternion> rotations, Span<float> weights,
        Span<bool> ikEnabled = default)
    {
        if (!ikEnabled.IsEmpty)
        {
            ikEnabled.Fill(true);
            if (model.Display is { } display)
            {
                IkSwitchSet ik = !display.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(display.Keys, frame, out DisplayKey key)
                    ? key.Ik ?? IkSwitchSet.AllOn
                    : display.Ik;
                foreach (int i in ik.Disabled)
                {
                    if ((uint)i < (uint)ikEnabled.Length)
                    {
                        ikEnabled[i] = false;
                    }
                }
            }
        }
        foreach (BoneNode bone in model.Bones)
        {
            int i = bone.Index;
            if ((uint)i >= (uint)translations.Length || (uint)i >= (uint)rotations.Length)
            {
                continue;
            }
            if (!bone.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(bone.Keys, frame, out Vector3 translation, out Quaternion rotation))
            {
                translations[i] = translation;
                rotations[i] = rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
            }
            else
            {
                translations[i] = bone.Position;
                rotations[i] = bone.Orientation;
            }
        }
        foreach (MorphNode morph in model.Morphs)
        {
            int i = morph.Index;
            if ((uint)i >= (uint)weights.Length)
            {
                continue;
            }
            weights[i] = !morph.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(morph.Keys, frame, out float weight) ? weight : morph.Weight;
        }
    }

    /// <summary>
    /// この変更で、ほかのフレームの姿勢も変わるか（物理の焼き込みをやり直すか）。変わるならそのモデルを返す。
    /// キー・未登録の印の変更と、キーの無い項目・未登録の変更がある項目の値の変更が当たる。キーのある項目の値は、
    /// 再生位置を動かしたときにキーから当てはめたもの（キーから決まる）なので当たらない。モデルの向きは重力の向きを変えるので当たる。
    /// </summary>
    public static ModelNode? ModelAffectedBy(SceneNodeChangedEventArgs e) => e.Node switch
    {
        BoneNode bone when e.PropertyName is nameof(BoneNode.Keys) or nameof(BoneNode.HasUnregisteredChange) => bone.Model,
        BoneNode bone when e.PropertyName is nameof(SceneNode.Position) or nameof(SceneNode.Rotation)
            && (bone.HasUnregisteredChange || bone.Keys.Count == 0) => bone.Model,
        MorphNode morph when e.PropertyName is nameof(MorphNode.Keys) or nameof(MorphNode.HasUnregisteredChange) => morph.Model,
        MorphNode morph when e.PropertyName == nameof(MorphNode.Weight) && (morph.HasUnregisteredChange || morph.Keys.Count == 0) => morph.Model,
        ModelNode model when e.PropertyName == nameof(SceneNode.Rotation) => model,
        ModelDisplayNode display when e.PropertyName is nameof(ModelDisplayNode.Keys) or nameof(ModelDisplayNode.HasUnregisteredChange) => display.Model,
        ModelDisplayNode display when e.PropertyName == nameof(ModelDisplayNode.Ik) && (display.HasUnregisteredChange || display.Keys.Count == 0)
            => display.Model,
        _ => null,
    };
}
