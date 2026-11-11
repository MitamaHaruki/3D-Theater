// レイトレーシングで当たった所の色（画面の中の反射で使う）。Post.hlsli と RayTrace.hlsli の後に読み込む
// （RayLight・RayLightColor・LinearClamp を使う）。

// 当たった所の色。モデルを描くときの色の計算を簡単にしたもの（メインの照明と環境光だけ。影・スフィア・反射光・足す照明・輪郭線は無し）。
// footprint は当たった所での光線の太さ（ワールドの長さ）。テクスチャはこの太さに合った縮小版を読む
float3 RayShade(RayHit hit, float3 direction, float footprint)
{
    RayInstance instance = RayInstances[hit.instance];
    uint4 tri = RayTriangles[hit.triangleIndex];
    RayVertex v0 = RayVertices[instance.offsets.z + tri.x];
    RayVertex v1 = RayVertices[instance.offsets.z + tri.y];
    RayVertex v2 = RayVertices[instance.offsets.z + tri.z];
    RayMaterial material = RayMaterials[instance.offsets.w + tri.w];
    float w = 1.0 - hit.barycentric.x - hit.barycentric.y;
    float2 uv = v0.uv * w + v1.uv * hit.barycentric.x + v2.uv * hit.barycentric.y;

    float3 w0 = RayPoint(instance.toWorld0, instance.toWorld1, instance.toWorld2, v0.position);
    float3 w1 = RayPoint(instance.toWorld0, instance.toWorld1, instance.toWorld2, v1.position);
    float3 w2 = RayPoint(instance.toWorld0, instance.toWorld1, instance.toWorld2, v2.position);
    float3 faceNormal = cross(w1 - w0, w2 - w0);
    float faceArea = length(faceNormal);
    float3 n = RayVector(instance.toWorld0, instance.toWorld1, instance.toWorld2, v0.normal * w + v1.normal * hit.barycentric.x + v2.normal * hit.barycentric.y);
    float normalLength = length(n);
    n = normalLength > 1e-6 ? n / normalLength : (faceArea > 1e-20 ? faceNormal / faceArea : float3(0.0, 1.0, 0.0));
    if (dot(n, direction) > 0.0)
    {
        n = -n; // 光線の来る側へ向ける（両面の材質の裏・面の向きと頂点の法線がずれた所）
    }

    // テクスチャの細かさ: 光線の太さが、テクスチャの何画素ぶんにあたるか（斜めに当たるほど広がる）
    float uvArea = abs((v1.uv.x - v0.uv.x) * (v2.uv.y - v0.uv.y) - (v2.uv.x - v0.uv.x) * (v1.uv.y - v0.uv.y));
    float texelsPerUnit = kRayTextureSize * sqrt(uvArea / max(faceArea, 1e-20));
    float slant = max(abs(dot(n, direction)), 0.2);
    float lod = clamp(log2(max(footprint * texelsPerUnit / slant, 1e-6)), 0.0, kRayTextureLevels);
    float4 texel = 1.0;
    if (material.info.x != kRayNoTexture)
    {
        texel = saturate(RayTextures.SampleLevel(RayTextureSampler, float3(uv, material.info.x), lod) * material.textureMultiply + material.textureAdd);
    }

    float3 toLight = -RayLight.xyz;
    float nl = dot(n, toLight);
    float3 color;
    if ((material.info.z & kRayPbr) != 0)
    {
        // PBR: 拡散（照明と、空・地面の 2 色の環境光）と、周りの映り込みのおおよその明るさ
        float3 base = material.diffuse.rgb * material.baseTint.rgb * texel.rgb;
        float metallic = material.emissive.w;
        float3 ambient = lerp(RayLightColor.rgb * 0.3, RayLightColor.rgb * 0.9, n.y * 0.5 + 0.5);
        float3 f0 = lerp(float3(0.04, 0.04, 0.04), base, metallic);
        color = base * (1.0 - metallic) * (RayLightColor.rgb * saturate(nl) + ambient) + f0 * RayLightColor.rgb;
    }
    else
    {
        // トゥーン: (拡散色 × 照明の色 + 環境色) × テクスチャ × トゥーン（光の当たり方で上から下を読む）
        color = saturate(material.diffuse.rgb * RayLightColor.rgb + material.ambient.rgb) * texel.rgb;
        if (material.info.y != kRayNoTexture)
        {
            float y = clamp(0.5 - nl * 0.5, 0.5 / kRayTextureSize, 1.0 - 0.5 / kRayTextureSize);
            float4 toon = RayTextures.SampleLevel(LinearClamp, float3(0.0, y, material.info.y), 0.0);
            color *= saturate(toon * material.toonMultiply + material.toonAdd).rgb;
        }
    }
    return color + material.emissive.rgb;
}
