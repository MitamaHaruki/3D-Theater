using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Rendering;

/// <summary>映像効果の設定（プロジェクトの値）を、描画エンジンへ渡す形にする。</summary>
public static class EffectsMapping
{
    public static PostEffects ToNative(this VisualEffects effects)
    {
        PostEffectFlags flags = PostEffectFlags.None;
        if (effects.ColorEnabled)
        {
            flags |= PostEffectFlags.Color;
        }
        if (effects.BloomEnabled)
        {
            flags |= PostEffectFlags.Bloom;
        }
        if (effects.VignetteEnabled)
        {
            flags |= PostEffectFlags.Vignette;
        }
        return new PostEffects(flags, effects.Exposure, effects.Contrast, effects.Saturation, effects.Temperature,
            effects.BloomIntensity, effects.BloomThreshold, effects.BloomSpread, effects.Vignette);
    }
}
