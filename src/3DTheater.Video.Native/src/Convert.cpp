// BGRA → NV12。Windows の変換に任せると、大きさによって別の決まり（BT.601 / BT.709）で変換されるため自分で行う。
#include "Convert.h"

#include <algorithm>

namespace tv {

namespace {

// 係数は 65536 倍の整数（Kr・Kb を、明るさ 219 段・色差 224 段に合わせたもの）
struct Coefficients {
    int yr, yg, yb;
    int ur, ug, ub;
    int vr, vg, vb;
};

// BT.709: Kr = 0.2126、Kb = 0.0722
constexpr Coefficients kBt709{ 11966, 40254, 4064, -6596, -22189, 28784, 28784, -26145, -2639 };
// BT.601: Kr = 0.299、Kb = 0.114
constexpr Coefficients kBt601{ 16829, 33039, 6416, -9714, -19071, 28784, 28784, -24103, -4681 };

} // namespace

void BgraToNv12(const BYTE* bgra, int stride, UINT32 width, UINT32 height, BYTE* nv12, YuvMatrix matrix)
{
    const Coefficients& c = matrix == YuvMatrix::Bt601 ? kBt601 : kBt709;
    constexpr int kHalf = 1 << 15;
    BYTE* luma = nv12;
    BYTE* chroma = nv12 + static_cast<size_t>(width) * height;
    for (UINT32 y = 0; y < height; y += 2) {
        const BYTE* row0 = bgra + static_cast<size_t>(y) * stride;
        const BYTE* row1 = row0 + stride;
        BYTE* luma0 = luma + static_cast<size_t>(y) * width;
        BYTE* luma1 = luma0 + width;
        BYTE* uv = chroma + static_cast<size_t>(y / 2) * width;
        for (UINT32 x = 0; x < width; x += 2) {
            int sumB = 0, sumG = 0, sumR = 0;
            const BYTE* pixels[4] = { row0 + x * 4, row0 + x * 4 + 4, row1 + x * 4, row1 + x * 4 + 4 };
            BYTE* targets[4] = { luma0 + x, luma0 + x + 1, luma1 + x, luma1 + x + 1 };
            for (int i = 0; i < 4; ++i) {
                const int b = pixels[i][0], g = pixels[i][1], r = pixels[i][2];
                *targets[i] = static_cast<BYTE>(((c.yr * r + c.yg * g + c.yb * b + kHalf) >> 16) + 16);
                sumB += b;
                sumG += g;
                sumR += r;
            }
            // 4 画素の平均（合計を 4 で割る分は、シフトを 2 つ増やして行う）
            const int u = ((c.ur * sumR + c.ug * sumG + c.ub * sumB + (kHalf << 2)) >> 18) + 128;
            const int v = ((c.vr * sumR + c.vg * sumG + c.vb * sumB + (kHalf << 2)) >> 18) + 128;
            uv[x] = static_cast<BYTE>(std::clamp(u, 16, 240));
            uv[x + 1] = static_cast<BYTE>(std::clamp(v, 16, 240));
        }
    }
}

} // namespace tv
