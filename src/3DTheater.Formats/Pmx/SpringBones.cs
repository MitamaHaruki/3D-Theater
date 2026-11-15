using System.Numerics;

namespace Theater3D.Formats.Pmx;

/// <summary>
/// VRM の揺れ物（スプリングボーン）の設定（設計判断記録 82 章の 4 段目）。PMX には無いので、VRM を読んだときだけ <see cref="PmxDocument.SpringBones"/> に持たせる。
/// 位置はモデルの座標（PMX と同じ。初めの姿勢）。強さ（<see cref="SpringBoneJoint.Stiffness"/>）と重力（<see cref="SpringBoneJoint.GravityPower"/>）は VRM の値のままで、
/// 計算するときに <see cref="LengthScale"/> を掛ける（VRM は 1 m を 1 として決めた値なので、長さを MMD の大きさにした分だけ大きくする）。
/// </summary>
/// <param name="Joints">揺れるボーン（根元が先）。</param>
/// <param name="Colliders">揺れるボーンが当たる物（体に付いた球とカプセル）。</param>
/// <param name="LengthScale">VRM の 1 m が、モデルの長さでいくつか。</param>
public sealed record SpringBoneSet(IReadOnlyList<SpringBoneJoint> Joints, IReadOnlyList<SpringBoneCollider> Colliders, float LengthScale);

/// <summary>
/// 揺れるボーン 1 本。<paramref name="Bone"/> の向きを、先端（<paramref name="Tail"/>。初めの姿勢の位置）が揺れた所を向くように回す。
/// </summary>
/// <param name="Stiffness">元の向きへ戻ろうとする強さ（VRM の stiffness）。</param>
/// <param name="GravityPower">重力の強さ（VRM の gravityPower。0 なら重力で垂れない）。</param>
/// <param name="GravityDirection">重力の向き（モデルの座標）。</param>
/// <param name="DragForce">動きを弱める割合（0〜1。VRM の dragForce）。</param>
/// <param name="HitRadius">先端の当たりの半径（モデルの長さ）。</param>
/// <param name="Colliders">当たる物（<see cref="SpringBoneSet.Colliders"/> の番号）。</param>
/// <param name="Chain">どの揺れ物か（名前。レポートと確かめのため）。</param>
public sealed record SpringBoneJoint(
    int Bone,
    Vector3 Tail,
    float Stiffness,
    float GravityPower,
    Vector3 GravityDirection,
    float DragForce,
    float HitRadius,
    IReadOnlyList<int> Colliders,
    string Chain);

/// <summary>揺れるボーンが当たる物。球か、<paramref name="Tail"/> があればカプセル（中心から Tail までの線の周り）。位置は初めの姿勢のモデルの座標。</summary>
public sealed record SpringBoneCollider(int Bone, Vector3 Center, float Radius, Vector3? Tail = null);
