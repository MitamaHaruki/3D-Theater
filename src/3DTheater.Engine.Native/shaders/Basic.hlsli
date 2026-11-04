// Phase 0 の確認用。頂点の色をそのまま出す。

cbuffer FrameConstants : register(b0)
{
    // C++ 側の DirectXMath と同じ「行ベクトル × 行列」の並び
    row_major float4x4 WorldViewProj;
};

struct VSInput
{
    float3 position : POSITION;
    float4 color : COLOR;
};

struct PSInput
{
    float4 position : SV_Position;
    float4 color : COLOR;
};
