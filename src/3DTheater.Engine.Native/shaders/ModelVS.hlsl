#include "Model.hlsli"

ModelPSInput main(ModelVSInput input)
{
    ModelPSInput output;
    output.position = mul(float4(input.position, 1.0), WorldViewProj);
    // 拡大が縦横で違うと法線が少しずれるが、Phase 2 の簡単な陰影では気にしない
    output.normal = mul(input.normal, (float3x3)World);
    output.uv = input.uv;
    return output;
}
