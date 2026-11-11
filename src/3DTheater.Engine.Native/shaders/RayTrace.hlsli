// レイトレーシング: 光線をモデルの三角形に当てる（RayTracing.h の下準備を読む）。映像効果の段とモデルの描画の両方で使う。
// 物ごとに、光線をモデルの座標へ移し、箱の入れ子（BVH）を手前の箱から順にたどる。
// - 見える面を探す光線（TraceRay）: いちばん近くで当たった所を探す。材質の不透明度が 0.5 より小さい所（テクスチャの透明な所も）と、
//   両面でない材質の裏の面は通り抜ける（モデルを描くときに見えない所）
// - 影を調べる光線（TraceShadowRay）: 影を落とす材質（「セルフ影マップ」）のどれかに当たるか。表も裏も遮る（奥行きの画像に
//   両面を描くのと同じ）。透明な所は通り抜ける。当たったらすぐやめる

struct RayNode
{
    float3 lo;
    uint leftOrFirst; // count が 0 なら子の箱の番号（子は 2 つ並ぶ）、でなければ三角形の番号
    float3 hi;
    uint count;
};

struct RayInstance
{
    float4 toObject0; // ワールド → モデルの座標（行ベクトル × 行列の 1〜3 列目）
    float4 toObject1;
    float4 toObject2;
    float4 toWorld0; // モデルの座標 → ワールド
    float4 toWorld1;
    float4 toWorld2;
    float4 boundsMin; // ワールドで囲む箱
    float4 boundsMax;
    uint4 offsets; // まとめたバッファーでの、箱・三角形・頂点・材質の始まり
};

struct RayVertex
{
    float3 position;
    float3 normal;
    float2 uv;
};

struct RayMaterial
{
    float4 diffuse; // a = 不透明度
    float4 ambient;
    float4 textureMultiply;
    float4 textureAdd;
    float4 toonMultiply;
    float4 toonAdd;
    float4 baseTint; // w = 粗さ
    float4 emissive; // w = 金属らしさ
    uint4 info;      // x = テクスチャの配列の何枚目、y = トゥーン、z = 印
};

StructuredBuffer<RayInstance> RayInstances : register(t5);
StructuredBuffer<RayNode> RayNodes : register(t6);
StructuredBuffer<uint4> RayTriangles : register(t7); // xyz = 頂点の番号、w = 材質の番号（どれもメッシュの中での番号）
StructuredBuffer<RayVertex> RayVertices : register(t8);
StructuredBuffer<RayMaterial> RayMaterials : register(t9);
Texture2DArray RayTextures : register(t10); // テクスチャを同じ大きさに縮めて並べたもの
SamplerState RayTextureSampler : register(s4); // 繰り返す

static const uint kRayNoTexture = 0xFFFFFFFF;
static const uint kRayDoubleSided = 1;
static const uint kRayAlphaTest = 2;
static const uint kRayPbr = 4;
static const uint kRayCastShadow = 8;
static const int kRayStackSize = 40;
static const uint kRayMaxVisits = 4096; // 1 本の光線で調べる箱の上限（とても込み入った所で止まらないように）
static const float kRayTextureSize = 256.0;
static const float kRayTextureLevels = 8.0; // 縮小版のいちばん小さい段（256 → 1）
static const float kRayMiss = 1e30;

struct RayHit
{
    float t;             // 光線の始めから当たった所までの長さ（ワールド）
    float2 barycentric;  // 三角形の 2 つ目・3 つ目の頂点の重み
    uint instance;
    uint triangleIndex; // まとめたバッファーでの三角形の番号
};

float3 RayPoint(float4 c0, float4 c1, float4 c2, float3 p)
{
    float4 h = float4(p, 1.0);
    return float3(dot(h, c0), dot(h, c1), dot(h, c2));
}

float3 RayVector(float4 c0, float4 c1, float4 c2, float3 v)
{
    float4 h = float4(v, 0.0);
    return float3(dot(h, c0), dot(h, c1), dot(h, c2));
}

