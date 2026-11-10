#include "Post.hlsli"

// 被写界深度: ピントの距離の前後をぼかす（場面の画像から、ぼかした場面の画像へ）。
// 点ごとの「ぼけの大きさ」（画素）は、ピントの合う幅の外へ出た距離に比例し（DofParams.w の距離で最大）、何も無い所は最大。
// ぼけの大きさの円の中を、黄金角の渦巻きで 40 点読んで平均する。読む点は、その点自身のぼけが真ん中まで届くときだけ使う
// （ピントの合った手前の物が、奥のぼけへにじまないように）
float CircleOfConfusion(float z)
{
    if (z >= kNothing)
    {
        return DofParams.z;
    }
    float outside = abs(z - DofParams.x) - DofParams.y;
    return saturate(outside / max(DofParams.w, 1e-3)) * DofParams.z;
}

float4 main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    int2 size = int2(SourceTexel.zw);
    float4 center = Source.Load(int3(pixel, 0));
    float radius = CircleOfConfusion(LinearDepth.Load(int3(pixel, 0)).x);
    if (radius < 0.5)
    {
        return center; // ピントが合っている
    }
    static const int kSamples = 40;
    float4 sum = center;
    float weight = 1.0;
    [loop] for (int i = 1; i < kSamples; i++)
    {
        float r = sqrt(i / (float)(kSamples - 1)) * radius;
        float angle = i * 2.39996323;
        float2 offset = float2(cos(angle), sin(angle)) * r;
        int2 at = clamp(pixel + int2(round(offset)), int2(0, 0), size - 1);
        float reach = CircleOfConfusion(LinearDepth.Load(int3(at, 0)).x);
        float w = saturate(reach - r + 1.0);
        sum += Source.SampleLevel(LinearClamp, input.uv + offset * SourceTexel.xy, 0) * w;
        weight += w;
    }
    return sum / weight;
}
