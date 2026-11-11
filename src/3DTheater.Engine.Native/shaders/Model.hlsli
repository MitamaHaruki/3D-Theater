// モデルの描画（MMD と同じトゥーンの色の計算・輪郭線・セルフシャドウ・地面の影）。

cbuffer ObjectConstants : register(b0)
{
    row_major float4x4 WorldViewProj;
    row_major float4x4 World;
    row_major float4x4 View;
    float4 LightDirection; // 光が進む向き（xyz、正規化済み）
    float4 LightColor;     // rgb
    float4 CameraPosition; // xyz（ワールド）
    float4 EdgeScale;      // xy = 材質の太さ 1 を、画面の座標（-1〜1）でどれだけにするか。0 なら輪郭線なし
    row_major float4x4 LightViewProj; // セルフシャドウ: ワールド → 照明から見た奥行きの画像の座標
    float4 ShadowParams;              // x = 1 / 奥行きの画像の 1 辺、y = 比べるときに奥へずらす量、z = 1 ならセルフシャドウあり
    float4 GroundShadowColor;
    float4 ReflectionParams;          // 床の映り込み: x = 1 なら映り込みを描いている、y = 濃さ、z = 消えるまでの距離
    float4 ShadowSoftness;            // 柔らかな影: x = 奥行き 1 あたりのワールドの長さ、y = 光源の見かけの半径の tan（0 ならくっきり）、
                                      //             zw = ワールドの長さ 1 あたりの、奥行きの画像の座標（横・縦）
    float4 PassParams;                // レンダーパス: x = 種類（TT_PASS_*。0 なら普通の色）、y = 物の番号（1 から）、z = 奥行きの手前、w = 奥
};

cbuffer MaterialConstants : register(b1)
{
    float4 Diffuse;           // a = 不透明度
    float4 AmbientAndTexture; // w = 1 ならテクスチャあり
    float4 SpecularAndPower;
    float4 TextureMultiply;   // 材質モーフで色を変える分（元の色 × 掛ける値 + 足す値）
    float4 TextureAdd;
    float4 SphereMultiply;
    float4 SphereAdd;
    float4 ToonMultiply;
    float4 ToonAdd;
    float4 EdgeColor;
    float4 Options;           // x = スフィア（0 なし・1 掛ける・2 足す）、y = 1 ならトゥーンあり、z = 輪郭線の太さ、w = 1 ならセルフシャドウを受ける
    float4 Pbr;               // 拡張材質: x = 1 なら PBR の色の計算、y = 金属らしさ、z = 粗さ
    float4 BaseTint;          // 拡張材質: PBR のベースカラーに掛ける色合い
    float4 Emissive;          // 拡張材質: 発光（色 × 強さ。どちらの色の計算にも足す）
    float4 MaterialIds;       // x = 材質の番号（0 から。レンダーパスの材質の番号の色）
};

// レンダーパスの種類（tt_engine.h の TT_PASS_* と同じ）
static const uint kPassAlpha = 1;
static const uint kPassDepth = 2;
static const uint kPassNormal = 3;
static const uint kPassObjectId = 4;
static const uint kPassMaterialId = 5;
static const uint kPassShadow = 6;
static const uint kPassEmission = 7;

uint RenderPass()
{
    return (uint)(PassParams.x + 0.5);
}

// 足す照明（メインの照明とは別。最大 8 つ）
struct ExtraLight
{
    float4 position;  // xyz = ワールドの位置、w = 種類（0 平行光・1 点光源・2 スポットライト）
    float4 direction; // xyz = 光が進む向き（正規化済み）、w = 届く距離
    float4 color;     // rgb = 色 × 明るさ、w = スポットの外側の角度の cos
    float4 spot;      // x = スポットの内側（ぼかし始め）の角度の cos
};

cbuffer LightConstants : register(b2)
{
    uint4 LightCount; // x = 数
    ExtraLight Lights[8];
    float4 Environment;              // 環境の画像: x = 1 なら使う、y = 明るさ、z = Y 軸のまわりの回転（ラジアン）、w = 縮小版の数
    float4 EnvironmentIrradiance[9]; // 拡散の明るさの球面調和関数の係数（rgb）
};

Texture2D DiffuseTexture : register(t0);
Texture2D ToonTexture : register(t1);
Texture2D SphereTexture : register(t2);
Texture2D<float> ShadowMap : register(t3);
Texture2D EnvironmentMap : register(t4);
SamplerState TextureSampler : register(s0);         // 繰り返す
SamplerState ClampSampler : register(s1);           // 端で止める（トゥーン・スフィア）
SamplerComparisonState ShadowSampler : register(s2); // 奥行きを比べて、手前なら 1（範囲の外も 1）
SamplerState EnvironmentSampler : register(s3);     // 環境の画像（横は繰り返し、縦は端で止める）

struct ModelVSInput
{
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float edgeScale : TEXCOORD1; // 頂点ごとの輪郭線の太さの倍率
};

struct ModelPSInput
{
    float4 position : SV_Position;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float2 sphereUv : TEXCOORD1;
    float3 eye : TEXCOORD2;            // 面からカメラへの向き
    float4 shadowPosition : TEXCOORD3; // 照明から見た奥行きの画像の座標
};

// 輪郭線・影など、位置だけを使う描き方
struct EdgePSInput
{
    float4 position : SV_Position;
};
