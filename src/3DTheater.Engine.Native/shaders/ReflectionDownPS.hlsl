#include "Reflection.hlsli"

struct DownOutput
{
    float4 color : SV_Target0;
    float depth : SV_Target1;
};

// 床の映り込み: 1 つ細かい段から、半分の大きさへ縮める。真ん中（2 x 2 画素の平均）を 4、斜めの 4 つ（それぞれ 2 x 2 画素の平均）を
// 1 ずつの重みで平均する（4 x 4 画素をなだらかに平均する。段を重ねると、ガウスのぼかしに近くなる）。色と深さを同じ重みで縮める
DownOutput main(ReflectionVertex input)
{
    float2 d = Texel.xy; // 読む段の 1 画素
    float4 color = Color.SampleLevel(LinearClamp, input.uv, 0) * 4.0;
    float depth = Depth.SampleLevel(LinearClamp, input.uv, 0) * 4.0;
    const float2 corners[4] = { float2(-d.x, -d.y), float2(d.x, -d.y), float2(-d.x, d.y), float2(d.x, d.y) };
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        color += Color.SampleLevel(LinearClamp, input.uv + corners[i], 0);
        depth += Depth.SampleLevel(LinearClamp, input.uv + corners[i], 0);
    }
    DownOutput output;
    output.color = color / 8.0;
    output.depth = depth / 8.0;
    return output;
}
