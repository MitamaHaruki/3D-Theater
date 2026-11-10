// モデルの描画（MMD と同じトゥーンの色の計算と輪郭線）。影（セルフシャドウ・地面の影）は次の段階で足す。

cbuffer ObjectConstants : register(b0)
{
    row_major float4x4 WorldViewProj;
    row_major float4x4 World;
    row_major float4x4 View;
    float4 LightDirection; // 光が進む向き（xyz、正規化済み）
    float4 LightColor;     // rgb
    float4 CameraPosition; // xyz（ワールド）
    float4 EdgeScale;      // xy = 材質の太さ 1 を、画面の座標（-1〜1）でどれだけにするか。0 なら輪郭線なし
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
    float4 Options;           // x = スフィア（0 なし・1 掛ける・2 足す）、y = 1 ならトゥーンあり、z = 輪郭線の太さ
};

Texture2D DiffuseTexture : register(t0);
Texture2D ToonTexture : register(t1);
Texture2D SphereTexture : register(t2);
SamplerState TextureSampler : register(s0); // 繰り返す
SamplerState ClampSampler : register(s1);   // 端で止める（トゥーン・スフィア）

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
    float3 eye : TEXCOORD2; // 面からカメラへの向き
};

struct EdgePSInput
{
    float4 position : SV_Position;
};
