#include "Post.hlsli"

// ブルーム: 半分の大きさへ縮める（4 点 x 2 x 2 画素 = 4 x 4 画素の平均）
float4 main(PostVertex input) : SV_Target
{
    float2 d = SourceTexel.xy;
    float3 color = Source.SampleLevel(LinearClamp, input.uv + float2(-d.x, -d.y), 0).rgb;
    color += Source.SampleLevel(LinearClamp, input.uv + float2(d.x, -d.y), 0).rgb;
    color += Source.SampleLevel(LinearClamp, input.uv + float2(-d.x, d.y), 0).rgb;
    color += Source.SampleLevel(LinearClamp, input.uv + float2(d.x, d.y), 0).rgb;
    return float4(color * 0.25, 0.0);
}
