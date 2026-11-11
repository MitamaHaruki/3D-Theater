#include "Model.hlsli"
#include "Shadow.hlsli"
#include "RayTrace.hlsli"
#include "RayShadow.hlsli"

// レイトレーシングでの地面の影: 床（高さ 0）の上の 1 枚の四角形の画素ごとに、床の点から照明へ光線を出し、地面の影を落とす材質
// （「地面影」）に当たった割合で、地面の影の色を重ねる。やわらかさは ShadowSoftness.y、物の数・光線の数は RayShadowParams.y・z。
// 光線は床から少し浮かせ、照明の向きに少し進めた所から出す（床に埋まった足の裏などは、そのまま影を落とす）
float4 main(float4 position : SV_Position, float4 shadowPosition : TEXCOORD3, float3 world : TEXCOORD4) : SV_Target
{
    float3 toLight = -LightDirection.xyz;
    float3 origin = float3(world.x, 0.001, world.z) + toLight * 0.01;
    float lit = RayShadowVisibility(origin, toLight, ShadowSoftness.y, (int)(RayShadowParams.z + 0.5), (uint)(RayShadowParams.y + 0.5),
        kRayGroundShadow, position.xy);
    return float4(GroundShadowColor.rgb, GroundShadowColor.a * (1.0 - lit));
}
