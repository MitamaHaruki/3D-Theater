#include "Model.hlsli"
#include "Shadow.hlsli"
#include "Environment.hlsli"

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

// 足す照明 1 つの強さ（色 × 明るさ × 距離・円すいの減り方）と、面から照明への向き。
// 点光源・スポットライトは、届く距離で 0 になるよう、なめらかに弱める
float3 ExtraLightAt(uint index, float3 worldPosition, out float3 toLight)
{
    ExtraLight light = Lights[index];
    uint type = (uint)(light.position.w + 0.5);
    if (type == 0)
    {
        toLight = -light.direction.xyz;
        return light.color.rgb;
    }
    float3 offset = light.position.xyz - worldPosition;
    float reach = length(offset);
    toLight = offset / max(reach, 1e-4);
    float fade = saturate(1.0 - reach / max(light.direction.w, 1e-3));
    float3 radiance = light.color.rgb * fade * fade;
    if (type == 2)
    {
        radiance *= smoothstep(light.color.w, light.spot.x, dot(-toLight, light.direction.xyz));
    }
    return radiance;
}

// トゥーンの材質への足す照明: 光の当たる側を、材質の色（拡散色 × テクスチャ）× 照明の色で明るくする。
// トゥーンのある材質は境目をくっきりさせ（セル調）、無い材質はなめらかにする。影は落とさない
float3 ExtraToonLight(ModelPSInput input, float3 n, float3 albedo)
{
    float3 sum = 0.0;
    float3 worldPosition = CameraPosition.xyz - input.eye;
    [loop] for (uint i = 0; i < min(LightCount.x, 8u); i++)
    {
        float3 toLight;
        float3 radiance = ExtraLightAt(i, worldPosition, toLight);
        float nl = dot(n, toLight);
        float lit = Options.y > 0.5 ? smoothstep(0.0, 0.15, nl) : saturate(nl);
        sum += albedo * radiance * lit;
    }
    return sum;
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
    float3 albedo = Diffuse.rgb; // 足す照明に使う材質の色
    if (AmbientAndTexture.w > 0.5)
    {
        float4 texel = saturate(DiffuseTexture.Sample(TextureSampler, input.uv) * TextureMultiply + TextureAdd);
        color *= texel;
        albedo *= texel.rgb;
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
        float lit = SelfShadowFactor(input.shadowPosition, input.position.xy);
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
    result.rgb += ExtraToonLight(input, n, albedo);
    return result;
}

// PBR: 光 1 つに対して返す光（GGX の照り返しと Lambert の拡散）。radiance は届く光の強さ
float3 PbrDirect(float3 n, float3 v, float3 l, float3 radiance, float3 base, float metallic, float roughness, float3 f0)
{
    float alpha = roughness * roughness;
    float3 h = normalize(v + l);
    float nl = saturate(dot(n, l));
    float nv = max(dot(n, v), 1e-4);
    float nh = saturate(dot(n, h));
    float vh = saturate(dot(v, h));
    float a2 = alpha * alpha;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / (kPi * d * d);
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float geometry = (nl / (nl * (1.0 - k) + k)) * (nv / (nv * (1.0 - k) + k));
    float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
    float3 specular = distribution * geometry * fresnel / max(4.0 * nl * nv, 1e-4);
    float3 diffuse = (1.0 - fresnel) * (1.0 - metallic) * base / kPi;
    return (diffuse + specular) * radiance * nl;
}

// 拡張材質（PBR）の色の計算: 金属らしさと粗さで光を返す（GGX の反射と、Lambert の拡散）。
// - ベースカラー = 材質の拡散色 × テクスチャ × 色合い。スフィア・トゥーン・MMD の反射光は使わない
// - 照明: シーンの平行光 1 つ（セルフシャドウを受ける材質は、影の中で光が届かない）
// - 環境光: 照明の色をもとにした、空（上）と地面（下）の 2 色。反射もこの 2 色を映す（粗いほど混ざる）
// 環境の映り込み（環境の色 × 映り込みの強さ）は、色に足さずに reflection へ返す（画面の中の反射で置き換えられるように）
float4 PbrColor(ModelPSInput input, float3 n, out float3 fresnel, out float3 reflection, out float roughnessOut)
{
    float4 base = float4(Diffuse.rgb * BaseTint.rgb, Diffuse.a);
    if (AmbientAndTexture.w > 0.5)
    {
        base *= saturate(DiffuseTexture.Sample(TextureSampler, input.uv) * TextureMultiply + TextureAdd);
    }
    float metallic = saturate(Pbr.y);
    float roughness = clamp(Pbr.z, 0.04, 1.0);
    float3 v = normalize(input.eye);
    float nv = max(dot(n, v), 1e-4);
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), base.rgb, metallic);

    float shadow = Options.w > 0.5 ? SelfShadowFactor(input.shadowPosition, input.position.xy) : 1.0;
    float3 direct = PbrDirect(n, v, -LightDirection.xyz, LightColor.rgb * kPbrLightScale * shadow, base.rgb, metallic, roughness, f0);
    float3 worldPosition = CameraPosition.xyz - input.eye;
    [loop] for (uint i = 0; i < min(LightCount.x, 8u); i++)
    {
        float3 toLight;
        float3 radiance = ExtraLightAt(i, worldPosition, toLight);
        direct += PbrDirect(n, v, toLight, radiance * kPbrLightScale, base.rgb, metallic, roughness, f0);
    }

    float3 r = reflect(-v, n);
    float3 ambient;
    float3 environment;
    if (Environment.x > 0.5)
    {
        // 環境の画像: 拡散は球面調和関数、映り込みは粗いほど縮小した画像を読む。床の映り込みでは景色も上下に映す
        float3 nEnv = EnvironmentDirection(n, Environment.z);
        float3 rEnv = EnvironmentDirection(r, Environment.z);
        if (ReflectionParams.x > 0.5)
        {
            nEnv.y = -nEnv.y;
            rEnv.y = -rEnv.y;
        }
        float basis[9];
        EnvironmentBasis(nEnv, basis);
        float3 irradiance = 0.0;
        [unroll] for (int k = 0; k < 9; k++)
        {
            irradiance += EnvironmentIrradiance[k].rgb * basis[k];
        }
        ambient = max(irradiance, 0.0) * Environment.y * base.rgb * (1.0 - metallic);
        float lod = roughness * max(Environment.w - 1.0, 0.0);
        environment = EnvironmentMap.SampleLevel(EnvironmentSampler, EnvironmentUv(rEnv), lod).rgb * Environment.y;
    }
    else
    {
        float3 sky = LightColor.rgb * kSkyScale;
        float3 ground = LightColor.rgb * kGroundScale;
        ambient = lerp(ground, sky, n.y * 0.5 + 0.5) * base.rgb * (1.0 - metallic);
        float3 reflectionSky = LightColor.rgb * kReflectionSkyScale;
        float3 reflectionGround = LightColor.rgb * kReflectionGroundScale;
        environment = lerp(reflectionGround, reflectionSky, smoothstep(-0.25, 0.25, r.y));
        environment = lerp(environment, (reflectionSky + reflectionGround) * 0.5, roughness);
    }
    float3 ambientFresnel = f0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), f0) - f0) * pow(1.0 - nv, 5.0);
    fresnel = ambientFresnel;
    reflection = environment * ambientFresnel;
    roughnessOut = roughness;
    return float4(direct + ambient, base.a);
}

