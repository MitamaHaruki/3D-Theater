// ふつうのモデルの描画（レンダーパスでも床の映り込みでもないとき）に使う、ModelPS から捨てる処理（clip）を外したもの。
// 捨てる処理があると、GPU はピクセルシェーダーの前に奥行きを比べる（隠れた所を走らせない）ことができなくなるため。
// 色の計算は ModelPS と同じ
#define MODEL_NO_CLIP 1
#include "ModelPS.hlsl"
