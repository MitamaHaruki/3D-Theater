#include "Model.hlsli"

// 輪郭線の色。材質の不透明度も掛ける（材質モーフで消した部分の線も消えるように）
float4 main(EdgePSInput input) : SV_Target
{
    return float4(EdgeColor.rgb, EdgeColor.a * Diffuse.a);
}
