#include "Model.hlsli"

// 位置だけを変換する（セルフシャドウの奥行きの画像と、地面の影に使う。WorldViewProj にそれぞれの変換を入れる）
EdgePSInput main(ModelVSInput input)
{
    EdgePSInput output;
    output.position = mul(float4(input.position, 1.0), WorldViewProj);
    return output;
}
