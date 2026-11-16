using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Rendering;

/// <summary>映像効果・イラスト風の表現・霧の設定（プロジェクトの値）と足した照明を、描画エンジンへ渡す形にする。</summary>
public static class EffectsMapping
{
    /// <summary>
    /// 影のフラグ（セルフシャドウ・地面の影・レイトレーシング）。描画の設定に、見えている照明だけが影を落とす決まり（<see cref="SceneLighting.Shadows"/>。
    /// 設計判断記録 86 章）を合わせる。プレビュー・配信の絵・書き出し・レンダーパスで同じにする。
    /// </summary>
    public static FrameFlags ShadowFlags(Domain.SceneDocument scene, Domain.LightNode? mainLight, RenderSettings rendering)
    {
        var (self, ground) = SceneLighting.Shadows(scene, mainLight, rendering.SelfShadow, rendering.GroundShadow, rendering.ShadowStyle.RayTraced);
        FrameFlags flags = FrameFlags.None;
        if (self)
        {
            flags |= FrameFlags.SelfShadow;
        }
        if (ground)
        {
            flags |= FrameFlags.GroundShadow;
        }
        if ((self || ground) && rendering.ShadowStyle.RayTraced)
        {
            flags |= FrameFlags.RayTracedShadows; // セルフシャドウと地面の影の両方
        }
        if (self && !SceneLighting.MainLightCastsShadow(mainLight))
        {
            flags |= FrameFlags.NoLightShadow; // 隠した MMD の照明は影を落とさない（足した照明の影だけ）
        }
        return flags;
    }

    /// <summary>シーンの足した照明（見えているもの）。</summary>
    public static LightSource[] LightsOf(Domain.SceneDocument scene) =>
        [.. SceneLighting.AddedLights(scene).Select(l => new LightSource(
            l.Type switch
            {
                Domain.LightType.Point => LightSourceKind.Point,
                Domain.LightType.Spot => LightSourceKind.Spot,
                _ => LightSourceKind.Directional,
            },
            l.Position, l.Direction, l.Range, l.Color, l.SpotAngle, l.SpotSoftness, l.CastsShadow))];

    /// <summary>
    /// 高さ <paramref name="pixelHeight"/> の画像を描くときの値。線画の太さは輪郭線と同じく高さ 1080 までは指定の画素のまま、
    /// それより大きい画像では比例して太くする。紙の粒と被写界深度のぼけは高さに比例させる（小さなプレビューでも書き出しと同じ見え方に）。
    /// 被写界深度は、シーンのカメラから描くとき（<paramref name="sceneCamera"/> が null でない）だけかける。
    /// </summary>
    public static PostEffects ToNative(this RenderSettings settings, int pixelHeight, Domain.CameraNode? sceneCamera = null)
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
        Add(effects.DofEnabled && sceneCamera is not null, PostEffectFlags.DepthOfField);
        Add(effects.AoEnabled, PostEffectFlags.AmbientOcclusion);
        Add(effects.AoEnabled && effects.AoRayTracing, PostEffectFlags.RayTracedAmbientOcclusion);
        Add(effects.SsrEnabled, PostEffectFlags.ScreenReflections);
        Add(effects.SsrEnabled && effects.SsrRayTracing, PostEffectFlags.RayTracedReflections);
        SceneFog fog = settings.Fog;
        Add(fog.Enabled, PostEffectFlags.Fog);

        float heightScale = Math.Max(1, pixelHeight) / 1080f;
        // 平行投影で注視点の側へ向きを回したカメラ（距離が負）では、注視点までの見る向きの距離は距離の大きさ
        float focus = effects.FocusDistanceFor(sceneCamera is null ? effects.DofFocusDistance
            : sceneCamera.Orthographic ? MathF.Abs(sceneCamera.TargetDistance) : sceneCamera.TargetDistance);
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
            PaperGrain: Math.Max(0.5f, style.PaperGrain * heightScale),
            DofFocusDistance: focus,
            DofFocusRange: effects.DofFocusRange,
            DofMaxBlur: effects.DofBlur * heightScale,
            DofTransition: VisualEffects.DofTransitionFor(focus),
            AoStrength: effects.AoStrength,
            AoRadius: effects.AoRadius,
            FogColor: fog.ColorValue,
            FogStart: fog.Start,
            FogDensity: fog.Density,
            FogHeight: fog.HeightScale,
            SsrStrength: effects.SsrStrength,
            SsrMaxDistance: effects.SsrDistance);

        void Add(bool enabled, PostEffectFlags flag)
        {
            if (enabled)
            {
                flags |= flag;
            }
        }
    }
}
