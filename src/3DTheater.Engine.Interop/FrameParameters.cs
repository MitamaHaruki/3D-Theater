using System.Numerics;

namespace Theater3D.Engine.Interop;

/// <summary>tt_engine.h の TT_FRAME_* と同じ値。</summary>
[Flags]
public enum FrameFlags : uint
{
    None = 0,
    ShowGrid = 1u << 0,
    ShowAxisX = 1u << 1,
    ShowAxisY = 1u << 2,
    ShowAxisZ = 1u << 3,
    ShowTestObject = 1u << 4,
    TestObjectSelected = 1u << 5,

    /// <summary><see cref="FrameParameters.LightDirection"/> と <see cref="FrameParameters.LightColor"/> を使う（無ければ MMD の初期の照明）。</summary>
    UseLight = 1u << 6,

    /// <summary>セルフシャドウ（モデルに落ちる影）を描く。</summary>
    SelfShadow = 1u << 7,

    /// <summary>地面の影（高さ 0 の床に落ちる影）を <see cref="FrameParameters.GroundShadowColor"/> で描く。</summary>
    GroundShadow = 1u << 8,

    /// <summary>格子と座標軸を、奥行きに関係なくいちばん奥に描く（モデルの後ろ。固定視点で使う）。</summary>
    GridBehind = 1u << 9,

    /// <summary>背景を <see cref="FrameParameters.ClearColor"/> で消す（a = 0 で透明。書き出し用）。</summary>
    ClearColor = 1u << 10,

    /// <summary>床の映り込み（<see cref="FrameParameters.ReflectionStrength"/>・<see cref="FrameParameters.ReflectionFade"/>）。</summary>
    FloorReflection = 1u << 11,
}

/// <summary>tt_engine.h の TT_LIGHT_* と同じ値（足す照明の種類）。</summary>
public enum LightSourceKind : uint
{
    /// <summary>平行光。</summary>
    Directional = 0,

    /// <summary>点光源（全方向。届く距離で 0 になる）。</summary>
    Point = 1,

    /// <summary>スポットライト（円すい。届く距離で 0 になる）。</summary>
    Spot = 2,
}

/// <summary>
/// 足す照明（メインの照明とは別。tt_engine.h の TtLight）。<see cref="Direction"/> は光が進む向き、<see cref="Color"/> は色 × 明るさ。
/// <see cref="SpotAngle"/> は円すいの全体の角度（度）、<see cref="SpotSoftness"/> は縁のぼかし（0〜1）。影は落とさない。
/// </summary>
public readonly record struct LightSource(
    LightSourceKind Kind,
    Vector3 Position,
    Vector3 Direction,
    float Range,
    Vector3 Color,
    float SpotAngle = 40f,
    float SpotSoftness = 0.3f)
{
    /// <summary>描画エンジンが 1 枚で使う数の上限（TT_MAX_LIGHTS）。</summary>
    public const int MaxCount = 8;
}

/// <summary>tt_engine.h の TT_POST_* と同じ値（映像効果）。</summary>
[Flags]
public enum PostEffectFlags : uint
{
    None = 0,

    /// <summary>色調補正（明るさ・コントラスト・彩度・色温度）。</summary>
    Color = 1u << 0,

    /// <summary>ブルーム（明るい所が周りへにじむ光）。</summary>
    Bloom = 1u << 1,

    /// <summary>周辺減光（画面の四隅を暗くする）。</summary>
    Vignette = 1u << 2,

    /// <summary>線画（奥行きの段差と面の折れ目に線を引く）。</summary>
    Lines = 1u << 3,

    /// <summary>塗り（明るさの段階を減らす）。</summary>
    Posterize = 1u << 4,

    /// <summary>影の色（暗い所を選んだ色合いに寄せる）。</summary>
    ShadowTint = 1u << 5,

    /// <summary>紙の質感（細かいむらを重ねる。背景にもかかる）。</summary>
    Paper = 1u << 6,

