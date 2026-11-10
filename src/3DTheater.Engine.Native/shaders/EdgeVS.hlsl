#include "Model.hlsli"

// 輪郭線: 裏側の面を、画面の上で法線の向きへ決まったピクセルだけ広げて描く（表の面の外にはみ出した分が線になる）。
// 太さ = 材質の太さ × 頂点の倍率 × 1 あたりのピクセル。遠くても近くても、画面の上で同じ太さになる
EdgePSInput main(ModelVSInput input)
{
    EdgePSInput output;
    float4 position = mul(float4(input.position, 1.0), WorldViewProj);
    float3 viewNormal = mul(mul(input.normal, (float3x3)World), (float3x3)View);
    float2 direction = viewNormal.xy;
    float length2 = dot(direction, direction);
    if (length2 > 1e-12)
    {
        direction *= rsqrt(length2);
        position.xy += direction * EdgeScale.xy * (Options.z * input.edgeScale) * position.w;
    }
    output.position = position;
    return output;
}
