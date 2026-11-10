#include "Post.hlsli"

// 仕上げ: 場面（Source、背景は透明）に色調補正をかけ、背景を後ろに置き、ブルーム（Second）を足し、周辺を暗くする。
// - 背景には色調補正・ブルームをかけない（白い背景が全部光ったり、色が変わったりしないように）
// - 色調補正は、透明度で割り戻した色にかけてから、かけ合わせ直す
// - ブルームは光を足すので、透明な背景の上では、足した光の分だけ不透明にする
float4 main(PostVertex input) : SV_Target
{
    float4 scene = Source.Load(int3(input.position.xy, 0));
    uint flags = PostFlags.x;
    float3 balance = WhiteBalance.rgb * GradeParams.x; // 色温度と明るさの倍率

    if ((flags & kPostColor) != 0 && scene.a > 0.0) {
        float3 graded = scene.rgb / scene.a;
        graded = saturate(graded * balance);
        graded = (graded - 0.5) * GradeParams.y + 0.5;
        float luminance = dot(graded, float3(0.2126, 0.7152, 0.0722));
        graded = lerp(luminance.xxx, graded, GradeParams.z);
        scene.rgb = saturate(graded) * scene.a;
    }

    float3 background = BackgroundColor.rgb * BackgroundColor.a;
    float3 color = scene.rgb + background * (1.0 - scene.a);
    float alpha = scene.a + BackgroundColor.a * (1.0 - scene.a);

    if ((flags & kPostBloom) != 0) {
        float3 bloom = Second.SampleLevel(LinearClamp, input.uv, 0).rgb * BloomParams.w;
        if ((flags & kPostColor) != 0) {
            bloom *= balance;
        }
        color += bloom;
        alpha += saturate(max(bloom.r, max(bloom.g, bloom.b))) * (1.0 - alpha);
    }

    if ((flags & kPostVignette) != 0) {
        // 中心からの距離（四隅で 1）。縦横の比は画面に合わせる
        float aspect = SourceTexel.z / max(SourceTexel.w, 1.0);
        float2 offset = (input.uv - 0.5) * float2(aspect, 1.0);
        float reach = length(offset) / length(float2(aspect, 1.0) * 0.5);
        color *= 1.0 - GradeParams.w * smoothstep(0.35, 1.0, reach);
    }
    return float4(saturate(color), saturate(alpha));
}
