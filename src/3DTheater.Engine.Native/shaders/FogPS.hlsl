#include "Post.hlsli"

// 霧: カメラから物までの道のりのうち、霧の始まりより先の分に応じて、場面を霧の色に溶けこませる。
// 霧の量 = 1 - exp(-τ)。τ は「濃さ × 霧の中を通る長さ」。高さの尺度があるときは、濃さを高さで薄くして（高さ 0 で濃さそのまま、
// 尺度だけ上がるごとに 1/e。地面より下は濃さそのまま）、光の通り道に沿って足し合わせる。
// 背景（何も無い所）は、どこまでも遠い所として扱い、背景が不透明なときだけ霧を重ねる（透明な背景の書き出しは透明のまま）。
// 地面の影のように、奥行きを書かずに床に描いた物は、床（高さ 0）までの距離で霧をかける

static const float kFar = 1e20;

// カメラから向き dir（長さ 1、見る向きの座標）へ、距離 a から b まで進む間の τ
float FogDepth(float3 dir, float a, float b)
{
    if (b <= a) {
        return 0.0;
    }
    float density = FogParams.x;
    float height = FogParams.y;
    if (height <= 0.0) {
        return density * (b - a);
    }
    float cameraY = FogHeightAxis.w;
    float slope = dot(dir, FogHeightAxis.xyz); // 1 進むごとの高さの変わり方
    if (abs(slope) < 1e-5) {
        return density * (b - a) * exp(-max(cameraY, 0.0) / height);
    }
    // 高さ 0 を横切る距離で、地面より下（濃さそのまま）と上（高さで薄くなる）の区間に分ける
    float crossing = -cameraY / slope;
    float belowA = slope > 0.0 ? a : max(a, crossing);
    float belowB = slope > 0.0 ? min(b, crossing) : b;
    float aboveA = slope > 0.0 ? max(a, crossing) : a;
    float aboveB = slope > 0.0 ? b : min(b, crossing);
    float tau = max(belowB - belowA, 0.0);
    if (aboveB > aboveA) {
        float ya = max(cameraY + slope * aboveA, 0.0);
        float yb = max(cameraY + slope * aboveB, 0.0);
        tau += height / slope * (exp(-ya / height) - exp(-yb / height));
    }
    return density * tau;
}

float4 main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float4 scene = Source.Load(int3(pixel, 0));
    float z = LinearDepth.Load(int3(pixel, 0)).x;
    float3 fog = FogColor.rgb;
    float start = FogColor.a;

    // 光の通り道の向き（見る向きの距離 1 の所の位置から）と、見る向きの距離 1 あたりの道のり
    float3 ray = ViewPositionAt(pixel, 1.0);
    float perDepth = length(ray);
    float3 dir = ray / perDepth;

    float behind = BackgroundColor.a > 0.0 ? 1.0 - exp(-FogDepth(dir, start, kFar)) : 0.0;
    float amount = 0.0;
    if (z < kNothing) {
        amount = 1.0 - exp(-FogDepth(dir, start, z * perDepth));
    } else if (scene.a > 0.0) {
        // 奥行きの無い物（地面の影）: 床までの距離。床に届かない向きなら背景と同じ
        float slope = dot(dir, FogHeightAxis.xyz);
        float floorDistance = (slope < 0.0 && FogHeightAxis.w > 0.0) ? -FogHeightAxis.w / slope : kFar;
        amount = 1.0 - exp(-FogDepth(dir, start, floorDistance));
    }
    // 物は霧の色に寄せ、物の後ろ（透明な分）には背景の霧を重ねる（色は透明度をかけ合わせた値）
    float3 rgb = scene.rgb * (1.0 - amount) + fog * (amount * scene.a + behind * (1.0 - scene.a));
    float alpha = scene.a + behind * (1.0 - scene.a);
    return float4(rgb, alpha);
}
