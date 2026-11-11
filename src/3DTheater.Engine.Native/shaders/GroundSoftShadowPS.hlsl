#include "Model.hlsli"
#include "Shadow.hlsli"

// 柔らかな地面の影: 床の点が、地面の影を落とす物の陰にどれだけ入るか（柔らかな影と同じ読み方）で、地面の影の色を重ねる。
// 影を落とす物と床が離れているほど境目がぼけ、足元のように接している所はくっきりになる
float4 main(float4 position : SV_Position, float4 shadowPosition : TEXCOORD3) : SV_Target
{
    float lit = SelfShadowFactor(shadowPosition, position.xy);
    return float4(GroundShadowColor.rgb, GroundShadowColor.a * (1.0 - lit));
}
