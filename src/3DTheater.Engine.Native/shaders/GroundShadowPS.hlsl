#include "Model.hlsli"

// 地面の影の色（重なった所を何度も塗らないよう、ステンシルで 1 ピクセル 1 回にしてある）
float4 main(EdgePSInput input) : SV_Target
{
    return GroundShadowColor;
}
