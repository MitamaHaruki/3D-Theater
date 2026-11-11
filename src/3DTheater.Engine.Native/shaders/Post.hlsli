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
    float4 ScreenToView;     // 画面の位置を見る向きの座標に戻す値: x = 投影の (1,1)、y = (2,2)、zw = 中心のずれ
    float4 FogColor;         // 霧: rgb = 色、a = 始まる距離
    float4 FogParams;        // 霧: x = 濃さ、y = 高さの尺度（0 なら高さに関係なく同じ）
    float4 FogHeightAxis;    // 見る向きの座標からワールドの高さを出す値: 高さ = dot(位置, xyz) + w（w はカメラの高さ）
    float4 SsrParams;        // 画面の中の反射: x = 強さ（0 なら分けて書いた環境の映り込みを戻すだけ）、y = 映る物を探す距離
    row_major float4x4 ViewToWorld; // 見る向きの座標 → ワールド（レイトレーシングで使う）
    float4 RayLight;         // レイトレーシングで当たった所の色の計算に使う照明: xyz = 光が進む向き（正規化済み）
    float4 RayLightColor;    // rgb = 照明の色
    float4 RayParams;        // レイトレーシング: x = 1 なら使う、y = 物の数、z = 画素 1 つ分の広がり（透視投影は距離 1 あたり、平行投影はワールドの長さ）、
                             //   w = 光線の数（環境光の遮りで 1 点あたり）
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
static const uint kPostFog = 512;
static const uint kPostSsr = 1024;

// 何も描かれていない所の距離
static const float kNothing = 1e30;

// 画素と見る向きの距離 z から、見る向きの座標での位置（カメラが原点）。SourceTexel.xy が書く画像の 1 画素の大きさであること。
// 透視投影で z = 1 なら、その画素を通る光の向き（長さは 1 でない）
float3 ViewPositionAt(int2 pixel, float z)
{
    float2 ndc = float2((pixel.x + 0.5) * SourceTexel.x * 2.0 - 1.0, 1.0 - (pixel.y + 0.5) * SourceTexel.y * 2.0);
    float2 xy = (ndc - ScreenToView.zw) / ScreenToView.xy;
    return float3(ProjectionParams.z > 0.5 ? xy : xy * z, z);
}
