#include "Post.hlsli"
#include "AoBlur.hlsli"

// 環境光の遮り（AO）: 光の届く割合（Source の r）を横にならす。縦は AoApplyPS でならす
float main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float z = LinearDepth.Load(int3(pixel, 0)).x;
    if (z >= kNothing) {
        return 1.0;
    }
    return BlurVisible(Source, pixel, int2(1, 0), z);
}
