using System.Numerics;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Preview;

/// <summary>
/// 「照明を追加」で足した照明の目印を、プレビューに線で重ねる（書き出しには映らない）。形は <see cref="LightMarkers"/>
/// （クリックで選ぶときと同じ形）。線は照明の色で描き（暗い色でも見えるよう白を混ぜる）、選んでいる照明は黄色にする。隠した照明は描かない。
/// </summary>
internal static class LightOverlay
{
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
            foreach (var (a, b) in LightMarkers.Lines(light))
            {
                lines.Add(a, b, color);
            }
        }
    }
}