// ---- レンダーパス（書き出しの合成用の画像。tt_engine.h の TtRenderPass） ----

// 材質とテクスチャの不透明度（色の計算と同じ掛け方）
float MaterialAlpha(ModelPSInput input)
{
    float alpha = Diffuse.a;
    if (AmbientAndTexture.w > 0.5)
    {
        alpha *= saturate(DiffuseTexture.Sample(TextureSampler, input.uv).a * TextureMultiply.a + TextureAdd.a);
    }
    return alpha;
}

// 番号から決まる色（C# の側でも同じ式で求め、色と物の対応表を作る。0〜255 の整数をそのまま書けるよう、255 で割った値にする）
float3 IdColor(uint key)
{
    uint h = key * 2654435761u;
    h ^= h >> 16;
    h *= 2246822519u;
    h ^= h >> 13;
    uint3 c = uint3(64u + (h & 0xFFu) % 192u, 64u + ((h >> 8) & 0xFFu) % 192u, 64u + ((h >> 16) & 0xFFu) % 192u);
    return float3(c) / 255.0;
}

float4 PassColor(ModelPSInput input, float3 n)
{
    uint kind = RenderPass();
    float alpha = MaterialAlpha(input);
    if (kind == kPassAlpha)
    {
        return float4(1.0, 1.0, 1.0, alpha);
    }
    clip(alpha - 0.5); // 透けた所は、物の形に含めない（境目をはっきりさせる）
    if (kind == kPassDepth)
    {
        float3 worldPosition = CameraPosition.xyz - input.eye;
        float viewZ = mul(float4(worldPosition, 1.0), View).z;
        float d = saturate((viewZ - PassParams.z) / max(PassParams.w - PassParams.z, 1e-6));
        uint v = (uint)round(d * 65535.0);
        return float4(float(v >> 8) / 255.0, float(v & 0xFFu) / 255.0, 0.0, 1.0);
    }
    if (kind == kPassNormal)
    {
        float3 facing = dot(n, input.eye) < 0.0 ? -n : n; // 裏の面は手前へ向ける
        float3 viewNormal = normalize(mul(float4(facing, 0.0), View).xyz);
        return float4(viewNormal * 0.5 + 0.5, 1.0);
    }
    if (kind == kPassObjectId)
    {
        return float4(IdColor((uint)(PassParams.y + 0.5)), 1.0);
    }
    if (kind == kPassMaterialId)
    {
        return float4(IdColor((uint)(PassParams.y + 0.5) * 4096u + (uint)(MaterialIds.x + 0.5) + 1u), 1.0);
    }
    if (kind == kPassShadow)
    {
        float shadow = Options.w > 0.5 ? 1.0 - SelfShadowFactor(input.shadowPosition, input.position.xy) : 0.0;
        return float4(shadow, shadow, shadow, 1.0);
    }
    return float4(saturate(Emissive.rgb), 1.0); // 発光
}

