// 環境光の遮り（AO）で共通の部分（AoPS・RayAoPS）。Post.hlsli の後に読み込む。
// 点の位置と面の向きを、見る向きの距離（LinearDepth）から作る

float DepthAt(int2 pixel)
{
    int2 size = int2(SourceTexel.zw);
    return LinearDepth.Load(int3(clamp(pixel, int2(0, 0), size - 1), 0)).x;
}

// 4 x 4 の模様（0〜15 を散らした並び）
float Dither(int2 pixel)
{
    static const float kPattern[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    return kPattern[(pixel.y & 3) * 4 + (pixel.x & 3)] / 16.0;
}

// 画素の点の、見る向きの座標での位置と面の向き（カメラの側を向けた長さ 1 の向き）。
// depthStep は、隣の画素との見る向きの距離の差（横と縦の合計）。MSAA では奥行きが画素の中心からずれた点のものなので、
// 位置はこの差の半分ほど、見る向きにずれることがある。
// 何も無い所・向きが決まらない所（隣が両方とも何も無い 1 画素の細い物など）は false
bool AoSurfaceAt(int2 pixel, out float z, out float3 center, out float3 normal, out float depthStep)
{
    z = DepthAt(pixel);
    center = 0.0;
    normal = float3(0.0, 0.0, -1.0);
    depthStep = 0.0;
    if (z >= kNothing)
    {
        return false;
    }
    center = ViewPositionAt(pixel, z);

    // 面の向き: 左右・上下のうち、奥行きの差の小さいほうの隣との差から作る（物の縁で、奥の物の側を使わないように）。
    // 画像の端では、内側の隣だけを使う（外側の隣は自分と同じ奥行きになり、向きが傾くため）
    int2 size = int2(SourceTexel.zw);
    float zl = DepthAt(pixel - int2(1, 0));
    float zr = DepthAt(pixel + int2(1, 0));
    float zu = DepthAt(pixel - int2(0, 1));
    float zd = DepthAt(pixel + int2(0, 1));
    bool useRight = pixel.x + 1 < size.x && (pixel.x == 0 || abs(zr - z) < abs(z - zl));
    bool useDown = pixel.y + 1 < size.y && (pixel.y == 0 || abs(zd - z) < abs(z - zu));
    float3 dx = useRight ? ViewPositionAt(pixel + int2(1, 0), zr) - center : center - ViewPositionAt(pixel - int2(1, 0), zl);
    float3 dy = useDown ? ViewPositionAt(pixel + int2(0, 1), zd) - center : center - ViewPositionAt(pixel - int2(0, 1), zu);
    depthStep = abs(dx.z) + abs(dy.z);
    float3 n = cross(dx, dy);
    float length2 = dot(n, n);
    if (!(length2 > 1e-20 && length2 < 1e20))
    {
        return false;
    }
    float3 toCamera = ProjectionParams.z > 0.5 ? float3(0.0, 0.0, -1.0) : -center;
    normal = n * (dot(n, toCamera) < 0.0 ? -1.0 : 1.0) * rsqrt(length2); // カメラの側を向ける
    return true;
}
