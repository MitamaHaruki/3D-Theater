using System.Numerics;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Preview;

/// <summary>
/// 「照明を追加」で足した照明の目印を、プレビューに線で重ねる（書き出しには映らない）。
/// - 点光源: 小さな球（3 つの輪）
/// - スポットライト: 小さな球と、光の向きへの円すい（長さは届く距離か 6 の短いほう）
/// - 平行光: 光の向きへの矢印
/// 線は照明の色で描き（暗い色でも見えるよう白を混ぜる）、選んでいる照明は黄色にする。隠した照明は描かない。
/// </summary>
internal static class LightOverlay
{
    private const int Segments = 20;
    private const float MarkerRadius = 0.6f;
    private static readonly Vector4 SelectedColor = new(1f, 0.9f, 0.25f, 1f);

    public static void AppendTo(LineBuffer lines, EditorContext context)
    {
        foreach (LightNode light in context.Scene.AddedLights)
        {
            if (!light.IsEffectivelyVisible)
            {
                continue;
            }
            Vector4 color = context.Selection.Contains(light)
                ? SelectedColor
                : new Vector4(Vector3.Lerp(light.Color, Vector3.One, 0.45f), 1f);
            Vector3 position = light.Position;
            Vector3 direction = SceneLighting.DirectionOf(light.Rotation);
            switch (light.Type)
            {
                case LightType.Point:
                    Sphere(lines, position, MarkerRadius, color);
                    break;
                case LightType.Spot:
                    Sphere(lines, position, MarkerRadius * 0.6f, color);
                    Cone(lines, position, direction, Math.Min(light.Range, 6f), light.SpotAngle, color);
                    break;
                default:
                    Arrow(lines, position, direction, 4f, color);
                    break;
            }
        }
    }

    private static void Sphere(LineBuffer lines, Vector3 center, float radius, Vector4 color)
    {
        Circle(lines, center, Vector3.UnitX, Vector3.UnitY, radius, color);
        Circle(lines, center, Vector3.UnitY, Vector3.UnitZ, radius, color);
        Circle(lines, center, Vector3.UnitZ, Vector3.UnitX, radius, color);
    }

    private static void Circle(LineBuffer lines, Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color)
    {
        Vector3 previous = center + u * radius;
        for (int i = 1; i <= Segments; i++)
        {
            float angle = i * MathF.Tau / Segments;
            Vector3 next = center + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
            lines.Add(previous, next, color);
            previous = next;
        }
    }

    private static void Cone(LineBuffer lines, Vector3 apex, Vector3 direction, float length, float angle, Vector4 color)
    {
        (Vector3 u, Vector3 v) = Basis(direction);
        float radius = length * MathF.Tan(Math.Clamp(angle, 1f, 179f) * 0.5f * MathF.PI / 180f);
        Vector3 center = apex + direction * length;
        Circle(lines, center, u, v, radius, color);
        foreach (Vector3 side in new[] { u, -u, v, -v })
        {
            lines.Add(apex, center + side * radius, color);
        }
    }

    private static void Arrow(LineBuffer lines, Vector3 start, Vector3 direction, float length, Vector4 color)
    {
        (Vector3 u, Vector3 v) = Basis(direction);
        Vector3 tip = start + direction * length;
        lines.Add(start, tip, color);
        Vector3 back = tip - direction * (length * 0.2f);
        foreach (Vector3 side in new[] { u, -u, v, -v })
        {
            lines.Add(tip, back + side * (length * 0.1f), color);
        }
        Circle(lines, start, u, v, length * 0.08f, color);
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
