// モデルの描画（Phase 2 の簡単な陰影）。トゥーン・スフィア・輪郭線・影は Phase 4 で足す。

cbuffer ObjectConstants : register(b0)
{
    row_major float4x4 WorldViewProj;
    row_major float4x4 World;
    float4 LightDirection; // 光が進む向き（xyz）
};

cbuffer MaterialConstants : register(b1)
{
    float4 Diffuse;           // a = 不透明度
    float4 AmbientAndTexture; // w = 1 ならテクスチャあり
    float4 SpecularAndPower;
    float4 TextureMultiply;   // 材質モーフでテクスチャの色を変える分（テクスチャ × 掛ける値 + 足す値）
    float4 TextureAdd;
};

Texture2D DiffuseTexture : register(t0);
SamplerState TextureSampler : register(s0);

struct ModelVSInput
{
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct ModelPSInput
{
    float4 position : SV_Position;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};
