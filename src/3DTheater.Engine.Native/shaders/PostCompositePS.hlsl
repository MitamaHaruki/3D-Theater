#include "Post.hlsli"

// 仕上げ: 場面（Source、背景は透明）の色を整え（色調補正・塗り・影の色）、線画を引き、背景を後ろに置き、
// ブルーム（Second）を足し、紙の質感を重ね、周辺を暗くする。
// - 背景には色調補正・塗り・影の色・線画・ブルームをかけない（紙の質感と周辺減光は背景にもかかる）
// - 色を整えるのは、透明度で割り戻した色にかけてから、かけ合わせ直す
// - ブルームは光を足すので、透明な背景の上では、足した光の分だけ不透明にする

static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

float2 DepthAt(int2 pixel)
{
    int2 size = int2(SourceTexel.zw);
    return LinearDepth.Load(int3(clamp(pixel, int2(0, 0), size - 1), 0));
}

// 線画の濃さ（0〜1）。線は手前の物の側に引く（何も無い所には引かない）。
// - 段差: 太さだけ離れた両隣のどちらかが、この点より一定の割合以上奥にある
// - 折れ目: 両隣の「面の上でまっすぐ変わる値」の 2 回の差が大きい（平らな面では 0）
float LineAt(int2 pixel)
{
    float2 center = DepthAt(pixel);
    if (center.x >= kNothing) {
        return 0.0;
    }
    int reach = max(1, (int)round(LineParams.x));
    static const int2 kDirections[4] = { int2(1, 0), int2(0, 1), int2(1, 1), int2(1, -1) };
    float ink = 0.0;
    [unroll]
    for (int i = 0; i < 4; ++i) {
        float2 a = DepthAt(pixel + kDirections[i] * reach);
        float2 b = DepthAt(pixel - kDirections[i] * reach);
        float gap = (max(a.x, b.x) - center.x) / center.x;
        ink = max(ink, smoothstep(LineParams.z, LineParams.z * 2.0, gap));
        if (a.x < kNothing && b.x < kNothing) {
            float bend = abs(a.y + b.y - 2.0 * center.y) / max(abs(center.y), 1e-6);
            ink = max(ink, smoothstep(LineParams.w, LineParams.w * 2.0, bend));
        }
    }
    return ink;
}

// 紙の質感に使う、なめらかなむら（格子点ごとの乱数を、なめらかにつないだもの。0〜1）
float Hash(int2 p)
{
    uint h = (uint)p.x * 374761393u + (uint)p.y * 668265263u;
    h = (h ^ (h >> 13)) * 1274126177u;
    h ^= h >> 16;
    return (float)(h & 0x00FFFFFFu) / 16777215.0;
}

float ValueNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = p - cell;
    f = f * f * (3.0 - 2.0 * f);
    int2 c = int2(cell);
    float a = Hash(c);
    float b = Hash(c + int2(1, 0));
    float d = Hash(c + int2(0, 1));
    float e = Hash(c + int2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(d, e, f.x), f.y);
}

float PaperAt(float2 pixel)
{
    float grain = max(PaintParams.w, 0.5);
    float n = ValueNoise(pixel / grain) * 0.5;
    n += ValueNoise(pixel / (grain * 3.0) + 17.0) * 0.3;
    n += ValueNoise(pixel / (grain * 9.0) + 53.0) * 0.2;
    return n;
}

float4 main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float4 scene = Source.Load(int3(pixel, 0));
    uint flags = PostFlags.x;
    float3 balance = WhiteBalance.rgb * GradeParams.x; // 色温度と明るさの倍率

    if ((flags & (kPostColor | kPostPosterize | kPostShadowTint)) != 0 && scene.a > 0.0) {
        float3 tone = scene.rgb / scene.a;
        if ((flags & kPostColor) != 0) {
            tone = saturate(tone * balance);
            tone = (tone - 0.5) * GradeParams.y + 0.5;
            float luminance = dot(tone, kLuma);
            tone = lerp(luminance.xxx, tone, GradeParams.z);
            tone = saturate(tone);
        }
        if ((flags & kPostPosterize) != 0) {
            // 明るさを段数に分け（黒と白は保つ）、色合いはそのままにする。段の境目は少しだけなめらかにする
            float luminance = dot(tone, kLuma);
            float steps = max(PaintParams.x - 1.0, 1.0);
            float level = luminance * steps;
            float band = floor(level) + smoothstep(0.45, 0.55, frac(level));
            tone *= (band / steps) / max(luminance, 1e-4);
            tone = saturate(tone);
        }
        if ((flags & kPostShadowTint) != 0) {
            // 暗い所ほど、影の色の色合いに寄せる（明るさはあまり変えない）
            float luminance = dot(tone, kLuma);
            float amount = PaintParams.y * (1.0 - smoothstep(0.2, 0.6, luminance));
            tone = saturate(lerp(tone, tone * ShadowTint.rgb, amount));
        }
        scene.rgb = tone * scene.a;
    }

    if ((flags & kPostLines) != 0) {
        float ink = LineAt(pixel) * LineParams.y;
        scene.rgb = lerp(scene.rgb, LineColor.rgb * scene.a, ink);
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

    if ((flags & kPostPaper) != 0) {
        // 紙のむら: 明るい所ほどわずかに暗くなる（紙の凹凸に絵の具がのった感じ）
        color *= 1.0 - PaintParams.z * 0.35 * PaperAt(input.position.xy);
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
