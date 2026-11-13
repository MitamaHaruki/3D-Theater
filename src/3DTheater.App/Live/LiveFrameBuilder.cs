using System.Numerics;
using Theater3D.App.Preview;
using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Live;

/// <summary>
/// 配信用の絵の描き方（配信用の画面と仮想カメラで同じにする）。シーンのカメラから見た絵だけを描く（格子・ボーン・ギズモ・
/// 照明の目印・選択の枠は描かない）。背景は、環境の画像を背景に出していなければ、設定の色（初めはグリーンバック）で塗る。
/// 描き方はプレビューの品質（影の細かさ・光線の数）に合わせ、輪郭線の太さと映像効果は絵の高さで決める（書き出しと同じ決め方）。
/// </summary>
internal sealed class LiveFrameBuilder(EditorContext context)
{
    private readonly List<DrawItem> _items = [];
    private readonly ViewCamera _fallback = new(ViewKind.Free);

    public FrameParameters Build(int pixelWidth, int pixelHeight)
    {
        CameraNode? camera = context.MainCamera;
        float aspect = pixelWidth / (float)pixelHeight;
        // シーンのカメラが無いとき（ふつうは無い）は、初めの自由視点から見る
        ViewMatrices matrices = camera is not null ? ViewCamera.SceneCameraMatrices(camera, aspect) : _fallback.GetMatrices(aspect, null, aspect);
        _items.Clear();
        foreach (var (node, model) in context.VisibleModels)
        {
            if (model.Mesh is { } mesh)
            {
                _items.Add(new DrawItem(mesh, node.WorldMatrix, DrawFlags.None));
            }
        }
        var (lightDirection, lightColor) = SceneLighting.From(context.MainLight);
        FrameFlags flags = FrameFlags.UseLight | FrameFlags.ClearColor;
        RenderSettings rendering = context.Rendering;
        if (rendering.SelfShadow)
        {
            flags |= FrameFlags.SelfShadow;
        }
        if (rendering.GroundShadow)
        {
            flags |= FrameFlags.GroundShadow;
        }
        if ((rendering.SelfShadow || rendering.GroundShadow) && rendering.ShadowStyle.RayTraced)
        {
            flags |= FrameFlags.RayTracedShadows;
        }
        FloorReflection reflection = rendering.Reflection;
        if (reflection.Enabled)
        {
            flags |= FrameFlags.FloorReflection;
        }
        SceneEnvironment environment = rendering.Environment;
        NativeEnvironment? environmentImage = context.EnvironmentImage.Current;
        if (environmentImage is not null && environment.ShowBackground)
        {
            flags |= FrameFlags.EnvironmentBackground;
        }
        var clear = new Vector4(context.LiveOutput.Background, 1f);
        QualityProfile quality = context.PreviewQuality.Profile;
        return new FrameParameters(flags, matrices.View, matrices.Projection, Matrix4x4.Identity, Matrix4x4.Identity, _items, default,
            lightDirection, lightColor, SceneLighting.EdgePixelScale(pixelHeight), rendering.ShadowStyle.GroundColor,
            quality.ShadowMapSize, clear, rendering.ToNative(pixelHeight, camera), EffectsMapping.LightsOf(context.Scene),
            reflection.Strength, reflection.Fade, rendering.ShadowStyle.SoftnessTangent, environmentImage, environment.Intensity,
            environment.RotationRadians, ReflectionBlur: reflection.Blur, RaySamples: quality.RaySamples);
    }
}
