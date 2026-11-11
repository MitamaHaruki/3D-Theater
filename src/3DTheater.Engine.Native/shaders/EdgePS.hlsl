#include "Model.hlsli"

struct EdgePSOutput
{
    float4 color : SV_Target0;
    // 画面の中の反射の下準備（描画先があるとき）: 線は映り込まないので、下の面の値を線の不透明度だけ消す
    float4 target1 : SV_Target1;
    float4 target2 : SV_Target2;
    float4 target3 : SV_Target3;
};

// 輪郭線の色。材質の不透明度も掛ける（材質モーフで消した部分の線も消えるように）
EdgePSOutput main(EdgePSInput input)
{
    float alpha = EdgeColor.a * Diffuse.a;
    EdgePSOutput output;
    output.target1 = float4(0.0, 0.0, 0.0, alpha);
    output.target2 = output.target1;
    output.target3 = output.target1;
    if (RenderPass() == kPassAlpha)
    {
        output.color = float4(1.0, 1.0, 1.0, alpha); // アルファのパス: 線も物の形に含める
        return output;
    }
    if (RenderPass() != 0)
    {
        discard; // ほかのパスでは描かない（呼ぶ側も輪郭線を切る）
    }
    output.color = float4(EdgeColor.rgb, alpha);
    return output;
}
