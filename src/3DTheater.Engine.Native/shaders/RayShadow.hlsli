// レイトレーシングでの影で共通の部分（モデルのセルフシャドウと地面の影）。Shadow.hlsli と RayTrace.hlsli の後に読み込む。
//
// 点 origin から照明の向き toLight へ光線を出し、影を落とす材質（casterMask の印を持つ材質）に当たらなかった割合を返す
// （1 = 光が当たる、0 = 影）。softness（光源の見かけの半径の tan）が 0 より大きければ、照明の見かけの円の中のいくつかの向きへ出す
// （画素 pixel ごとに並びを回す）。まず 4 本出し、全部が同じ（影の中・光の中）ならそれで決め、違えば影の境目なので、
// さらに samples 本出す

static const float kRayShadowReach = 10000.0; // 照明の向きに調べる長さ

float RayShadowVisibility(float3 origin, float3 toLight, float softness, int samples, uint instanceCount, uint casterMask, float2 pixel)
{
    if (softness <= 0.0)
    {
        return TraceShadowRay(origin, toLight, kRayShadowReach, instanceCount, casterMask) ? 0.0 : 1.0;
    }
    float3 helper = abs(toLight.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(helper, toLight));
    float3 bitangent = cross(toLight, tangent);
    float rotation = ShadowNoise(pixel) * 6.2831853;
    samples = clamp(samples, 1, 64);
    int first = min(samples, 4);
    float lit = 0.0;
    [loop]
    for (int i = 0; i < first; i++)
    {
        float2 d = ShadowSpiral(i, first, rotation) * softness;
        float3 direction = normalize(toLight + tangent * d.x + bitangent * d.y);
        if (!TraceShadowRay(origin, direction, kRayShadowReach, instanceCount, casterMask))
        {
            lit += 1.0;
        }
    }
    if (samples <= 4 || lit == 0.0 || lit == (float)first)
    {
        return lit / first; // 全部が同じ: 影の境目ではない
    }
    float more = 0.0;
    [loop]
    for (int j = 0; j < samples; j++)
    {
        float2 d = ShadowSpiral(j, samples, rotation + 1.7) * softness;
        float3 direction = normalize(toLight + tangent * d.x + bitangent * d.y);
        if (!TraceShadowRay(origin, direction, kRayShadowReach, instanceCount, casterMask))
        {
            more += 1.0;
        }
    }
    return (lit + more) / (first + samples);
}
