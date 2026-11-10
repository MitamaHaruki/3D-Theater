#include "Model.hlsli"

static const float kPi = 3.14159265;

// PBR の材質で、照明の色（MMD の初期値は 154/255）を光の強さにするときの倍率。
// 正面から当たる光で、拡散の明るさが「材質の色 × 照明の色」ほどになる。環境光（空・地面）と合わせて、
// トゥーンの材質と並べて明るさが大きく違わないようにした
static const float kPbrLightScale = kPi;

// 環境光（空と地面の明るさ。照明の色に掛ける）。拡散（つや消しの面）に使う分と、映り込み（照り返し）に使う分を分ける。
// 金属は周りの映り込みで色が決まるので、映り込みを暗くすると沈んで見える。拡散まで明るくすると、つや消しの面が白く飛ぶ
static const float kSkyScale = 0.9;
static const float kGroundScale = 0.3;
static const float kReflectionSkyScale = 1.6;
static const float kReflectionGroundScale = 0.45;

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
float4 ToonColor(ModelPSInput input, float3 n)
{
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

    float4 result;
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
        result = lerp(shadowColor, float4(color.rgb + specular, color.a), lit);
    }
    else
    {
        if (Options.y > 0.5)
        {
            float lightNormal = dot(n, toLight);
            color *= saturate(ToonTexture.Sample(ClampSampler, float2(0.0, 0.5 - lightNormal * 0.5)) * ToonMultiply + ToonAdd);
        }
        color.rgb += specular;
        result = color;
    }
    return result;
}

// 拡張材質（PBR）の色の計算: 金属らしさと粗さで光を返す（GGX の反射と、Lambert の拡散）。
// - ベースカラー = 材質の拡散色 × テクスチャ × 色合い。スフィア・トゥーン・MMD の反射光は使わない
// - 照明: シーンの平行光 1 つ（セルフシャドウを受ける材質は、影の中で光が届かない）
// - 環境光: 照明の色をもとにした、空（上）と地面（下）の 2 色。反射もこの 2 色を映す（粗いほど混ざる）
float4 PbrColor(ModelPSInput input, float3 n)
{
    float4 base = float4(Diffuse.rgb * BaseTint.rgb, Diffuse.a);
    if (AmbientAndTexture.w > 0.5)
    {
        base *= saturate(DiffuseTexture.Sample(TextureSampler, input.uv) * TextureMultiply + TextureAdd);
    }
    float metallic = saturate(Pbr.y);
    float roughness = clamp(Pbr.z, 0.04, 1.0);
    float alpha = roughness * roughness;

    float3 v = normalize(input.eye);
    float3 l = -LightDirection.xyz;
    float3 h = normalize(v + l);
    float nl = saturate(dot(n, l));
    float nv = max(dot(n, v), 1e-4);
    float nh = saturate(dot(n, h));
    float vh = saturate(dot(v, h));

    float3 f0 = lerp(float3(0.04, 0.04, 0.04), base.rgb, metallic);
    float a2 = alpha * alpha;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / (kPi * d * d);
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float geometry = (nl / (nl * (1.0 - k) + k)) * (nv / (nv * (1.0 - k) + k));
    float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
    float3 specular = distribution * geometry * fresnel / max(4.0 * nl * nv, 1e-4);
    float3 diffuse = (1.0 - fresnel) * (1.0 - metallic) * base.rgb / kPi;
    float shadow = Options.w > 0.5 ? SelfShadowFactor(input.shadowPosition) : 1.0;
    float3 direct = (diffuse + specular) * LightColor.rgb * kPbrLightScale * nl * shadow;

    float3 sky = LightColor.rgb * kSkyScale;
    float3 ground = LightColor.rgb * kGroundScale;
    float3 ambient = lerp(ground, sky, n.y * 0.5 + 0.5) * base.rgb * (1.0 - metallic);
    float3 r = reflect(-v, n);
    float3 reflectionSky = LightColor.rgb * kReflectionSkyScale;
    float3 reflectionGround = LightColor.rgb * kReflectionGroundScale;
    float3 environment = lerp(reflectionGround, reflectionSky, smoothstep(-0.25, 0.25, r.y));
    environment = lerp(environment, (reflectionSky + reflectionGround) * 0.5, roughness);
    float3 ambientFresnel = f0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), f0) - f0) * pow(1.0 - nv, 5.0);
    return float4(direct + ambient + environment * ambientFresnel, base.a);
}

// 材質の発光（拡張材質）は、どちらの色の計算にも足す（1 より明るくでき、ブルームで光る）
float4 main(ModelPSInput input) : SV_Target
{
    float3 n = normalize(input.normal);
    float4 color = Pbr.x > 0.5 ? PbrColor(input, n) : ToonColor(input, n);
    color.rgb += Emissive.rgb;
    return color;
}