struct ModelPSOutput
{
    float4 color : SV_Target0;
    // 2〜4 番目は、描画先があるときだけ使う（無ければ捨てられる）。a はどれも color.a（色と同じ合成 a と 1 - a で重なり、
    // 描画先には「値 × 不透明度」がたまる）
    // 床の映り込みをぼかすとき: target1.x = 床からの深さ
    // 画面の中の反射の下準備のとき: target1.rgb = 映り込みの強さ（フレネル）、target2.rgb = 環境の映り込み（強さを掛けた色）、
    //   target3.xyz = 見る向きの座標での面の向き × (1 + 粗さ)。トゥーンの材質は強さ 0（映さない）
    float4 target1 : SV_Target1;
    float4 target2 : SV_Target2;
    float4 target3 : SV_Target3;
};

// 材質の発光（拡張材質）は、どちらの色の計算にも足す（1 より明るくでき、ブルームで光る）
ModelPSOutput main(ModelPSInput input)
{
    ModelPSOutput output;
    output.target1 = 0.0;
    output.target2 = 0.0;
    output.target3 = 0.0;
    float3 n = normalize(input.normal);
    if (RenderPass() != 0)
    {
        output.color = PassColor(input, n);
        return output;
    }
    float3 fresnel = 0.0;
    float3 reflection = 0.0;
    float roughness = 1.0;
    // 条件の演算子（? :）は両方を計算するので、if で分ける（トゥーンの材質に PBR の映り込みが入らないように）
    float4 color;
    if (Pbr.x > 0.5)
    {
        color = PbrColor(input, n, fresnel, reflection, roughness);
    }
    else
    {
        color = ToonColor(input, n);
    }
    color.rgb += Emissive.rgb;
    if (SsrParams.x > 0.5)
    {
        output.target1 = float4(fresnel, color.a);
        output.target2 = float4(reflection, color.a);
        output.target3 = float4(mul(float4(n, 0.0), View).xyz * (1.0 + roughness), color.a);
    }
    else
    {
        color.rgb += reflection;
    }
    if (ReflectionParams.x > 0.5)
    {
        // 床の映り込み: 映した点（床より下）を描き、床から深いほど薄くする。床より上へ出た所（もとの物の、床より下の部分）は描かない
        float depth = -(CameraPosition.y - input.eye.y);
        clip(depth);
        color.a *= ReflectionParams.y * saturate(1.0 - depth / max(ReflectionParams.z, 1e-3));
        output.target1 = float4(depth, 0.0, 0.0, color.a);
    }
    output.color = color;
    return output;
}
