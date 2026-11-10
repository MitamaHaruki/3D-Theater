#include "Model.hlsli"

// セルフシャドウ: 照明から見て、ほかの面より奥にあるか（1 = 光が当たる、0 = 影）。周りの 9 か所を比べて境目をぼかす
float SelfShadowFactor(float4 shadowPosition)
{
    float3 p = shadowPosition.xyz / shadowPosition.w;
    float2 uv = float2(p.x * 0.5 + 0.5, p.y * -0.5 + 0.5);
    float lit = 1.0; // 奥行きの画像の範囲の外は影なし
    if (all(uv >= 0.0) && all(uv <= 1.0) && p.z <= 1.0)
    {
        float depth = p.z - ShadowParams.y;
        float sum = 0.0;
        [unroll] for (int y = -1; y <= 1; y++)
        {
            [unroll] for (int x = -1; x <= 1; x++)
            {
                sum += ShadowMap.SampleCmpLevelZero(ShadowSampler, uv + float2(x, y) * ShadowParams.x, depth);
            }
        }
        lit = sum / 9.0;
    }
    return lit;
}

// MMD の色の計算（MMD の照明は「拡散」が 0 で、「環境」と「反射」が照明の色）:
//   色 = (材質の拡散色 × 照明の色 + 材質の環境色) × テクスチャ → スフィア（掛ける・足す） → × トゥーン → + 反射光
// トゥーンは、光の当たり方（-1〜1）でトゥーンのテクスチャの上（明るい側）から下（暗い側）を読む。
// トゥーンの無い材質には陰影を付けない（MMD と同じ）。
// セルフシャドウを受ける材質は、MMD のセルフシャドウの描き方にする: 光の当たり方（× 3 で 0〜1）と影の少ないほうで、
// 「影の色（色 × トゥーンのいちばん下の色）」と「光の当たる色（色 + 反射光）」を混ぜる。
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
    float3 specular = 0.0;
    if (any(SpecularAndPower.rgb > 0.0))
    {
        float3 halfVector = normalize(normalize(input.eye) + toLight);
        // pow(0, 0) を避けるため、0 より少し大きくしてから累乗する（強さ 0 は全体に反射色を足す。MMD と同じ）
        float strength = pow(max(dot(halfVector, n), 1e-6), max(SpecularAndPower.w, 0.0));
        specular = strength * SpecularAndPower.rgb * LightColor.rgb;
    }

    if (Options.w > 0.5)
    {
        float4 shadowColor = color;
        float lit = SelfShadowFactor(input.shadowPosition);
        if (Options.y > 0.5)
        {
            lit = min(saturate(dot(n, toLight) * 3.0), lit);
            float4 toonColor = saturate(ToonTexture.Sample(ClampSampler, float2(0.0, 1.0)) * ToonMultiply + ToonAdd);
            shadowColor.rgb *= toonColor.rgb;
        }
        return lerp(shadowColor, float4(color.rgb + specular, color.a), lit);
    }

    if (Options.y > 0.5)
    {
        float lightNormal = dot(n, toLight);
        color *= saturate(ToonTexture.Sample(ClampSampler, float2(0.0, 0.5 - lightNormal * 0.5)) * ToonMultiply + ToonAdd);
    }
    color.rgb += specular;
    return color;
}
