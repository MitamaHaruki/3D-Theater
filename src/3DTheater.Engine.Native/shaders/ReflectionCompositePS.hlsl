#include "Reflection.hlsli"

// 段 level（小数なら前後の段を混ぜる）の絵を、まわりの 9 点でなだらかに読む（粗い段を広げたときの角ばりを消す）。
// 点の間隔は段の 1 画素ほど。いちばん細かい段では 0（くっきりのまま）
float4 SampleBlurred(float2 uv, float level)
{
    float2 o = (exp2(level) - 1.0) * Texel.xy;
    float4 c = Color.SampleLevel(LinearClamp, uv, level) * 4.0;
    c += (Color.SampleLevel(LinearClamp, uv + float2(o.x, 0.0), level) + Color.SampleLevel(LinearClamp, uv - float2(o.x, 0.0), level)
        + Color.SampleLevel(LinearClamp, uv + float2(0.0, o.y), level) + Color.SampleLevel(LinearClamp, uv - float2(0.0, o.y), level)) * 2.0;
    c += Color.SampleLevel(LinearClamp, uv + o, level) + Color.SampleLevel(LinearClamp, uv - o, level)
        + Color.SampleLevel(LinearClamp, uv + float2(o.x, -o.y), level) + Color.SampleLevel(LinearClamp, uv + float2(-o.x, o.y), level);
    return c / 16.0;
}

// 床の映り込みを場面に重ねる（透明度をかけ合わせた色のまま。合成は 1 と 1 - a）。
// 映した点が床から深いほど、ぼけを大きくする（つやのある床では、床に接する所はくっきり映り、離れるほどぼやける）
float4 main(ReflectionVertex input) : SV_Target
{
    float2 uv = input.uv;
    float levels = BlurParams.z;
    // 床からの深さ: 段ごとの「深さ × 不透明度」と「不透明度」を、細かい段ほど重くして足し合わせ、割って出す。映り込みの中では
    // 細かい段の値、外のぼけて広がる所では近くの映り込みの値になり、場所によってなめらかに変わる（段の切り替わりで、ぼけの大きさが
    // 急に変わって輪のような筋が出ないように）
    float weightedDepth = 0.0;
    float weightedAlpha = 0.0;
    float weight = 1.0;
    [loop]
    for (float k = 0.0; k <= levels + 0.5; k += 1.0)
    {
        weightedDepth += weight * Depth.SampleLevel(LinearClamp, uv, k);
        weightedAlpha += weight * Color.SampleLevel(LinearClamp, uv, k).a;
        weight *= 0.125;
    }
    if (weightedAlpha <= 0.0)
    {
        return 0.0; // 近くに映り込みが無い
    }
    float depth = max(weightedDepth / weightedAlpha, 0.0);
    // 映した点（この画素を通る光の上で、床から depth の深さの点）での、ワールドの長さ 1 の画素の数から、ぼけの半径（画素）を出す
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 nearPoint = mul(float4(ndc, 0.25, 1.0), InverseViewProj);
    float4 farPoint = mul(float4(ndc, 0.75, 1.0), InverseViewProj);
    nearPoint.xyz /= nearPoint.w;
    farPoint.xyz /= farPoint.w;
    float3 ray = farPoint.xyz - nearPoint.xyz;
    float t = abs(ray.y) > 1e-6 ? (-depth - nearPoint.y) / ray.y : 0.0;
    float3 position = nearPoint.xyz + ray * max(t, 0.0);
    float w = max(dot(float4(position, 1.0), ClipW), 1e-4);
    float radius = BlurParams.x * depth * BlurParams.y / w;
    // 縮めた段 k のぼけの半径は、およそ 2^k 画素
    float level = clamp(log2(max(radius, 1.0)), 0.0, levels);
    return SampleBlurred(uv, level);
}
