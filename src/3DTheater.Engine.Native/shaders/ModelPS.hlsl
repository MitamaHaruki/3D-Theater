#include "Model.hlsli"

// MMD の色の計算（MMD の照明は「拡散」が 0 で、「環境」と「反射」が照明の色）:
//   色 = (材質の拡散色 × 照明の色 + 材質の環境色) × テクスチャ → スフィア（掛ける・足す） → × トゥーン → + 反射光
// トゥーンは、光の当たり方（-1〜1）でトゥーンのテクスチャの上（明るい側）から下（暗い側）を読む。
// トゥーンの無い材質には陰影を付けない（MMD と同じ）。
float4 main(ModelPSInput input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 toLight = -LightDirection.xyz;

    float4 color = float4(saturate(Diffuse.rgb * LightColor.rgb + AmbientAndTexture.rgb), Diffuse.a);
    if (AmbientAndTexture.w > 0.5)
    {
        color *= saturate(DiffuseTexture.Sample(TextureSampler, input.uv) * TextureMultiply + TextureAdd);
    }
    if (Options.x > 0.5)
    {
        float3 sphere = saturate(SphereTexture.Sample(ClampSampler, input.sphereUv) * SphereMultiply + SphereAdd).rgb;
        color.rgb = Options.x < 1.5 ? color.rgb * sphere : color.rgb + sphere;
    }
    if (Options.y > 0.5)
    {
        float lightNormal = dot(n, toLight);
        color *= saturate(ToonTexture.Sample(ClampSampler, float2(0.0, 0.5 - lightNormal * 0.5)) * ToonMultiply + ToonAdd);
    }
    if (any(SpecularAndPower.rgb > 0.0))
    {
        float3 halfVector = normalize(normalize(input.eye) + toLight);
        // pow(0, 0) を避けるため、0 より少し大きくしてから累乗する（強さ 0 は全体に反射色を足す。MMD と同じ）
        float strength = pow(max(dot(halfVector, n), 1e-6), max(SpecularAndPower.w, 0.0));
        color.rgb += strength * SpecularAndPower.rgb * LightColor.rgb;
    }
    return color;
}
