#include "Post.hlsli"
#include "RayTrace.hlsli"

// 画面の中の反射（SSR）: つやのある PBR の材質の映り込みを、画面に映っている物の色にする。
// モデルを描くとき、PBR の材質は環境の映り込みを色に入れずに分けて書いておく（EnvironmentReflection）。ここで、面の向きから
// 映り込む向きへ光線を進め、画面の奥行きと交わった所の色を読み、見つかった分だけ環境の映り込みと置き換えて足す。
// 見つからない所（画面の外・物の裏・探す距離より遠い所）は、環境の映り込みのまま。色はどれも透明度をかけ合わせた値。
// レイトレーシングでの映り込み（RayParams.x = 1 のとき）: 画面の中で見つからない分（画面の外・物の裏・カメラの方へ戻る向き）は、
// 同じ光線をモデルの三角形に当てて、当たった物の色（簡単にした色の計算）を映す。当たらなければ環境の映り込みのまま

Texture2D EnvironmentReflection : register(t3); // 環境の映り込み（映り込みの強さを掛けた色）
Texture2D SurfaceNormal : register(t4);         // xyz = 見る向きの座標での面の向き × (1 + 粗さ)、w = 透明度

static const int kSteps = 48;
static const int kRefineSteps = 6;

// 見る向きの座標の位置から、画面の画素の位置（小数。整数の部分が画素の番号）
float2 PixelAt(float3 p)
{
    float2 xy = ProjectionParams.z > 0.5 ? p.xy : p.xy / p.z;
    float2 ndc = xy * ScreenToView.xy + ScreenToView.zw;
    return float2((ndc.x * 0.5 + 0.5) * SourceTexel.z, (0.5 - ndc.y * 0.5) * SourceTexel.w);
}

// 画素の中の位置での、画面の奥行き（周りの 4 画素の真ん中から補う。斜めに見る床では、1 画素の中でも奥行きが大きく変わるので、
// 画素の真ん中の値で比べると、自分の面に当たったことになりやすい）
float SceneDepthAt(float2 position)
{
    return LinearDepth.SampleLevel(LinearClamp, position * SourceTexel.xy, 0).x;
}

// 画面の上で光線の始めから終わりへ s（0〜1）進んだ所の、見る向きの距離（透視投影では 1/z がまっすぐ変わる）
float RayDepth(float s, float startZ, float endZ)
{
    return ProjectionParams.z > 0.5 ? lerp(startZ, endZ, s) : 1.0 / lerp(1.0 / startZ, 1.0 / endZ, s);
}

// 画面に映っている色（モデルの色と、分けて書いた環境の映り込みを足したもの）
float3 SceneColorAt(float2 uv)
{
    return Source.SampleLevel(LinearClamp, uv, 0).rgb + EnvironmentReflection.SampleLevel(LinearClamp, uv, 0).rgb;
}

// 画面の上で、見る向きの座標の start から r の向きへ reach だけ光線を進め、画面に映っている物と交わる所を探す。
// 見つかれば、その色と、使ってよい割合（画面の端の近く・探す距離の終わりの近くは、なめらかに弱める）
bool MarchScreen(int2 pixel, float3 start, float3 r, float reach, float roughness, out float3 color, out float confidence)
{
    color = 0.0;
    confidence = 0.0;
    float3 end = start + r * reach;
    float2 a = PixelAt(start);
    float2 b = PixelAt(end);

    // 画面の上で等しい間隔に進み、光線が画面の奥行きの手前から奥へ入った所を探す（間隔は画素ごとに少しずらし、段の模様を細かな粒にする）。
    // 始めの 1 画素の中は比べない（自分の面に当たったことにしないため）
    float jitter = frac(52.9829189 * frac(dot(float2(pixel), float2(0.06711056, 0.00583715))));
    float previous = 0.0;
    float previousZ = start.z;
    bool wasInFront = true; // 始めは面の手前（1 画素より先で、奥にあれば当たり）
    float hit = -1.0;
    [loop]
    for (int i = 1; i <= kSteps; ++i)
    {
        float s = (float(i) - jitter) / kSteps;
        float2 position = lerp(a, b, s);
        if (any(position < 0.0) || any(position >= SourceTexel.zw))
        {
            break; // 画面の外へ出た
        }
        float rayZ = RayDepth(s, start.z, end.z);
        if (length(position - a) >= 1.0)
        {
            float sceneZ = SceneDepthAt(position);
            float thickness = max(abs(rayZ - previousZ) * 2.0, 0.3 + rayZ * 0.01); // これより後ろへ抜けた所は、物の裏を通ったとみなす
            if (rayZ <= sceneZ)
            {
                wasInFront = true;
            }
            else if (wasInFront && rayZ - sceneZ < thickness)
            {
                hit = s;
                break;
            }
            else
            {
                wasInFront = false;
            }
        }
        previous = s;
        previousZ = rayZ;
    }
    if (hit < 0.0)
    {
        return false;
    }
    // 交わった所を、前の点との間で半分ずつ詰めて細かく決める
    float lower = previous;
    float upper = hit;
    [unroll]
    for (int k = 0; k < kRefineSteps; ++k)
    {
        float middle = (lower + upper) * 0.5;
        if (RayDepth(middle, start.z, end.z) > SceneDepthAt(lerp(a, b, middle)))
        {
            upper = middle;
        }
        else
        {
            lower = middle;
        }
    }
    float2 hitPosition = lerp(a, b, upper);

    // 当たった面が光線と同じ向きを向いている（物の裏に当たった）なら使わない
    float4 hitSurface = SurfaceNormal.Load(int3(int2(hitPosition), 0));
    if (hitSurface.w > 1e-3 && dot(normalize(hitSurface.xyz), r) > 0.3)
    {
        return false;
    }

    // 画面の端の近く・探す距離の終わりの近くは、なめらかに弱める
    float2 border = min(hitPosition, SourceTexel.zw - hitPosition) / max(0.08 * SourceTexel.w, 1.0);
    float fade = saturate(min(border.x, border.y));
    float travelled = abs(end.z - start.z) > 1e-4 ? saturate((RayDepth(upper, start.z, end.z) - start.z) / (end.z - start.z)) : upper;
    fade *= 1.0 - smoothstep(0.75, 1.0, travelled);

    // 当たった所の色。粗いほど、遠い物ほど、周りの 4 点も混ぜてぼかす
    float2 uv = hitPosition * SourceTexel.xy;
    color = SceneColorAt(uv);
    float spread = roughness * 0.2 * length(hitPosition - a);
    if (spread > 0.5)
    {
        float2 o = spread * 0.7071 * SourceTexel.xy;
        color += SceneColorAt(uv + o) + SceneColorAt(uv - o) + SceneColorAt(uv + float2(o.x, -o.y)) + SceneColorAt(uv + float2(-o.x, o.y));
        color *= 0.2;
    }
    confidence = fade;
    return true;
}

