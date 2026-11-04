#include "Basic.hlsli"

PSInput main(VSInput input)
{
    PSInput output;
    output.position = mul(float4(input.position, 1.0), WorldViewProj);
    output.color = input.color;
    return output;
}
