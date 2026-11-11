#include "Model.hlsli"

// 柔らかな地面の影: 床の上の 1 枚の四角形（頂点は番号から作る。帯状の 4 頂点）。
// World は 0〜1 の四角形（XZ）を床の範囲へ広げる行列、LightViewProj は地面の影の奥行きの画像の座標への変換。
// レイトレーシングでの地面の影（GroundRayShadowPS）も同じ四角形を使う（ワールドの位置を読む）
struct GroundSoftShadowPSInput
{
    float4 position : SV_Position;
    float4 shadowPosition : TEXCOORD3;
    float3 world : TEXCOORD4;
};

GroundSoftShadowPSInput main(uint id : SV_VertexID)
{
    float3 local = float3(id & 1, 0.0, id >> 1);
    float4 world = mul(float4(local, 1.0), World);
    GroundSoftShadowPSInput output;
    output.position = mul(float4(local, 1.0), WorldViewProj);
    output.shadowPosition = mul(world, LightViewProj);
    output.world = world.xyz;
    return output;
}
