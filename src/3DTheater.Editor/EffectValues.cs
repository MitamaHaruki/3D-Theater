using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 映像効果のキーで動かす値（<see cref="EffectPose"/>）と、作品の描き方の設定（<see cref="RenderSettings"/>）の行き来。
/// 使うか（ON / OFF）と、キーで動かさない値（環境光の遮り・画面の中の反射・霧の高さなど）は、設定のまま変えない。
/// </summary>
public static class EffectValues
{
    /// <summary>設定の今の値。</summary>
    public static EffectPose Capture(RenderSettings settings)
    {
        VisualEffects e = settings.Effects;
        SceneFog f = settings.Fog;
        return new EffectPose(e.Exposure, e.Contrast, e.Saturation, e.Temperature, e.BloomIntensity, e.BloomThreshold, e.BloomSpread,
            e.Vignette, e.DofFocusDistance, e.DofFocusRange, e.DofBlur, f.Start, f.Visibility, f.ColorValue);
    }

    /// <summary>値を設定へ入れる（範囲は設定の決まりで収める。霧の色は #RRGGBB に丸める）。</summary>
    public static void Apply(RenderSettings settings, EffectPose pose)
    {
        settings.Effects = settings.Effects with
        {
            Exposure = pose.Exposure,
            Contrast = pose.Contrast,
            Saturation = pose.Saturation,
            Temperature = pose.Temperature,
            BloomIntensity = pose.BloomIntensity,
            BloomThreshold = pose.BloomThreshold,
            BloomSpread = pose.BloomSpread,
            Vignette = pose.Vignette,
            DofFocusDistance = pose.DofFocusDistance,
            DofFocusRange = pose.DofFocusRange,
            DofBlur = pose.DofBlur,
        };
        settings.Fog = settings.Fog with
        {
            Start = pose.FogStart,
            Visibility = pose.FogVisibility,
            Color = ImageExport.FormatColor(pose.FogColor),
        };
    }
}