float4 main(PostVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    float4 scene = Source.Load(int3(pixel, 0));
    float4 environment = EnvironmentReflection.Load(int3(pixel, 0));
    float4 fresnel = Second.Load(int3(pixel, 0)); // 映り込みの強さ（透明度をかけ合わせた値）
    float4 unchanged = float4(scene.rgb + environment.rgb, scene.a);
    float strength = SsrParams.x;
    if (strength <= 0.0 || max(fresnel.r, max(fresnel.g, fresnel.b)) <= 1e-4)
    {
        return unchanged; // 映り込まない物（トゥーンの材質など）・効果を使わないとき
    }
    float z = LinearDepth.Load(int3(pixel, 0)).x;
    if (z >= kNothing)
    {
        return unchanged;
    }

    // 面の向きと粗さ（透明度をかけ合わせてあるので、透明度で割ってから、長さで分ける）
    float4 surface = SurfaceNormal.Load(int3(pixel, 0));
    float3 scaled = surface.xyz / max(surface.w, 1e-4);
    float scaledLength = length(scaled);
    float roughness = saturate(scaledLength - 1.0);
    float3 n = scaled / max(scaledLength, 1e-4);

    float3 p = ViewPositionAt(pixel, z);
    float3 v = ProjectionParams.z > 0.5 ? float3(0.0, 0.0, 1.0) : normalize(p);
    float3 r = reflect(v, n);
    // 粗い材質ほど弱く（ぼけた映り込みは環境のまま）
    float weight = strength * (1.0 - smoothstep(0.25, 0.65, roughness));
    if (weight <= 0.0)
    {
        return unchanged;
    }

    // 光線: 面から少し浮かせて、探す距離だけ進む
    float3 start = p + n * (0.01 + z * 0.002);
    float reach = SsrParams.y;

    // 画面の中: カメラの方へ戻る光線は、見えていない面に当たるので弱める。透視投影では、カメラのすぐ前より手前へ出ないように切る
    float facing = smoothstep(-0.3, 0.0, r.z);
    float3 screenColor = 0.0;
    float screenConfidence = 0.0;
    if (facing > 0.0)
    {
        float screenReach = reach;
        if (ProjectionParams.z < 0.5 && r.z < 0.0)
        {
            screenReach = min(reach, max(start.z - 0.1, 0.0) / -r.z);
        }
        float found;
        if (MarchScreen(pixel, start, r, screenReach, roughness, screenColor, found))
        {
            screenConfidence = facing * found;
        }
    }

    // 画面の中で見つからない分: レイトレーシングを使うときは、同じ光線をモデルの三角形に当てる（当たらなければ環境の映り込み）
    float3 behind = environment.rgb;
    if (RayParams.x > 0.5 && screenConfidence < 0.999)
    {
        float3 origin = mul(float4(start, 1.0), ViewToWorld).xyz;
        float3 direction = normalize(mul(float4(r, 0.0), ViewToWorld).xyz);
        RayHit hit;
        if (TraceRay(origin, direction, reach, hit))
        {
            // 光線の太さ（画素 1 つ分の広がり）: 面までに広がった分と、映ってから進む間に広がる分。粗いほど広がる
            bool orthographic = ProjectionParams.z > 0.5;
            float footprint = RayParams.z * (orthographic ? 1.0 : z + hit.t) + roughness * 0.5 * hit.t;
            float3 traced = RayShade(hit, direction, footprint);
            float far = 1.0 - smoothstep(0.75, 1.0, hit.t / reach); // 探す距離の終わりの近くは弱める
            behind = lerp(environment.rgb, traced * fresnel.rgb, far);
        }
    }

    float3 reflected = lerp(behind, screenColor * fresnel.rgb, screenConfidence);
    return float4(scene.rgb + lerp(environment.rgb, reflected, weight), scene.a);
}
