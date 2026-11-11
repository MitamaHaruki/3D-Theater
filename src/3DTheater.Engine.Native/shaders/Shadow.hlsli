// セルフシャドウの奥行きの画像の読み方（モデルと、柔らかな地面の影で共通）。Model.hlsli の後に読み込む。

// 柔らかな影で、ぼかす幅（円の半径）の上限（ワールドの長さ。人の大きさのモデルの背丈は 20 ほど）。
// ワールドの長さで決めるので、奥行きの画像の大きさ（プレビューと書き出し）によらず同じ見え方になる
static const float kMaxSoftShadowWorld = 3.0;

// 画素ごとに違う回し方（0〜1）。柔らかな影の、見る点の並びを画素ごとに回して、境目が段々にならないようにする
float ShadowNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// 黄金角の渦巻きの i 番目（count 個で半径 1 の円を埋める）
float2 ShadowSpiral(int i, int count, float rotation)
{
    float angle = i * 2.39996323 + rotation;
    return float2(cos(angle), sin(angle)) * sqrt((i + 0.5) / count);
}

// セルフシャドウ: 照明から見て、ほかの面より奥にあるか（1 = 光が当たる、0 = 影）。周りの 9 か所を比べて境目をぼかす。
// 柔らかな影（ShadowSoftness.y > 0）では、照明を大きさのある光源として扱う（PCSS）:
// 1. 受ける点の周り（照明までの距離 × tan の範囲）で、影を落とす物の奥行きを探して平均する
// 2. 物と受ける点の距離 × tan を境目の幅にして、その円の中の 24 か所を比べて平均する（接している所はくっきり、離れるほどぼける）
float SelfShadowFactor(float4 shadowPosition, float2 pixel)
{
    float3 p = shadowPosition.xyz / shadowPosition.w;
    float2 uv = float2(p.x * 0.5 + 0.5, p.y * -0.5 + 0.5);
    float lit = 1.0; // 奥行きの画像の範囲の外は影なし
    if (all(uv >= 0.0) && all(uv <= 1.0) && p.z <= 1.0)
    {
        float depth = p.z - ShadowParams.y;
        float texel = ShadowParams.x;
        float softness = ShadowSoftness.y;
        if (softness > 0.0)
        {
            int size = (int)round(1.0 / texel);
            float rotation = ShadowNoise(pixel) * 6.2831853;
            float2 search = min(depth * ShadowSoftness.x * softness, kMaxSoftShadowWorld) * ShadowSoftness.zw;
            float blockerSum = 0.0;
            float blockers = 0.0;
            [unroll] for (int i = 0; i < 16; i++)
            {
                int2 at = clamp(int2((uv + ShadowSpiral(i, 16, rotation) * search) * size), int2(0, 0), int2(size - 1, size - 1));
                float d = ShadowMap.Load(int3(at, 0));
                if (d < depth)
                {
                    blockerSum += d;
                    blockers += 1.0;
                }
            }
            if (blockers > 0.0)
            {
                float gap = (depth - blockerSum / blockers) * ShadowSoftness.x; // 物と受ける点の距離（ワールド）
                float2 radius = min(gap * softness, kMaxSoftShadowWorld) * ShadowSoftness.zw;
                if (max(radius.x, radius.y) > texel * 1.5) // それより狭い境目は、下の 9 か所で十分
                {
                    float sum = 0.0;
                    [unroll] for (int j = 0; j < 24; j++)
                    {
                        sum += ShadowMap.SampleCmpLevelZero(ShadowSampler, uv + ShadowSpiral(j, 24, rotation) * radius, depth);
                    }
                    return sum / 24.0;
                }
            }
        }
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
