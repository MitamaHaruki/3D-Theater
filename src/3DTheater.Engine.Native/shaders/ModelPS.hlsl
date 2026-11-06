#include "Model.hlsli"

// MMD の初期の照明の明るさ（154 / 255）
static const float LightIntensity = 0.6;

float4 main(ModelPSInput input) : SV_Target
{
    float3 n = normalize(input.normal);
    float lit = saturate(dot(n, -LightDirection.xyz));
    // 暗い側も形が分かる程度に明るく残す（簡単な陰影）
    float shade = lerp(0.62, 1.0, lit);

    float3 baseColor = saturate(AmbientAndTexture.rgb + Diffuse.rgb * LightIntensity);
    float4 texel = AmbientAndTexture.w > 0.5 ? DiffuseTexture.Sample(TextureSampler, input.uv) : float4(1, 1, 1, 1);

    return float4(baseColor * texel.rgb * shade, Diffuse.a * texel.a);
}
