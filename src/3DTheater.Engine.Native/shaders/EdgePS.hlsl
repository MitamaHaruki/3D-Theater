#include "Model.hlsli"

// 輪郭線の色。材質の不透明度も掛ける（材質モーフで消した部分の線も消えるように）
float4 main(EdgePSInput input) : SV_Target
{
    if (RenderPass() == kPassAlpha)
    {
        return float4(1.0, 1.0, 1.0, EdgeColor.a * Diffuse.a); // アルファのパス: 線も物の形に含める
    }
    if (RenderPass() != 0)
    {
        discard; // ほかのパスでは描かない（呼ぶ側も輪郭線を切る）
    }
    return float4(EdgeColor.rgb, EdgeColor.a * Diffuse.a);
}
