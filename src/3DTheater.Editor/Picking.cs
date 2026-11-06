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

    /// <summary>
    /// 三角形の集まり（<paramref name="world"/> で置いたもの）に視線が当たるか。表裏どちらから当たってもよい。
    /// 当たれば視線の始点から最も近い当たりまでの距離を返す。先に囲む箱で外れを除くので、大きなモデルでも速い。
    /// </summary>
    public static float? IntersectTriangles(
        Ray ray, Matrix4x4 world, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, Vector3 boundsMin, Vector3 boundsMax)
    {
        if (positions.IsEmpty || indices.Length < 3 || !Matrix4x4.Invert(world, out Matrix4x4 toLocal))
        {
            return null;
        }
        Vector3 origin = Vector3.Transform(ray.Origin, toLocal);
        Vector3 direction = Vector3.TransformNormal(ray.Direction, toLocal);
        if (IntersectBox(origin, direction, boundsMin, boundsMax) is null)
        {
            return null;
        }

        float best = float.PositiveInfinity;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length)
            {
                continue;
            }
            float? t = IntersectTriangle(origin, direction, positions[(int)a], positions[(int)b], positions[(int)c]);
            if (t is { } hit && hit < best)
            {
                best = hit;
            }
        }
        if (float.IsPositiveInfinity(best))
        {
            return null;
        }
        Vector3 hitWorld = Vector3.Transform(origin + (direction * best), world);
        return Vector3.Distance(ray.Origin, hitWorld);
    }

    /// <summary>箱に当たる位置（視線のパラメーター t）。外れなら null。</summary>
    internal static float? IntersectBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
    {
        float tMin = float.NegativeInfinity;
        float tMax = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < min[axis] || o > max[axis])
                {
                    return null;
                }
                continue;
            }
            float t1 = (min[axis] - o) / d;
            float t2 = (max[axis] - o) / d;
            tMin = Math.Max(tMin, Math.Min(t1, t2));
            tMax = Math.Min(tMax, Math.Max(t1, t2));
        }
        return tMax < Math.Max(tMin, 0f) ? null : Math.Max(tMin, 0f);
    }

    // Möller–Trumbore 法。表裏は区別しない
    private static float? IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        const float epsilon = 1e-9f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < epsilon)
        {
            return null;
        }
        float inverse = 1f / determinant;
        Vector3 s = origin - a;
        float u = Vector3.Dot(s, p) * inverse;
        if (u is < 0f or > 1f)
        {
            return null;
        }
        Vector3 q = Vector3.Cross(s, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            return null;
        }
        float t = Vector3.Dot(edge2, q) * inverse;
        return t > 0f ? t : null;
    }

    /// <summary>
    /// 3D の点を画面上の点（左上が原点、単位はピクセル）に写す。カメラの後ろにある点は false。
    /// </summary>
    public static bool TryProject(Vector3 world, Matrix4x4 viewProjection, Vector2 viewportSize, out Vector2 screen)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (clip.W <= 1e-6f)
        {
            screen = default;
            return false;
        }
        float x = clip.X / clip.W;
        float y = clip.Y / clip.W;
        screen = new Vector2((x + 1f) * 0.5f * viewportSize.X, (1f - y) * 0.5f * viewportSize.Y);
        return true;
    }

    /// <summary>画面上の点 <paramref name="point"/> から、線分 a–b までの距離（ピクセル）。</summary>
    public static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        float t = lengthSquared < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, a + (ab * t));
    }

    /// <summary>
    /// 画面上の点にいちばん近い線分を選ぶ（ボーンの選択に使う）。<paramref name="tolerance"/> ピクセルより遠ければ -1。
    /// 両端がカメラの後ろにある線分は選ばない。
    /// </summary>
    public static int PickSegment(
        Vector2 point, Vector2 viewportSize, Matrix4x4 viewProjection, IReadOnlyList<(Vector3 A, Vector3 B)> segments, float tolerance)
    {
        int best = -1;
        float bestDistance = tolerance;
        for (int i = 0; i < segments.Count; i++)
        {
            bool hasA = TryProject(segments[i].A, viewProjection, viewportSize, out Vector2 a);
            bool hasB = TryProject(segments[i].B, viewProjection, viewportSize, out Vector2 b);
            if (!hasA && !hasB)
            {
                continue;
            }
            float distance = DistanceToSegment(point, hasA ? a : b, hasB ? b : a);
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static Vector3 Unproject(Vector4 clip, Matrix4x4 inverseViewProjection)
    {
        Vector4 world = Vector4.Transform(clip, inverseViewProjection);
        return new Vector3(world.X, world.Y, world.Z) / world.W;
    }
}
