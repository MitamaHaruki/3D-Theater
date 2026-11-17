// ModelOpaquePS から、レイトレーシングでの影（光線をたどる処理）を外した軽い版（設計判断記録 90 章）。
// レイトレーシングでの影を使わない回の、ふつうのモデルの描画に使う（いちばんよく使う。初めの設定はこれ）。色の計算は ModelPS と同じ
#define MODEL_NO_CLIP 1
#define MODEL_NO_RAYS 1
#include "ModelPS.hlsl"