// 向きの 1 / 値（0 の成分は、とても小さな値にして割る）
float3 RayInverse(float3 direction)
{
    float3 safe = abs(direction) > 1e-20 ? direction : (direction >= 0.0 ? 1e-20 : -1e-20);
    return 1.0 / safe;
}

// 光線が箱を通る区間の始め（tMax までに通らなければ kRayMiss）
float RayBoxEntry(float3 origin, float3 inverse, float3 lo, float3 hi, float tMax)
{
    float3 a = (lo - origin) * inverse;
    float3 b = (hi - origin) * inverse;
    float3 lower = min(a, b);
    float3 upper = max(a, b);
    float enter = max(max(lower.x, lower.y), max(lower.z, 0.0));
    float leave = min(min(upper.x, upper.y), min(upper.z, tMax));
    return enter <= leave ? enter : kRayMiss;
}

// 三角形との交わり（Möller–Trumbore）。front は表の面（正面から見て時計回り。モデルを描くときと同じ）に当たったか
bool RayTriangleHit(float3 origin, float3 direction, float3 p0, float3 p1, float3 p2, out float t, out float2 weights, out bool front)
{
    t = kRayMiss;
    weights = 0.0;
    float3 e1 = p1 - p0;
    float3 e2 = p2 - p0;
    float3 pv = cross(direction, e2);
    float det = dot(e1, pv);
    front = det > 0.0;
    if (abs(det) < 1e-30)
    {
        return false;
    }
    float inverse = 1.0 / det;
    float3 s = origin - p0;
    float u = dot(s, pv) * inverse;
    if (u < 0.0 || u > 1.0)
    {
        return false;
    }
    float3 q = cross(s, e1);
    float v = dot(direction, q) * inverse;
    if (v < 0.0 || u + v > 1.0)
    {
        return false;
    }
    t = dot(e2, q) * inverse;
    weights = float2(u, v);
    return true;
}

// 当たった所を通り抜けるか（見える面を探す光線: 両面でない材質の裏・透明な所。影を調べる光線: 影を落とさない材質・透明な所）
bool RayPassesThrough(RayMaterial material, uint4 tri, uint vertexOffset, float2 weights, bool front, bool shadow)
{
    if (shadow)
    {
        if ((material.info.z & kRayCastShadow) == 0)
        {
            return true;
        }
    }
    else if (!front && (material.info.z & kRayDoubleSided) == 0)
    {
        return true;
    }
    float alpha = material.diffuse.a;
    if ((material.info.z & kRayAlphaTest) != 0 && material.info.x != kRayNoTexture)
    {
        float2 uv = RayVertices[vertexOffset + tri.x].uv * (1.0 - weights.x - weights.y) + RayVertices[vertexOffset + tri.y].uv * weights.x
            + RayVertices[vertexOffset + tri.z].uv * weights.y;
        float texel = RayTextures.SampleLevel(RayTextureSampler, float3(uv, material.info.x), 1.0).a;
        alpha *= saturate(texel * material.textureMultiply.a + material.textureAdd.a);
    }
    return alpha < 0.5;
}

