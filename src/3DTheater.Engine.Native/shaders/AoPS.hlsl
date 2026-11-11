#include "Post.hlsli"
#include "Ao.hlsli"

// 環境光の遮り（AO）: 奥行きから、物の隙間や接する所ほど小さくなる「光の届く割合」（1 = 遮られない）を求める。
// 点の位置と面の向きを奥行きから作り、広がり（ワールドの長さ）の円の中の 16 点を見て、
// 面の表の側にある点ほど（そして近いほど）光を遮ると数える。平らな面の上の点は、どれも面の上にあるので遮らない。
// 点ごとに見る向きを 4 x 4 の模様で回し、次の段（AoBlurPS・AoApplyPS）で横と縦にならして模様を消す

float main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float z;
    float3 center;
    float3 normal;
    float depthStep;
    if (!AoSurfaceAt(pixel, z, center, normal, depthStep)) {
        return 1.0; // 何も無い・向きが決まらない
    }

    // 広がりを画面の画素に直す（遠いほど小さい）。大きすぎると点がまばらになるので、上限（画像の高さの 15%）で止め、
    // そのときは広がりも同じ割合で縮める（ごく近くから見ると、遮りも狭くなる）
    float radius = AoParams.y;
    float pixels = radius * ScreenToView.y * 0.5 * SourceTexel.w / (ProjectionParams.z > 0.5 ? 1.0 : z);
    if (pixels > AoParams.z) {
        radius *= AoParams.z / pixels;
        pixels = AoParams.z;
    }
    if (pixels < 1.0) {
        return 1.0;
    }

    static const int kSamples = 16;
    static const float kBias = 0.1; // 少しだけ表の側にある点は数えない（曲面の細かな折れで暗くならないように）
    float rotation = Dither(pixel) * 6.2831853;
    float radius2 = radius * radius;
    float occlusion = 0.0;
    [loop] for (int i = 0; i < kSamples; i++)
    {
        float t = (i + 0.5) / kSamples;
        float angle = i * 2.39996323 + rotation;
        float2 offset = float2(cos(angle), sin(angle)) * max(sqrt(t) * pixels, 1.0);
        int2 at = pixel + int2(round(offset));
        float sz = DepthAt(at);
        if (sz >= kNothing) {
            continue;
        }
        float3 v = ViewPositionAt(at, sz) - center;
        float vv = dot(v, v);
        float cosine = dot(v, normal) * rsqrt(max(vv, 1e-8));
        float falloff = saturate(1.0 - vv / radius2);
        occlusion += saturate(cosine - kBias) * falloff;
    }
    // 3 倍: 直角に接する所で、強さ 1 のとき明るさが半分ほどになる（半球のうち半分が遮られるのと同じ）
    return saturate(1.0 - AoParams.x * 3.0 * occlusion / kSamples);
}
