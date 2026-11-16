using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>時刻（朝・昼・夕・夜）。</summary>
public enum TimeOfDay
{
    Morning,
    Noon,
    Evening,
    Night,
}

/// <summary>
/// 時刻の明るさと色合い（設計判断記録 87 章）。サンライト（MMD の照明。太陽として扱う）の高さ・色・明るさと、
/// 画面の色合い（映像効果の色調補正）をまとめて変える。太陽の向き（左右）は変えない（窓などに合わせて決めた向きを保つ）。
/// 太陽は影を落とす（セルフシャドウを入れ、サンライトを見えるようにする）。変えた後は、それぞれの欄で手で直せる。
/// </summary>
public static class TimeOfDayPresets
{
    /// <summary>
    /// 1 つの時刻の値。<see cref="Elevation"/> は太陽の高さ（度。照明の下向きの回転）、<see cref="Color"/> は「#RRGGBB」、
    /// 色調補正は <see cref="VisualEffects"/> と同じ決まり（明るさは段、色温度は -1 で青く +1 で黄色く）。
    /// </summary>
    public readonly record struct Preset(string Name, float Elevation, string Color, float Intensity,
        float Exposure, float Contrast, float Saturation, float Temperature)
    {
        /// <summary>色調補正で何も変えない（昼）。</summary>
        public bool IsNeutral => Exposure == 0f && Contrast == 1f && Saturation == 1f && Temperature == 0f;
    }

    /// <summary>時刻の値（昼は MMD の初めの照明と同じ高さ・色・明るさで、色合いを変えない）。</summary>
    public static Preset Of(TimeOfDay time) => time switch
    {
        TimeOfDay.Morning => new("朝", 25f, "#FFE3C8", 0.95f, 0.1f, 0.95f, 0.95f, 0.15f),
        TimeOfDay.Evening => new("夕", 12f, "#FF9E5E", 1.1f, -0.15f, 1.05f, 1.15f, 0.45f),
        TimeOfDay.Night => new("夜", 40f, "#8FA8FF", 0.55f, -0.9f, 1.05f, 0.7f, -0.45f),
        _ => new("昼", SceneLighting.MmdRotation.X, "#FFFFFF", 1f, 0f, 1f, 1f, 0f),
    };

    /// <summary>
    /// <paramref name="sun"/>（サンライト）と画面の色合いを、時刻 <paramref name="time"/> にする（元に戻すは 1 回にまとめる）。
    /// 太陽の向き（左右の回転）は変えない。サンライトを見えるようにし、セルフシャドウを入れる（太陽は影を落とす）。
    /// </summary>
    public static void Apply(EditService edits, RenderSettings rendering, LightNode sun, TimeOfDay time)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(rendering);
        ArgumentNullException.ThrowIfNull(sun);
        Preset preset = Of(time);
        Vector3 color = ImageExport.TryParseColor(preset.Color, out Vector3 parsed) ? parsed : Vector3.One;
        string description = $"時刻を「{preset.Name}」に";
        using (edits.Undo.BeginGroup())
        {
            edits.Change(description, [sun], () =>
            {
                sun.IsVisible = true;
                sun.Rotation = sun.Rotation with { X = preset.Elevation };
                sun.Color = color;
                sun.Intensity = preset.Intensity;
            });
            edits.ChangeSettings(description, rendering, () =>
            {
                rendering.SelfShadow = true;
                rendering.Effects = rendering.Effects with
                {
                    ColorEnabled = !preset.IsNeutral,
                    Exposure = preset.Exposure,
                    Contrast = preset.Contrast,
                    Saturation = preset.Saturation,
                    Temperature = preset.Temperature,
                };
            });
        }
    }
}
