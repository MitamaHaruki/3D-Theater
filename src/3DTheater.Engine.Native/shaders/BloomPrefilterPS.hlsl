#include "Post.hlsli"

// ブルームの 1 段目: 場面を半分の大きさに縮めながら、明るい所だけを取り出す。
// 4 点（それぞれ 2 x 2 画素の平均）を平均して 4 x 4 画素の範囲をならし、ちらつきを抑える。
// 明るさは r・g・b のいちばん大きい値。光り始める明るさの前後は、なめらかに増やす（境目のやわらかさ）
float4 main(PostVertex input) : SV_Target
{
    float2 d = SourceTexel.xy;
    float3 color = Source.SampleLevel(LinearClamp, input.uv + float2(-d.x, -d.y), 0).rgb;
    color += Source.SampleLevel(LinearClamp, input.uv + float2(d.x, -d.y), 0).rgb;
    color += Source.SampleLevel(LinearClamp, input.uv + float2(-d.x, d.y), 0).rgb;
    color += Source.SampleLevel(LinearClamp, input.uv + float2(d.x, d.y), 0).rgb;
    color *= 0.25;

    float threshold = BloomParams.x;
    float knee = BloomParams.y;
    float brightness = max(color.r, max(color.g, color.b));
    float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-5);
    float contribution = max(soft, brightness - threshold) / max(brightness, 1e-5);
    return float4(color * max(contribution, 0.0), 0.0);
}
