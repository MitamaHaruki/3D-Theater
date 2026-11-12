using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// モデルをほかのモデルのボーンに付ける（MMD の外部親）ときの計算。付けたモデルのワールドでの置き方は
/// 「自分の位置・回転・拡大 × 付けた先のボーンの行列（ボーンのモデルの座標での行列 × 付けた先のモデルのワールドでの置き方）」。
/// </summary>
public static class ModelAttachments
{
    /// <summary>付けた先をたどる回数の上限（付けた先のさらに付けた先…）。</summary>
    public const int MaxDepth = 16;

    /// <summary>付けた先のボーンの、ワールドでの行列。</summary>
    public static Matrix4x4 ParentMatrix(Matrix4x4 boneGlobalInModel, Matrix4x4 parentWorld) => boneGlobalInModel * parentWorld;

    /// <summary>
    /// 見た目（ワールドでの置き方 <paramref name="world"/>）を変えずに、付けた先を <paramref name="newParent"/> にするときの、
    /// 新しい位置・回転（度）・拡大。付けた先を外すときは <paramref name="newParent"/> を単位行列にする。
    /// 縦横で違う拡大の親の下で回したときなど、行列が位置・回転・拡大に分けられなければ、位置だけを合わせる。
    /// </summary>
    public static (Vector3 Position, Vector3 RotationDegrees, Vector3 Scale) KeepWorld(Matrix4x4 world, Matrix4x4 newParent)
    {
        if (!Matrix4x4.Invert(newParent, out Matrix4x4 inverse))
        {
            inverse = Matrix4x4.Identity;
        }
        Matrix4x4 local = world * inverse;
        if (Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
            && float.IsFinite(scale.X) && float.IsFinite(rotation.W))
        {
            return (translation, SceneRotation.ToDegrees(rotation), scale);
        }
        return (local.Translation, Vector3.Zero, Vector3.One);
    }

    /// <summary><paramref name="child"/> を <paramref name="parent"/> に付けると、付けた先をたどって自分に戻る（輪になる）か。</summary>
    public static bool WouldLoop(ModelNode child, ModelNode parent, Func<Guid, ModelNode?> find)
    {
        ModelNode? current = parent;
        for (int depth = 0; current is not null && depth <= MaxDepth; depth++)
        {
            if (ReferenceEquals(current, child))
            {
                return true;
            }
            current = current.Attachment is { } a ? find(a.ModelId) : null;
        }
        return current is not null; // たどりきれないほど深い
    }

    /// <summary>行列の軸の長さのうち、いちばん大きいもの（画面に収めるときの大きさの目安）。</summary>
    public static float MaxAxisScale(Matrix4x4 m) => MathF.Max(
        new Vector3(m.M11, m.M12, m.M13).Length(),
        MathF.Max(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));

    /// <summary>ワールドでの置き方を、位置・回転・縦の拡大に分ける（物理の床と重力に使う）。分けられなければ位置だけ。</summary>
    public static (Vector3 Position, Quaternion Rotation, float ScaleY) Placement(Matrix4x4 world)
    {
        if (Matrix4x4.Decompose(world, out Vector3 scale, out Quaternion rotation, out Vector3 translation) && float.IsFinite(rotation.W))
        {
            return (translation, Quaternion.Normalize(rotation), scale.Y);
        }
        return (world.Translation, Quaternion.Identity, 1f);
    }
}
