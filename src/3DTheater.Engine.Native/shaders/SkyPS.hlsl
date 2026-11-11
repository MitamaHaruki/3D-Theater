#include "Environment.hlsli"

// 環境の画像を背景（空）に描く。画面の点ごとに、カメラからの向きの景色を読む（縮小していない画像）

cbuffer SkyConstants : register(b0)
{
    row_major float4x4 InverseViewProj; // 画面の座標 → ワールド
    float4 SkyParams;                   // y = 明るさ、z = Y 軸のまわりの回転（ラジアン）
};

Texture2D SkyMap : register(t0);
SamplerState SkySampler : register(s0); // 横は繰り返し、縦は端で止める

struct SkyVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

float4 main(SkyVertex input) : SV_Target
{
    float2 ndc = float2(input.uv.x * 2.0 - 1.0, 1.0 - input.uv.y * 2.0);
    float4 nearPoint = mul(float4(ndc, 0.0, 1.0), InverseViewProj);
    float4 farPoint = mul(float4(ndc, 1.0, 1.0), InverseViewProj);
    float3 dir = farPoint.xyz / farPoint.w - nearPoint.xyz / nearPoint.w;
    float3 color = SkyMap.SampleLevel(SkySampler, EnvironmentUv(EnvironmentDirection(dir, SkyParams.z)), 0.0).rgb * SkyParams.y;
    return float4(color, 1.0);
}
