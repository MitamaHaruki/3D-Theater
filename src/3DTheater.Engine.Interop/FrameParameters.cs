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
}

/// <summary>
/// 映像効果の値（tt_engine.h の TtFrameParams の post_flags 以降）。使う効果を <see cref="Flags"/> で選ぶ。
/// <see cref="Exposure"/> は段（+1 で 2 倍の明るさ）、<see cref="Contrast"/>・<see cref="Saturation"/> は 1 で変えない、
/// <see cref="Temperature"/> は -1（青く）〜 +1（黄色く）。ブルームは強さ・光り始める明るさ（0〜1）・広がり（0〜1）。
/// <see cref="Vignette"/> は四隅の暗さ（0〜1）。背景・格子・選択の枠・重ね描きの線には、色調補正とブルームはかからない。
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
    float Vignette = 0f)
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
    PostEffects Effects = default);