    /// <summary>被写界深度（ピントの距離の前後をぼかす）。</summary>
    DepthOfField = 1u << 7,
}

/// <summary>
/// 映像効果の値（tt_engine.h の TtFrameParams の post_flags 以降）。使う効果を <see cref="Flags"/> で選ぶ。
/// <see cref="Exposure"/> は段（+1 で 2 倍の明るさ）、<see cref="Contrast"/>・<see cref="Saturation"/> は 1 で変えない、
/// <see cref="Temperature"/> は -1（青く）〜 +1（黄色く）。ブルームは強さ・光り始める明るさ（0〜1）・広がり（0〜1）。
/// <see cref="Vignette"/> は四隅の暗さ（0〜1）。背景・格子・選択の枠・重ね描きの線には、色調補正とブルームはかからない。
/// イラスト風: 線画は色・太さ（画素）・濃さ（0〜1）・細かさ（0〜1）、塗りは段数（2〜16）、影の色は色と強さ（0〜1）、
/// 紙の質感は強さ（0〜1）と粒の大きさ（画素）。線画・塗り・影の色は背景にかからない。
/// 被写界深度は、ピントの距離・ピントの合う幅（前後の合計）・ぼけの最大（画素）・最大になるまでの距離（カメラから見る向きの距離）。
/// </summary>
public readonly record struct PostEffects(
    PostEffectFlags Flags,
    float Exposure = 0f,
    float Contrast = 1f,
    float Saturation = 1f,
    float Temperature = 0f,
    float BloomIntensity = 0f,
    float BloomThreshold = 0.8f,
    float BloomSpread = 0.7f,
    float Vignette = 0f,
    Vector3 LineColor = default,
    float LineWidth = 1f,
    float LineStrength = 1f,
    float LineDetail = 0.5f,
    int PosterizeLevels = 4,
    Vector3 ShadowTint = default,
    float ShadowTintStrength = 0f,
    float PaperStrength = 0f,
    float PaperGrain = 2f,
    float DofFocusDistance = 45f,
    float DofFocusRange = 4f,
    float DofMaxBlur = 0f,
    float DofTransition = 20f)
{
    /// <summary>映像効果なし。</summary>
    public static PostEffects None => new(PostEffectFlags.None);
}

/// <summary>
/// 1 つのビューを 1 枚描くための条件。行列は System.Numerics と同じ「行ベクトル × 行列」で、左手系・Y が上。
/// <see cref="OverlayLines"/> はモデルより手前に描く線（ボーンの表示など。2 つの頂点で 1 本）。配列を使い回せるよう範囲で渡す。
/// 照明（<see cref="LightDirection"/> は光が進む向き）は <see cref="FrameFlags.UseLight"/> のときだけ使う。
/// <see cref="EdgePixelScale"/> は材質の輪郭線の太さ 1 を何ピクセルにするか（0 なら輪郭線を描かない）。
/// <see cref="ShadowMapSize"/> はセルフシャドウの奥行きの画像の 1 辺（0 なら 2048）。
/// <see cref="ClearColor"/> は <see cref="FrameFlags.ClearColor"/> のときの背景の色。透明で描くと、読み出した色は透明度をかけ合わせた値になる。
/// </summary>
public readonly record struct FrameParameters(
    FrameFlags Flags,
    Matrix4x4 View,
    Matrix4x4 Projection,
    Matrix4x4 GridWorld,
    Matrix4x4 TestObjectWorld,
    IReadOnlyList<DrawItem>? Items = null,
    ReadOnlyMemory<LineVertex> OverlayLines = default,
    Vector3 LightDirection = default,
    Vector3 LightColor = default,
    float EdgePixelScale = 0,
    Vector4 GroundShadowColor = default,
    int ShadowMapSize = 0,
    Vector4 ClearColor = default,
    PostEffects Effects = default,
    IReadOnlyList<LightSource>? Lights = null,
    float ReflectionStrength = 0f,
    float ReflectionFade = 10f);
