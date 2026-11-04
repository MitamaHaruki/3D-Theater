using System.Numerics;

namespace Theater3D.Editor;

public readonly record struct Ray(Vector3 Origin, Vector3 Direction);

/// <summary>画面上の点から 3D の物を選ぶための計算。</summary>
public static class Picking
{
    /// <summary>画面上の点（左上が原点、単位はピクセル）を通る視線を作る。</summary>
    public static Ray CreateRay(Vector2 point, Vector2 viewportSize, Matrix4x4 view, Matrix4x4 projection)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f || !Matrix4x4.Invert(view * projection, out Matrix4x4 inverse))
        {
            return new Ray(Vector3.Zero, Vector3.UnitZ);
        }
        float x = (2f * point.X / viewportSize.X) - 1f;
        float y = 1f - (2f * point.Y / viewportSize.Y);
        Vector3 near = Unproject(new Vector4(x, y, 0f, 1f), inverse);
        Vector3 far = Unproject(new Vector4(x, y, 1f, 1f), inverse);
        return new Ray(near, Vector3.Normalize(far - near));
    }

    /// <summary>
    /// 原点中心・1 辺 1 の立方体を <paramref name="world"/> で置いたものに視線が当たるか。
    /// 当たれば視線の始点からの距離を返す。
    /// </summary>
    public static float? IntersectUnitCube(Ray ray, Matrix4x4 world)
    {
        if (!Matrix4x4.Invert(world, out Matrix4x4 toLocal))
        {
            return null;
        }
        Vector3 origin = Vector3.Transform(ray.Origin, toLocal);
        Vector3 direction = Vector3.TransformNormal(ray.Direction, toLocal);

        // 各軸の板（スラブ）に入る・出る位置を重ねる
        float tMin = float.NegativeInfinity;
        float tMax = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < -0.5f || o > 0.5f)
                {
                    return null;
                }
                continue;
            }
            float t1 = (-0.5f - o) / d;
            float t2 = (0.5f - o) / d;
            tMin = Math.Max(tMin, Math.Min(t1, t2));
            tMax = Math.Min(tMax, Math.Max(t1, t2));
        }
        if (tMax < Math.Max(tMin, 0f))
        {
            return null;
        }
        float t = tMin >= 0f ? tMin : tMax;
        // 局所空間で求めた t は、変換前の視線の長さに直す
        Vector3 hitWorld = Vector3.Transform(origin + (direction * t), world);
        return Vector3.Distance(ray.Origin, hitWorld);
    }

    private static Vector3 Unproject(Vector4 clip, Matrix4x4 inverseViewProjection)
    {
        Vector4 world = Vector4.Transform(clip, inverseViewProjection);
        return new Vector3(world.X, world.Y, world.Z) / world.W;
    }
}
