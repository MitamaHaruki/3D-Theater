using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Rendering;

/// <summary>映像効果とイラスト風の表現の設定（プロジェクトの値）と足した照明を、描画エンジンへ渡す形にする。</summary>
public static class EffectsMapping
{
    /// <summary>シーンの足した照明（見えているもの）。</summary>
    public static LightSource[] LightsOf(Domain.SceneDocument scene) =>
        [.. SceneLighting.AddedLights(scene).Select(l => new LightSource(
            l.Type switch
            {
                Domain.LightType.Point => LightSourceKind.Point,
                Domain.LightType.Spot => LightSourceKind.Spot,
                _ => LightSourceKind.Directional,
            },
            l.Position, l.Direction, l.Range, l.Color, l.SpotAngle, l.SpotSoftness))];

    /// <summary>
    /// 高さ <paramref name="pixelHeight"/> の画像を描くときの値。線画の太さは輪郭線と同じく高さ 1080 までは指定の画素のまま、
    /// それより大きい画像では比例して太くする。紙の粒は高さに比例させる（小さなプレビューでも書き出しと同じ見え方に）。
    /// </summary>
    public static PostEffects ToNative(this RenderSettings settings, int pixelHeight)
    {
        VisualEffects effects = settings.Effects;
        IllustrationStyle style = settings.Illustration;
        PostEffectFlags flags = PostEffectFlags.None;
        Add(effects.ColorEnabled, PostEffectFlags.Color);
        Add(effects.BloomEnabled, PostEffectFlags.Bloom);
        Add(effects.VignetteEnabled, PostEffectFlags.Vignette);
        Add(style.LinesEnabled, PostEffectFlags.Lines);
        Add(style.PaintEnabled, PostEffectFlags.Posterize);
        Add(style.ShadowTintEnabled, PostEffectFlags.ShadowTint);
        Add(style.PaperEnabled, PostEffectFlags.Paper);

        float heightScale = Math.Max(1, pixelHeight) / 1080f;
        return new PostEffects(flags, effects.Exposure, effects.Contrast, effects.Saturation, effects.Temperature,
            effects.BloomIntensity, effects.BloomThreshold, effects.BloomSpread, effects.Vignette,
            LineColor: IllustrationStyle.ColorOf(style.LineColor),
            LineWidth: style.LineWidth * SceneLighting.EdgePixelScale(pixelHeight),
            LineStrength: style.LineStrength,
            LineDetail: style.LineDetail,
            PosterizeLevels: style.PaintLevels,
            ShadowTint: IllustrationStyle.ColorOf(style.ShadowColor),
            ShadowTintStrength: style.ShadowTintStrength,
            PaperStrength: style.PaperStrength,
            PaperGrain: Math.Max(0.5f, style.PaperGrain * heightScale));

        void Add(bool enabled, PostEffectFlags flag)
        {
            if (enabled)
            {
                flags |= flag;
            }
        }
    }
}
