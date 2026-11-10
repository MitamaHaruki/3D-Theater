// 映像効果（色調補正・ブルーム・周辺減光）で共通の宣言。
// 画面全体を覆う 1 枚の三角形を描き、画像から画像へ処理する。色は「透明度をかけ合わせた値」（背景を透明にして描いた場面）。

cbuffer PostConstants : register(b0)
{
    float4 SourceTexel;    // xy = 読む画像の 1 画素の大きさ（UV）、zw = 書く画像の幅と高さ（画素）
    float4 BloomParams;    // x = 光り始める明るさ、y = 境目のやわらかさ、z = 広がり、w = 強さ
    float4 GradeParams;    // x = 明るさの倍率、y = コントラスト、z = 彩度、w = 周辺減光の強さ
    float4 WhiteBalance;   // rgb = 色温度の倍率
    float4 BackgroundColor; // 場面の後ろに置く背景（透明度をかけていない値。a = 0 なら透明のまま）
    uint4 PostFlags;       // x = 使う効果（1 = 色調補正、2 = ブルーム、4 = 周辺減光）
};

Texture2D Source : register(t0);
Texture2D Second : register(t1);
SamplerState LinearClamp : register(s0);

struct PostVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

static const uint kPostColor = 1;
static const uint kPostBloom = 2;
static const uint kPostVignette = 4;
