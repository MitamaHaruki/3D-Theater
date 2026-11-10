#include "Post.hlsli"

// 頂点番号 0〜2 から、画面全体を覆う三角形を作る（頂点バッファーは使わない）
PostVertex main(uint id : SV_VertexID)
{
    PostVertex output;
    float2 uv = float2((id << 1) & 2, id & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = uv;
    return output;
}
