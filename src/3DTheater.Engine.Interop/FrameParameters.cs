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
}

/// <summary>
/// 1 つのビューを 1 枚描くための条件。行列は System.Numerics と同じ「行ベクトル × 行列」で、左手系・Y が上。
/// <see cref="OverlayLines"/> はモデルより手前に描く線（ボーンの表示など。2 つの頂点で 1 本）。配列を使い回せるよう範囲で渡す。
/// 照明（<see cref="LightDirection"/> は光が進む向き）は <see cref="FrameFlags.UseLight"/> のときだけ使う。
/// <see cref="EdgePixelScale"/> は材質の輪郭線の太さ 1 を何ピクセルにするか（0 なら輪郭線を描かない）。
/// <see cref="ShadowMapSize"/> はセルフシャドウの奥行きの画像の 1 辺（0 なら 2048）。
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
    int ShadowMapSize = 0);
