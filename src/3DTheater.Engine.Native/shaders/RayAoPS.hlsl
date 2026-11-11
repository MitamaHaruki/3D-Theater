#include "Post.hlsli"
#include "Ao.hlsli"
#include "RayTrace.hlsli"

// 環境光の遮り（AO）をレイトレーシングで求める（AoPS の代わり）: 面から半球の向きへ、広がり（ワールドの長さ）の長さの光線を 8 本出し、
// モデルの三角形に当たった割合（近くで当たるほど重く数える）で「光の届く割合」を決める。画面の外・物の裏にある物も遮る。
// 光線の向きは、面の向きに近いほど多く選び（余弦の重み）、点ごとに 4 x 4 の模様で回す（次の段 AoBlurPS・AoApplyPS でならして模様を消す）

static const int kRays = 8;

float main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float z;
    float3 center;
    float3 normal;
    float depthStep;
    if (!AoSurfaceAt(pixel, z, center, normal, depthStep))
    {
        return 1.0; // 何も無い・向きが決まらない
    }
    float radius = AoParams.y;

    // ワールドの位置と向き。面から少し浮かせて、自分の面の下から光線を出さないようにする。
    // 浮かせる高さ: 奥行きの画像の細かさの分と、MSAA で奥行きが画素の中心からずれた点のものである分（浅い角度で見る面ほど、
    // 1 画素の中で奥行きが大きく変わり、求めた位置が面の下になりやすい。ずれは見る向きに、隣との差の半分まで）
    float3 ray = ProjectionParams.z > 0.5 ? float3(0.0, 0.0, 1.0) : center / z;
    float lift = 0.002 + z * 0.0005 + 0.6 * depthStep * abs(dot(ray, normal));
    float3 n = normalize(mul(float4(normal, 0.0), ViewToWorld).xyz);
    float3 origin = mul(float4(center, 1.0), ViewToWorld).xyz + n * lift;
    // 面に沿った 2 つの向き
    float3 helper = abs(n.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(helper, n));
    float3 bitangent = cross(n, tangent);

    float rotation = Dither(pixel) * 6.2831853;
    float shift = frac(52.9829189 * frac(dot(float2(pixel), float2(0.06711056, 0.00583715))));
    float occlusion = 0.0;
    [loop]
    for (int i = 0; i < kRays; i++)
    {
        float u = frac((i + 0.5) / kRays + shift);
        float angle = i * 2.39996323 + rotation;
        float r = sqrt(u);
        float3 direction = normalize(tangent * (r * cos(angle)) + bitangent * (r * sin(angle)) + n * sqrt(max(1.0 - u, 0.0)));
        RayHit hit;
        if (TraceRay(origin, direction, radius, (uint)(RayParams.y + 0.5), hit))
        {
            float d = hit.t / radius;
            occlusion += 1.0 - d * d; // 遠くで当たるほど軽く（広がりで 0）
        }
    }
    // 1 倍: 直角に接する所で、強さ 1 のとき明るさが半分ほどになる（半球の向きのうち半分が、すぐ近くで当たる）
    return saturate(1.0 - AoParams.x * occlusion / kRays);
}
