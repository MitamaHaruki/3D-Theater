// 環境光の遮り（AO）の光の届く割合を、同じ面の上でならす（AoPS の 4 x 4 の模様を消す）。
// 横か縦の 9 点を、真ん中ほど重く（1〜5〜1）平均する。4 画素ごとの模様のどの位置もほぼ同じ重さになる。
// 平均には、奥行きの近い点だけを使う（手前の物と奥の物の割合が混ざって、縁がにじまないように）

float BlurVisible(Texture2D visible, int2 pixel, int2 step, float z)
{
    int2 size = int2(SourceTexel.zw);
    float sum = 0.0;
    float weight = 0.0;
    [unroll] for (int i = -4; i <= 4; i++)
    {
        int2 at = clamp(pixel + step * i, int2(0, 0), size - 1);
        float sz = LinearDepth.Load(int3(at, 0)).x;
        float w = sz < kNothing ? (5.0 - abs(i)) * saturate(1.0 - abs(sz - z) / (z * 0.05)) : 0.0;
        sum += visible.Load(int3(at, 0)).r * w;
        weight += w;
    }
    return weight > 0.0 ? sum / weight : 1.0;
}
