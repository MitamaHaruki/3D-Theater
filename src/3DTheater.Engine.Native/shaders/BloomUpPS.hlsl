#include "Post.hlsli"

// ブルーム: 1 つ小さい段（Second）をなめらかに広げ（3 x 3 のテント）、この段（Source）と混ぜる。
// 広がりが大きいほど小さい段（遠くまで広がった光）の割合を増やす。割合の合計は 1 なので、明るさの総量は変わらない
float4 main(PostVertex input) : SV_Target
{
    float2 d = SourceTexel.xy; // 小さい段の 1 画素
    float3 blurred = Second.SampleLevel(LinearClamp, input.uv, 0).rgb * 4.0;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(-d.x, 0.0), 0).rgb * 2.0;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(d.x, 0.0), 0).rgb * 2.0;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(0.0, -d.y), 0).rgb * 2.0;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(0.0, d.y), 0).rgb * 2.0;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(-d.x, -d.y), 0).rgb;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(d.x, -d.y), 0).rgb;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(-d.x, d.y), 0).rgb;
    blurred += Second.SampleLevel(LinearClamp, input.uv + float2(d.x, d.y), 0).rgb;
    blurred /= 16.0;

    float3 current = Source.SampleLevel(LinearClamp, input.uv, 0).rgb;
    return float4(lerp(current, blurred, BloomParams.z), 0.0);
}