// ワールドの光線（direction は長さ 1）を、tMax までの間の三角形に当てる（instanceCount は物の数）。
// shadow が false なら、いちばん近くで当たる所を探す。true なら、影を落とす材質に当たった時点でやめる
bool RayTraverse(float3 origin, float3 direction, float tMax, uint instanceCount, bool shadow, out RayHit hit)
{
    hit.t = tMax;
    hit.barycentric = 0.0;
    hit.instance = 0;
    hit.triangleIndex = 0;
    bool found = false;
    float3 worldInverse = RayInverse(direction);
    uint visits = 0;
    uint count = min(instanceCount, 64u);
    uint stackNode[kRayStackSize];
    float stackEntry[kRayStackSize];
    [loop]
    for (uint i = 0; i < count; ++i)
    {
        RayInstance instance = RayInstances[i];
        if (RayBoxEntry(origin, worldInverse, instance.boundsMin.xyz, instance.boundsMax.xyz, hit.t) >= kRayMiss)
        {
            continue;
        }
        // モデルの座標での光線（置き方は形を変えても直線は直線なので、長さ t の値はワールドと同じ）
        float3 o = RayPoint(instance.toObject0, instance.toObject1, instance.toObject2, origin);
        float3 d = RayVector(instance.toObject0, instance.toObject1, instance.toObject2, direction);
        float3 inverse = RayInverse(d);
        int depth = 0;
        uint node = instance.offsets.x;
        [loop]
        while (visits < kRayMaxVisits)
        {
            visits++;
            RayNode current = RayNodes[node];
            bool descend = false;
            if (current.count > 0)
            {
                [loop]
                for (uint k = 0; k < current.count; ++k)
                {
                    uint index = instance.offsets.y + current.leftOrFirst + k;
                    uint4 tri = RayTriangles[index];
                    float3 p0 = RayVertices[instance.offsets.z + tri.x].position;
                    float3 p1 = RayVertices[instance.offsets.z + tri.y].position;
                    float3 p2 = RayVertices[instance.offsets.z + tri.z].position;
                    float t;
                    float2 weights;
                    bool front;
                    if (RayTriangleHit(o, d, p0, p1, p2, t, weights, front) && t > 0.0 && t < hit.t)
                    {
                        RayMaterial material = RayMaterials[instance.offsets.w + tri.w];
                        if (!RayPassesThrough(material, tri, instance.offsets.z, weights, front, shadow))
                        {
                            hit.t = t;
                            hit.barycentric = weights;
                            hit.instance = i;
                            hit.triangleIndex = index;
                            found = true;
                            if (shadow)
                            {
                                return true; // 影は、どれかに当たれば十分
                            }
                        }
                    }
                }
            }
            else
            {
                // 2 つの子の箱のうち、光線が先に入る方へ進み、もう一方は後で調べる
                uint left = instance.offsets.x + current.leftOrFirst;
                RayNode a = RayNodes[left];
                RayNode b = RayNodes[left + 1];
                float entryA = RayBoxEntry(o, inverse, a.lo, a.hi, hit.t);
                float entryB = RayBoxEntry(o, inverse, b.lo, b.hi, hit.t);
                uint first = left;
                uint second = left + 1;
                if (entryB < entryA)
                {
                    float swapEntry = entryA;
                    entryA = entryB;
                    entryB = swapEntry;
                    first = left + 1;
                    second = left;
                }
                if (entryA < kRayMiss)
                {
                    if (entryB < kRayMiss && depth < kRayStackSize)
                    {
                        stackNode[depth] = second;
                        stackEntry[depth] = entryB;
                        depth++;
                    }
                    node = first;
                    descend = true;
                }
            }
            if (!descend)
            {
                // 後で調べる箱のうち、今いちばん近い当たりより手前で入る箱へ戻る
                bool resumed = false;
                [loop]
                while (depth > 0 && !resumed)
                {
                    depth--;
                    if (stackEntry[depth] < hit.t)
                    {
                        node = stackNode[depth];
                        resumed = true;
                    }
                }
                if (!resumed)
                {
                    break;
                }
            }
        }
    }
    return found;
}

// 見える面を探す光線: いちばん近くで当たる所
bool TraceRay(float3 origin, float3 direction, float tMax, uint instanceCount, out RayHit hit)
{
    return RayTraverse(origin, direction, tMax, instanceCount, false, hit);
}

// 影を調べる光線: 影を落とす材質のどれかに当たるか
bool TraceShadowRay(float3 origin, float3 direction, float tMax, uint instanceCount)
{
    RayHit hit;
    return RayTraverse(origin, direction, tMax, instanceCount, true, hit);
}
