using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 足した照明の目印（プレビューに重ねる線。書き出しには映らない）の形と、クリックでの当たり判定。描くときと選ぶときで同じ形を使う。
/// - 点光源: 小さな球（3 つの輪）
/// - スポットライト: 小さな球と、光の向きへの円すい（長さは届く距離か 6 の短いほう）
/// - 平行光: 光の向きへの矢印（根元に小さな輪）
/// </summary>
public static class LightMarkers
{
    public const int Segments = 20;
    public const float PointRadius = 0.6f;
    public const float SpotRadius = PointRadius * 0.6f;
    public const float ArrowLength = 4f;

    /// <summary>照明 1 つの目印の線（ワールドの座標の線分の並び）。</summary>
    public static List<(Vector3 A, Vector3 B)> Lines(LightNode light)
    {
        var lines = new List<(Vector3, Vector3)>();
        Vector3 position = light.Position;
        Vector3 direction = SceneLighting.DirectionOf(light.Rotation);
        switch (light.Type)
        {
            case LightType.Point:
                Sphere(lines, position, PointRadius);
                break;
            case LightType.Spot:
                Sphere(lines, position, SpotRadius);
                Cone(lines, position, direction, Math.Min(light.Range, 6f), light.SpotAngle);
                break;
            default:
                Arrow(lines, position, direction, ArrowLength);
                break;
        }
        return lines;
    }

    /// <summary>目印の球の半径（点光源・スポットライト。平行光は 0）。球の内側をクリックしても選べるようにするため。</summary>
    public static float SphereRadius(LightNode light) => light.Type switch
    {
        LightType.Point => PointRadius,
        LightType.Spot => SpotRadius,
        _ => 0f,
    };

    /// <summary>
    /// 画面上の点 <paramref name="point"/> でクリックした照明（<paramref name="lights"/> の番号）。目印の線から
    /// <paramref name="tolerance"/> ピクセル以内か、球の内側なら当たり。いくつも当たれば、いちばん近いもの。当たらなければ -1。
    /// カメラの後ろにある所は当たらない。
    /// </summary>
    public static int Pick(Vector2 point, Vector2 viewportSize, Matrix4x4 viewProjection, IReadOnlyList<LightNode> lights, float tolerance)
    {
        int best = -1;
        float bestDistance = tolerance;
        for (int i = 0; i < lights.Count; i++)
        {
            float distance = Distance(point, viewportSize, viewProjection, lights[i]);
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    // 画面上で、点から目印までの距離（ピクセル。球の内側は 0。見えなければ無限大）
    private static float Distance(Vector2 point, Vector2 viewportSize, Matrix4x4 viewProjection, LightNode light)
    {
        float best = float.PositiveInfinity;
        foreach (var (a, b) in Lines(light))
        {
            bool hasA = Picking.TryProject(a, viewProjection, viewportSize, out Vector2 sa);
            bool hasB = Picking.TryProject(b, viewProjection, viewportSize, out Vector2 sb);
            if (!hasA && !hasB)
            {
                continue;
            }
            best = Math.Min(best, Picking.DistanceToSegment(point, hasA ? sa : sb, hasB ? sb : sa));
        }
        float radius = SphereRadius(light);
        if (radius > 0f && Picking.TryProject(light.Position, viewProjection, viewportSize, out Vector2 center))
        {
            // 画面上の球の半径: 3 つの軸の向きにずらした点のうち、いちばん離れて写るもの
            float projected = 0f;
            foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                if (Picking.TryProject(light.Position + axis * radius, viewProjection, viewportSize, out Vector2 edge))
                {
                    projected = Math.Max(projected, Vector2.Distance(center, edge));
                }
            }
            best = Math.Min(best, Math.Max(0f, Vector2.Distance(point, center) - projected));
        }
        return best;
    }

    private static void Sphere(List<(Vector3, Vector3)> lines, Vector3 center, float radius)
    {
        Circle(lines, center, Vector3.UnitX, Vector3.UnitY, radius);
        Circle(lines, center, Vector3.UnitY, Vector3.UnitZ, radius);
        Circle(lines, center, Vector3.UnitZ, Vector3.UnitX, radius);
    }

    private static void Circle(List<(Vector3, Vector3)> lines, Vector3 center, Vector3 u, Vector3 v, float radius)
    {
        Vector3 previous = center + u * radius;
        for (int i = 1; i <= Segments; i++)
        {
            float angle = i * MathF.Tau / Segments;
            Vector3 next = center + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
            lines.Add((previous, next));
            previous = next;
        }
    }

    private static void Cone(List<(Vector3, Vector3)> lines, Vector3 apex, Vector3 direction, float length, float angle)
    {
        (Vector3 u, Vector3 v) = Basis(direction);
        float radius = length * MathF.Tan(Math.Clamp(angle, 1f, 179f) * 0.5f * MathF.PI / 180f);
        Vector3 center = apex + direction * length;
        Circle(lines, center, u, v, radius);
        foreach (Vector3 side in new[] { u, -u, v, -v })
        {
            lines.Add((apex, center + side * radius));
        }
    }

    private static void Arrow(List<(Vector3, Vector3)> lines, Vector3 start, Vector3 direction, float length)
    {
        (Vector3 u, Vector3 v) = Basis(direction);
        Vector3 tip = start + direction * length;
        lines.Add((start, tip));
        Vector3 back = tip - direction * (length * 0.2f);
        foreach (Vector3 side in new[] { u, -u, v, -v })
        {
            lines.Add((tip, back + side * (length * 0.1f)));
        }
        Circle(lines, start, u, v, length * 0.08f);
    }

    /// <summary><paramref name="direction"/> に直交する 2 つの向き。</summary>
    private static (Vector3 U, Vector3 V) Basis(Vector3 direction)
    {
        Vector3 helper = MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(helper, direction));
        Vector3 v = Vector3.Cross(direction, u);
        return (u, v);
    }
}
