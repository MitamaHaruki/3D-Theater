#include "Post.hlsli"
#include "AoBlur.hlsli"

// 環境光の遮り（AO）をかける: 横にならした光の届く割合（Second の r）を縦にならして、場面（Source）の色に掛ける。
// 透明度は変えない（透明な背景の上でも、物だけが暗くなる）
float4 main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float4 scene = Source.Load(int3(pixel, 0));
    float z = LinearDepth.Load(int3(pixel, 0)).x;
    if (z >= kNothing || scene.a <= 0.0) {
        return scene;
    }
    return float4(scene.rgb * BlurVisible(Second, pixel, int2(0, 1), z), scene.a);
}
