#include "Model.hlsli"

ModelPSInput main(ModelVSInput input)
{
    ModelPSInput output;
    float4 position = float4(input.position, 1.0);
    float4 worldPosition = mul(position, World);
    output.position = mul(position, WorldViewProj);
    // モデルの拡大が縦横で違うと法線が少しずれるが、ここでは気にしない
    output.normal = normalize(mul(input.normal, (float3x3)World));
    output.uv = input.uv;
    output.eye = CameraPosition.xyz - worldPosition.xyz;
    // スフィアマップは、カメラから見た法線の向きで読む（MMD と同じ）
    float3 viewNormal = normalize(mul(output.normal, (float3x3)View));
    output.sphereUv = float2(viewNormal.x * 0.5 + 0.5, viewNormal.y * -0.5 + 0.5);
    output.shadowPosition = mul(worldPosition, LightViewProj);
    return output;
}
