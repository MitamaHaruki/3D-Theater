// 床の映り込みのぼかし（つやのある床）で共通の宣言。
// 映り込みだけを別の画像に描き（透明度をかけ合わせた色と、床からの深さ × 不透明度）、半分ずつ縮めた段を作ってから、
// 画素ごとに床からの深さに合った段を読んで場面に重ねる。画面全体を覆う 1 枚の三角形で描く（頂点シェーダーは映像効果と同じ）。

cbuffer ReflectionConstants : register(b0)
{
    row_major float4x4 InverseViewProj; // 画面の座標（-1〜1、奥行き 0〜1）→ ワールド
    float4 ClipW;      // ワールドの位置から投影の w を出す値（ビュー投影行列の 4 列目）
    float4 Texel;      // xy = 読む画像の 1 画素の大きさ（UV）、zw = 書く画像の幅と高さ（画素）
    float4 BlurParams; // x = 床からの深さ 1 あたりのぼけの半径（ワールドの長さ）、y = ワールドの長さ 1 の画素の数（w で割る前）、
                       // z = いちばん粗い段の番号
};

Texture2D Color : register(t0);        // 透明度をかけ合わせた色
Texture2D<float> Depth : register(t1); // 床からの深さ × 不透明度（同じ段の Color の a で割ると深さ）
SamplerState LinearClamp : register(s0);

struct ReflectionVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};
