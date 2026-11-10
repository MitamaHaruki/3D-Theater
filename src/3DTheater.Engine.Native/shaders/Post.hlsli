// 映像効果（色調補正・ブルーム・周辺減光・イラスト風）で共通の宣言。
// 画面全体を覆う 1 枚の三角形を描き、画像から画像へ処理する。色は「透明度をかけ合わせた値」（背景を透明にして描いた場面）。

cbuffer PostConstants : register(b0)
{
    float4 SourceTexel;      // xy = 読む画像の 1 画素の大きさ（UV）、zw = 書く画像の幅と高さ（画素）
    float4 BloomParams;      // x = 光り始める明るさ、y = 境目のやわらかさ、z = 広がり、w = 強さ
    float4 GradeParams;      // x = 明るさの倍率、y = コントラスト、z = 彩度、w = 周辺減光の強さ
    float4 WhiteBalance;     // rgb = 色温度の倍率
    float4 BackgroundColor;  // 場面の後ろに置く背景（透明度をかけていない値。a = 0 なら透明のまま）
    uint4 PostFlags;         // x = 使う効果（TT_POST_* と同じ）
    float4 LineParams;       // x = 線画の太さ（画素）、y = 濃さ、z = 段差と見なす奥行きの差の割合、w = 折れ目と見なす曲がりの割合
    float4 LineColor;        // rgb = 線画の色
    float4 PaintParams;      // x = 塗りの段数、y = 影の色の強さ、z = 紙の質感の強さ、w = 紙の粒の大きさ（画素）
    float4 ShadowTint;       // rgb = 影の色（明るさを 1 にそろえたもの）
    float4 ProjectionParams; // x = 投影の (3,3)、y = 投影の (4,3)、z = 1 なら平行投影
    float4 DofParams;        // 被写界深度: x = ピントの距離、y = ピントの合う幅の半分、z = ぼけの最大（画素）、w = 最大になるまでの距離
    float4 AoParams;         // 環境光の遮り: x = 強さ、y = 広がり（ワールドの長さ）、z = 広がりの上限（画素）
    float4 AoProjection;     // 画面の位置を見る向きの座標に戻す値: x = 投影の (1,1)、y = (2,2)、zw = 中心のずれ
};

Texture2D Source : register(t0);
Texture2D Second : register(t1);
Texture2D<float2> LinearDepth : register(t2); // x = 見る向きの距離（何も無い所はとても大きい値）、y = 面の上でまっすぐ変わる値
SamplerState LinearClamp : register(s0);

struct PostVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

static const uint kPostColor = 1;
static const uint kPostBloom = 2;
static const uint kPostVignette = 4;
static const uint kPostLines = 8;
static const uint kPostPosterize = 16;
static const uint kPostShadowTint = 32;
static const uint kPostPaper = 64;
static const uint kPostDof = 128;
static const uint kPostAo = 256;

// 何も描かれていない所の距離
static const float kNothing = 1e30;
