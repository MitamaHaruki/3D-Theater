using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// あるフレームでのシーンの見え方を求める。プレビューと（将来の）書き出しが同じ計算を使うための入口。
/// Phase 1 では確認用の立方体だけを扱う。
/// </summary>
public static class SceneEvaluator
{
    /// <summary>確認用の立方体が 1 フレームで回る角度（120 フレームで 1 周）。</summary>
    public const float TestObjectDegreesPerFrame = 3f;

    public static Matrix4x4 TestObjectWorld(TestObjectNode node, int frame)
    {
        Matrix4x4 local = node.GetLocalMatrix();
        if (!node.SpinWithFrame)
        {
            return local;
        }
        float spin = frame * TestObjectDegreesPerFrame * MathF.PI / 180f;
        Vector3 radians = node.Rotation * (MathF.PI / 180f);
        return Matrix4x4.CreateScale(node.Scale)
            * Matrix4x4.CreateFromYawPitchRoll(radians.Y + spin, radians.X, radians.Z)
            * Matrix4x4.CreateTranslation(node.Position);
    }

    /// <summary>物を画面に収めるときに使う、おおよその球（中心と半径）。</summary>
    public static (Vector3 Center, float Radius) BoundingSphere(SceneNode node)
    {
        float maxScale = Math.Max(Math.Abs(node.Scale.X), Math.Max(Math.Abs(node.Scale.Y), Math.Abs(node.Scale.Z)));
        return node.Kind == SceneNodeKind.TestObject
            ? (node.Position, 0.87f * Math.Max(maxScale, 0.01f)) // 1 辺 1 の立方体の対角線の半分
            : (node.Position, 1f);
    }
}
