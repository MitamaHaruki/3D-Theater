#include "Post.hlsli"

// テクスチャを、レイトレーシングで使う配列の 1 枚（256 x 256）へ縮めて写す。
// 縮める割合に合った縮小版（ミップマップ）を読む（細かい模様がちらつかないように）
float4 main(PostVertex input) : SV_Target
{
    uint width;
    uint height;
    uint levels;
    Source.GetDimensions(0, width, height, levels);
    float lod = max(log2(float(max(width, height)) / 256.0), 0.0);
    return Source.SampleLevel(LinearClamp, input.uv, lod);
}
