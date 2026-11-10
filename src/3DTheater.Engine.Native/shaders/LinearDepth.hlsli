#include "Post.hlsli"

// 線画の準備: 奥行きの画像（0〜1）を、見る向きの距離に直す。
// y には「平らな面の上で、画面の位置に対してまっすぐ変わる値」を入れる（透視投影では距離の逆数、平行投影では距離そのもの）。
// これの 2 回の差（両隣の和 - 2 × 中心）が 0 でない所が、面の折れ目になる
#ifdef MULTISAMPLED
Texture2DMS<float> DepthBuffer : register(t3);
#else
Texture2D<float> DepthBuffer : register(t3);
#endif

float2 main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
#ifdef MULTISAMPLED
    float depth = DepthBuffer.Load(pixel, 0); // 1 つ目の点（MSAA の点はまとめられないので代表にする）
#else
    float depth = DepthBuffer.Load(int3(pixel, 0));
#endif
    if (depth >= 1.0) {
        return float2(kNothing, 0.0);
    }
    bool orthographic = ProjectionParams.z > 0.5;
    float z = orthographic ? (depth - ProjectionParams.y) / ProjectionParams.x : ProjectionParams.y / (depth - ProjectionParams.x);
    z = max(z, 1e-4);
    return float2(z, orthographic ? z : 1.0 / z);
}
